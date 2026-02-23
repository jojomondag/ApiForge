using System.Text;
using ApiForge;
using Microsoft.Playwright;
using StudyBeeDemo;

Console.OutputEncoding = Encoding.UTF8;
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });

const string harFile = "studybee.har";
const string cookieFile = "studybee_cookies.json";
const string baseUrl = "https://insights.studybee.io";
// Validation URL — the main page should work if authenticated
const string validationUrl = baseUrl;

Console.WriteLine("=== Studybee API-utforskare ===");
Console.WriteLine($"    URL: {baseUrl}");
Console.WriteLine($"    Auth: Google Authentication");
Console.WriteLine();

// Kolla om vi har en sparad session
var existingSession = await TokenManager.LoadSession();
bool sessionValid = false;

if (existingSession != null)
{
    Console.WriteLine("Hittade sparad session:");
    TokenManager.PrintSessionInfo(existingSession);
    Console.WriteLine();

    if (TokenManager.IsSessionValid(existingSession))
    {
        Console.WriteLine("Testar sessionen mot Studybee...");
        var onlineResult = await TokenManager.ValidateSessionOnline(existingSession, validationUrl);

        Console.WriteLine($"  HTTP-status: {onlineResult.HttpStatus?.ToString() ?? "N/A"}");
        if (onlineResult.RedirectUrl != null)
            Console.WriteLine($"  Slutlig URL: {onlineResult.RedirectUrl}");
        Console.WriteLine($"  Resultat: {onlineResult.Reason}");
        if (onlineResult.BodySnippet != null)
            Console.WriteLine($"  Svar (utdrag): {onlineResult.BodySnippet}");
        if (onlineResult.ErrorMessage != null)
            Console.WriteLine($"  Fel: {onlineResult.ErrorMessage}");

        if (onlineResult.IsValid)
        {
            Console.WriteLine("Session GILTIG — du ar fortfarande inloggad!");
            sessionValid = true;
        }
        else
        {
            bool serverRejected = onlineResult.Reason.Contains("inloggning") ||
                                  onlineResult.Reason.Contains("google.com");

            if (serverRejected)
            {
                Console.WriteLine("Sessionen har gatt ut. Ny inloggning kravs via webblasare.");
            }
            else
            {
                var sessionAge = DateTime.UtcNow - existingSession.SavedAt;
                if (sessionAge.TotalHours < 6)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Online-validering misslyckades (oklar orsak), men sessionen ar bara {sessionAge.Hours}h {sessionAge.Minutes}m gammal.");
                    Console.Write("Vill du forsoka anvanda sessionen anda? (j/n): ");
                    var tryAnyway = Console.ReadLine()?.Trim().ToLower();
                    if (tryAnyway is "j" or "ja" or "y" or "yes")
                    {
                        Console.WriteLine("Anvander befintlig session.");
                        sessionValid = true;
                    }
                    else
                    {
                        Console.WriteLine("OK, ny inloggning kravs.");
                    }
                }
                else
                {
                    Console.WriteLine("Sessionen har troligen gatt ut online.");
                }
            }
        }
    }
    else
    {
        Console.WriteLine("Sessionen har gatt ut (lokalt).");
    }
    Console.WriteLine();
}

