using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ApiForge.Models;

namespace ApiForge.Utilities
{
    /// <summary>
    /// Represents a result from searching for a value within a JSON structure.
    /// </summary>
    public class JsonPathResult
    {
        /// <summary>
        /// The path of keys/indices leading to the matched value.
        /// Each element is a string (for object keys) or a stringified integer (for array indices).
        /// </summary>
        public List<string> KeyPath { get; set; } = new List<string>();

        /// <summary>
        /// The matched value as a string.
        /// </summary>
        public string Value { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{{key_path: [{string.Join(", ", KeyPath)}], value: {Value}}}";
        }
    }

    /// <summary>
    /// Provides methods for printing/visualizing a DAG and generating code from it.
    /// Provides methods for printing/visualizing a DAG and generating code from it.
    /// </summary>
    public static class DagPrinter
    {
        // ───────────────────────────────────────────────────────────────
        // 1. PrintDag
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Recursively prints the DAG structure with visual tree-style connectors.
        /// </summary>
        /// <param name="dag">The DAG manager instance.</param>
        /// <param name="currentNodeId">The node to start printing from.</param>
        /// <param name="prefix">Indentation prefix carried through recursion.</param>
        /// <param name="isLast">Whether the current node is the last sibling.</param>
        /// <param name="visited">Set of already-visited node IDs (to avoid infinite loops).</param>
        /// <param name="depth">Current recursion depth.</param>
        /// <param name="maxDepth">Optional maximum depth to print.</param>
        public static void PrintDag(
            DagManager dag,
            string currentNodeId,
            string prefix = "",
            bool isLast = true,
            HashSet<string>? visited = null,
            int depth = 0,
            int? maxDepth = null)
        {
            if (visited == null)
                visited = new HashSet<string>();

            string connector = isLast ? "\u2514\u2500\u2500 " : "\u251c\u2500\u2500 ";
            string newPrefix = prefix + (isLast ? "    " : "\u2502   ");

            DagNode? node = dag.GetNode(currentNodeId);
            if (node == null)
            {
                Console.WriteLine($"{prefix}{connector}[unknown node_id: {currentNodeId}]");
                return;
            }

            var dynamicParts = node.DynamicParts ?? new List<string>();
            var extractedParts = node.ExtractedParts ?? new List<string>();
            var inputVariables = node.InputVariables;
            string nodeType = node.NodeType;

            string contentKey = string.Empty;
            if (node.Content != null && node.Content.TryGetValue("key", out object? keyVal) && keyVal != null)
            {
                contentKey = keyVal.ToString() ?? string.Empty;
            }

            string nodeLabel = $"[{nodeType}] [node_id: {currentNodeId}]";
            if (inputVariables != null && inputVariables.Count > 0)
            {
                string ivStr = FormatDictionary(inputVariables);
                nodeLabel += $"\n{newPrefix}    [input_variables: {ivStr}]";
            }
            nodeLabel += $"\n{newPrefix}    [dynamic_parts: {FormatList(dynamicParts)}]";
            nodeLabel += $"\n{newPrefix}    [extracted_parts: {FormatList(extractedParts)}]";
            nodeLabel += $"\n{newPrefix}    [{contentKey}]";

            Console.WriteLine($"{prefix}{connector}{nodeLabel}");

            visited.Add(currentNodeId);

            if (maxDepth.HasValue && depth >= maxDepth.Value)
                return;

            List<string> children = dag.GetSuccessors(currentNodeId);
            int childCount = children.Count;

            for (int i = 0; i < childCount; i++)
            {
                string childId = children[i];
                bool isLastChild = i == childCount - 1;

                if (visited.Contains(childId))
                {
                    string loopConnector = isLastChild ? "\u2514\u2500\u2500 " : "\u251c\u2500\u2500 ";
                    Console.WriteLine($"{newPrefix}{loopConnector}(Already visited) [node_id: {childId}]");
                }
                else
                {
                    PrintDag(
                        dag,
                        childId,
                        prefix: newPrefix,
                        isLast: isLastChild,
                        visited: visited,
                        depth: depth + 1,
                        maxDepth: maxDepth);
                }
            }
        }

