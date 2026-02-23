using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ApiForge.Models;
using ApiForge.Utilities;

namespace ApiForge.Agents
{
    /// <summary>
    /// Core integration agent that builds a DAG of HTTP request dependencies.
    /// Core integration agent that builds a DAG of HTTP request dependencies.
    /// </summary>
    public class IntegrationAgent
    {
        // State keys used by the agent pipeline.
        public const string ActionUrlKey = "action_url";
        public const string InProcessNodeKey = "in_process_node";
        public const string ToBeProcessedNodesKey = "to_be_processed_nodes";
        public const string InProcessNodeDynamicPartsKey = "in_process_node_dynamic_parts";
        public const string MasterNodeKey = "master_node";
        public const string InputVariablesKey = "input_variables";

        private readonly string _prompt;
        private readonly LlmService _llmService;

        /// <summary>
        /// Tracks dynamic parts that have already been processed to avoid duplicates.
        /// </summary>
        public HashSet<string> DuplicatePartSet { get; }

        /// <summary>
        /// The ID of the global master node in the DAG, once identified.
        /// </summary>
        public string? GlobalMasterNodeId { get; set; }

        /// <summary>
        /// Maps each HttpRequestModel to its formatted response dictionary.
        /// </summary>
        public Dictionary<HttpRequestModel, Dictionary<string, string>> ReqToResMap { get; }

        /// <summary>
        /// Maps URL strings to their (Request, Response) pairs.
        /// </summary>
        public Dictionary<string, (HttpRequestModel Request, Dictionary<string, string> Response)> UrlToResReqDict { get; }

        /// <summary>
        /// List of URL details extracted from the HAR file.
        /// </summary>
        public List<(string Method, string Url, string ResponseFormat, string ResponsePreview)> HarUrls { get; }

        /// <summary>
        /// Parsed cookie data keyed by cookie name.
        /// </summary>
        public Dictionary<string, CookieData> CookieDict { get; }

        /// <summary>
        /// Maps curl command strings to DAG node IDs to avoid duplicate nodes.
        /// </summary>
        public Dictionary<string, string> CurlToIdDict { get; }

        /// <summary>
        /// Maps cookie keys to DAG node IDs to avoid duplicate cookie nodes.
        /// </summary>
        public Dictionary<string, string> CookieToIdDict { get; }

        /// <summary>
        /// The DAG manager that holds the dependency graph.
        /// </summary>
        public DagManager DagManager { get; }

        /// <summary>
        /// Creates a new IntegrationAgent.
        /// </summary>
        /// <param name="prompt">The user prompt describing the desired action.</param>
        /// <param name="harFilePath">Path to the HAR file.</param>
        /// <param name="cookiePath">Path to the cookies JSON file.</param>
        /// <param name="llmService">The LLM service used for function-calling interactions.</param>
        public IntegrationAgent(string prompt, string harFilePath, string cookiePath, LlmService llmService)
        {
            _prompt = prompt;
            _llmService = llmService;

            DuplicatePartSet = new HashSet<string>();
            GlobalMasterNodeId = null;

            ReqToResMap = HarProcessing.ParseHarFile(harFilePath);
            UrlToResReqDict = HarProcessing.BuildUrlToReqResMap(ReqToResMap);
            HarUrls = HarProcessing.GetHarUrls(harFilePath);
            CookieDict = HarProcessing.ParseCookieFile(cookiePath);

            CurlToIdDict = new Dictionary<string, string>();
            CookieToIdDict = new Dictionary<string, string>();
            DagManager = new DagManager();
        }

