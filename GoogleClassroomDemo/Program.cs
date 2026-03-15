using System.Text;
using System.Text.Json;
using ApiForge;
using GoogleClassroomDemo;

Console.OutputEncoding = Encoding.UTF8;
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });

const string harFile = "classroom.har";
const string cookieFile = "classroom_cookies.json";
const string stopFile = "STOP";
const string defaultUrl = "https://classroom.google.com";

// Chrome debug profile for CDP recording
var chromeDebugProfileDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Google", "Chrome-CDP-Debug");

// Parse command
var command = args.Length > 0 ? args[0].ToLower() : "help";

switch (command)
{
    case "login":
        await Login();
        break;
    case "record":
        await RecordWithCdp();
        break;
    case "analyze":
        RunAnalysis();
        break;
    case "summary":
        await ShowHarSummary();
        break;
    case "courses":
        await ListCourses();
        break;
    case "submissions":
        await ListSubmissions();
        break;
    case "comments":
        await ListComments();
        break;
    case "test":
        await TestClient();
        break;
    case "oauth-test":
        await TestOAuth();
        break;
    default:
        PrintHelp();
        break;
}

void PrintHelp()
{
    Console.WriteLine("=== Google Classroom API-utforskare ===");
    Console.WriteLine();
    Console.WriteLine("Användning:");
    Console.WriteLine("  dotnet run -- login                         Logga in via GrandID (headless)");
    Console.WriteLine("  dotnet run -- record [url]                  Spela in trafik via CDP");
    Console.WriteLine("  dotnet run -- analyze [prompt]              Analysera HAR-fil med LLM");
    Console.WriteLine("  dotnet run -- summary [all|api|classroom]   Visa HAR-sammanfattning");
    Console.WriteLine("  dotnet run -- courses                       Lista alla kurser");
    Console.WriteLine("  dotnet run -- submissions <courseId> <assignmentId>  Lista inlämningar");
    Console.WriteLine("  dotnet run -- comments <courseId> <assignmentId> <studentId>  Visa kommentarer");
    Console.WriteLine("  dotnet run -- test                          Testa API-klienten");
    Console.WriteLine();
    Console.WriteLine("Exempel:");
    Console.WriteLine("  dotnet run -- login");
    Console.WriteLine("  dotnet run -- record \"https://classroom.google.com/u/1/c/xxx\"");
    Console.WriteLine("  dotnet run -- courses");
    Console.WriteLine("  dotnet run -- submissions 21622019490 23514882581");
    Console.WriteLine();

    var hasSession = File.Exists("session_tokens.json");
    Console.WriteLine($"Session:  {(hasSession ? "JA" : "NEJ — kör 'login' först")}");
    Console.WriteLine($"HAR-fil:  {(File.Exists(harFile) ? "JA" : "NEJ")}");
}

// === Login via GrandID ===

async Task Login()
{
    Console.WriteLine("=== Google Classroom — Inloggning via GrandID ===");
    Console.WriteLine();

    // Check for existing session
    var existingSession = await TokenManager.LoadSession();
    if (existingSession != null && TokenManager.IsSessionValid(existingSession))
    {
        Console.WriteLine("Befintlig session hittad:");
        TokenManager.PrintSessionInfo(existingSession);
        Console.WriteLine();

        Console.WriteLine("Testar sessionen online...");
        var result = await TokenManager.ValidateSessionOnline(existingSession, defaultUrl);
        Console.WriteLine($"  Status: {result.HttpStatus} — {result.Reason}");

        if (result.IsValid)
        {
            Console.WriteLine("Session giltig! Ingen ny inloggning behövs.");
            return;
        }
        Console.WriteLine("Session utgången. Ny inloggning krävs.");
        Console.WriteLine();
    }

    // Get credentials
    var username = args.Length > 1 ? args[1] : null;
    var password = args.Length > 2 ? args[2] : null;

    if (string.IsNullOrEmpty(username))
    {
        Console.Write("GrandID användarnamn: ");
        username = Console.ReadLine()?.Trim();
    }
    if (string.IsNullOrEmpty(password))
    {
        Console.Write("GrandID lösenord: ");
        password = Console.ReadLine()?.Trim();
    }

    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
    {
        Console.WriteLine("Användarnamn och lösenord krävs.");
        Console.WriteLine("  dotnet run -- login <username> <password>");
        return;
    }

    var classroomUrl = args.Length > 3 ? args[3] : defaultUrl;

    Console.WriteLine();
    Console.WriteLine($"Loggar in som '{username}' till {classroomUrl}...");
    Console.WriteLine();

    try
    {
        var auth = new HeadlessAuthenticator();
        var session = await auth.LoginAsync(username, password, classroomUrl);
        await TokenManager.SaveSession(session);

        Console.WriteLine();
        Console.WriteLine("Session sparad!");
        TokenManager.PrintSessionInfo(session);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nFel: {ex.Message}");
    }
}