        // ───────────────────────────────────────────────────────────────
        // 2. PrintDagInReverse
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Prints the DAG starting from source nodes (no incoming edges), processing
        /// children before parents. Optionally generates code for each node.
        /// </summary>
        /// <param name="dag">The DAG manager instance.</param>
        /// <param name="maxDepth">Optional maximum recursion depth.</param>
        /// <param name="toGenerateCode">If true, generates code for each node and writes output.</param>
        /// <param name="llm">The LLM service instance, required when <paramref name="toGenerateCode"/> is true.</param>
        public static async Task PrintDagInReverse(
            DagManager dag,
            int? maxDepth = null,
            bool toGenerateCode = false,
            LlmService? llm = null)
        {
            if (toGenerateCode)
            {
                Console.WriteLine("--------------Generating code------------");
            }

            var generatedCode = new StringBuilder();
            var dynamicPartsList = new List<string>();

            string GetNodeLabel(string nodeId)
            {
                DagNode? node = dag.GetNode(nodeId);
                if (node == null)
                    return $"[unknown] [node_id: {nodeId}]";

                var dynamicParts = node.DynamicParts ?? new List<string>();
                var extractedParts = node.ExtractedParts ?? new List<string>();
                var inputVariables = node.InputVariables;
                string nodeType = node.NodeType;

                string contentKey = string.Empty;
                if (node.Content != null && node.Content.TryGetValue("key", out object? keyVal) && keyVal != null)
                {
                    contentKey = keyVal.ToString() ?? string.Empty;
                }

                if (dynamicParts.Count > 0)
                {
                    dynamicPartsList.AddRange(dynamicParts);
                }

                string label = $"[{nodeType}] ";
                label += $"[node_id: {nodeId}]";
                label += $" [dynamic_parts: {FormatList(dynamicParts)}]";
                label += $" [extracted_parts: {FormatList(extractedParts)}]";
                if (inputVariables != null && inputVariables.Count > 0)
                {
                    label += $" [input_variables: {FormatDictionary(inputVariables)}]";
                }
                label += $" [{contentKey}]";
                return label;
            }

            async Task PrintDagRecursive(
                string currentNodeId,
                string prefix,
                bool isLast,
                HashSet<string> visited,
                HashSet<string> fullyProcessed,
                int depth)
            {
                if (fullyProcessed.Contains(currentNodeId))
                    return;

                if (visited.Contains(currentNodeId))
                    return;

                visited.Add(currentNodeId);

                if (maxDepth.HasValue && depth >= maxDepth.Value)
                {
                    visited.Remove(currentNodeId);
                    return;
                }

                // Get child nodes (successors)
                List<string> children = dag.GetSuccessors(currentNodeId);
                int childCount = children.Count;

                // Recursively process child nodes first
                for (int i = 0; i < childCount; i++)
                {
                    string childId = children[i];
                    bool isLastChild = i == childCount - 1;
                    string newPrefix = prefix + (isLast ? "    " : "\u2502   ");
                    await PrintDagRecursive(
                        childId,
                        prefix: newPrefix,
                        isLast: isLastChild,
                        visited: visited,
                        fullyProcessed: fullyProcessed,
                        depth: depth + 1);
                }

                // After all children have been processed, print the current node
                string connector = isLast ? "\u2514\u2500\u2500 " : "\u251c\u2500\u2500 ";
                Console.WriteLine($"{prefix}{connector}{GetNodeLabel(currentNodeId)}");

                if (toGenerateCode && llm != null)
                {
                    string code = await GenerateCode(currentNodeId, dag, llm);
                    generatedCode.AppendLine(code);
                    generatedCode.AppendLine();
                }

                fullyProcessed.Add(currentNodeId);
                visited.Remove(currentNodeId);
            }

            // Start from source nodes (nodes with no incoming edges)
            List<DagNode> sourceNodes = dag.GetSourceNodes();
            var fullyProcessedSet = new HashSet<string>();

            for (int idx = 0; idx < sourceNodes.Count; idx++)
            {
                bool isLastSource = idx == sourceNodes.Count - 1;
                await PrintDagRecursive(
                    sourceNodes[idx].Id,
                    prefix: "",
                    isLast: isLastSource,
                    visited: new HashSet<string>(),
                    fullyProcessed: fullyProcessedSet,
                    depth: 0);
            }

            if (toGenerateCode && llm != null)
            {
                var obfuscationMap = GenerateObfuscationMap(dynamicPartsList);
                string finalCode = SwapStringUsingObfuscationMap(generatedCode.ToString(), obfuscationMap);
                await File.WriteAllTextAsync("generated_code.txt", finalCode);

                await AggregateFunctions("generated_code.txt", "generated_code.py", llm);
                Console.WriteLine("--------------Generated integration code in generated_code.py!!------------");
            }
        }

