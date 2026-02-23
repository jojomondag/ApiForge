using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyBeeDemo;

/// <summary>
/// Manages session tokens for Studybee — save, load, validate, and use them for HTTP requests.
/// Studybee uses Google Authentication, so cookies come from both Google and Studybee domains.
/// </summary>
public class TokenManager
{
    private const string TokenFile = "session_tokens.json";

    // Studybee domain for cookie filtering
    private const string StudybeeDomain = "studybee.io";

    // Google auth-related domains
    private static readonly string[] GoogleDomains = { "google.com", "googleapis.com", "accounts.google.com" };

    public class StoredSession
    {
        [JsonPropertyName("cookies")]
        public List<StoredCookie> Cookies { get; set; } = new();

        [JsonPropertyName("savedAt")]
        public DateTime SavedAt { get; set; }

        [JsonPropertyName("studybeeBaseUrl")]
        public string StudybeeBaseUrl { get; set; } = string.Empty;
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

    /// <summary>
    /// Extracts session tokens from recorded Playwright cookies and saves them.
    /// </summary>
    public static async Task<StoredSession> ExtractAndSaveFromCookieFile(string cookieFilePath)
    {
        var json = await File.ReadAllTextAsync(cookieFilePath);
        var playwrightCookies = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new();

        var session = new StoredSession
        {
            SavedAt = DateTime.UtcNow,
            Cookies = new List<StoredCookie>()
        };

        foreach (var cookie in playwrightCookies)
        {
            var stored = new StoredCookie
            {
                Name = cookie.GetProperty("name").GetString() ?? "",
                Value = cookie.GetProperty("value").GetString() ?? "",
                Domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "",
                Path = cookie.TryGetProperty("path", out var p) ? p.GetString() ?? "/" : "/",
                Secure = cookie.TryGetProperty("secure", out var s) && s.GetBoolean(),
                HttpOnly = cookie.TryGetProperty("httpOnly", out var h) && h.GetBoolean()
            };

            if (cookie.TryGetProperty("expires", out var exp) && exp.ValueKind == JsonValueKind.Number)
            {
                stored.Expires = exp.GetDouble();
            }

            session.Cookies.Add(stored);

            // Detect Studybee base URL from cookie domains
            if (CookieDomainMatches(stored.Domain, StudybeeDomain) && string.IsNullOrEmpty(session.StudybeeBaseUrl))
            {
                var domain = stored.Domain.TrimStart('.');
                session.StudybeeBaseUrl = $"https://{domain}";
            }
        }

        await SaveSession(session);
        return session;
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
    /// Local validation: checks for Studybee-domain cookies and session age.
    /// </summary>
    public static bool IsSessionValid(StoredSession session)
    {
        if (session.Cookies.Count == 0)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Must have at least one Studybee cookie
        var studybeeCookies = session.Cookies
            .Where(c => CookieDomainMatches(c.Domain, StudybeeDomain))
            .ToList();

        if (studybeeCookies.Count == 0)
            return false;

        // Check expiry on cookies that have one
        var expiredCount = studybeeCookies.Count(c => c.Expires.HasValue && c.Expires.Value > 0 && c.Expires.Value < now);
        var totalWithExpiry = studybeeCookies.Count(c => c.Expires.HasValue && c.Expires.Value > 0);

        if (totalWithExpiry > 0 && expiredCount > totalWithExpiry / 2)
            return false;

        // Google OAuth sessions typically last a while, but session cookies without expiry
        // should be considered stale after some hours
        var sessionAge = DateTime.UtcNow - session.SavedAt;
        bool hasOnlySessionCookies = totalWithExpiry == 0;
        if (hasOnlySessionCookies && sessionAge.TotalHours > 12)
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
            catch
            {
                // Skip malformed cookies
            }
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
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/json");
        client.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9,en-US;q=0.8,en;q=0.7");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        return client;
    }

    /// <summary>
    /// Builds a cookie header string for a specific domain.
    /// </summary>
    public static string GetCookieString(StoredSession session, string targetDomain)
    {
        var cookies = session.Cookies
            .Where(c => CookieDomainMatches(c.Domain, targetDomain))
            .Select(c => $"{c.Name}={c.Value}");

        return string.Join("; ", cookies);
    }

    public class OnlineValidationResult
    {
        public bool IsValid { get; set; }
        public string Reason { get; set; } = "";
        public int? HttpStatus { get; set; }
        public string? RedirectUrl { get; set; }
        public string? BodySnippet { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Validates the session by making a real HTTP request to Studybee.
    /// Follows redirects and checks the final response for login/auth indicators.
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

            // 401/403 = definitely expired
            if (status is 401 or 403)
            {
                result.IsValid = false;
                result.Reason = $"HTTP {status} — session avvisad av servern";
                return result;
            }

            // Read body
            string body = "";
            try
            {
                body = await response.Content.ReadAsStringAsync();
            }
            catch { }

            // Save a snippet for debug output
            var snippet = body.Length > 300 ? body[..300] : body;
            snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"<[^>]+>", " ");
            snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"\s+", " ").Trim();
            result.BodySnippet = $"({body.Length} bytes) " + (snippet.Length > 180 ? snippet[..180] + "..." : snippet);

            // Check: Did we end up on a Google login page?
            var finalUrlLower = finalUrl.ToLower();
            bool redirectedToLogin = finalUrlLower.Contains("accounts.google.com/signin") ||
                                     finalUrlLower.Contains("accounts.google.com/o/oauth2") ||
                                     finalUrlLower.Contains("/auth/login") ||
                                     finalUrlLower.Contains("/login");

            if (redirectedToLogin)
            {
                result.IsValid = false;
                result.Reason = $"Omdirigerades till inloggning: {finalUrl}";
                return result;
            }

            // Check body for login indicators
            var bodyLower = body.ToLower();
            bool definitelyExpired =
                bodyLower.Contains("accounts.google.com/signin") ||
                bodyLower.Contains("\"authenticated\":false") ||
                bodyLower.Contains("\"loggedIn\":false".ToLower()) ||
                (bodyLower.Contains("location.replace") && bodyLower.Contains("login"));

            if (definitelyExpired && status == 200)
            {
                result.IsValid = false;
                result.Reason = "Sidan innehaller inloggningsomdirigering — sessionen har gatt ut";
                return result;
            }

            if (response.IsSuccessStatusCode)
            {
                bool hasContent = bodyLower.Contains("studybee") ||
                                  bodyLower.Contains("insights") ||
                                  bodyLower.Contains("dashboard") ||
                                  bodyLower.Contains("student") ||
                                  bodyLower.Contains("<script") ||
                                  body.Length > 500;

                if (hasContent)
                {
                    result.IsValid = true;
                    result.Reason = $"HTTP {status} med Studybee-innehall ({body.Length} bytes)";
                    return result;
                }

                result.IsValid = true;
                result.Reason = $"HTTP {status} ({body.Length} bytes, inget inloggningsformular)";
                return result;
            }

            result.IsValid = false;
            result.Reason = $"HTTP {status} — servern svarade med felkod";
            return result;
        }
        catch (TaskCanceledException)
        {
            result.IsValid = false;
            result.Reason = "Timeout — servern svarade inte inom 15 sekunder";
            result.ErrorMessage = "Timeout";
            return result;
        }
        catch (HttpRequestException ex)
        {
            result.IsValid = false;
            result.Reason = $"Natverksfel: {ex.Message}";
            result.ErrorMessage = ex.Message;
            return result;
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Reason = $"Ovantat fel: {ex.Message}";
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public static void PrintSessionInfo(StoredSession session)
    {
        Console.WriteLine($"  Sparad: {session.SavedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"  Studybee URL: {session.StudybeeBaseUrl}");
        Console.WriteLine($"  Antal cookies: {session.Cookies.Count}");

        var domains = session.Cookies.Select(c => c.Domain.TrimStart('.')).Distinct();
        Console.WriteLine($"  Domaner: {string.Join(", ", domains)}");

        // Show Studybee vs Google cookie breakdown
        var studybeeCookieCount = session.Cookies.Count(c => CookieDomainMatches(c.Domain, StudybeeDomain));
        var googleCookieCount = session.Cookies.Count(c =>
            GoogleDomains.Any(gd => CookieDomainMatches(c.Domain, gd)));
        Console.WriteLine($"  Studybee-cookies: {studybeeCookieCount}");
        Console.WriteLine($"  Google-cookies: {googleCookieCount}");

        var valid = IsSessionValid(session);
        var age = DateTime.UtcNow - session.SavedAt;
        Console.WriteLine($"  Alder: {age.Hours}h {age.Minutes}m");
        Console.WriteLine($"  Status: {(valid ? "Giltig" : "Utgangen - ny inloggning kravs")}");
    }

    private static bool CookieDomainMatches(string cookieDomain, string targetDomain)
    {
        var cleanCookie = cookieDomain.TrimStart('.').ToLower();
        var cleanTarget = targetDomain.TrimStart('.').ToLower();

        return cleanCookie == cleanTarget ||
               cleanCookie.EndsWith("." + cleanTarget) ||
               cleanTarget.EndsWith("." + cleanCookie);
    }
}