// Om sessionen inte ar giltig, logga in via webblasare
if (!sessionValid)
{
    Console.WriteLine("Inloggning kravs. Studybee anvander Google Authentication.");
    Console.WriteLine("  En webblasare oppnas dar du loggar in med ditt Google-konto.");
    Console.WriteLine();
    Console.Write("Tryck Enter for att oppna webblasaren...");
    Console.ReadLine();
    Console.WriteLine();

    Console.WriteLine("Kontrollerar Playwright Chromium...");
    var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
    if (exitCode != 0)
    {
        Console.WriteLine("Kunde inte installera Chromium. Avbryter.");
        return;
    }
    Console.WriteLine("Chromium redo!");
    Console.WriteLine();

    Console.WriteLine("Spelar in webblasartrafik...");
    Console.WriteLine($"  Webblasaren oppnas nu pa: {baseUrl}");
    Console.WriteLine("  1. Logga in med ditt Google-konto");
    Console.WriteLine("  2. Vanta tills du ser Studybee-dashboarden");
    Console.WriteLine("  3. Navigera runt lite (klicka pa olika sidor/menyer)");
    Console.WriteLine("  4. Tryck Enter har i konsolen nar du ar klar");
    Console.WriteLine();

    var recorder = new ApiForgeClient();
    await recorder.RecordHarAsync(harFile, cookieFile, startUrl: baseUrl);

    Console.WriteLine();
    Console.WriteLine($"Inspelning sparad: {harFile} + {cookieFile}");
    Console.WriteLine();

    Console.WriteLine("Extraherar session-tokens...");
    existingSession = await TokenManager.ExtractAndSaveFromCookieFile(cookieFile);
    sessionValid = true;

    // Spara och verifiera
    Console.WriteLine("Session sparad!");
    TokenManager.PrintSessionInfo(existingSession);
    Console.WriteLine();

    Console.WriteLine("Verifierar sessionen...");
    var verifyResult = await TokenManager.ValidateSessionOnline(existingSession, validationUrl);

    Console.WriteLine($"  HTTP-status: {verifyResult.HttpStatus?.ToString() ?? "N/A"}");
    if (verifyResult.RedirectUrl != null)
        Console.WriteLine($"  Slutlig URL: {verifyResult.RedirectUrl}");
    Console.WriteLine($"  Resultat: {verifyResult.Reason}");
    if (verifyResult.ErrorMessage != null)
        Console.WriteLine($"  Fel: {verifyResult.ErrorMessage}");

    if (verifyResult.IsValid)
    {
        Console.WriteLine("Session verifierad — tokens fungerar!");
    }
    else
    {
        Console.WriteLine("VARNING: Kunde inte verifiera sessionen online.");
        Console.WriteLine("Forsaker anvanda sessionen anda (den ar precis skapad)...");
    }
    Console.WriteLine();
}

// === Huvudmeny ===
bool hasHarFile = File.Exists(harFile);

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== Studybee - Huvudmeny ===");
    Console.WriteLine("  1. Spela in nytt flode (webblasare + HAR-inspelning)");
    if (hasHarFile)
        Console.WriteLine("  2. Analysera HAR-fil med LLM (hitta API-endpoints)");
    if (hasHarFile)
        Console.WriteLine("  3. Visa HAR-sammanfattning (requests/responses)");
    Console.WriteLine("  4. Testa en API-endpoint manuellt");
    Console.WriteLine("  5. Spela in med befintlig session (injicera cookies)");
    Console.WriteLine("  q. Avsluta");
    Console.Write("Val: ");
    var choice = Console.ReadLine()?.Trim().ToLower();
    Console.WriteLine();

    if (choice is "q" or "quit" or "avsluta")
        break;

    switch (choice)
    {
        case "1":
            await RecordNewFlow(null);
            hasHarFile = File.Exists(harFile);
            break;

        case "2" when hasHarFile:
            RunAnalysis();
            break;

        case "3" when hasHarFile:
            await ShowHarSummary();
            break;

        case "4":
            await TestEndpoint();
            break;

        case "5":
            await RecordNewFlow(existingSession);
            hasHarFile = File.Exists(harFile);
            break;

        default:
            Console.WriteLine("Ogiltigt val.");
            break;
    }
}

// === Funktioner ===

