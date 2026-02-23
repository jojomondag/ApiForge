using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiForge.Models;

namespace ApiForge.Utilities
{
    /// <summary>
    /// Cookie data parsed from a cookies.json file.
    /// </summary>
    public class CookieData
    {
        public string? Value { get; set; }
        public string? Domain { get; set; }
        public string? Path { get; set; }
        public object? Expires { get; set; }
        public bool? HttpOnly { get; set; }
        public bool? Secure { get; set; }
        public string? SameSite { get; set; }
    }

    /// <summary>
    /// Utility class for processing HAR (HTTP Archive) files.
    /// Provides utilities for parsing and filtering HAR (HTTP Archive) files.
    /// </summary>
    public static class HarProcessing
    {
        /// <summary>
        /// Keywords used to exclude entire URLs/entries from HAR extraction.
        /// </summary>
        private static readonly string[] ExcludedKeywords =
        {
            "google",
            "gstatic",
            "googleapis",
            "googleusercontent",
            "taboola",
            "datadog",
            "sentry",
            "facebook",
            "fbcdn",
            "doubleclick",
            "googlesyndication",
            "googletagmanager",
            "cloudflare",
            "recaptcha"
        };

        /// <summary>
        /// Keywords used to filter out headers whose names contain any of these (case-insensitive).
        /// </summary>
        private static readonly string[] ExcludedHeaderKeywords =
        {
            "cookie",
            "sec-",
            "accept",
            "user-agent",
            "referer",
            "relic",
            "sentry",
            "datadog",
            "amplitude",
            "mixpanel",
            "segment",
            "heap",
            "hotjar",
            "fullstory",
            "pendo",
            "optimizely",
            "adobe",
            "analytics",
            "tracking",
            "telemetry",
            "clarity",
            "matomo",
            "plausible"
        };

        /// <summary>
        /// File extensions to exclude when extracting URLs from a HAR file.
        /// </summary>
        private static readonly HashSet<string> ExcludedExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico",
            ".css", ".js", ".mjs",
            ".woff", ".woff2", ".ttf", ".otf", ".eot",
            ".mp3", ".mp4", ".wav", ".avi", ".mov", ".flv", ".wmv", ".webm",
            ".rar", ".7z", ".tar", ".gz", ".exe", ".dmg",
            ".map"
        };

        /// <summary>
        /// Converts a HAR request into an <see cref="HttpRequestModel"/>.
        /// Filters out headers containing excluded keywords and optionally parses
        /// the body as JSON when the content-type is application/json.
        /// </summary>
        /// <param name="harRequest">The HAR request entry.</param>
        /// <returns>A formatted <see cref="HttpRequestModel"/>.</returns>
        public static HttpRequestModel FormatRequest(HarRequest harRequest)
        {
            string method = harRequest.Method ?? "GET";
            string url = harRequest.Url ?? "";

            // Build headers dictionary, excluding headers whose name contains any excluded keyword
            var headers = new Dictionary<string, string>();
            if (harRequest.Headers != null)
            {
                foreach (var header in harRequest.Headers)
                {
                    string headerName = header.Name ?? "";
                    bool excluded = ExcludedHeaderKeywords.Any(keyword =>
                        headerName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                    if (!excluded)
                    {
                        headers[headerName] = header.Value ?? "";
                    }
                }
            }

            // Build query params dictionary
            Dictionary<string, string>? queryParams = null;
            if (harRequest.QueryString != null && harRequest.QueryString.Count > 0)
            {
                queryParams = new Dictionary<string, string>();
                foreach (var param in harRequest.QueryString)
                {
                    queryParams[param.Name ?? ""] = param.Value ?? "";
                }

                // Strip query string from URL since params are stored separately
                var qIndex = url.IndexOf('?');
                if (qIndex >= 0)
                {
                    url = url[..qIndex];
                }
            }

            // Extract body from postData
            object? body = null;
            if (harRequest.PostData != null && !string.IsNullOrEmpty(harRequest.PostData.Text))
            {
                body = harRequest.PostData.Text;

                // Try to parse body as JSON if Content-Type is application/json
                var headersLower = headers
                    .ToDictionary(kvp => kvp.Key.ToLowerInvariant(), kvp => kvp.Value);

                if (headersLower.TryGetValue("content-type", out string? contentType) &&
                    contentType != null &&
                    contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse((string)body);
                        // Convert to a dictionary or keep as JsonElement
                        body = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        // Keep body as-is if not valid JSON
                    }
                }
            }

            return new HttpRequestModel
            {
                Method = method,
                Url = url,
                Headers = headers,
                QueryParams = queryParams,
                Body = body
            };
        }

        /// <summary>
        /// Extracts the content text and MIME type from a HAR response.
        /// </summary>
        /// <param name="harResponse">The HAR response entry.</param>
        /// <returns>A dictionary with "text" and "type" keys.</returns>
        public static Dictionary<string, string> FormatResponse(HarResponse harResponse)
        {
            string text = "";
            string mimeType = "";

            if (harResponse.Content != null)
            {
                text = harResponse.Content.Text ?? "";
                mimeType = harResponse.Content.MimeType ?? "";
            }

            return new Dictionary<string, string>
            {
                { "text", text },
                { "type", mimeType }
            };
        }