        // ───────────────────────────────────────────────────────────────
        // 3. GenerateCode
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates code for a given node in the DAG based on its attributes,
        /// using the LLM to produce the function body.
        /// </summary>
        /// <param name="nodeId">The ID of the node to generate code for.</param>
        /// <param name="dag">The DAG manager instance.</param>
        /// <param name="llm">The LLM service instance.</param>
        /// <returns>The generated code as a string.</returns>
        public static async Task<string> GenerateCode(string nodeId, DagManager dag, LlmService llm)
        {
            DagNode? node = dag.GetNode(nodeId);
            if (node == null)
                return $"# Node '{nodeId}' not found in the DAG.";

            // Handle cookie nodes directly
            if (node.NodeType == "cookie")
            {
                string cookieValue = string.Empty;
                string cookieKey = string.Empty;
                if (node.Content != null)
                {
                    if (node.Content.TryGetValue("value", out object? valObj) && valObj != null)
                        cookieValue = ConvertContentValue(valObj);
                    if (node.Content.TryGetValue("key", out object? keyObj) && keyObj != null)
                        cookieKey = ConvertContentValue(keyObj);
                }
                return $"{cookieValue} = cookie_dict['{cookieKey}']";
            }

            // Extract content attributes
            string curl = string.Empty;
            string responseType = string.Empty;
            string responseText = string.Empty;

            if (node.Content != null)
            {
                if (node.Content.TryGetValue("key", out object? keyObj) && keyObj != null)
                    curl = ConvertContentValue(keyObj);

                if (node.Content.TryGetValue("value", out object? valObj) && valObj != null)
                {
                    // The value is expected to be a dictionary-like object with "type" and "text"
                    if (valObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                    {
                        if (jsonEl.TryGetProperty("type", out JsonElement typeProp))
                            responseType = typeProp.GetString() ?? string.Empty;
                        if (jsonEl.TryGetProperty("text", out JsonElement textProp))
                            responseText = textProp.GetString() ?? string.Empty;
                    }
                    else if (valObj is Dictionary<string, object> valDict)
                    {
                        if (valDict.TryGetValue("type", out object? typeVal))
                            responseType = typeVal?.ToString() ?? string.Empty;
                        if (valDict.TryGetValue("text", out object? textVal))
                            responseText = textVal?.ToString() ?? string.Empty;
                    }
                }
            }

            var dynamicParts = node.DynamicParts ?? new List<string>();
            var extractedParts = node.ExtractedParts ?? new List<string>();
            var inputVariables = node.InputVariables;

            // Build parse response prompt based on response type
            string parseResponsePrompt = string.Empty;

            // Binary / downloadable file types
            var binaryTypes = new HashSet<string>
            {
                "application/octet-stream", "application/pdf", "application/zip",
                "image/jpeg", "image/png"
            };

            if (binaryTypes.Contains(responseType))
            {
                parseResponsePrompt = $@"
            The response is a downloadable file of type {responseType}.
            Include code to save the response content to a file with an appropriate extension.
        ";
            }

            // JSON response
            if (responseType.Contains("application/json"))
            {
                var keyPaths = new List<List<JsonPathResult>>();
                foreach (string extractedPart in extractedParts)
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(responseText);
                        var paths = FindJsonPath(doc.RootElement, extractedPart);
                        keyPaths.Add(paths);
                    }
                    catch (JsonException)
                    {
                        keyPaths.Add(new List<JsonPathResult>());
                    }
                }

                string keyPathsStr = FormatKeyPaths(keyPaths);
                parseResponsePrompt = $@"
            Response:
            {responseText}

            Parse out the following variables from the response using JSON keys:
            {keyPathsStr}

            Through your judgement from analyzing the response, if polling is required to retrieve the variables above from the response. If so, implement polling else dont.
        ";
            }

            // HTML or JavaScript response
            if (responseType.Contains("text/html") || responseType.Contains("application/javascript"))
            {
                if (responseText.Length > 100000)
                {
                    var contextSnippets = new List<string>();
                    foreach (string part in extractedParts)
                    {
                        int index = responseText.IndexOf(part, StringComparison.Ordinal);
                        if (index != -1)
                        {
                            int start = Math.Max(0, index - 50);
                            int end = Math.Min(responseText.Length, index + part.Length + 50);
                            string snippet = responseText.Substring(start, end - start);
                            contextSnippets.Add($"{part}: {snippet}");
                        }
                    }

                    parseResponsePrompt = $@"
                The HTML response is too long to process entirely.
                Here are the relevant sections for each variable to be extracted:

                {string.Join("\n", contextSnippets)}

            ";
                }
                else
                {
                    parseResponsePrompt = $@"
                Response:
                {responseText}
            ";
                }

                string extractedPartsStr = FormatList(extractedParts);
                parseResponsePrompt += $@"
            Parse out the variables following variables locations from the response using regex using locational context:

            {extractedPartsStr}
            Do not include the variable in the regex filter as the variable will change. And do not be too specific with the regex.

        ";
            }

