using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;

namespace GoogleClassroomDemo;

/// <summary>
/// Performs headless SSO login to Google Classroom via GrandID/AcadeMedia.
/// Flow: Google Classroom → Google Login → AcadeMedia IDP (GrandID) → SAML → Google → Classroom
/// Saves GrandID device cookie so SMS OTP is only needed once.
/// </summary>
public class HeadlessAuthenticator
{
    private const string GrandIdCookieFile = "grandid_device.json";

    /// <summary>
    /// Performs the full SSO login flow:
    /// 1. Use Google Workspace domain-specific login URL → redirects to GrandID
    /// 2. GrandID login (username + password + optional 2FA)
    /// 3. SAML assertion → back to Google
    /// 4. Google grants session → cookies for Classroom
    /// </summary>
    public async Task<TokenManager.StoredSession> LoginAsync(string username, string password, string classroomUrl)
    {
        var cookieContainer = new CookieContainer();

        // Load saved GrandID device cookie (skips SMS on subsequent logins)
        var savedDeviceCookie = await LoadDeviceCookie();
        if (savedDeviceCookie != null)
        {
            cookieContainer.Add(new Uri("https://login.grandid.com"), savedDeviceCookie);
            Console.WriteLine($"[Auth] Enhetscookie laddad — försöker hoppa över SMS.");
        }

        using var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = false,
            UseCookies = true
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        // Extract domain from email for Google Workspace domain-specific login
        var emailDomain = username.Contains('@') ? username.Split('@')[1] : "ga.ntig.se";

        // Use Google Workspace domain-specific login — bypasses JS-driven email form
        // and redirects directly to the SAML IdP (GrandID via AcadeMedia)
        var domainLoginUrl = $"https://accounts.google.com/a/{emailDomain}/ServiceLogin?continue={Uri.EscapeDataString(classroomUrl)}";

        Console.WriteLine($"[Auth] Använder domän-specifik inloggning: {emailDomain}");
        Console.WriteLine($"[Auth] Navigerar till Google Workspace SSO...");

        var (resp, currentUrl) = await FollowRedirectsAsync(client, domainLoginUrl);
        var html = await resp.Content.ReadAsStringAsync();

        Console.WriteLine($"[Auth] Landade på: {TruncateUrl(currentUrl)}");

        // If we're still on Google, check for redirect to SAML IdP
        if (!currentUrl.Contains("grandid.com") && !currentUrl.Contains("login.grandid.com"))
        {
            // Check for JS/meta redirects in the page
            var redirectUrl = FindRedirectInHtml(html);
            if (redirectUrl != null)
            {
                Console.WriteLine($"[Auth] Följer redirect: {TruncateUrl(redirectUrl)}");
                (resp, currentUrl) = await FollowRedirectsAsync(client, redirectUrl);
                html = await resp.Content.ReadAsStringAsync();
            }
        }

        if (!currentUrl.Contains("grandid.com"))
        {
            // Save debug info
            var debugPath = Path.Combine(Path.GetTempPath(), "gc_auth_debug.html");
            await File.WriteAllTextAsync(debugPath, html);
            Console.WriteLine($"[Auth] Kunde inte hitta GrandID. Sparade debug: {debugPath}");
            Console.WriteLine($"[Auth] Nuvarande URL: {currentUrl}");
            throw new InvalidOperationException("Kunde inte navigera till GrandID-inloggning.");
        }

        Console.WriteLine("[Auth] GrandID-inloggningssida hittad!");

        // Step 4: Extract sessionid and login form
        var sessionIdMatch = Regex.Match(currentUrl, @"sessionid=([a-f0-9]+)");
        if (!sessionIdMatch.Success)
            throw new InvalidOperationException("Kunde inte hitta GrandID session-ID.");
        var sessionId = sessionIdMatch.Groups[1].Value;
        Console.WriteLine($"[Auth] GrandID session: {sessionId[..Math.Min(8, sessionId.Length)]}...");

        // Parse login form
        var (formFields, formAction) = ExtractLoginForm(html, currentUrl);
        formFields["username"] = username;
        formFields["password"] = password;

        Console.WriteLine($"[Auth] Loggar in ({formFields.Count} fält)...");

        // Step 5: Submit credentials
        var loginContent = new FormUrlEncodedContent(formFields);
        resp = await client.PostAsync(formAction, loginContent);
        html = await resp.Content.ReadAsStringAsync();

        if (html.Contains("Felaktigt anv") || html.Contains("incorrect") || html.Contains("angivit fel"))
        {
            throw new InvalidOperationException("Inloggning misslyckades — felaktigt användarnamn eller lösenord.");
        }

        // Check for direct redirect (2FA skipped)
        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            Console.WriteLine("[Auth] 2FA hoppades över!");
            var redirectUrl = GetRedirectUrl(resp, currentUrl);
            return await CompleteSamlFlow(client, cookieContainer, redirectUrl, classroomUrl);
        }

