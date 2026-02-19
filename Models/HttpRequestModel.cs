using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ApiForge.Models
{
    /// <summary>
    /// Represents an HTTP request with methods to generate cURL commands.
    /// </summary>
    public class HttpRequestModel
    {
        public string Method { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public Dictionary<string, string>? QueryParams { get; set; }

        /// <summary>
        /// The request body. Can be a string, a Dictionary&lt;string, object&gt;, or null.
        /// </summary>
        public object? Body { get; set; }

        /// <summary>
        /// Parameterless constructor for object initializer and deserialization scenarios.
        /// </summary>
        public HttpRequestModel()
        {
            Method = "GET";
            Url = string.Empty;
            Headers = new Dictionary<string, string>();
        }

        public HttpRequestModel(
            string method,
            string url,
            Dictionary<string, string> headers,
            Dictionary<string, string>? queryParams = null,
            object? body = null)
        {
            Method = method;
            Url = url;
            Headers = headers;
            QueryParams = queryParams;
            Body = body;
        }

        /// <summary>
        /// Builds a curl command string from this request, including all headers,
        /// query parameters, and body.
        /// </summary>
        public string ToCurlCommand()
        {
            return BuildCurlCommand(excludeHeaders: null);
        }

        /// <summary>
        /// Builds a minified curl command that excludes 'referer' and 'cookie' headers.
        /// This is done to reduce LLM hallucinations.
        /// </summary>
        public string ToMinifiedCurlCommand()
        {
            var excludedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "referer",
                "cookie"
            };
            return BuildCurlCommand(excludeHeaders: excludedHeaders);
        }

        public override string ToString()
        {
            return ToCurlCommand();
        }

        private string BuildCurlCommand(HashSet<string>? excludeHeaders)
        {
            var curlParts = new List<string>
            {
                $"curl -X {Method}"
            };

            foreach (var kvp in Headers)
            {
                if (excludeHeaders != null && excludeHeaders.Contains(kvp.Key))
                {
                    continue;
                }
                curlParts.Add($"-H '{kvp.Key}: {kvp.Value}'");
            }

            string finalUrl = Url;
            if (QueryParams != null && QueryParams.Count > 0)
            {
                var queryString = string.Join("&", QueryParams.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                finalUrl += $"?{queryString}";
            }

            // Treat null and empty string as no body
            if (Body != null && !(Body is string s && s.Length == 0))
            {
                string? contentType = null;
                foreach (var kvp in Headers)
                {
                    if (string.Equals(kvp.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = kvp.Value;
                        break;
                    }
                }

                if (Body is IDictionary<string, object> || Body is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                {
                    if (contentType == null)
                    {
                        curlParts.Add("-H 'Content-Type: application/json'");
                    }
                    string jsonBody = JsonSerializer.Serialize(Body);
                    curlParts.Add($"--data '{jsonBody}'");
                }
                else if (Body is string bodyStr)
                {
                    curlParts.Add($"--data '{bodyStr}'");
                }
                else
                {
                    // Fallback: serialize whatever object it is as JSON
                    string serialized = JsonSerializer.Serialize(Body);
                    curlParts.Add($"--data '{serialized}'");
                }
            }

            curlParts.Add($"'{finalUrl}'");

            return string.Join(" ", curlParts);
        }
    }
}