        /// <summary>
        /// Identifies the URL responsible for the action described by the prompt.
        /// Uses LLM function calling to pick the correct URL from the HAR URLs.
        /// </summary>
        public async Task<AgentState> EndUrlIdentifyAgentAsync(AgentState state)
        {
            var parametersSchema = ParseJsonSchema(@"{
                ""type"": ""object"",
                ""properties"": {
                    ""url"": {
                        ""type"": ""string"",
                        ""description"": ""The URL responsible for " + EscapeJsonString(_prompt) + @"""
                    }
                },
                ""required"": [""url""]
            }");

            Console.WriteLine($"[EndUrlIdentify] {HarUrls.Count} URL:er efter filtrering");
            foreach (var (m, u, _, _) in HarUrls)
                Console.WriteLine($"  {m} {u}");

            // Chunk the URL list so each LLM call stays within context limits
            const int maxUrlsPerChunk = 15;
            var chunks = new List<List<(string Method, string Url, string ResponseFormat, string ResponsePreview)>>();
            for (int i = 0; i < HarUrls.Count; i += maxUrlsPerChunk)
                chunks.Add(HarUrls.Skip(i).Take(maxUrlsPerChunk).ToList());

            if (chunks.Count == 0)
            {
                throw new InvalidOperationException(
                    "Inga API-URL:er hittades i HAR-filen efter filtrering.\n" +
                    "Tips: Spela in fler åtgärder i webbläsaren (navigera, klicka, etc.).");
            }

            string? bestUrl = null;

            for (int ci = 0; ci < chunks.Count; ci++)
            {
                var chunk = chunks[ci];
                var formatted = FormatHarUrlsReadable(chunk);

                var chunkPrompt = $@"API endpoints captured from a web application:

{formatted}

Task: Which endpoint is MOST LIKELY responsible for this action: ""{_prompt}""

Pick the BEST matching URL. Only respond with url=""NONE"" if absolutely none of the endpoints could be related.
Return the full URL exactly as shown above (including any #op= suffix).
";

                Console.WriteLine($"[EndUrlIdentify] Chunk {ci + 1}/{chunks.Count} ({chunk.Count} URL:er)");

                var result = await _llmService.InvokeWithFunctionAsync(
                    chunkPrompt,
                    "identify_end_url",
                    "Identify the URL responsible for a specific action",
                    parametersSchema);

                var candidate = result.GetProperty("url").GetString()?.Trim() ?? string.Empty;
                Console.WriteLine($"[EndUrlIdentify] LLM svarade: \"{candidate}\"");

                if (!string.IsNullOrEmpty(candidate) &&
                    !candidate.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
                    candidate.Length > 5)
                {
                    bestUrl = candidate;
                    break; // Found a match, no need to check more chunks
                }
            }

            var endUrl = bestUrl ?? string.Empty;

            // Fuzzy match against UrlToResReqDict
            if (!string.IsNullOrEmpty(endUrl) && !UrlToResReqDict.ContainsKey(endUrl))
            {
                var match = UrlToResReqDict.Keys.FirstOrDefault(k =>
                    k.Contains(endUrl, StringComparison.OrdinalIgnoreCase) ||
                    endUrl.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    Console.WriteLine($"[EndUrlIdentify] Fuzzy-matchade: {endUrl} -> {match}");
                    endUrl = match;
                }
            }

            if (string.IsNullOrEmpty(endUrl) || !UrlToResReqDict.ContainsKey(endUrl))
            {
                var availableUrls = string.Join("\n  ",
                    HarUrls.Select(h => $"{h.Method} {h.Url}"));
                throw new InvalidOperationException(
                    $"LLM returnerade en ogiltig URL: \"{endUrl}\"\n" +
                    $"Tillgängliga URLer (efter filtrering):\n  {availableUrls}");
            }

            state.ActionUrl = endUrl;
            return state;
        }

        /// <summary>
        /// Identifies which input variables from the state appear in the current cURL command.
        /// Removes identified variables from the node's dynamic parts and records them as input variables on the node.
        /// </summary>
        public async Task<AgentState> InputVariablesIdentifyingAgentAsync(AgentState state)
        {
            var inProcessNodeId = state.InProcessNode!;
            var node = DagManager.GetNode(inProcessNodeId)!;
            var request = (HttpRequestModel)node.Content!["key"];
            var curl = request.ToCurlCommand();
            var inputVariables = state.InputVariables;

            if (inputVariables == null || inputVariables.Count == 0)
            {
                return state;
            }

            var parametersSchema = ParseJsonSchema(@"{
                ""type"": ""object"",
                ""properties"": {
                    ""identified_variables"": {
                        ""type"": ""array"",
                        ""items"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""variable_name"": { ""type"": ""string"", ""description"": ""The original key of the variable"" },
                                ""variable_value"": { ""type"": ""string"", ""description"": ""The exact version of the variable that is present in the cURL command. This should closely match the value in the provided Input Variables."" }
                            },
                            ""required"": [""variable_name"", ""variable_value""]
                        },
                        ""description"": ""A list of identified variables and their values.""
                    }
                },
                ""required"": [""identified_variables""]
            }");