// === CDP Recording ===

async Task RecordWithCdp()
{
    var recordUrl = args.Length > 1 ? args[1] : defaultUrl;

    if (File.Exists(stopFile)) File.Delete(stopFile);

    Console.WriteLine("=== CDP-inspelning ===");
    Console.WriteLine();

    // Check for session — inject cookies if available
    var session = await TokenManager.LoadSession();
    if (session != null && TokenManager.IsSessionValid(session))
    {
        Console.WriteLine($"Befintlig session hittad ({session.Cookies.Count} cookies).");
    }
    else
    {
        Console.WriteLine("Ingen giltig session. Du kan behöva logga in i webbläsaren.");
        Console.WriteLine("Tips: Kör 'dotnet run -- login' först för headless inloggning.");
    }

    // Kill Chrome first
    Console.WriteLine();
    Console.WriteLine("Stänger Chrome...");
    try
    {
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("chrome"))
        {
            try { proc.Kill(); } catch { }
        }
        await Task.Delay(2000);
    }
    catch { }

    Console.WriteLine();
    Console.WriteLine($"  URL:   {recordUrl}");
    Console.WriteLine($"  HAR:   {harFile}");
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════════╗");
    Console.WriteLine("║  Skapa filen 'STOP' för att avsluta          ║");
    Console.WriteLine("╚══════════════════════════════════════════════╝");
    Console.WriteLine();

    var recorder = new ApiForgeClient();
    await recorder.RecordHarAsync(
        harFile,
        cookieFile,
        startUrl: recordUrl,
        userProfilePath: chromeDebugProfileDir,
        waitForInput: async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var tasks = new List<Task>();

            // Only listen to stdin if we have an interactive console
            bool isInteractive = !Console.IsInputRedirected;
            if (isInteractive)
            {
                tasks.Add(Task.Run(() => { try { Console.ReadLine(); } catch { } }));
            }

            // Always poll for STOP file
            tasks.Add(Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (File.Exists(stopFile))
                    {
                        File.Delete(stopFile);
                        return;
                    }
                    await Task.Delay(500, cts.Token);
                }
            }));

            // Timeout task
            tasks.Add(Task.Delay(Timeout.Infinite, cts.Token));

            await Task.WhenAny(tasks);
            Console.WriteLine("\nStoppar inspelningen...");
        });

    Console.WriteLine();
    Console.WriteLine($"Inspelning sparad: {harFile} + {cookieFile}");
}

// === LLM Analysis ===