async Task RecordNewFlow(TokenManager.StoredSession? session)
{
    Console.WriteLine("Kontrollerar Playwright Chromium...");
    var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
    if (exitCode != 0)
    {
        Console.WriteLine("Kunde inte installera Chromium. Avbryter.");
        return;
    }

    Console.Write($"Vilken URL vill du borja pa? (Enter = {baseUrl}): ");
    var startInput = Console.ReadLine()?.Trim();
    var recordStartUrl = string.IsNullOrEmpty(startInput) ? baseUrl : startInput;

    Console.WriteLine();
    Console.WriteLine("Spelar in webblasartrafik...");
    Console.WriteLine($"  Webblasaren oppnas nu pa: {recordStartUrl}");
    Console.WriteLine("  1. Navigera till det du vill fanga (API-anrop, sidor, etc.)");
    Console.WriteLine("  2. Vanta tills sidan har laddats klart");
    Console.WriteLine("  3. Tryck Enter har i konsolen");
    Console.WriteLine();

    List<Cookie>? playwrightCookies = null;

    if (session != null)
    {
        playwrightCookies = session.Cookies
            .Where(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Domain))
            .Select(c => new Cookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain.StartsWith('.') ? c.Domain : c.Domain,
                Path = c.Path ?? "/",
                Secure = c.Secure,
                HttpOnly = c.HttpOnly,
                Expires = c.Expires.HasValue ? (float)c.Expires.Value : -1
            })
            .ToList();

        Console.WriteLine($"  Injicerar {playwrightCookies.Count} session-cookies i webblasaren...");
    }

    var recorder = new ApiForgeClient();
    await recorder.RecordHarAsync(harFile, cookieFile, startUrl: recordStartUrl, cookies: playwrightCookies);

    Console.WriteLine($"Inspelning sparad: {harFile}");
    Console.WriteLine();

    // Update cookies from recording
    existingSession = await TokenManager.ExtractAndSaveFromCookieFile(cookieFile);

    Console.Write("Vill du analysera HAR-filen nu? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() is "j" or "ja")
    {
        RunAnalysis();
    }
}

void RunAnalysis()
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    var endpoint = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
    var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";

    if (string.IsNullOrEmpty(endpoint) && string.IsNullOrEmpty(apiKey))
    {
        endpoint = "http://127.0.0.1:1234/v1";
        model = "openai/gpt-oss-20b";
        apiKey = "lm-studio";
        Console.WriteLine($"Anvander lokal LLM: {model} pa {endpoint}");
    }
    else if (!string.IsNullOrEmpty(endpoint))
    {
        Console.WriteLine($"Anvander custom endpoint: {endpoint} med modell: {model}");
    }
    else
    {
        Console.WriteLine($"Anvander OpenAI: {model}");
    }

    Console.WriteLine();
    Console.Write("Vad vill du analysera? (Enter = 'hitta alla API-endpoints'): ");
    var promptInput = Console.ReadLine()?.Trim();
    var prompt = string.IsNullOrEmpty(promptInput)
        ? "Find all API endpoints, their parameters, authentication requirements, and data structures. Focus on REST API calls and GraphQL queries."
        : promptInput;

    Console.WriteLine();
    Console.WriteLine($"Analyserar med prompt: \"{prompt}\"");
    Console.WriteLine();

    var analyzeClient = new ApiForgeClient(openAiApiKey: apiKey, model: model, endpoint: endpoint);
    var result = analyzeClient.AnalyzeAsync(
        prompt: prompt,
        harFilePath: harFile,
        cookiePath: cookieFile,
        generateCode: true,
        maxSteps: 25
    ).GetAwaiter().GetResult();

    Console.WriteLine();
    Console.WriteLine("=== Resultat ===");
    Console.WriteLine($"Master Node ID: {result.MasterNodeId}");
    Console.WriteLine($"DAG-struktur:");
    Console.WriteLine(result.DagManager.ToString());
    Console.WriteLine();
}