            // Dynamic parts prompt
            string dynamicPartsPrompt = string.Empty;
            if (dynamicParts.Count > 0)
            {
                string dpStr = FormatList(dynamicParts);
                dynamicPartsPrompt = $@"
    Instead of hard coding, pass the following variables into the function as parameters in a dict. The dict should have keys thats the same as the value itself
    {dpStr}

    Keep everything else in the header hardcoded.
    ";
            }

            string parametersDescription = dynamicParts.Count > 0
                ? "1. a dict of all the parameters and 2. Just the cookie string"
                : "only the cookie string";

            string prompt = $@"
    Task:
    Write a Python function with a descriptive name that makes a request like the cURL below:
    {curl}


    Assume cookies are in a variable as parameter called ""cookie_string"".

    The parameters should be {parametersDescription}.

    {dynamicPartsPrompt}

    {parseResponsePrompt}

    Return a dictionary with the keys as the original parsed values content (needs to be hardcoded) and the values as the parsed values.

    Do not include pseudo-headers or any headers that start with a colon in the request.

    IMPORTANT! Do not include any backticks or markdown syntax AT ALL

    ";

            // Invoke the LLM using the alternate model with fallback
            string code;
            try
            {
                code = await llm.InvokeWithAlternateModelAsync(prompt);
            }
            catch (Exception)
            {
                Console.WriteLine("Switching to default model");
                llm.RevertToDefaultModel();
                code = await llm.InvokeWithAlternateModelAsync(prompt);
            }

            code = code.Trim();

            // Strip markdown fences that the LLM may include despite instructions
            if (code.StartsWith("```python"))
                code = code.Substring("```python".Length);
            if (code.StartsWith("```"))
                code = code.Substring("```".Length);
            if (code.EndsWith("```"))
                code = code.Substring(0, code.Length - "```".Length);

            return code.Trim();
        }

        // ───────────────────────────────────────────────────────────────
        // 4. AggregateFunctions
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads generated functions from a text file, sends them to the LLM to aggregate
        /// into directly runnable code, and writes the result to the output path.
        /// </summary>
        /// <param name="txtPath">Path to the text file containing generated functions.</param>
        /// <param name="outputPath">Path to write the aggregated, runnable code.</param>
        /// <param name="llm">The LLM service instance.</param>
        public static async Task AggregateFunctions(string txtPath, string outputPath, LlmService llm)
        {
            string content = await File.ReadAllTextAsync(txtPath);

            string prompt = $@"
    The following text contains multiple Python functions:

    {content}

    Please generate Python code that does the following:
    1. Fix up the functions if needed in the order they appear in the text.
    2. Leave everything that is hardcoded as is.
    3. Call each function in the order they appear in the text.
    4. The cookies will be hard coded in the file in a string format of key=value;key=value. You will need to convert them to a dict to retrieve values from them.
    5. Pass the return value of each function as an argument to the next function, if applicable.
    6. Ensure that the last function in the text is called last.
    7. Output the entire directly runnable code



    Only provide the Python code, without any explanations or markdown formatting.
    DO NOT include any backticks or markdown syntax AT ALL
    ";

            string generatedCode;
            try
            {
                generatedCode = await llm.InvokeWithAlternateModelAsync(prompt);
            }
            catch (Exception)
            {
                Console.WriteLine("Switching to default model");
                llm.RevertToDefaultModel();
                generatedCode = await llm.InvokeWithAlternateModelAsync(prompt);
            }

            generatedCode = generatedCode.Trim();

            await File.WriteAllTextAsync(outputPath, generatedCode);
            Console.WriteLine($"Aggregated function calls have been saved to '{outputPath}'");
        }