            var inputVarsJson = JsonSerializer.Serialize(inputVariables);
            var prompt = $@"
cURL: {curl}
Input Variables: {inputVarsJson}

Task:
Identify which input variables (the value in the key-value pair) from the Input Variables provided above are present in the cURL command.

Important:
- If an input variable is found in the cURL, include it in the output.
- Do not include variables that are not provided above.
- The key of the input variable is a description of the variable.
- The value is the value that should closely match the value in the cURL command. No substitutions.
";

            var result = await _llmService.InvokeWithFunctionAsync(
                prompt,
                "identify_input_variables",
                "Identify input variables present in the cURL command.",
                parametersSchema);

            if (result.TryGetProperty("identified_variables", out var identifiedVarsElement)
                && identifiedVarsElement.ValueKind == JsonValueKind.Array
                && identifiedVarsElement.GetArrayLength() > 0)
            {
                // Convert identified variables to a dictionary: variable_name -> variable_value
                var convertedVariables = new Dictionary<string, string>();
                foreach (var item in identifiedVarsElement.EnumerateArray())
                {
                    var varName = item.GetProperty("variable_name").GetString() ?? string.Empty;
                    var varValue = item.GetProperty("variable_value").GetString() ?? string.Empty;
                    convertedVariables[varName] = varValue;
                }

                // Remove the identified variable values from dynamic_parts
                var currentDynamicParts = node.DynamicParts ?? new List<string>();
                var updatedDynamicParts = currentDynamicParts
                    .Where(part => !convertedVariables.Values.Contains(part))
                    .ToList();

                DagManager.UpdateNode(
                    inProcessNodeId,
                    dynamicParts: updatedDynamicParts,
                    inputVariables: convertedVariables);
            }

            return state;
        }

        /// <summary>
        /// Pops a node from ToBeProcessedNodes, identifies dynamic parts in its cURL command
        /// via LLM, and updates the node and state accordingly. Skips .js file URLs.
        /// </summary>
        public async Task<AgentState> DynamicPartIdentifyingAgentAsync(AgentState state)
        {
            // Pop the last node from the to-be-processed list
            var toBeProcessed = state.ToBeProcessedNodes;
            var inProcessNodeId = toBeProcessed[toBeProcessed.Count - 1];
            toBeProcessed.RemoveAt(toBeProcessed.Count - 1);

            var node = DagManager.GetNode(inProcessNodeId)!;
            var request = (HttpRequestModel)node.Content!["key"];
            var curl = request.ToMinifiedCurlCommand();

            // Skip .js files
            if (curl.EndsWith(".js'"))
            {
                DagManager.UpdateNode(inProcessNodeId, dynamicParts: new List<string>());
                state.InProcessNodeDynamicParts = new List<string>();
                state.InProcessNode = inProcessNodeId;
                return state;
            }

            var inputVariables = state.InputVariables;

            var parametersSchema = ParseJsonSchema(@"{
                ""type"": ""object"",
                ""properties"": {
                    ""dynamic_parts"": {
                        ""type"": ""array"",
                        ""items"": { ""type"": ""string"" },
                        ""description"": ""List of dynamic parts identified in the cURL command. Do not include duplicates. Only strictly include the dynamic values (not the keys or any not extra part in front and after the value) of parts that are unique to a user or session and, if incorrect, will cause the request to fail. Do not include the keys, only the values.""
                    }
                },
                ""required"": [""dynamic_parts""]
            }");

            var prompt = $@"
URL: {curl}

Task:

Use your best judgment to identify which parts of the cURL command are dynamic, specific to a user or session, and are checked by the server for validity. These include tokens, IDs, session variables, or any other values that are unique to a user or session and, if incorrect, will cause the request to fail.

Important:
    - IGNORE THE COOKIE HEADER
    - Ignore common headers like user-agent, sec-ch-ua, accept-encoding, referer, etc.
    - Exclude parameters that represent arbitrary user input or general data that can be hardcoded, such as amounts, notes, messages, actions, etc.
    - Only output the variable values and not the keys.
    - Only include dynamic parts that are unique identifiers, tokens, or session variables.
";

            var result = await _llmService.InvokeWithFunctionAsync(
                prompt,
                "identify_dynamic_parts",
                "Given the above cURL command, identify which parts are dynamic and validated by the server for correctness (e.g., IDs, tokens, session variables). Exclude any parameters that represent arbitrary user input or general data that can be hardcoded (e.g., amounts, notes, messages).",
                parametersSchema);

            var dynamicParts = new List<string>();
            if (result.TryGetProperty("dynamic_parts", out var partsElement)
                && partsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in partsElement.EnumerateArray())
                {
                    var partStr = part.GetString();
                    if (partStr != null)
                    {
                        dynamicParts.Add(partStr);
                    }
                }
            }