async Task ShowHarSummary()
{
    if (!File.Exists(harFile))
    {
        Console.WriteLine("Ingen HAR-fil hittad. Spela in ett flode forst.");
        return;
    }

    var json = await File.ReadAllTextAsync(harFile);
    var harData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

    if (!harData.TryGetProperty("log", out var log) || !log.TryGetProperty("entries", out var entries))
    {
        Console.WriteLine("Kunde inte parsa HAR-filen.");
        return;
    }

    var entryList = new List<(string method, string url, int status, string mimeType, long size)>();

    foreach (var entry in entries.EnumerateArray())
    {
        var request = entry.GetProperty("request");
        var response = entry.GetProperty("response");
        var method = request.GetProperty("method").GetString() ?? "?";
        var url = request.GetProperty("url").GetString() ?? "?";
        var status = response.GetProperty("status").GetInt32();
        var mimeType = "";
        var size = 0L;

        if (response.TryGetProperty("content", out var content))
        {
            mimeType = content.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "";
            size = content.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
        }

        entryList.Add((method, url, status, mimeType, size));
    }

    Console.WriteLine($"=== HAR-sammanfattning ({entryList.Count} requests) ===");
    Console.WriteLine();

    // Filter options
    Console.WriteLine("Filter:");
    Console.WriteLine("  1. Visa alla");
    Console.WriteLine("  2. Bara API-anrop (XHR/JSON)");
    Console.WriteLine("  3. Bara Studybee-domaner");
    Console.Write("Val (Enter = 2): ");
    var filterChoice = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(filterChoice)) filterChoice = "2";
    Console.WriteLine();

    var filtered = entryList.AsEnumerable();
    if (filterChoice == "2")
    {
        filtered = filtered.Where(e =>
            e.mimeType.Contains("json") ||
            e.mimeType.Contains("graphql") ||
            e.url.Contains("/api/") ||
            e.url.Contains("/graphql") ||
            e.url.Contains("/v1/") ||
            e.url.Contains("/v2/"));
    }
    else if (filterChoice == "3")
    {
        filtered = filtered.Where(e => e.url.Contains("studybee.io"));
    }

    var results = filtered.ToList();
    Console.WriteLine($"{"#",-4} {"Method",-8} {"Status",-7} {"MIME",-25} {"URL"}");
    Console.WriteLine(new string('-', 100));

    for (int i = 0; i < results.Count; i++)
    {
        var (method, url, status, mimeType, size) = results[i];
        // Truncate URL for display
        var displayUrl = url.Length > 80 ? url[..77] + "..." : url;
        var displayMime = mimeType.Length > 24 ? mimeType[..21] + "..." : mimeType;
        Console.WriteLine($"{i + 1,-4} {method,-8} {status,-7} {displayMime,-25} {displayUrl}");
    }

    Console.WriteLine();
    Console.WriteLine($"Totalt: {results.Count} requests (av {entryList.Count})");

    // Detail view
    while (true)
    {
        Console.WriteLine();
        Console.Write($"Visa detaljer for request (1-{results.Count}), eller Enter for att ga tillbaka: ");
        var detailInput = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(detailInput)) break;

        if (!int.TryParse(detailInput, out var idx) || idx < 1 || idx > results.Count)
        {
            Console.WriteLine("Ogiltigt val.");
            continue;
        }

        var selected = results[idx - 1];
        Console.WriteLine();
        Console.WriteLine($"=== Request #{idx} ===");
        Console.WriteLine($"  Method: {selected.method}");
        Console.WriteLine($"  URL:    {selected.url}");
        Console.WriteLine($"  Status: {selected.status}");
        Console.WriteLine($"  MIME:   {selected.mimeType}");
        Console.WriteLine($"  Size:   {selected.size} bytes");

        // Find the full entry in the HAR for headers and body
        var fullEntry = entries.EnumerateArray().ElementAt(entryList.IndexOf(selected));
        var reqHeaders = fullEntry.GetProperty("request");
        var respData = fullEntry.GetProperty("response");

        // Request headers
        if (reqHeaders.TryGetProperty("headers", out var headers))
        {
            Console.WriteLine("  Request Headers:");
            foreach (var h in headers.EnumerateArray())
            {
                var name = h.GetProperty("name").GetString() ?? "";
                var value = h.GetProperty("value").GetString() ?? "";
                // Skip noisy headers
                if (name.StartsWith(":") || name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                    continue;
                Console.WriteLine($"    {name}: {(value.Length > 80 ? value[..77] + "..." : value)}");
            }
        }

        // Request body
        if (reqHeaders.TryGetProperty("postData", out var postData))
        {
            var bodyText = postData.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(bodyText))
            {
                Console.WriteLine($"  Request Body ({bodyText.Length} chars):");
                var displayBody = bodyText.Length > 500 ? bodyText[..500] + "\n    ... (truncated)" : bodyText;
                foreach (var line in displayBody.Split('\n'))
                    Console.WriteLine($"    {line}");
            }
        }

        // Response body
        if (respData.TryGetProperty("content", out var respContent) &&
            respContent.TryGetProperty("text", out var respText))
        {
            var bodyText = respText.GetString() ?? "";
            if (!string.IsNullOrEmpty(bodyText))
            {
                Console.WriteLine($"  Response Body ({bodyText.Length} chars):");
                var displayBody = bodyText.Length > 500 ? bodyText[..500] + "\n    ... (truncated)" : bodyText;
                foreach (var line in displayBody.Split('\n'))
                    Console.WriteLine($"    {line}");
            }
        }
    }
}

