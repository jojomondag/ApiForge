using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ApiForge.Models
{
    /// <summary>
    /// Root object of a HAR (HTTP Archive) file.
    /// </summary>
    public class HarFile
    {
        [JsonPropertyName("log")]
        public HarLog Log { get; set; } = new HarLog();
    }

    /// <summary>
    /// The log section of a HAR file containing all recorded entries.
    /// </summary>
    public class HarLog
    {
        [JsonPropertyName("entries")]
        public List<HarEntry> Entries { get; set; } = new List<HarEntry>();
    }

    /// <summary>
    /// A single entry in the HAR log, representing one HTTP request/response pair.
    /// </summary>
    public class HarEntry
    {
        [JsonPropertyName("request")]
        public HarRequest Request { get; set; } = new HarRequest();

        [JsonPropertyName("response")]
        public HarResponse Response { get; set; } = new HarResponse();
    }

    /// <summary>
    /// The request portion of a HAR entry.
    /// </summary>
    public class HarRequest
    {
        [JsonPropertyName("method")]
        public string Method { get; set; } = "GET";

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("headers")]
        public List<HarHeader> Headers { get; set; } = new List<HarHeader>();

        [JsonPropertyName("queryString")]
        public List<HarQueryParam> QueryString { get; set; } = new List<HarQueryParam>();

        [JsonPropertyName("postData")]
        public HarPostData? PostData { get; set; }
    }

    /// <summary>
    /// The response portion of a HAR entry.
    /// </summary>
    public class HarResponse
    {
        [JsonPropertyName("content")]
        public HarContent Content { get; set; } = new HarContent();
    }

    /// <summary>
    /// An HTTP header with a name and value.
    /// </summary>
    public class HarHeader
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// A query string parameter with a name and value.
    /// </summary>
    public class HarQueryParam
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST data from a HAR request entry.
    /// </summary>
    public class HarPostData
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }
    }

    /// <summary>
    /// Response content from a HAR response entry.
    /// </summary>
    public class HarContent
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }
    }

    /// <summary>
    /// Represents a single cookie entry, typically from a cookies.json file.
    /// </summary>
    public class CookieEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("domain")]
        public string? Domain { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("expires")]
        public double? Expires { get; set; }

        [JsonPropertyName("httpOnly")]
        public bool? HttpOnly { get; set; }

        [JsonPropertyName("secure")]
        public bool? Secure { get; set; }

        [JsonPropertyName("sameSite")]
        public string? SameSite { get; set; }
    }
}