        // ───────────────────────────────────────────────────────────────
        // 5. FindJsonPath
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds the path(s) to a given target value within a JSON structure.
        /// </summary>
        /// <param name="jsonObj">The JSON element to search.</param>
        /// <param name="targetValue">The string value to find.</param>
        /// <param name="currentPath">The current path being explored (used for recursion).</param>
        /// <returns>A list of <see cref="JsonPathResult"/> for each occurrence of the target value.</returns>
        public static List<JsonPathResult> FindJsonPath(
            JsonElement jsonObj,
            string targetValue,
            List<string>? currentPath = null)
        {
            if (currentPath == null)
                currentPath = new List<string>();

            var results = new List<JsonPathResult>();

            if (jsonObj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in jsonObj.EnumerateObject())
                {
                    var newPath = new List<string>(currentPath) { property.Name };

                    if (property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() == targetValue)
                    {
                        results.Add(new JsonPathResult
                        {
                            KeyPath = newPath,
                            Value = targetValue
                        });
                    }

                    if (property.Value.ValueKind == JsonValueKind.Object ||
                        property.Value.ValueKind == JsonValueKind.Array)
                    {
                        results.AddRange(FindJsonPath(property.Value, targetValue, newPath));
                    }
                }
            }
            else if (jsonObj.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in jsonObj.EnumerateArray())
                {
                    var newPath = new List<string>(currentPath) { index.ToString() };

                    if (item.ValueKind == JsonValueKind.String &&
                        item.GetString() == targetValue)
                    {
                        results.Add(new JsonPathResult
                        {
                            KeyPath = newPath,
                            Value = targetValue
                        });
                    }

                    if (item.ValueKind == JsonValueKind.Object ||
                        item.ValueKind == JsonValueKind.Array)
                    {
                        results.AddRange(FindJsonPath(item, targetValue, newPath));
                    }

                    index++;
                }
            }

            return results;
        }

        // ───────────────────────────────────────────────────────────────
        // 6. Helper Methods
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates an obfuscation map that maps each dynamic part string to a safe
        /// variable-name-style replacement (e.g., "var_123456").
        /// </summary>
        /// <param name="dynamicParts">The list of dynamic part strings to obfuscate.</param>
        /// <returns>A dictionary mapping original strings to their obfuscated replacements.</returns>
        public static Dictionary<string, string> GenerateObfuscationMap(List<string> dynamicParts)
        {
            var obfuscationMap = new Dictionary<string, string>();
            foreach (string part in dynamicParts)
            {
                // Use a hash-based key, replacing invalid identifier characters
                string safeKey = $"var_{part.GetHashCode()}"
                    .Replace('-', '_')
                    .Replace('.', '_');
                obfuscationMap[part] = safeKey;
            }
            return obfuscationMap;
        }

        /// <summary>
        /// Replaces all occurrences of obfuscation map keys in the input string with their
        /// corresponding obfuscated values.
        /// </summary>
        /// <param name="input">The string to perform replacements on.</param>
        /// <param name="obfuscationMap">The mapping of original strings to their obfuscated replacements.</param>
        /// <returns>The modified string with all replacements applied.</returns>
        public static string SwapStringUsingObfuscationMap(string input, Dictionary<string, string> obfuscationMap)
        {
            foreach (var kvp in obfuscationMap)
            {
                input = input.Replace(kvp.Key, kvp.Value);
            }
            return input;
        }

        // ───────────────────────────────────────────────────────────────
        // Private formatting helpers
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Formats a list of strings in bracket notation for display.
        /// </summary>
        private static string FormatList(List<string> items)
        {
            if (items.Count == 0)
                return "[]";

            var quoted = items.Select(s => $"'{s}'");
            return $"[{string.Join(", ", quoted)}]";
        }

        /// <summary>
        /// Formats a dictionary in brace notation for display.
        /// </summary>
        private static string FormatDictionary(Dictionary<string, string> dict)
        {
            if (dict.Count == 0)
                return "{}";

            var entries = dict.Select(kv => $"'{kv.Key}': '{kv.Value}'");
            return $"{{{string.Join(", ", entries)}}}";
        }

        /// <summary>
        /// Formats a list of lists of <see cref="JsonPathResult"/> for display in prompts.
        /// </summary>
        private static string FormatKeyPaths(List<List<JsonPathResult>> keyPaths)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < keyPaths.Count; i++)
            {
                sb.Append('[');
                sb.Append(string.Join(", ", keyPaths[i].Select(r => r.ToString())));
                sb.Append(']');
                if (i < keyPaths.Count - 1)
                    sb.Append(", ");
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// Converts a content value (which may be a <see cref="JsonElement"/> or plain object)
        /// to its string representation.
        /// </summary>
        private static string ConvertContentValue(object value)
        {
            if (value is JsonElement jsonEl)
            {
                return jsonEl.ValueKind == JsonValueKind.String
                    ? jsonEl.GetString() ?? string.Empty
                    : jsonEl.GetRawText();
            }
            return value.ToString() ?? string.Empty;
        }
    }
}