async Task TestEndpoint()
{
    if (existingSession == null || !sessionValid)
    {
        Console.WriteLine("Ingen giltig session. Logga in forst.");
        return;
    }

    Console.Write("URL att testa (full URL): ");
    var url = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(url))
    {
        Console.WriteLine("Ingen URL angiven.");
        return;
    }

    Console.Write("HTTP-metod (Enter = GET): ");
    var methodInput = Console.ReadLine()?.Trim().ToUpper();
    var method = string.IsNullOrEmpty(methodInput) ? "GET" : methodInput;

    string? body = null;
    if (method is "POST" or "PUT" or "PATCH")
    {
        Console.Write("Request body (JSON, eller Enter for tom): ");
        body = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(body)) body = null;
    }

    Console.WriteLine();
    Console.WriteLine($"Testar {method} {url}...");

    try
    {
        using var client = TokenManager.CreateAuthenticatedClient(existingSession);
        client.Timeout = TimeSpan.FromSeconds(30);

        HttpResponseMessage response;
        if (method == "GET")
        {
            response = await client.GetAsync(url);
        }
        else
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            response = await client.SendAsync(request);
        }

        Console.WriteLine($"  Status: {(int)response.StatusCode} {response.StatusCode}");

        // Response headers
        Console.WriteLine("  Response Headers:");
        foreach (var header in response.Headers)
        {
            Console.WriteLine($"    {header.Key}: {string.Join(", ", header.Value)}");
        }
        foreach (var header in response.Content.Headers)
        {
            Console.WriteLine($"    {header.Key}: {string.Join(", ", header.Value)}");
        }

        // Response body
        var responseBody = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"  Response Body ({responseBody.Length} chars):");

        // Try to pretty-print JSON
        try
        {
            var jsonDoc = System.Text.Json.JsonDocument.Parse(responseBody);
            var prettyJson = System.Text.Json.JsonSerializer.Serialize(jsonDoc, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            var lines = prettyJson.Split('\n');
            var displayLines = lines.Length > 50 ? lines.Take(50).Append("    ... (truncated)") : lines;
            foreach (var line in displayLines)
                Console.WriteLine($"    {line}");
        }
        catch
        {
            // Not JSON, display raw
            var display = responseBody.Length > 1000 ? responseBody[..1000] + "\n    ... (truncated)" : responseBody;
            foreach (var line in display.Split('\n'))
                Console.WriteLine($"    {line}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Fel: {ex.Message}");
    }
}