        /// <summary>
        /// Parses a HAR file and returns a dictionary mapping each formatted request
        /// to its corresponding formatted response.
        /// </summary>
        /// <param name="harFilePath">Path to the HAR file.</param>
        /// <returns>Dictionary of request to response mappings.</returns>
        public static Dictionary<HttpRequestModel, Dictionary<string, string>> ParseHarFile(string harFilePath)
        {
            var reqResDict = new Dictionary<HttpRequestModel, Dictionary<string, string>>();

            string jsonContent = File.ReadAllText(harFilePath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var harFile = JsonSerializer.Deserialize<HarFile>(jsonContent, options);

            if (harFile?.Log?.Entries == null)
            {
                return reqResDict;
            }

            foreach (var entry in harFile.Log.Entries)
            {
                if (entry.Request == null || entry.Response == null)
                {
                    continue;
                }

                var formattedRequest = FormatRequest(entry.Request);
                var responseDict = FormatResponse(entry.Response);
                reqResDict[formattedRequest] = responseDict;
            }

            return reqResDict;
        }

        /// <summary>
        /// Builds a dictionary mapping URLs to their request/response pairs.
        /// If multiple requests share the same URL, later entries overwrite earlier ones.
        /// </summary>
        /// <param name="reqResDict">The request-to-response dictionary from <see cref="ParseHarFile"/>.</param>
        /// <returns>Dictionary mapping URL strings to (Request, Response) tuples.</returns>
        public static Dictionary<string, (HttpRequestModel Request, Dictionary<string, string> Response)>
            BuildUrlToReqResMap(Dictionary<HttpRequestModel, Dictionary<string, string>> reqResDict)
        {
            var urlToReqRes = new Dictionary<string, (HttpRequestModel Request, Dictionary<string, string> Response)>();

            foreach (var kvp in reqResDict)
            {
                var request = kvp.Key;
                string baseUrl = request.Url;

                // For GraphQL endpoints, include operation name in the key
                string mapKey = baseUrl;
                string opName = ExtractOperationNameFromBody(request.Body);
                if (!string.IsNullOrEmpty(opName) &&
                    baseUrl.Contains("/graphql", StringComparison.OrdinalIgnoreCase))
                {
                    mapKey = $"{baseUrl}#op={opName}";
                }

                urlToReqRes[mapKey] = (request, kvp.Value);

                // Also store with query params so LLM-returned URLs (which include query strings) match
                if (request.QueryParams != null && request.QueryParams.Count > 0)
                {
                    var qs = string.Join("&", request.QueryParams.Select(p => $"{p.Key}={p.Value}"));
                    urlToReqRes[$"{mapKey}?{qs}"] = (request, kvp.Value);
                }
            }

            return urlToReqRes;
        }

        /// <summary>
        /// Extracts the GraphQL operation name from an HttpRequestModel body (JsonElement or string).
        /// Tries: 1) explicit "operationName", 2) root field from the "query" string.
        /// </summary>
        private static string ExtractOperationNameFromBody(object? body)
        {
            try
            {
                if (body is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    if (je.TryGetProperty("operationName", out var opProp))
                    {
                        var opName = opProp.GetString();
                        if (!string.IsNullOrEmpty(opName))
                            return opName;
                    }
                    if (je.TryGetProperty("query", out var qProp))
                    {
                        var q = qProp.GetString() ?? "";
                        return ExtractGraphqlRootField(q);
                    }
                }
                if (body is string bodyStr)
                {
                    using var doc = JsonDocument.Parse(bodyStr);
                    if (doc.RootElement.TryGetProperty("operationName", out var op))
                    {
                        var opName = op.GetString();
                        if (!string.IsNullOrEmpty(opName))
                            return opName;
                    }
                    if (doc.RootElement.TryGetProperty("query", out var q2))
                        return ExtractGraphqlRootField(q2.GetString() ?? "");
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Extracts URL details from a HAR file, excluding static assets, media files,
        /// and URLs containing excluded keywords.
        /// </summary>
        /// <param name="harFilePath">Path to the HAR file.</param>
        /// <returns>
        /// A list of tuples containing (Method, Url, ResponseFormat, ResponsePreview)
        /// for each qualifying entry.
        /// </returns>
        public static List<(string Method, string Url, string ResponseFormat, string ResponsePreview)>
            GetHarUrls(string harFilePath)
        {
            var urlsWithDetails = new List<(string Method, string Url, string ResponseFormat, string ResponsePreview)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string jsonContent = File.ReadAllText(harFilePath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var harFile = JsonSerializer.Deserialize<HarFile>(jsonContent, options);

            if (harFile?.Log?.Entries == null)
            {
                return urlsWithDetails;
            }

            foreach (var entry in harFile.Log.Entries)
            {
                var request = entry.Request;
                var response = entry.Response;
                if (request == null || response == null)
                {
                    continue;
                }

                string url = request.Url ?? "";
                string method = request.Method ?? "GET";
                string responseFormat = response.Content?.MimeType ?? "";
                string responseText = response.Content?.Text ?? "";
                string responsePreview = responseText.Length > 30
                    ? responseText.Substring(0, 30)
                    : responseText;

                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                // Skip data: URLs (base64 images etc.)
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Parse the URL to get the path and check the file extension
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
                {
                    continue;
                }

                string path = parsedUri.AbsolutePath.ToLowerInvariant();
                string extension = Path.GetExtension(path);

                // Exclude URLs with excluded extensions or containing excluded keywords in the hostname
                bool hasExcludedExtension = !string.IsNullOrEmpty(extension) &&
                                            ExcludedExtensions.Contains(extension);
                string hostname = parsedUri.Host.ToLowerInvariant();
                bool hasExcludedKeyword = ExcludedKeywords.Any(keyword =>
                    hostname.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                if (hasExcludedExtension || hasExcludedKeyword)
                {
                    continue;
                }

                // For GraphQL endpoints, extract operationName to distinguish calls
                string displayUrl = url;
                string graphqlOpName = ExtractGraphqlOperationName(request.PostData);
                if (!string.IsNullOrEmpty(graphqlOpName))
                {
                    displayUrl = $"{url}#op={graphqlOpName}";
                    responsePreview = $"[{graphqlOpName}] {responsePreview}";
                }

                // Deduplicate by method + display URL
                string dedupeKey = $"{method} {displayUrl}";
                if (seen.Contains(dedupeKey))
                {
                    continue;
                }
                seen.Add(dedupeKey);

                urlsWithDetails.Add((method, displayUrl, responseFormat, responsePreview));
            }

            return urlsWithDetails;
        }

        /// <summary>
        /// Extracts the GraphQL operation name from HAR postData.
        /// Tries: 1) explicit "operationName" field, 2) root field name from the query string.
        /// </summary>
        private static string ExtractGraphqlOperationName(HarPostData? postData)
        {
            if (postData?.Text == null) return "";
            try
            {
                using var doc = JsonDocument.Parse(postData.Text);
                // 1) Explicit operationName
                if (doc.RootElement.TryGetProperty("operationName", out var opProp))
                {
                    var opName = opProp.GetString();
                    if (!string.IsNullOrEmpty(opName))
                        return opName;
                }
                // 2) Extract root field name from the query string
                if (doc.RootElement.TryGetProperty("query", out var queryProp))
                {
                    var query = queryProp.GetString() ?? "";
                    return ExtractGraphqlRootField(query);
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Extracts the root field name from a GraphQL query string.
        /// E.g. "query ($id: String!) { classroom (...) { ... } }" -> "classroom"
        /// Also handles "mutation (...) { syncClassroom (...) }" -> "syncClassroom"
        /// </summary>
        private static string ExtractGraphqlRootField(string query)
        {
            // Find the first '{' and then the next word token
            var match = Regex.Match(query, @"\{\s*(\w+)");
            return match.Success ? match.Groups[1].Value : "";
        }

        /// <summary>
        /// Parses a cookies.json file into a dictionary keyed by cookie name.
        /// </summary>
        /// <param name="cookieFilePath">Path to the cookies.json file.</param>
        /// <returns>Dictionary mapping cookie names to their <see cref="CookieData"/>.</returns>
        public static Dictionary<string, CookieData> ParseCookieFile(string cookieFilePath)
        {
            var parsedData = new Dictionary<string, CookieData>();

            string jsonContent = File.ReadAllText(cookieFilePath);

            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                return parsedData;
            }

            foreach (var cookie in root.EnumerateArray())
            {
                string? name = cookie.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString()
                    : null;

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var cookieData = new CookieData
                {
                    Value = cookie.TryGetProperty("value", out var valProp)
                        ? valProp.GetString()
                        : null,
                    Domain = cookie.TryGetProperty("domain", out var domProp)
                        ? domProp.GetString()
                        : null,
                    Path = cookie.TryGetProperty("path", out var pathProp)
                        ? pathProp.GetString()
                        : null,
                    Expires = cookie.TryGetProperty("expires", out var expProp)
                        ? GetJsonValue(expProp)
                        : null,
                    HttpOnly = cookie.TryGetProperty("httpOnly", out var httpProp)
                        ? httpProp.ValueKind == JsonValueKind.True
                        : null,
                    Secure = cookie.TryGetProperty("secure", out var secProp)
                        ? secProp.ValueKind == JsonValueKind.True
                        : null,
                    SameSite = cookie.TryGetProperty("sameSite", out var sameProp)
                        ? sameProp.ValueKind == JsonValueKind.String
                            ? sameProp.GetString()
                            : sameProp.ValueKind == JsonValueKind.Number
                                ? sameProp.GetInt32() switch { 0 => "None", 1 => "Lax", 2 => "Strict", _ => null }
                                : null
                        : null
                };

                parsedData[name] = cookieData;
            }

            return parsedData;
        }

        /// <summary>
        /// Extracts a value from a <see cref="JsonElement"/>, returning it as the
        /// most appropriate CLR type (string, number, bool, or null).
        /// </summary>
        private static object? GetJsonValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => element.GetRawText()
            };
        }
    }
}
