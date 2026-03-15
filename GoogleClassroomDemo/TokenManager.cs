using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoogleClassroomDemo;

/// <summary>
/// Manages session tokens for Google Classroom — save, load, validate, and use them for HTTP requests.
/// Google Classroom uses Google Authentication via AcadeMedia GrandID SSO.
/// </summary>
public class TokenManager
{
    private const string TokenFile = "session_tokens.json";

    public class StoredSession
    {
        [JsonPropertyName("cookies")]
        public List<StoredCookie> Cookies { get; set; } = new();

        [JsonPropertyName("savedAt")]
        public DateTime SavedAt { get; set; }
    }

    public class StoredCookie
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = "/";

        [JsonPropertyName("expires")]
        public double? Expires { get; set; }

        [JsonPropertyName("secure")]
        public bool Secure { get; set; }

        [JsonPropertyName("httpOnly")]
        public bool HttpOnly { get; set; }
    }

    public static async Task SaveSession(StoredSession session)
    {
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(TokenFile, json);
    }

    public static async Task<StoredSession?> LoadSession()
    {
        if (!File.Exists(TokenFile))
            return null;

        var json = await File.ReadAllTextAsync(TokenFile);
        return JsonSerializer.Deserialize<StoredSession>(json);
    }

    /// <summary>
    /// Local validation: checks for Google cookies and session age.
    /// </summary>
    public static bool IsSessionValid(StoredSession session)
    {
        if (session.Cookies.Count == 0)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Must have Google cookies
        var googleCookies = session.Cookies
            .Where(c => CookieDomainMatches(c.Domain, "google.com") ||
                        CookieDomainMatches(c.Domain, "classroom.google.com"))
            .ToList();

        if (googleCookies.Count == 0)
            return false;

        // Check expiry
        var expiredCount = googleCookies.Count(c => c.Expires.HasValue && c.Expires.Value > 0 && c.Expires.Value < now);
        var totalWithExpiry = googleCookies.Count(c => c.Expires.HasValue && c.Expires.Value > 0);

        if (totalWithExpiry > 0 && expiredCount > totalWithExpiry / 2)
            return false;

        // Google sessions typically last a while, but check age
        var sessionAge = DateTime.UtcNow - session.SavedAt;
        if (sessionAge.TotalHours > 12)
            return false;

        return true;
    }

    /// <summary>
    /// Creates an HttpClient with all session cookies loaded.
    /// </summary>
    public static HttpClient CreateAuthenticatedClient(StoredSession session)
    {
        var cookieContainer = new CookieContainer();

        foreach (var cookie in session.Cookies)
        {
            try
            {
                var domain = cookie.Domain.TrimStart('.');
                cookieContainer.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, domain)
                {
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly
                });
            }
            catch { }
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9,en-US;q=0.8,en;q=0.7");
        return client;
    }

    public class OnlineValidationResult
    {
        public bool IsValid { get; set; }
        public string Reason { get; set; } = "";
        public int? HttpStatus { get; set; }
        public string? RedirectUrl { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Validates the session by making a real HTTP request to Google Classroom.
    /// </summary>
    public static async Task<OnlineValidationResult> ValidateSessionOnline(StoredSession session, string testUrl)
    {
        var result = new OnlineValidationResult();

        try
        {
            using var client = CreateAuthenticatedClient(session);
            client.Timeout = TimeSpan.FromSeconds(15);
            var response = await client.GetAsync(testUrl);
            var status = (int)response.StatusCode;
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? testUrl;

            result.HttpStatus = status;
            result.RedirectUrl = finalUrl != testUrl ? finalUrl : null;

            if (status is 401 or 403)
            {
                result.IsValid = false;
                result.Reason = $"HTTP {status} — session avvisad";
                return result;
            }

            var finalUrlLower = finalUrl.ToLower();
            bool redirectedToLogin = finalUrlLower.Contains("accounts.google.com/signin") ||
                                     finalUrlLower.Contains("accounts.google.com/o/oauth2") ||
                                     finalUrlLower.Contains("grandid.com") ||
                                     finalUrlLower.Contains("/login");

            if (redirectedToLogin)
            {
                result.IsValid = false;
                result.Reason = $"Omdirigerades till inloggning: {finalUrl}";
                return result;
            }

            if (response.IsSuccessStatusCode)
            {
                result.IsValid = true;
                result.Reason = $"HTTP {status} OK";
                return result;
            }

            result.IsValid = false;
            result.Reason = $"HTTP {status}";
            return result;
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Reason = ex.Message;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public static void PrintSessionInfo(StoredSession session)
    {
        Console.WriteLine($"  Sparad: {session.SavedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"  Antal cookies: {session.Cookies.Count}");

        var domains = session.Cookies.Select(c => c.Domain.TrimStart('.')).Distinct();
        Console.WriteLine($"  Domäner: {string.Join(", ", domains)}");

        var googleCookies = session.Cookies.Count(c => CookieDomainMatches(c.Domain, "google.com"));
        Console.WriteLine($"  Google-cookies: {googleCookies}");

        var valid = IsSessionValid(session);
        var age = DateTime.UtcNow - session.SavedAt;
        Console.WriteLine($"  Ålder: {age.Hours}h {age.Minutes}m");
        Console.WriteLine($"  Status: {(valid ? "Giltig" : "Utgången")}");
    }

    public static bool CookieDomainMatches(string cookieDomain, string targetDomain)
    {
        var cleanCookie = cookieDomain.TrimStart('.').ToLower();
        var cleanTarget = targetDomain.TrimStart('.').ToLower();

        return cleanCookie == cleanTarget ||
               cleanCookie.EndsWith("." + cleanTarget) ||
               cleanTarget.EndsWith("." + cleanCookie);
    }
}
