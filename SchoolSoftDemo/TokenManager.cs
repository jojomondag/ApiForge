using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchoolSoftDemo;

/// <summary>
/// Manages SSO session tokens — save, load, validate, and use them for HTTP requests.
/// Handles Shibboleth/SAML cookies from GrandID SSO used by SchoolSoft.
/// </summary>
public class TokenManager
{
    private const string TokenFile = "session_tokens.json";

    // Shibboleth session cookies always start with this prefix
    private const string ShibCookiePrefix = "_shibsession_";

    // Known critical cookie names for SchoolSoft auth
    private static readonly HashSet<string> CriticalCookieNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "JSESSIONID", "PHPSESSID"
    };

    public class StoredSession
    {
        [JsonPropertyName("cookies")]
        public List<StoredCookie> Cookies { get; set; } = new();

        [JsonPropertyName("savedAt")]
        public DateTime SavedAt { get; set; }

        [JsonPropertyName("schoolSoftBaseUrl")]
        public string SchoolSoftBaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("schoolPath")]
        public string SchoolPath { get; set; } = string.Empty;
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
    public static async Task<StoredSession> ExtractAndSaveFromCookieFile(string cookieFilePath, string? schoolPath = null)
    {
        var json = await File.ReadAllTextAsync(cookieFilePath);
        var playwrightCookies = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new();

        var session = new StoredSession
        {
            SavedAt = DateTime.UtcNow,
            Cookies = new List<StoredCookie>(),
            SchoolPath = schoolPath ?? ""
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

            // Detect SchoolSoft base URL from cookie domains
            if (stored.Domain.Contains("schoolsoft.se") && string.IsNullOrEmpty(session.SchoolSoftBaseUrl))
            {
                var domain = stored.Domain.TrimStart('.');
                session.SchoolSoftBaseUrl = $"https://{domain}";
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
    /// Validates the session by checking for critical Shibboleth and SchoolSoft cookies.
    /// </summary>
    public static bool IsSessionValid(StoredSession session)
    {
        if (session.Cookies.Count == 0)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1. Check for Shibboleth session cookie — this is the primary auth indicator
        var shibCookie = session.Cookies.FirstOrDefault(c =>
            c.Name.StartsWith(ShibCookiePrefix, StringComparison.OrdinalIgnoreCase) &&
            CookieDomainMatches(c.Domain, "schoolsoft.se"));

        // 2. Check for JSESSIONID on SchoolSoft domain
        var jSessionCookie = session.Cookies.FirstOrDefault(c =>
            CriticalCookieNames.Contains(c.Name) &&
            CookieDomainMatches(c.Domain, "schoolsoft.se"));

        // Must have at least the Shibboleth cookie OR a session cookie
        if (shibCookie == null && jSessionCookie == null)
            return false;

        // 3. Check expiry on cookies that have one
        //    Shibboleth session cookies are often session-only (no expires) — that's OK,
        //    they're valid until browser closes. We check SavedAt age instead.
        var authCookies = session.Cookies
            .Where(c => CookieDomainMatches(c.Domain, "schoolsoft.se") ||
                        CookieDomainMatches(c.Domain, "grandid.com"))
            .ToList();

        var expiredCount = authCookies.Count(c => c.Expires.HasValue && c.Expires.Value > 0 && c.Expires.Value < now);
        var totalWithExpiry = authCookies.Count(c => c.Expires.HasValue && c.Expires.Value > 0);

        if (totalWithExpiry > 0 && expiredCount > totalWithExpiry / 2)
            return false;

        // 4. Shibboleth sessions typically last 1-8 hours.
        //    If session cookies (no expiry) were saved more than 8 hours ago, assume expired.
        var sessionAge = DateTime.UtcNow - session.SavedAt;
        bool hasOnlySessionCookies = totalWithExpiry == 0;
        if (hasOnlySessionCookies && sessionAge.TotalHours > 8)
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
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9");
        client.DefaultRequestHeaders.Add("Origin", "https://sms.schoolsoft.se");
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

    /// <summary>
    /// Result of an online session validation attempt, with debug info.
    /// </summary>
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
    /// Validates the session by making a real HTTP request to SchoolSoft.
    /// Follows redirects and checks the final response for login indicators.
    /// Returns a detailed result with debug info.
    /// </summary>
    public static async Task<OnlineValidationResult> ValidateSessionOnline(StoredSession session, string testUrl)
    {
        var result = new OnlineValidationResult();

        try
        {
            // Reuse CreateAuthenticatedClient to ensure identical headers/cookies as the main client
            using var followClient = CreateAuthenticatedClient(session);
            followClient.Timeout = TimeSpan.FromSeconds(15);
            var followResponse = await followClient.GetAsync(testUrl);
            var followStatus = (int)followResponse.StatusCode;
            var finalUrl = followResponse.RequestMessage?.RequestUri?.ToString() ?? testUrl;

            result.HttpStatus = followStatus;
            result.RedirectUrl = finalUrl != testUrl ? finalUrl : null;

            // 401/403 = definitely expired
            if (followStatus is 401 or 403)
            {
                result.IsValid = false;
                result.Reason = $"HTTP {followStatus} — session avvisad av servern";
                return result;
            }

            // Read body to inspect content
            string body = "";
            try
            {
                var bytes = await followResponse.Content.ReadAsByteArrayAsync();
                body = System.Text.Encoding.UTF8.GetString(bytes);
                if (body.Contains('\uFFFD'))
                    body = System.Text.Encoding.GetEncoding(1252).GetString(bytes);
            }
            catch { }

            // Save a snippet for debug output
            var snippet = body.Length > 300 ? body[..300] : body;
            snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"<[^>]+>", " ");
            snippet = System.Text.RegularExpressions.Regex.Replace(snippet, @"\s+", " ").Trim();
            result.BodySnippet = $"({body.Length} bytes) " + (snippet.Length > 180 ? snippet[..180] + "..." : snippet);

            // Check 1: Did we end up on a login/SSO/redirect page?
            var finalUrlLower = finalUrl.ToLower();
            bool redirectedToLogin = finalUrlLower.Contains("/saml/") ||
                                     finalUrlLower.Contains("grandid.com") ||
                                     finalUrlLower.Contains("/login") ||
                                     finalUrlLower.Contains("redirect_login") ||
                                     // SchoolSoft's SSO entry point is specifically /{school}/sso — detect it precisely
                                     System.Text.RegularExpressions.Regex.IsMatch(finalUrlLower, @"/[^/]+/sso(\?|$|/)");

            if (redirectedToLogin)
            {
                result.IsValid = false;
                result.Reason = $"Omdirigerades till inloggning: {finalUrl}";
                return result;
            }

            // Check 2: Does the page body contain definitive login/expired indicators?
            // Be strict — only match patterns that PROVE the session is expired.
            // Normal SchoolSoft pages can contain "location.replace", "login.jsp", "grandid" in
            // navigation/JS without meaning the session is expired.
            var bodyLower = body.ToLower();
            bool definitelyExpired =
                bodyLower.Contains("eventmessage=err") ||                          // SchoolSoft explicit error
                bodyLower.Contains("name=\"username\"") ||                         // actual login form
                bodyLower.Contains("name=\"password\"") ||                         // actual login form
                (bodyLower.Contains("location.replace") &&                         // JS redirect TO login
                    (bodyLower.Contains("login.jsp") || bodyLower.Contains("redirect_login")));

            if (definitelyExpired && followStatus == 200)
            {
                result.IsValid = false;
                result.Reason = "Sidan innehaller inloggningsformular — sessionen har gatt ut";
                return result;
            }

            // Check 3: Is the response a success with actual SchoolSoft content?
            if (followResponse.IsSuccessStatusCode)
            {
                // Even a minimal SchoolSoft page should have some HTML structure
                bool hasSchoolSoftContent = bodyLower.Contains("schoolsoft") ||
                                            bodyLower.Contains("tab_dark") ||       // schedule table class
                                            bodyLower.Contains("right_teacher") ||   // teacher page URLs
                                            bodyLower.Contains("jsp/teacher") ||     // teacher JSP paths
                                            bodyLower.Contains("<table") ||          // any table content
                                            bodyLower.Contains("lektion") ||         // lesson-related
                                            bodyLower.Contains("schema") ||          // schedule-related
                                            body.Length > 500;                       // substantial response

                if (hasSchoolSoftContent)
                {
                    result.IsValid = true;
                    result.Reason = $"HTTP {followStatus} med SchoolSoft-innehall ({body.Length} bytes)";
                    return result;
                }

                // Got a 200 but with minimal content — probably still OK
                result.IsValid = true;
                result.Reason = $"HTTP {followStatus} ({body.Length} bytes, inget inloggningsformular)";
                return result;
            }

            // Non-success, non-auth error (e.g. 500) — inconclusive
            result.IsValid = false;
            result.Reason = $"HTTP {followStatus} — servern svarade med felkod";
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
            result.Reason = $"Ovantad fel: {ex.Message}";
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public static void PrintSessionInfo(StoredSession session)
    {
        Console.WriteLine($"  Sparad: {session.SavedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"  SchoolSoft URL: {session.SchoolSoftBaseUrl}");
        if (!string.IsNullOrEmpty(session.SchoolPath))
            Console.WriteLine($"  Skola: {session.SchoolPath}");
        Console.WriteLine($"  Antal cookies: {session.Cookies.Count}");

        var domains = session.Cookies.Select(c => c.Domain.TrimStart('.')).Distinct();
        Console.WriteLine($"  Domaner: {string.Join(", ", domains)}");

        // Show critical cookie status
        var shibCookie = session.Cookies.FirstOrDefault(c => c.Name.StartsWith(ShibCookiePrefix));
        Console.WriteLine($"  Shibboleth-cookie: {(shibCookie != null ? "Hittad" : "Saknas")}");

        var jSession = session.Cookies.FirstOrDefault(c => CriticalCookieNames.Contains(c.Name) &&
            CookieDomainMatches(c.Domain, "schoolsoft.se"));
        Console.WriteLine($"  JSESSIONID: {(jSession != null ? "Hittad" : "Saknas")}");

        var valid = IsSessionValid(session);
        var age = DateTime.UtcNow - session.SavedAt;
        Console.WriteLine($"  Alder: {age.Hours}h {age.Minutes}m");
        Console.WriteLine($"  Status: {(valid ? "Giltig" : "Utgangen - ny inloggning kravs")}");
    }

    /// <summary>
    /// Checks if a cookie domain matches a target domain (handles leading dots and subdomains).
    /// </summary>
    private static bool CookieDomainMatches(string cookieDomain, string targetDomain)
    {
        var cleanCookie = cookieDomain.TrimStart('.').ToLower();
        var cleanTarget = targetDomain.TrimStart('.').ToLower();

        // Exact match or subdomain match in either direction
        return cleanCookie == cleanTarget ||
               cleanCookie.EndsWith("." + cleanTarget) ||
               cleanTarget.EndsWith("." + cleanCookie);
    }
}