void RunAnalysis()
{
    if (!File.Exists(harFile))
    {
        Console.WriteLine($"Ingen HAR-fil hittad ({harFile}). Kör 'record' först.");
        return;
    }

    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    var endpoint = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
    var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";

    if (string.IsNullOrEmpty(endpoint) && string.IsNullOrEmpty(apiKey))
    {
        endpoint = "http://127.0.0.1:1234/v1";
        model = "openai/gpt-oss-20b";
        apiKey = "lm-studio";
        Console.WriteLine($"Använder lokal LLM: {model} på {endpoint}");
    }
    else if (!string.IsNullOrEmpty(endpoint))
    {
        Console.WriteLine($"Använder custom endpoint: {endpoint} med modell: {model}");
    }
    else
    {
        Console.WriteLine($"Använder OpenAI: {model}");
    }

    var prompt = args.Length > 1 ? string.Join(" ", args.Skip(1)) :
        "Find all API endpoints, their parameters, authentication requirements, and data structures. Focus on REST API calls and GraphQL queries.";

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

// === HAR Summary ===

async Task ShowHarSummary()
{
    if (!File.Exists(harFile))
    {
        Console.WriteLine("Ingen HAR-fil hittad. Kör 'record' först.");
        return;
    }

    var json = await File.ReadAllTextAsync(harFile);
    var harData = JsonSerializer.Deserialize<JsonElement>(json);

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

    var filterArg = args.Length > 1 ? args[1].ToLower() : "api";

    var filtered = entryList.AsEnumerable();
    if (filterArg == "api")
    {
        filtered = filtered.Where(e =>
            e.mimeType.Contains("json") ||
            e.mimeType.Contains("protobuf") ||
            e.url.Contains("/api/") ||
            e.url.Contains("/graphql") ||
            e.url.Contains("/v1/") ||
            e.url.Contains("/v2/") ||
            e.url.Contains("/_/") ||
            e.url.Contains("/batchexecute"));
    }
    else if (filterArg == "classroom")
    {
        filtered = filtered.Where(e =>
            e.url.Contains("classroom.google.com") ||
            e.url.Contains("classroom.googleapis.com"));
    }

    var results = filtered.ToList();

    Console.WriteLine($"=== HAR-sammanfattning ({results.Count} av {entryList.Count} requests) ===");
    Console.WriteLine($"    Filter: {filterArg}");
    Console.WriteLine();
    Console.WriteLine($"{"#",-4} {"Method",-8} {"Status",-7} {"Size",-10} {"MIME",-30} {"URL"}");
    Console.WriteLine(new string('-', 120));

    for (int i = 0; i < results.Count; i++)
    {
        var (method, url, status, mimeType, size) = results[i];
        var displayUrl = url.Length > 80 ? url[..77] + "..." : url;
        var displayMime = mimeType.Length > 29 ? mimeType[..26] + "..." : mimeType;
        var displaySize = size > 1024 ? $"{size / 1024}KB" : $"{size}B";
        Console.WriteLine($"{i + 1,-4} {method,-8} {status,-7} {displaySize,-10} {displayMime,-30} {displayUrl}");
    }

    Console.WriteLine();

    if (args.Length > 2 && int.TryParse(args[2], out int detailIdx) && detailIdx >= 1 && detailIdx <= results.Count)
    {
        var selected = results[detailIdx - 1];
        Console.WriteLine($"=== Request #{detailIdx} ===");
        Console.WriteLine($"  Method: {selected.method}");
        Console.WriteLine($"  URL:    {selected.url}");
        Console.WriteLine($"  Status: {selected.status}");
        Console.WriteLine($"  MIME:   {selected.mimeType}");
        Console.WriteLine($"  Size:   {selected.size} bytes");

        var fullEntry = entries.EnumerateArray().ElementAt(entryList.IndexOf(selected));
        var reqHeaders = fullEntry.GetProperty("request");
        var respData = fullEntry.GetProperty("response");

        if (reqHeaders.TryGetProperty("headers", out var headers))
        {
            Console.WriteLine("  Request Headers:");
            foreach (var h in headers.EnumerateArray())
            {
                var name = h.GetProperty("name").GetString() ?? "";
                var value = h.GetProperty("value").GetString() ?? "";
                if (name.StartsWith(":") || name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                    continue;
                Console.WriteLine($"    {name}: {(value.Length > 80 ? value[..77] + "..." : value)}");
            }
        }

        if (reqHeaders.TryGetProperty("postData", out var postData))
        {
            var bodyText = postData.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(bodyText))
            {
                Console.WriteLine($"  Request Body ({bodyText.Length} chars):");
                var displayBody = bodyText.Length > 2000 ? bodyText[..2000] + "\n    ... (truncated)" : bodyText;
                foreach (var line in displayBody.Split('\n'))
                    Console.WriteLine($"    {line}");
            }
        }

        if (respData.TryGetProperty("content", out var respContent) &&
            respContent.TryGetProperty("text", out var respText))
        {
            var bodyText = respText.GetString() ?? "";
            if (!string.IsNullOrEmpty(bodyText))
            {
                Console.WriteLine($"  Response Body ({bodyText.Length} chars):");
                var displayBody = bodyText.Length > 2000 ? bodyText[..2000] + "\n    ... (truncated)" : bodyText;
                foreach (var line in displayBody.Split('\n'))
                    Console.WriteLine($"    {line}");
            }
        }
    }
}

// === API Client Commands ===

async Task<GoogleClassroomClient> CreateClient()
{
    var session = await TokenManager.LoadSession();
    if (session == null || !TokenManager.IsSessionValid(session))
    {
        Console.WriteLine("Ingen giltig session. Kör 'dotnet run -- login' först.");
        throw new InvalidOperationException("Ingen session.");
    }

    var client = new GoogleClassroomClient(session);
    await client.InitializeAsync();
    return client;
}

async Task ListCourses()
{
    var client = await CreateClient();
    Console.WriteLine();
    Console.WriteLine("=== Kurser ===");

    var data = await client.GetCoursesAsync();
    if (data.ValueKind == JsonValueKind.Null)
    {
        Console.WriteLine("Inga data returnerades.");
        return;
    }

    // Response: ["hrq.crs", [false], [<courses>]]
    // Each course: [[courseId], ..., "Course Name", ...]
    if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() >= 3)
    {
        var courses = data[2];
        if (courses.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"Antal kurser: {courses.GetArrayLength()}");
            Console.WriteLine();
            Console.WriteLine($"{"#",-4} {"Kurs-ID",-15} {"Namn"}");
            Console.WriteLine(new string('-', 60));

            int idx = 1;
            foreach (var course in courses.EnumerateArray())
            {
                var courseId = course[0][0].ToString().Trim('"');
                var name = course.GetArrayLength() > 5 ? course[5].GetString() ?? "?" : "?";
                Console.WriteLine($"{idx,-4} {courseId,-15} {name}");
                idx++;
            }
        }
    }
    else
    {
        Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}

async Task ListSubmissions()
{
    if (args.Length < 3)
    {
        Console.WriteLine("Användning: dotnet run -- submissions <courseId> <assignmentId>");
        Console.WriteLine("Exempel:    dotnet run -- submissions 21622019490 23514882581");
        return;
    }

    var courseId = long.Parse(args[1]);
    var assignmentId = long.Parse(args[2]);

    var client = await CreateClient();
    Console.WriteLine();
    Console.WriteLine($"=== Inlämningar (kurs: {courseId}, uppgift: {assignmentId}) ===");

    var data = await client.GetSubmissionsAsync(assignmentId, courseId);
    if (data.ValueKind == JsonValueKind.Null)
    {
        Console.WriteLine("Inga data returnerades.");
        return;
    }

    // Response: ["hrq.sub", [false], [<submissions>]]
    if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() >= 3)
    {
        var submissions = data[2];
        if (submissions.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"Antal inlämningar: {submissions.GetArrayLength()}");
            Console.WriteLine();

            // Also fetch user profiles for the student IDs
            var studentIds = new List<long>();
            foreach (var sub in submissions.EnumerateArray())
            {
                if (sub.ValueKind == JsonValueKind.Array && sub.GetArrayLength() > 0 &&
                    sub[0].ValueKind == JsonValueKind.Array && sub[0].GetArrayLength() > 0)
                {
                    var idStr = sub[0][0].ToString().Trim('"');
                    if (long.TryParse(idStr, out var id)) studentIds.Add(id);
                }
            }

            Dictionary<string, string>? nameMap = null;
            if (studentIds.Count > 0)
            {
                try
                {
                    var users = await client.GetUsersAsync(studentIds.ToArray());
                    nameMap = new Dictionary<string, string>();
                    if (users.ValueKind == JsonValueKind.Array && users.GetArrayLength() >= 3 &&
                        users[2].ValueKind == JsonValueKind.Array)
                    {
                        foreach (var user in users[2].EnumerateArray())
                        {
                            if (user.ValueKind == JsonValueKind.Array && user.GetArrayLength() > 2)
                            {
                                var uid = user[0][0].ToString().Trim('"');
                                var uname = user[1].GetString() ?? "?";
                                nameMap[uid] = uname;
                            }
                        }
                    }
                }
                catch { }
            }

            Console.WriteLine($"{"#",-4} {"Elev-ID",-16} {"Namn",-25} {"Status"}");
            Console.WriteLine(new string('-', 70));

            int idx = 1;
            foreach (var sub in submissions.EnumerateArray())
            {
                var sid = sub[0][0].ToString().Trim('"');
                var name = nameMap?.GetValueOrDefault(sid, "?") ?? "?";
                var statusCode = sub.GetArrayLength() > 5 ? sub[5].ToString() : "?";
                var status = statusCode switch
                {
                    "1" => "Inlämnad",
                    "3" => "Returnerad",
                    "4" => "Inlämnad",
                    _ => $"Status {statusCode}"
                };
                Console.WriteLine($"{idx,-4} {sid,-16} {name,-25} {status}");
                idx++;
            }
        }
    }
    else
    {
        Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}

async Task ListComments()
{
    if (args.Length < 4)
    {
        Console.WriteLine("Användning: dotnet run -- comments <courseId> <assignmentId> <studentId>");
        return;
    }

    var courseId = long.Parse(args[1]);
    var assignmentId = long.Parse(args[2]);
    var studentId = long.Parse(args[3]);

    var client = await CreateClient();
    Console.WriteLine();
    Console.WriteLine($"=== Kommentarer (elev: {studentId}) ===");

    var data = await client.GetCommentsAsync(studentId, assignmentId, courseId);
    if (data.ValueKind == JsonValueKind.Null)
    {
        Console.WriteLine("Inga data returnerades.");
        return;
    }

    // Response: ["hrq.cmt", [false], [<comments>]]
    if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() >= 3)
    {
        var comments = data[2];
        if (comments.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"Antal kommentarer: {comments.GetArrayLength()}");
            Console.WriteLine();

            foreach (var comment in comments.EnumerateArray())
            {
                // Comment structure: [[id,...], created_ts, modified_ts, ..., [author_id], ..., ["edu.rt", "text", ...]]
                var authorId = comment.GetArrayLength() > 4 ? comment[4].ToString() : "?";
                var textPart = comment.GetArrayLength() > 11 ? comment[11] : default;
                var text = "?";
                if (textPart.ValueKind == JsonValueKind.Array && textPart.GetArrayLength() > 1)
                    text = textPart[1].GetString() ?? "?";

                var ts = comment.GetArrayLength() > 3 ? comment[3].ToString() : "?";
                Console.WriteLine($"  [{ts}] {authorId}: {text}");
            }
        }
    }
    else
    {
        Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}

async Task TestClient()
{
    Console.WriteLine("=== API-klient Test ===");
    Console.WriteLine();

    var client = await CreateClient();

    Console.WriteLine();
    Console.WriteLine("--- Hämtar kurser ---");
    var courses = await client.GetCoursesAsync();
    Console.WriteLine($"Rå-svar (första 500 tecken):");
    var raw = JsonSerializer.Serialize(courses);
    Console.WriteLine(raw.Length > 500 ? raw[..500] + "..." : raw);

    Console.WriteLine();
    Console.WriteLine("Klar!");
}

// === Test: OAuth access token → Classroom session ===

async Task TestOAuth()
{
    Console.WriteLine("=== Test: OAuth Access Token → Classroom batchexecute ===");
    Console.WriteLine();

    // Read cached OAuth token from TeachersLittleHelper (TLH-Dev or TLH)
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var tokenPaths = new[]
    {
        Path.Combine(localAppData, "TLH-Dev", "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user"),
        Path.Combine(localAppData, "TLH", "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user"),
    };

    string? accessToken = null;
    foreach (var tokenPath in tokenPaths)
    {
        Console.WriteLine($"Provar: {tokenPath}");
        if (!File.Exists(tokenPath)) continue;

        var content = await File.ReadAllTextAsync(tokenPath);
        var tokenJson = JsonSerializer.Deserialize<JsonElement>(content);
        if (tokenJson.TryGetProperty("access_token", out var at))
        {
            accessToken = at.GetString();
            Console.WriteLine($"  → access_token hittad ({accessToken?[..20]}...)");
            break;
        }
    }

    if (string.IsNullOrEmpty(accessToken))
    {
        Console.WriteLine("\nIngen OAuth access token hittad.");
        Console.WriteLine("Kör TeachersLittleHelper och logga in först.");
        return;
    }

    // Test 1: Use OAuth token as Authorization header to load classroom.google.com
    Console.WriteLine();
    Console.WriteLine("--- Test 1: OAuth Bearer → classroom.google.com ---");

    using var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = true };
    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

    var resp = await client.GetAsync("https://classroom.google.com");
    var html = await resp.Content.ReadAsStringAsync();
    var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "?";

    Console.WriteLine($"  Status: {(int)resp.StatusCode}");
    Console.WriteLine($"  Final URL: {(finalUrl.Length > 80 ? finalUrl[..77] + "..." : finalUrl)}");
    Console.WriteLine($"  HTML length: {html.Length}");

    // Check if we got session tokens
    var sidMatch = System.Text.RegularExpressions.Regex.Match(html, @"""FdrFJe""\s*:\s*""(-?\d+)""");
    var atMatch = System.Text.RegularExpressions.Regex.Match(html, @"""SNlM0e""\s*:\s*""([^""]+)""");
    var blMatch = System.Text.RegularExpressions.Regex.Match(html, @"""cfb2h""\s*:\s*""([^""]+)""");

    Console.WriteLine($"  f.sid: {(sidMatch.Success ? sidMatch.Groups[1].Value : "EJ HITTAT")}");
    Console.WriteLine($"  at:    {(atMatch.Success ? atMatch.Groups[1].Value[..Math.Min(20, atMatch.Groups[1].Value.Length)] + "..." : "EJ HITTAT")}");
    Console.WriteLine($"  bl:    {(blMatch.Success ? blMatch.Groups[1].Value : "EJ HITTAT")}");

    if (atMatch.Success)
    {
        Console.WriteLine();
        Console.WriteLine("  ✓ OAuth → session FUNGERAR! Access token kan användas för batchexecute.");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("  ✗ OAuth → session fungerade INTE. Session-tokens saknas i HTML.");
        Console.WriteLine($"  Kontroll: Innehåller HTML 'accounts.google.com'? {html.Contains("accounts.google.com")}");
        Console.WriteLine($"  Innehåller 'classroom'? {html.Contains("classroom")}");

        // Show first 500 chars to debug
        Console.WriteLine($"  HTML (500 tecken): {(html.Length > 500 ? html[..500] : html)}");
    }
}