        // Handle 2FA
        if (html.Contains("2fa-hidden") || html.Contains("2fa-otp"))
        {
            Console.WriteLine("[Auth] 2FA krävs, försöker med enhetscookie...");
            var smsUrl = $"https://login.grandid.com/?sessionid={sessionId}&2fa-hidden=1";
            (resp, var hiddenFinalUrl) = await FollowRedirectsAsync(client, smsUrl);
            var smsHtml = await resp.Content.ReadAsStringAsync();

            bool deviceCookieWorked =
                smsHtml.Contains("SAMLResponse") ||
                hiddenFinalUrl.Contains("resume.php") ||
                hiddenFinalUrl.Contains("saml2.grandid.com") ||
                Regex.IsMatch(smsHtml, @"(?:window\.location|location\.href)\s*=\s*[""'][^""']*(?:resume|saml)", RegexOptions.IgnoreCase);

            if (deviceCookieWorked)
            {
                Console.WriteLine("[Auth] Enhetscookien fungerade — SMS hoppades över!");
                html = smsHtml;
            }
            else
            {
                if (savedDeviceCookie != null)
                {
                    Console.WriteLine("[Auth] Enhetscookien fungerade inte. Raderar.");
                    try { File.Delete(GrandIdCookieFile); } catch { }
                }

                Console.Write("[Auth] Ange SMS-kod: ");
                var otpCode = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(otpCode))
                    throw new InvalidOperationException("Ingen SMS-kod angiven.");

                var otpContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["fc"] = "",
                    ["grandidsession"] = sessionId,
                    ["otp"] = otpCode
                });
                resp = await client.PostAsync($"https://login.grandid.com/?sessionid={sessionId}", otpContent);
                html = await resp.Content.ReadAsStringAsync();

                if (html.Contains("Felaktig") || html.Contains("Ogiltig"))
                    throw new InvalidOperationException("Felaktig SMS-kod.");

                Console.WriteLine("[Auth] Kopplar enheten...");
                var approveUrl = $"https://login.grandid.com/?sessionid={sessionId}&approveBrowser=true";
                (resp, _) = await FollowRedirectsAsync(client, approveUrl);
            }
        }
        else if (html.Contains("Koppla webbl"))
        {
            Console.WriteLine("[Auth] SMS hoppades över! Kopplar enheten...");
            var approveUrl = $"https://login.grandid.com/?sessionid={sessionId}&approveBrowser=true";
            (resp, _) = await FollowRedirectsAsync(client, approveUrl);
        }

        // Save device cookie
        await SaveDeviceCookie(cookieContainer);

        // Complete SAML flow back to Google
        return await ResolveSamlFromResponse(client, cookieContainer, resp, html, sessionId, classroomUrl);
    }

    /// <summary>
    /// Resolves SAML assertion from GrandID response and completes flow back to Google.
    /// </summary>
    private async Task<TokenManager.StoredSession> ResolveSamlFromResponse(
        HttpClient client, CookieContainer cookieContainer,
        HttpResponseMessage resp, string html, string sessionId, string classroomUrl)
    {
        // Case 1: HTTP redirect
        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            var redirectUrl = GetRedirectUrl(resp, $"https://login.grandid.com/?sessionid={sessionId}");
            return await CompleteSamlFlow(client, cookieContainer, redirectUrl, classroomUrl);
        }

        // Case 2: SAML form in response
        if (html.Contains("SAMLResponse"))
        {
            Console.WriteLine("[Auth] SAML-svar hittat...");
            var (samlResponse, relayState, postAction) = ExtractSamlForm(html);
            return await CompleteSamlPostFlow(client, cookieContainer, samlResponse, relayState, postAction, classroomUrl);
        }

        // Case 3: JS redirect
        var jsRedirect = FindRedirectInHtml(html);
        if (jsRedirect != null)
        {
            Console.WriteLine($"[Auth] Följer JS-redirect: {TruncateUrl(jsRedirect)}");
            resp = await client.GetAsync(jsRedirect);
            html = await resp.Content.ReadAsStringAsync();
            return await ResolveSamlFromResponse(client, cookieContainer, resp, html, sessionId, classroomUrl);
        }

        // Case 4: Form with auto-submit
        var formAction = Regex.Match(html, @"<form[^>]*action=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (formAction.Success)
        {
            Console.WriteLine("[Auth] Följer form-action...");
            var url = formAction.Groups[1].Value;
            if (!url.StartsWith("http"))
                url = new Uri(new Uri($"https://login.grandid.com/?sessionid={sessionId}"), url).ToString();
            resp = await client.GetAsync(url);
            html = await resp.Content.ReadAsStringAsync();
            return await ResolveSamlFromResponse(client, cookieContainer, resp, html, sessionId, classroomUrl);
        }

        var debugPath = Path.Combine(Path.GetTempPath(), "gc_saml_debug.html");
        await File.WriteAllTextAsync(debugPath, html);
        throw new InvalidOperationException($"Kunde inte hitta SAML-redirect. Debug sparad: {debugPath}");
    }

    /// <summary>
    /// Completes SAML flow: follows redirect chain to SAML IdP → Google → Classroom.
    /// </summary>
    private async Task<TokenManager.StoredSession> CompleteSamlFlow(
        HttpClient client, CookieContainer cookieContainer, string samlResumeUrl, string classroomUrl)
    {
        Console.WriteLine("[Auth] Slutför SAML-flöde...");
        var (resp, finalUrl) = await FollowRedirectsAsync(client, samlResumeUrl);
        var html = await resp.Content.ReadAsStringAsync();

        // Look for SAML form
        if (html.Contains("SAMLResponse"))
        {
            var (samlResponse, relayState, postAction) = ExtractSamlForm(html);
            return await CompleteSamlPostFlow(client, cookieContainer, samlResponse, relayState, postAction, classroomUrl);
        }

        // Maybe we're already at Google — check if we can reach Classroom
        if (finalUrl.Contains("google.com") && !finalUrl.Contains("accounts.google.com/signin"))
        {
            Console.WriteLine("[Auth] Inloggning lyckades!");
            await SaveDeviceCookie(cookieContainer);
            return BuildSession(cookieContainer);
        }

        throw new InvalidOperationException($"SAML-flöde slutade på oväntad URL: {finalUrl}");
    }

    /// <summary>
    /// Posts SAML assertion and follows through Google's auth flow.
    /// </summary>
    private async Task<TokenManager.StoredSession> CompleteSamlPostFlow(
        HttpClient client, CookieContainer cookieContainer,
        string samlResponse, string relayState, string postAction, string classroomUrl)
    {
        Console.WriteLine($"[Auth] Skickar SAML-assertion till {TruncateUrl(postAction)}...");

        var formData = new Dictionary<string, string>
        {
            ["SAMLResponse"] = samlResponse,
            ["RelayState"] = relayState
        };
        var content = new FormUrlEncodedContent(formData);
        var resp = await client.PostAsync(postAction, content);

        // Follow all redirects through Google's auth chain
        var currentUrl = postAction;
        int maxSteps = 20;
        var visitedUrls = new HashSet<string>();

        for (int i = 0; i < maxSteps; i++)
        {
            // Handle HTTP redirects
            if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found ||
                resp.StatusCode == HttpStatusCode.MovedPermanently)
            {
                currentUrl = GetRedirectUrl(resp, currentUrl);
                Console.WriteLine($"[Auth]   → {TruncateUrl(currentUrl)}");
                resp = await client.GetAsync(currentUrl);
                continue;
            }

            var html = await resp.Content.ReadAsStringAsync();

            // Handle auto-submit forms (SAML or other)
            var formResult = TryExtractAutoSubmitForm(html);
            if (formResult != null)
            {
                var formKey = $"{formResult.Value.Action}|{formResult.Value.Fields.Count}";
                if (visitedUrls.Contains(formKey))
                {
                    // We're in a loop — break out
                    Console.WriteLine("[Auth]   Loop detekterad, avbryter.");
                    // Save debug
                    var loopDebug = Path.Combine(Path.GetTempPath(), "gc_loop_debug.html");
                    await File.WriteAllTextAsync(loopDebug, html);
                    Console.WriteLine($"[Auth]   Debug sparad: {loopDebug}");
                    break;
                }
                visitedUrls.Add(formKey);

                Console.WriteLine($"[Auth]   Formulär → {TruncateUrl(formResult.Value.Action)}");
                var fd = new FormUrlEncodedContent(formResult.Value.Fields);
                resp = await client.PostAsync(formResult.Value.Action, fd);
                currentUrl = formResult.Value.Action;
                continue;
            }

            // Check for JS/meta redirects
            var redirect = FindRedirectInHtml(html);
            if (redirect != null)
            {
                Console.WriteLine($"[Auth]   JS → {TruncateUrl(redirect)}");
                resp = await client.GetAsync(redirect);
                currentUrl = redirect;
                continue;
            }

            break;
        }

        // Try to reach Google Classroom to verify login
        Console.WriteLine("[Auth] Verifierar åtkomst till Google Classroom...");
        (resp, currentUrl) = await FollowRedirectsAsync(client, classroomUrl);

        if (currentUrl.Contains("classroom.google.com") && resp.IsSuccessStatusCode)
        {
            Console.WriteLine("[Auth] Inloggning lyckades!");
        }
        else
        {
            Console.WriteLine($"[Auth] Varning: Slutade på {TruncateUrl(currentUrl)} (status {(int)resp.StatusCode})");
        }

        await SaveDeviceCookie(cookieContainer);
        return BuildSession(cookieContainer);
    }

    // --- Device cookie persistence (same as SchoolSoftDemo) ---

    private static async Task SaveDeviceCookie(CookieContainer container)
    {
        var cookies = container.GetCookies(new Uri("https://login.grandid.com"));
        foreach (Cookie cookie in cookies)
        {
            if (cookie.Name.Length == 32 && cookie.Value.Contains("Hiddenfactor"))
            {
                var data = new { Name = cookie.Name, Value = cookie.Value, SavedAt = DateTime.UtcNow };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(GrandIdCookieFile, json);
                Console.WriteLine("[Auth] Enhetscookie sparad — SMS krävs inte nästa gång!");
                return;
            }
        }
    }

    private static async Task<Cookie?> LoadDeviceCookie()
    {
        if (!File.Exists(GrandIdCookieFile))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(GrandIdCookieFile);
            using var doc = JsonDocument.Parse(json);
            var name = doc.RootElement.GetProperty("Name").GetString();
            var value = doc.RootElement.GetProperty("Value").GetString();
            if (string.IsNullOrEmpty(name)) return null;

            return new Cookie(name!, value!, "/", "login.grandid.com")
            {
                Secure = true,
                Expires = DateTime.Now.AddYears(10)
            };
        }
        catch { return null; }
    }

    // --- Auto-submit form extraction ---

    private struct AutoSubmitFormData
    {
        public string Action;
        public Dictionary<string, string> Fields;
    }

    /// <summary>
    /// Extracts any auto-submit form (SAML, Google ACS, etc.) from HTML.
    /// Returns null if no suitable form found.
    /// </summary>
    private static AutoSubmitFormData? TryExtractAutoSubmitForm(string html)
    {
        // Only consider pages that look like auto-submit forms
        // (have a form with hidden inputs and possibly auto-submit JS)
        if (!html.Contains("<form")) return null;

        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(html)).Result;

        // Find forms with POST method
        var form = document.QuerySelector("form[method='POST']")
                   ?? document.QuerySelector("form[method='post']");
        if (form == null) return null;

        var action = form.GetAttribute("action");
        if (string.IsNullOrEmpty(action)) return null;

        // Extract all hidden inputs
        var hiddenInputs = form.QuerySelectorAll("input[type='hidden']");
        if (!hiddenInputs.Any()) return null;

        var fields = new Dictionary<string, string>();
        foreach (var input in hiddenInputs)
        {
            var name = input.GetAttribute("name");
            var value = input.GetAttribute("value") ?? "";
            if (!string.IsNullOrEmpty(name))
                fields[name] = value;
        }

        if (fields.Count == 0) return null;

        return new AutoSubmitFormData { Action = action, Fields = fields };
    }

    // --- Session building ---

    private static TokenManager.StoredSession BuildSession(CookieContainer container)
    {
        var session = new TokenManager.StoredSession
        {
            SavedAt = DateTime.UtcNow,
            Cookies = new List<TokenManager.StoredCookie>()
        };

        var domains = new[]
        {
            new Uri("https://classroom.google.com"),
            new Uri("https://accounts.google.com"),
            new Uri("https://www.google.com"),
            new Uri("https://www.google.com/a/ga.ntig.se/"),
            new Uri("https://myaccount.google.com"),
            new Uri("https://google.com"),
            new Uri("https://googleapis.com"),
            new Uri("https://saml2.grandid.com"),
            new Uri("https://login.grandid.com")
        };

        var seen = new HashSet<string>();
        foreach (var uri in domains)
        {
            var cookies = container.GetCookies(uri);
            foreach (Cookie cookie in cookies)
            {
                var key = $"{cookie.Domain}|{cookie.Name}";
                if (!seen.Add(key)) continue;

                session.Cookies.Add(new TokenManager.StoredCookie
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Domain = cookie.Domain,
                    Path = cookie.Path,
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                    Expires = cookie.Expires != DateTime.MinValue
                        ? new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds()
                        : null
                });
            }
        }

        return session;
    }

    // --- SAML form parsing ---

    private static (string samlResponse, string relayState, string postAction) ExtractSamlForm(string html)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(html)).Result;

        var form = document.QuerySelector("form[action*='SAML']")
                   ?? document.QuerySelector("form[action*='saml']")
                   ?? document.QuerySelector("form[action*='Shibboleth']")
                   ?? document.QuerySelector("form[action*='acs']")
                   ?? document.QuerySelector("form[method='POST']")
                   ?? document.QuerySelector("form[method='post']");

        if (form == null)
            throw new InvalidOperationException("Kunde inte hitta SAML-formuläret.");

        var action = form.GetAttribute("action")
            ?? throw new InvalidOperationException("SAML-formuläret saknar action.");

        var samlInput = form.QuerySelector("input[name='SAMLResponse']");
        var relayInput = form.QuerySelector("input[name='RelayState']");

        var samlResponse = samlInput?.GetAttribute("value")
            ?? throw new InvalidOperationException("SAMLResponse saknas.");
        var relayState = relayInput?.GetAttribute("value") ?? "";

        return (samlResponse, relayState, action);
    }

    private static (Dictionary<string, string> Fields, string Action) ExtractLoginForm(
        string html, string currentUrl)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(html)).Result;

        var form = document.QuerySelector("form[method='POST']")
                   ?? document.QuerySelector("form[method='post']")
                   ?? document.QuerySelector("form");

        var fields = new Dictionary<string, string>();

        if (form != null)
        {
            var inputs = form.QuerySelectorAll("input[type='hidden']");
            foreach (var input in inputs)
            {
                var name = input.GetAttribute("name");
                var value = input.GetAttribute("value") ?? "";
                if (!string.IsNullOrEmpty(name))
                    fields[name] = value;
            }

            var action = form.GetAttribute("action");
            if (!string.IsNullOrEmpty(action))
            {
                if (!action.StartsWith("http"))
                    action = new Uri(new Uri(currentUrl), action).ToString();
                return (fields, action);
            }
        }

        return (fields, currentUrl);
    }

    // --- Helpers ---

    private static string? FindRedirectInHtml(string html)
    {
        // JS redirect
        var jsRedirect = Regex.Match(html,
            @"(?:window\.location|location\.href|location\.replace)\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (jsRedirect.Success)
            return System.Net.WebUtility.HtmlDecode(jsRedirect.Groups[1].Value);

        // Meta refresh
        var metaRefresh = Regex.Match(html,
            @"<meta[^>]*http-equiv=[""']?refresh[""']?[^>]*url=([^""'\s>]+)",
            RegexOptions.IgnoreCase);
        if (metaRefresh.Success)
            return System.Net.WebUtility.HtmlDecode(metaRefresh.Groups[1].Value);

        return null;
    }

    private static string GetRedirectUrl(HttpResponseMessage resp, string requestUrl)
    {
        if (resp.Headers.Location == null)
            throw new InvalidOperationException($"Förväntade redirect men fick {resp.StatusCode} utan Location.");

        var location = resp.Headers.Location;
        if (location.IsAbsoluteUri)
            return location.ToString();

        return new Uri(new Uri(requestUrl), location).ToString();
    }

    private async Task<(HttpResponseMessage Response, string FinalUrl)> FollowRedirectsAsync(
        HttpClient client, string startUrl, int maxRedirects = 20)
    {
        var url = startUrl;
        HttpResponseMessage resp;

        for (int i = 0; i < maxRedirects; i++)
        {
            resp = await client.GetAsync(url);

            if (resp.StatusCode != HttpStatusCode.Redirect &&
                resp.StatusCode != HttpStatusCode.Found &&
                resp.StatusCode != HttpStatusCode.MovedPermanently)
            {
                return (resp, url);
            }

            var newUrl = GetRedirectUrl(resp, url);
            Console.WriteLine($"[Auth]   {(int)resp.StatusCode} → {TruncateUrl(newUrl)}");
            url = newUrl;
        }

        throw new InvalidOperationException($"För många redirects (>{maxRedirects})");
    }

    private static string TruncateUrl(string url)
    {
        return url.Length > 80 ? url[..77] + "..." : url;
    }
}
