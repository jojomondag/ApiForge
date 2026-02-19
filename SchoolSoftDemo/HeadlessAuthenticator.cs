using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;

namespace SchoolSoftDemo;

/// <summary>
/// Performs headless SSO login to SchoolSoft via GrandID/AcadeMedia.
/// Saves GrandID device cookie so SMS OTP is only needed once.
/// </summary>
public class HeadlessAuthenticator
{
    private const string GrandIdCookieFile = "grandid_device.json";
    private readonly string _baseUrl;
    private readonly string _schoolSlug;

    public HeadlessAuthenticator(string baseUrl, string schoolSlug)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _schoolSlug = schoolSlug;
    }

    /// <summary>
    /// Performs the full SSO login flow and returns a StoredSession with all cookies.
    /// If a saved GrandID device cookie exists, SMS OTP is skipped.
    /// </summary>
    /// <param name="username">GrandID username</param>
    /// <param name="password">GrandID password</param>
    /// <param name="expiredSession">Optional expired session — device cookie will be extracted from it</param>
    public async Task<TokenManager.StoredSession> LoginAsync(string username, string password, TokenManager.StoredSession? expiredSession = null)
    {
        var cookieContainer = new CookieContainer();

        // Load saved GrandID device cookie (skips SMS on subsequent logins)
        // Priority: 1) grandid_device.json, 2) expired session cookies
        var savedDeviceCookie = await LoadDeviceCookie();
        if (savedDeviceCookie == null && expiredSession != null)
        {
            savedDeviceCookie = ExtractDeviceCookieFromSession(expiredSession);
            if (savedDeviceCookie != null)
            {
                Console.WriteLine("[Auth] Enhetscookie hittad i utgangen session.");
                // Also save to file so it's available even without an expired session next time
                await SaveDeviceCookieToFile(savedDeviceCookie.Name, savedDeviceCookie.Value);
            }
        }
        if (savedDeviceCookie != null)
        {
            cookieContainer.Add(new Uri("https://login.grandid.com"), savedDeviceCookie);
            Console.WriteLine($"[Auth] Enhetscookie laddad ({savedDeviceCookie.Name[..8]}...) — forsaker hoppa over SMS.");
        }

        using var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = false,
            UseCookies = true
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        // Step 1: Follow the full SSO → SAML → GrandID redirect chain
        Console.WriteLine("[Auth] Startar SSO...");
        var ssoUrl = $"{_baseUrl}/{_schoolSlug}/sso";
        var (resp, loginPageUrl) = await FollowRedirectsAsync(client, ssoUrl);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Kunde inte na inloggningssidan: HTTP {(int)resp.StatusCode}");

        // Extract sessionid from the final URL
        var sessionIdMatch = Regex.Match(loginPageUrl, @"sessionid=([a-f0-9]+)");
        if (!sessionIdMatch.Success)
            throw new InvalidOperationException("Kunde inte hitta GrandID session-ID i URL.");
        var sessionId = sessionIdMatch.Groups[1].Value;
        Console.WriteLine($"[Auth] GrandID session: {sessionId[..Math.Min(8, sessionId.Length)]}...");

        // Step 2: Parse the login page to extract all hidden form fields and action URL
        var loginHtml = await resp.Content.ReadAsStringAsync();
        var (formFields, formAction) = ExtractLoginForm(loginHtml, loginPageUrl);

        // Add user credentials
        formFields["username"] = username;
        formFields["password"] = password;

        Console.WriteLine($"[Auth] Loggar in (POST {formAction.Split('?')[0]}, {formFields.Count} falt, losenord: {password.Length} tecken)...");

        // Step 3: Submit credentials
        var loginContent = new FormUrlEncodedContent(formFields);
        resp = await client.PostAsync(formAction, loginContent);
        EnsureSuccess(resp, "skicka inloggning");

        var responseHtml = await resp.Content.ReadAsStringAsync();
        if (responseHtml.Contains("Felaktigt anv") || responseHtml.Contains("incorrect") ||
            responseHtml.Contains("angivit fel") ||
            (responseHtml.Contains("error") && responseHtml.Contains("password")))
        {
            Console.WriteLine($"[Auth] Inloggning nekad av GrandID. Anvandarnamn: '{username}', Losenord: {password.Length} tecken");
            throw new InvalidOperationException("Inloggning misslyckades — felaktigt anvandarnamn eller losenord.");
        }

        // Check if we got a direct redirect to SAML (2FA completely skipped)
        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            Console.WriteLine("[Auth] 2FA hoppades over (kand enhet)!");
            var redirectUrl = GetRedirectUrl(resp, loginPageUrl);
            return await CompleteSamlFlow(client, cookieContainer, redirectUrl);
        }

        // Check response: does it show 2FA choice or direct SAML form?
        if (responseHtml.Contains("2fa-hidden") || responseHtml.Contains("2fa-otp"))
        {
            // Try the 2FA-hidden path first — the device cookie may work server-side
            // even though the page shows the 2FA form.
            Console.WriteLine("[Auth] 2FA-sida visas, forsoker med enhetscookie...");
            var smsUrl = $"https://login.grandid.com/?sessionid={sessionId}&2fa-hidden=1";
            (resp, var hiddenFinalUrl) = await FollowRedirectsAsync(client, smsUrl);
            var smsHtml = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[Auth] 2FA-svar: HTTP {(int)resp.StatusCode}, {smsHtml.Length} tecken");
            Console.WriteLine($"[Auth] 2FA slutlig URL: {hiddenFinalUrl}");

            // Dump for debugging
            var debugHiddenPath = Path.Combine(Path.GetTempPath(), "grandid_2fa_hidden_debug.html");
            await File.WriteAllTextAsync(debugHiddenPath, smsHtml);
            Console.WriteLine($"[Auth] 2FA-svar sparat: {debugHiddenPath}");

            // Check if the 2FA request completed auth:
            // 1. Response contains SAMLResponse form
            // 2. Final URL is SAML resume endpoint
            // 3. Response contains JS redirect to SAML/resume
            // 4. Response contains auto-submit form pointing to SAML endpoint
            var smsFinalUrl = hiddenFinalUrl;
            bool deviceCookieWorked =
                smsHtml.Contains("SAMLResponse") ||
                smsFinalUrl.Contains("resume.php") ||
                smsFinalUrl.Contains("saml2.grandid.com") ||
                Regex.IsMatch(smsHtml, @"(?:window\.location|location\.href|location\.replace)\s*=\s*[""'][^""']*(?:resume|saml|Shibboleth)", RegexOptions.IgnoreCase) ||
                (smsHtml.Contains("<form") && Regex.IsMatch(smsHtml, @"action=[""'][^""']*(?:resume|saml|Shibboleth)", RegexOptions.IgnoreCase));

            if (deviceCookieWorked)
            {
                // Device cookie worked! Auth completed without SMS.
                Console.WriteLine("[Auth] Enhetscookien fungerade — SMS hoppades over!");
                responseHtml = smsHtml;
                // Don't delete the device cookie — it's still valid
            }
            else
            {
                // Device cookie didn't help — delete it and ask for SMS
                if (savedDeviceCookie != null)
                {
                    Console.WriteLine("[Auth] OBS: Enhetscookien fungerade inte. Raderar den.");
                    try { File.Delete(GrandIdCookieFile); } catch { }
                }

                Console.Write("[Auth] Ange SMS-kod (eller 'r' for att skicka om): ");
                var otpCode = Console.ReadLine()?.Trim();

                // Retry SMS
                if (otpCode?.ToLower() == "r")
                {
                    Console.WriteLine("[Auth] Skickar om SMS...");
                    (resp, _) = await FollowRedirectsAsync(client, smsUrl);
                    Console.WriteLine($"[Auth] Omsandning: HTTP {(int)resp.StatusCode}");
                    Console.Write("[Auth] Ange SMS-kod: ");
                    otpCode = Console.ReadLine()?.Trim();
                }

                if (string.IsNullOrEmpty(otpCode))
                    throw new InvalidOperationException("Ingen SMS-kod angiven.");

                var otpContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["fc"] = "",
                    ["grandidsession"] = sessionId,
                    ["otp"] = otpCode
                });
                var otpPostUrl = $"https://login.grandid.com/?sessionid={sessionId}";
                resp = await client.PostAsync(otpPostUrl, otpContent);
                EnsureSuccess(resp, "verifiera SMS-kod");

                responseHtml = await resp.Content.ReadAsStringAsync();
                if (responseHtml.Contains("Felaktig") || responseHtml.Contains("Ogiltig"))
                {
                    throw new InvalidOperationException("Felaktig SMS-kod.");
                }

                // Approve browser — saves device cookie for next time (no more SMS!)
                Console.WriteLine("[Auth] Kopplar enheten (slipper SMS nasta gang)...");
                var approveUrl = $"https://login.grandid.com/?sessionid={sessionId}&approveBrowser=true";
                (resp, _) = await FollowRedirectsAsync(client, approveUrl);
                Console.WriteLine($"[Auth] approveBrowser svar: HTTP {(int)resp.StatusCode}");
            }
        }
        else if (responseHtml.Contains("Koppla webbl"))
        {
            // OTP was skipped, just the browser approval page
            Console.WriteLine("[Auth] SMS hoppades over! Kopplar enheten...");
            var approveUrl = $"https://login.grandid.com/?sessionid={sessionId}&approveBrowser=true";
            (resp, _) = await FollowRedirectsAsync(client, approveUrl);
            Console.WriteLine($"[Auth] approveBrowser svar: HTTP {(int)resp.StatusCode}");
        }

        // Save the GrandID device cookie for future logins
        await SaveDeviceCookie(cookieContainer);

        // Try to complete SAML flow from whatever response we have
        return await ResolveSamlFromResponse(client, cookieContainer, resp, sessionId);
    }

    /// <summary>
    /// Tries multiple strategies to find and follow the SAML redirect/form from a GrandID response.
    /// </summary>
    private async Task<TokenManager.StoredSession> ResolveSamlFromResponse(
        HttpClient client, CookieContainer cookieContainer, HttpResponseMessage resp, string sessionId)
    {
        // Case 1: HTTP redirect
        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            var samlResumeUrl = GetRedirectUrl(resp, $"https://login.grandid.com/?sessionid={sessionId}");
            return await CompleteSamlFlow(client, cookieContainer, samlResumeUrl);
        }

        var html = await resp.Content.ReadAsStringAsync();

        // Case 2: Response contains SAML form directly
        if (html.Contains("SAMLResponse"))
        {
            Console.WriteLine("[Auth] SAML-svar hittat direkt i svaret...");
            var (samlResponse, relayState, postAction) = ExtractSamlForm(html);
            return await CompleteSamlPostFlow(client, cookieContainer, samlResponse, relayState, postAction);
        }

        // Case 3: JavaScript redirect (window.location, location.href, etc.)
        var jsRedirect = Regex.Match(html, @"(?:window\.location|location\.href|location\.replace)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (jsRedirect.Success)
        {
            Console.WriteLine("[Auth] Foljer JS-redirect...");
            var url = jsRedirect.Groups[1].Value;
            resp = await client.GetAsync(url);
            return await ResolveSamlFromResponse(client, cookieContainer, resp, sessionId);
        }

        // Case 4: Meta-refresh redirect
        var metaRefresh = Regex.Match(html, @"<meta[^>]*http-equiv=[""']?refresh[""']?[^>]*url=([^""'\s>]+)", RegexOptions.IgnoreCase);
        if (metaRefresh.Success)
        {
            Console.WriteLine("[Auth] Foljer meta-refresh...");
            var url = metaRefresh.Groups[1].Value;
            resp = await client.GetAsync(url);
            return await ResolveSamlFromResponse(client, cookieContainer, resp, sessionId);
        }

        // Case 5: Form with auto-submit (common in SAML flows)
        var formAction = Regex.Match(html, @"<form[^>]*action=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (formAction.Success)
        {
            Console.WriteLine("[Auth] Foljer form-action...");
            var url = formAction.Groups[1].Value;
            resp = await client.GetAsync(url);
            return await ResolveSamlFromResponse(client, cookieContainer, resp, sessionId);
        }

        // Case 6: Link to saml/shibboleth endpoint
        var samlLink = Regex.Match(html, @"href=[""']([^""']*(?:saml|Shibboleth|sso)[^""']*)[""']", RegexOptions.IgnoreCase);
        if (samlLink.Success)
        {
            Console.WriteLine("[Auth] Foljer SAML-lank...");
            var url = samlLink.Groups[1].Value;
            resp = await client.GetAsync(url);
            return await ResolveSamlFromResponse(client, cookieContainer, resp, sessionId);
        }

        // No pattern matched — dump full response for debugging
        var debugPath = Path.Combine(Path.GetTempPath(), "grandid_debug_response.html");
        await File.WriteAllTextAsync(debugPath, html);
        Console.WriteLine($"[Auth] Sparat svaret till: {debugPath}");
        throw new InvalidOperationException(
            $"Kunde inte hitta SAML-redirect efter inloggning (status {resp.StatusCode}). " +
            $"Svaret ({html.Length} tecken) sparat till {debugPath}");
    }

    /// <summary>
    /// Completes the SAML flow from an already-extracted SAML form.
    /// </summary>
    private async Task<TokenManager.StoredSession> CompleteSamlPostFlow(
        HttpClient client, CookieContainer cookieContainer,
        string samlResponse, string relayState, string postAction)
    {
        Console.WriteLine("[Auth] Skickar SAML-assertion till SchoolSoft...");
        var samlBody = $"SAMLResponse={Uri.EscapeDataString(samlResponse)}&RelayState={Uri.EscapeDataString(relayState)}";
        var samlContent = new StringContent(samlBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        var resp = await client.PostAsync(postAction, samlContent);
        if (resp.StatusCode != HttpStatusCode.Redirect && resp.StatusCode != HttpStatusCode.Found)
            throw new InvalidOperationException($"Shibboleth svarade med {resp.StatusCode} istallet for redirect.");

        var samlLoginRedirect = GetRedirectUrl(resp, postAction);
        resp = await client.GetAsync(samlLoginRedirect);

        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            var startPageUrl = GetRedirectUrl(resp, samlLoginRedirect);
            resp = await client.GetAsync(startPageUrl);
        }

        Console.WriteLine("[Auth] Inloggning lyckades!");
        await SaveDeviceCookie(cookieContainer);
        return BuildSession(cookieContainer);
    }

    /// <summary>
    /// Completes the SAML flow: resume → extract assertion → POST to Shibboleth → session.
    /// </summary>
    private async Task<TokenManager.StoredSession> CompleteSamlFlow(
        HttpClient client, CookieContainer cookieContainer, string samlResumeUrl)
    {
        // Step 9: SAML resume → get SAMLResponse form
        Console.WriteLine("[Auth] Slutfor autentisering...");
        var resp = await client.GetAsync(samlResumeUrl);
        EnsureSuccess(resp, "hamta SAML-svar");

        var samlPageHtml = await resp.Content.ReadAsStringAsync();
        var (samlResponse, relayState, postAction) = ExtractSamlForm(samlPageHtml);

        // Step 10: POST SAML assertion to Shibboleth SP
        Console.WriteLine("[Auth] Skickar SAML-assertion till SchoolSoft...");
        var samlBody = $"SAMLResponse={Uri.EscapeDataString(samlResponse)}&RelayState={Uri.EscapeDataString(relayState)}";
        var samlContent = new StringContent(samlBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        resp = await client.PostAsync(postAction, samlContent);
        if (resp.StatusCode != HttpStatusCode.Redirect && resp.StatusCode != HttpStatusCode.Found)
            throw new InvalidOperationException($"Shibboleth svarade med {resp.StatusCode} istallet for redirect.");

        // Step 11: Follow redirect to samlLogin.jsp → gets JSESSIONID
        var samlLoginRedirect = GetRedirectUrl(resp, postAction);
        resp = await client.GetAsync(samlLoginRedirect);

        // Step 12: Follow redirect to startpage (if 302)
        if (resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.Found)
        {
            var startPageUrl = GetRedirectUrl(resp, samlLoginRedirect);
            resp = await client.GetAsync(startPageUrl);
        }

        Console.WriteLine("[Auth] Inloggning lyckades!");

        // Save device cookie after successful login
        await SaveDeviceCookie(cookieContainer);

        return BuildSession(cookieContainer);
    }

    // --- Device cookie persistence ---

    /// <summary>
    /// Extracts the GrandID device cookie from a stored session (e.g. expired browser session).
    /// </summary>
    private static Cookie? ExtractDeviceCookieFromSession(TokenManager.StoredSession session)
    {
        var dc = session.Cookies.FirstOrDefault(c =>
            c.Domain.Contains("login.grandid.com") &&
            c.Name.Length == 32 &&
            c.Value.Contains("Hiddenfactor"));

        if (dc == null) return null;

        return new Cookie(dc.Name, dc.Value, "/", "login.grandid.com")
        {
            Secure = true,
            Expires = DateTime.Now.AddYears(10)
        };
    }

    /// <summary>
    /// Checks if a stored session contains a GrandID device cookie.
    /// </summary>
    public static bool HasDeviceCookie(TokenManager.StoredSession? session)
    {
        if (session == null) return false;

        // Check session cookies
        if (session.Cookies.Any(c =>
            c.Domain.Contains("login.grandid.com") &&
            c.Name.Length == 32 &&
            c.Value.Contains("Hiddenfactor")))
            return true;

        // Check grandid_device.json
        return File.Exists(GrandIdCookieFile);
    }

    private static async Task SaveDeviceCookieToFile(string name, string value)
    {
        var data = new DeviceCookieData
        {
            Name = name,
            Value = value,
            SavedAt = DateTime.UtcNow
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(GrandIdCookieFile, json);
        Console.WriteLine($"[Auth] Enhetscookie sparad till {Path.GetFullPath(GrandIdCookieFile)}");
    }

    private static async Task SaveDeviceCookie(CookieContainer container)
    {
        var cookies = container.GetCookies(new Uri("https://login.grandid.com"));
        Console.WriteLine($"[Auth] Soker enhetscookie bland {cookies.Count} cookies for login.grandid.com:");
        foreach (Cookie cookie in cookies)
        {
            Console.WriteLine($"[Auth]   {cookie.Name} ({cookie.Name.Length} tecken) = {cookie.Value[..Math.Min(30, cookie.Value.Length)]}...");

            // The hidden factor cookie has a hash-like name and expires far in the future
            if (cookie.Name.Length == 32 && cookie.Value.Contains("Hiddenfactor"))
            {
                await SaveDeviceCookieToFile(cookie.Name, cookie.Value);
                Console.WriteLine("[Auth] SMS kravs inte nasta gang!");
                return;
            }
        }
        Console.WriteLine("[Auth] VARNING: Ingen enhetscookie hittades! SMS kravs fortfarande nasta gang.");
    }

    private static async Task<Cookie?> LoadDeviceCookie()
    {
        if (!File.Exists(GrandIdCookieFile))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(GrandIdCookieFile);
            var data = JsonSerializer.Deserialize<DeviceCookieData>(json);
            if (data == null || string.IsNullOrEmpty(data.Name))
                return null;

            return new Cookie(data.Name, data.Value, "/", "login.grandid.com")
            {
                Secure = true,
                Expires = DateTime.Now.AddYears(10)
            };
        }
        catch
        {
            return null;
        }
    }

    private class DeviceCookieData
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public DateTime SavedAt { get; set; }
    }

    // --- SAML form parsing ---

    private static (string samlResponse, string relayState, string postAction) ExtractSamlForm(string html)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(html)).Result;

        var form = document.QuerySelector("form[action*='Shibboleth']")
                   ?? document.QuerySelector("form[action*='SAML2']")
                   ?? document.QuerySelector("form[method='post']");

        if (form == null)
            throw new InvalidOperationException("Kunde inte hitta SAML-formularet i svaret.");

        var action = form.GetAttribute("action")
            ?? throw new InvalidOperationException("SAML-formularet saknar action-attribut.");

        var samlInput = form.QuerySelector("input[name='SAMLResponse']");
        var relayInput = form.QuerySelector("input[name='RelayState']");

        var samlResponse = samlInput?.GetAttribute("value")
            ?? throw new InvalidOperationException("SAMLResponse saknas i formularet.");
        var relayState = relayInput?.GetAttribute("value") ?? "";

        return (samlResponse, relayState, action);
    }

    // --- Session building ---

    private TokenManager.StoredSession BuildSession(CookieContainer container)
    {
        var session = new TokenManager.StoredSession
        {
            SavedAt = DateTime.UtcNow,
            SchoolSoftBaseUrl = _baseUrl,
            SchoolPath = _schoolSlug,
            Cookies = new List<TokenManager.StoredCookie>()
        };

        var domains = new[]
        {
            new Uri(_baseUrl),
            new Uri($"{_baseUrl}/{_schoolSlug}/"),
            new Uri($"{_baseUrl}/Shibboleth.sso/"),
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
                if (!seen.Add(key))
                    continue;

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

    // --- Helpers ---

    private static string GetRedirectUrl(HttpResponseMessage resp, string requestUrl)
    {
        if (resp.Headers.Location == null)
            throw new InvalidOperationException($"Forventade redirect fran {requestUrl} men fick {resp.StatusCode} utan Location-header.");

        var location = resp.Headers.Location;
        if (location.IsAbsoluteUri)
            return location.ToString();

        var baseUri = new Uri(requestUrl);
        return new Uri(baseUri, location).ToString();
    }

    private static void EnsureSuccess(HttpResponseMessage resp, string step)
    {
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Redirect && resp.StatusCode != HttpStatusCode.Found)
            throw new InvalidOperationException($"Misslyckades med att {step}: HTTP {(int)resp.StatusCode}");
    }

    /// <summary>
    /// Follows all HTTP redirects manually until a non-redirect response is received.
    /// </summary>
    private async Task<(HttpResponseMessage Response, string FinalUrl)> FollowRedirectsAsync(
        HttpClient client, string startUrl, int maxRedirects = 15)
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
            var shortUrl = newUrl.Length > 80 ? newUrl[..77] + "..." : newUrl;
            Console.WriteLine($"[Auth]   {(int)resp.StatusCode} -> {shortUrl}");
            url = newUrl;
        }

        throw new InvalidOperationException($"For manga redirects (>{maxRedirects}) fran {startUrl}");
    }

    /// <summary>
    /// Parses the login page HTML to extract all hidden form fields and the form action URL.
    /// </summary>
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
            // Extract all hidden inputs
            var inputs = form.QuerySelectorAll("input[type='hidden']");
            foreach (var input in inputs)
            {
                var name = input.GetAttribute("name");
                var value = input.GetAttribute("value") ?? "";
                if (!string.IsNullOrEmpty(name))
                    fields[name] = value;
            }

            // Determine action URL
            var action = form.GetAttribute("action");
            if (!string.IsNullOrEmpty(action))
            {
                if (!action.StartsWith("http"))
                {
                    var baseUri = new Uri(currentUrl);
                    action = new Uri(baseUri, action).ToString();
                }
                return (fields, action);
            }
        }

        // No action = POST to current URL (HTML default)
        return (fields, currentUrl);
    }
}