            DagManager.UpdateNode(inProcessNodeId, dynamicParts: dynamicParts);

            // Detect if any input variables are present in the curl and remove them from dynamic parts
            if (inputVariables != null && inputVariables.Count > 0)
            {
                var presentVariables = inputVariables.Values
                    .Where(variable => curl.Contains(variable, StringComparison.Ordinal))
                    .ToList();

                if (presentVariables.Count > 0)
                {
                    foreach (var variable in presentVariables)
                    {
                        dynamicParts.Remove(variable);
                    }

                    // Store the present variable values as input_variables on the node
                    // Convert to a dictionary
                    // matching the node's InputVariables type.
                    var nodeInputVars = new Dictionary<string, string>();
                    foreach (var kvp in inputVariables)
                    {
                        if (presentVariables.Contains(kvp.Value))
                        {
                            nodeInputVars[kvp.Key] = kvp.Value;
                        }
                    }
                    DagManager.UpdateNode(inProcessNodeId, inputVariables: nodeInputVars);
                }
            }

            state.InProcessNodeDynamicParts = dynamicParts;
            state.InProcessNode = inProcessNodeId;
            return state;
        }

        /// <summary>
        /// Converts the action URL to a master cURL node in the DAG.
        /// Looks up the request/response for the action URL and creates (or reuses) a DAG node.
        /// </summary>
        public Task<AgentState> UrlToCurlAsync(AgentState state)
        {
            var actionUrl = state.ActionUrl;
            var (request, response) = UrlToResReqDict[actionUrl];
            var curl = request.ToCurlCommand();

            string masterNodeId;
            if (CurlToIdDict.TryGetValue(curl, out var existingId))
            {
                masterNodeId = existingId;
            }
            else
            {
                masterNodeId = DagManager.AddNode(
                    nodeType: "master_curl",
                    content: new Dictionary<string, object>
                    {
                        { "key", request },
                        { "value", ReqToResMap[request] }
                    },
                    dynamicParts: new List<string> { "None" },
                    extractedParts: new List<string> { "None" });

                CurlToIdDict[curl] = masterNodeId;
            }

            state.MasterNode = masterNodeId;
            state.ToBeProcessedNodes.Add(masterNodeId);
            GlobalMasterNodeId = masterNodeId;

            return Task.FromResult(state);
        }

        /// <summary>
        /// For each dynamic part in the current node, searches all HAR responses to find
        /// which request produced that value. Handles cookie values, finds the simplest
        /// request when multiple matches exist, and creates dependency edges in the DAG.
        /// </summary>
        public async Task<AgentState> FindCurlFromContentAsync(AgentState state)
        {
            var searchStringList = state.InProcessNodeDynamicParts;
            var searchStringListLeftovers = new List<string>(searchStringList);
            var inProcessNodeId = state.InProcessNode!;
            var newToBeProcessedNodes = new List<string>();

            // Handle cookies
            foreach (var searchString in searchStringList.ToList())
            {
                var cookieKey = FindKeyByStringInValue(CookieDict, searchString);
                if (cookieKey != null)
                {
                    searchStringListLeftovers.Remove(searchString);

                    string cookieNodeId;
                    if (CookieToIdDict.TryGetValue(cookieKey, out var existingCookieNodeId))
                    {
                        cookieNodeId = existingCookieNodeId;
                    }
                    else
                    {
                        cookieNodeId = DagManager.AddNode(
                            nodeType: "cookie",
                            content: new Dictionary<string, object>
                            {
                                { "key", cookieKey },
                                { "value", searchString }
                            },
                            extractedParts: new List<string> { searchString });

                        CookieToIdDict[cookieKey] = cookieNodeId;
                    }

                    DagManager.AddEdge(inProcessNodeId, cookieNodeId);
                }
            }

            // Handle curls
            if (searchStringListLeftovers.Count > 0)
            {
                foreach (var searchString in searchStringListLeftovers.ToList())
                {
                    var requestsWithSearchString = new List<HttpRequestModel>();

                    foreach (var kvp in ReqToResMap)
                    {
                        var request = kvp.Key;
                        var response = kvp.Value;
                        var curlStr = request.ToString();
                        var responseText = response.ContainsKey("text") ? response["text"] : string.Empty;

                        // Check if search string is in the response text but not in the curl command itself
                        if (responseText.Contains(searchString, StringComparison.OrdinalIgnoreCase)
                            && !curlStr.Contains(searchString, StringComparison.OrdinalIgnoreCase))
                        {
                            requestsWithSearchString.Add(request);
                        }
                        else
                        {
                            // Also check URL-decoded version
                            var decoded = Uri.UnescapeDataString(searchString);
                            if (decoded != searchString
                                && curlStr.Contains(decoded, StringComparison.Ordinal)
                                && !curlStr.Contains(decoded, StringComparison.Ordinal))
                            {
                                requestsWithSearchString.Add(request);
                            }
                        }
                    }

                    HttpRequestModel? simplestRequest = null;

                    if (requestsWithSearchString.Count > 1)
                    {
                        try
                        {
                            simplestRequest = await GetSimplestRequest(requestsWithSearchString);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[FindCurl] LLM-fel vid GetSimplestRequest: {ex.Message}");
                            // Fall back to first match instead of crashing
                            simplestRequest = requestsWithSearchString[0];
                        }
                    }
                    else if (requestsWithSearchString.Count == 1)
                    {
                        simplestRequest = requestsWithSearchString[0];
                    }
                    else
                    {
                        Console.WriteLine($"Could not find curl with search string: {searchString} in response");
                        var notFoundNodeId = DagManager.AddNode(
                            nodeType: "not found",
                            content: new Dictionary<string, object>
                            {
                                { "key", searchString }
                            });
                        DagManager.AddEdge(inProcessNodeId, notFoundNodeId);
                        searchStringListLeftovers.Remove(searchString);
                        continue;
                    }

                    // Skip .js files and text/html responses
                    if (simplestRequest!.Url.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                        || (ReqToResMap.TryGetValue(simplestRequest, out var respDict)
                            && respDict.ContainsKey("type")
                            && respDict["type"].Contains("text/html", StringComparison.OrdinalIgnoreCase)))
                    {
                        var currentNode = DagManager.GetNode(inProcessNodeId)!;
                        var currentDynamicParts = currentNode.DynamicParts ?? new List<string>();
                        var updatedDynamicParts = currentDynamicParts
                            .Where(part => part != searchString)
                            .ToList();
                        DagManager.UpdateNode(inProcessNodeId, dynamicParts: updatedDynamicParts);
                        searchStringListLeftovers.Remove(searchString);
                        continue;
                    }

                    var simplestCurl = simplestRequest.ToCurlCommand();

                    string curlNodeId;
                    if (!CurlToIdDict.TryGetValue(simplestCurl, out curlNodeId!))
                    {
                        if (simplestRequest.Url.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        {
                            DagManager.UpdateNode(inProcessNodeId, dynamicParts: new List<string>());
                            continue;
                        }

                        curlNodeId = DagManager.AddNode(
                            nodeType: "curl",
                            content: new Dictionary<string, object>
                            {
                                { "key", simplestRequest },
                                { "value", ReqToResMap[simplestRequest] }
                            },
                            extractedParts: new List<string> { searchString });

                        CurlToIdDict[simplestCurl] = curlNodeId;
                        newToBeProcessedNodes.Add(curlNodeId);
                    }
                    else
                    {
                        // Append new extracted part to existing curl node
                        var existingNode = DagManager.GetNode(curlNodeId)!;
                        var newExtractedParts = existingNode.ExtractedParts != null
                            ? new List<string>(existingNode.ExtractedParts)
                            : new List<string>();
                        if (!newExtractedParts.Contains(searchString))
                        {
                            newExtractedParts.Add(searchString);
                        }
                        DagManager.UpdateNode(curlNodeId, extractedParts: newExtractedParts);
                    }

                    DagManager.AddEdge(inProcessNodeId, curlNodeId);
                }
            }

            state.ToBeProcessedNodes.AddRange(newToBeProcessedNodes);
            state.InProcessNodeDynamicParts = new List<string>();
            return state;
        }

        /// <summary>
        /// Uses LLM to find which request in the list has the fewest dependencies and variables.
        /// </summary>
        /// <param name="requests">A list of HttpRequestModel candidates.</param>
        /// <returns>The simplest request from the list.</returns>
        public async Task<HttpRequestModel> GetSimplestRequest(List<HttpRequestModel> requests)
        {
            var parametersSchema = ParseJsonSchema(@"{
                ""type"": ""object"",
                ""properties"": {
                    ""index"": {
                        ""type"": ""integer"",
                        ""description"": ""The index of the simplest cURL command in the list""
                    }
                },
                ""required"": [""index""]
            }");

            var serializableList = requests.Select(req => req.ToString()).ToList();
            var listJson = JsonSerializer.Serialize(serializableList);

            var prompt = $@"
{listJson}
Task:
Given the above list of cURL commands, find the index of the curl that has the least number of dependencies and variables.
The index should be 0-based (i.e., the first item has index 0).
";

            var result = await _llmService.InvokeWithFunctionAsync(
                prompt,
                "get_simplest_curl_index",
                "Find the index of the simplest cURL command from a list",
                parametersSchema);

            var index = result.GetProperty("index").GetInt32();

            // Clamp index to valid range
            if (index < 0) index = 0;
            if (index >= requests.Count) index = requests.Count - 1;

            return requests[index];
        }

        /// <summary>
        /// Finds the cookie key whose CookieData.Value contains the given search string.
        /// Returns null if no match is found.
        /// </summary>
        /// <param name="dict">The cookie dictionary to search.</param>
        /// <param name="searchString">The string to look for within cookie values.</param>
        /// <returns>The cookie key, or null if not found.</returns>
        public static string? FindKeyByStringInValue(Dictionary<string, CookieData> dict, string searchString)
        {
            foreach (var kvp in dict)
            {
                if (kvp.Value.Value != null && kvp.Value.Value.Contains(searchString, StringComparison.Ordinal))
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        /// <summary>
        /// Parses a JSON string into a JsonElement for use as a function parameter schema.
        /// </summary>
        private static JsonElement ParseJsonSchema(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// Escapes a string for safe embedding inside a JSON string literal.
        /// </summary>
        private static string EscapeJsonString(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        /// <summary>
        /// Formats the HAR URLs list into a readable string for LLM prompts.
        /// </summary>
        private string FormatHarUrls()
        {
            return FormatHarUrlsFromList(HarUrls);
        }

        private static string FormatHarUrlsFromList(
            List<(string Method, string Url, string ResponseFormat, string ResponsePreview)> urls)
        {
            var entries = new List<string>();
            foreach (var (method, url, responseFormat, responsePreview) in urls)
            {
                entries.Add($"('{method}', '{url}', '{responseFormat}', '{responsePreview}')");
            }
            return "[" + string.Join(", ", entries) + "]";
        }

        /// <summary>
        /// Formats URLs in a clear numbered list that's easier for local LLMs to parse.
        /// </summary>
        private static string FormatHarUrlsReadable(
            List<(string Method, string Url, string ResponseFormat, string ResponsePreview)> urls)
        {
            var lines = new List<string>();
            for (int i = 0; i < urls.Count; i++)
            {
                var (method, url, responseFormat, responsePreview) = urls[i];
                lines.Add($"{i + 1}. {method} {url}");
                lines.Add($"   Response: {responseFormat} | Preview: {responsePreview}");
            }
            return string.Join("\n", lines);
        }
    }
}
