using System.Text;
using ApiForge;
using Microsoft.Playwright;
using SchoolSoftDemo;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });

const string harFile = "schoolsoft.har";
const string cookieFile = "schoolsoft_cookies.json";
const string schoolSlug = "nti";
const string baseUrl = "https://sms.schoolsoft.se";
const string ssoUrl = $"{baseUrl}/{schoolSlug}/sso";
// Use the startpage for session validation — it's the most reliable (no special params needed)
const string validationUrl = $"{baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_startpage.jsp?action=select_sidebar&menutype=1";

Console.WriteLine("=== SchoolSoft Schema Hamtare ===");
Console.WriteLine($"    Skola: {schoolSlug}");
Console.WriteLine($"    SSO:   {ssoUrl}");
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
        Console.WriteLine("Testar sessionen mot SchoolSoft...");
        var onlineResult = await TokenManager.ValidateSessionOnline(existingSession, validationUrl);

        // Show debug info so user can see what happened
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
            // If server explicitly rejected (redirect to login, ERR_No), don't offer fallback
            bool serverRejected = onlineResult.Reason.Contains("inloggning") ||
                                  onlineResult.Reason.Contains("inloggningsformular");

            if (serverRejected)
            {
                Console.WriteLine("Sessionen har gatt ut. Ny inloggning kravs.");
            }
            else
            {
                // Ambiguous failure (timeout, network, unclear response) — offer fallback if session is young
                var sessionAge = DateTime.UtcNow - existingSession.SavedAt;
                if (sessionAge.TotalHours < 4)
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

// Om sessionen inte ar giltig, logga in
if (!sessionValid)
{
    Console.WriteLine("Inloggning kravs. Valj metod:");
    Console.WriteLine("  1. Headless inloggning (anvandarnamn + losenord + SMS-kod)");
    Console.WriteLine("  2. Webblasare (manuell inloggning + HAR-inspelning)");
    Console.Write("Val (1/2): ");
    var loginChoice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    if (loginChoice == "2")
    {
        // Browser recording flow
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
        Console.WriteLine($"  Webblasaren oppnas nu pa: {ssoUrl}");
        Console.WriteLine("  1. Logga in via SSO");
        Console.WriteLine("  2. Nar du ar inloggad, ga till ditt schema");
        Console.WriteLine("  3. Vanta tills schemat har laddats klart");
        Console.WriteLine("  4. Tryck Enter har i konsolen");
        Console.WriteLine();

        var recorder = new ApiForgeClient();
        await recorder.RecordHarAsync(harFile, cookieFile, startUrl: ssoUrl);

        Console.WriteLine();
        Console.WriteLine($"Inspelning sparad: {harFile} + {cookieFile}");
        Console.WriteLine();

        Console.WriteLine("Extraherar SSO-tokens...");
        existingSession = await TokenManager.ExtractAndSaveFromCookieFile(cookieFile, schoolPath: schoolSlug);
    }
    else
    {
        // Headless login flow with retry
        var auth = new HeadlessAuthenticator(baseUrl, schoolSlug);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Console.Write("Anvandarnamn: ");
            var username = Console.ReadLine()?.Trim() ?? "";
            Console.Write("Losenord (dolt): ");
            var password = ReadPassword();
            Console.WriteLine();

            try
            {
                existingSession = await auth.LoginAsync(username, password);
                break; // Success
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("felaktigt") && attempt < 3)
            {
                Console.WriteLine($"Fel: {ex.Message}");
                Console.WriteLine($"  Forsok {attempt}/3. Tips: om losenordet inte funkar, skriv 'v' for synlig inmatning.");
                Console.Write("Forsok igen? (Enter = ja, 'v' = synligt losenord, 'n' = avbryt): ");
                var retry = Console.ReadLine()?.Trim().ToLower();
                if (retry == "n") throw;
                if (retry == "v")
                {
                    Console.Write("Anvandarnamn: ");
                    username = Console.ReadLine()?.Trim() ?? "";
                    Console.Write("Losenord (SYNLIGT): ");
                    password = Console.ReadLine()?.Trim() ?? "";

                    existingSession = await auth.LoginAsync(username, password);
                    break;
                }
            }
        }
    }

    // Spara och verifiera
    await TokenManager.SaveSession(existingSession);
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
        sessionValid = true;
    }
    else
    {
        Console.WriteLine("VARNING: Kunde inte verifiera sessionen online.");
        Console.WriteLine("Forsaker anvanda sessionen anda (den ar precis skapad)...");
        sessionValid = true; // Just logged in — use it regardless of validation glitch
    }
    Console.WriteLine();
}

// Meny
bool hasHarFile = File.Exists(harFile);
if (sessionValid)
{
    Console.WriteLine("Vad vill du gora?");
    Console.WriteLine("  1. Hamta schemat (snabbt)");
    if (hasHarFile)
        Console.WriteLine("  2. Kor full analys med LLM (befintlig HAR)");
    Console.WriteLine("  3. Spela in nytt flode (webblasare) + analysera");
    Console.Write("Val: ");
    var choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    if (choice == "3")
    {
        // Ny inspelning med befintlig session
        Console.WriteLine("Kontrollerar Playwright Chromium...");
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
        {
            Console.WriteLine("Kunde inte installera Chromium. Avbryter.");
            return;
        }

        Console.Write("Vilken URL vill du borja pa? (Enter = startsidan): ");
        var startInput = Console.ReadLine()?.Trim();
        var recordStartUrl = string.IsNullOrEmpty(startInput)
            ? $"{baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_startpage.jsp?action=select_sidebar&menutype=1"
            : startInput;

        Console.WriteLine();
        Console.WriteLine("Spelar in webblasartrafik...");
        Console.WriteLine($"  Webblasaren oppnas nu pa: {recordStartUrl}");
        Console.WriteLine("  1. Navigera till det du vill fanga (t.ex. narvaro)");
        Console.WriteLine("  2. Vanta tills sidan har laddats klart");
        Console.WriteLine("  3. Tryck Enter har i konsolen");
        Console.WriteLine();

        // Convert session cookies to Playwright format and inject into browser
        var playwrightCookies = existingSession?.Cookies
            .Where(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Domain))
            .Select(c => new Microsoft.Playwright.Cookie
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

        Console.WriteLine($"  Injicerar {playwrightCookies?.Count ?? 0} session-cookies i webblasaren...");
        var recorder = new ApiForgeClient();
        await recorder.RecordHarAsync(harFile, cookieFile, startUrl: recordStartUrl, cookies: playwrightCookies);

        Console.WriteLine($"Inspelning sparad: {harFile}");
        Console.WriteLine();

        // Update cookies from recording
        existingSession = await TokenManager.ExtractAndSaveFromCookieFile(cookieFile, schoolPath: schoolSlug);
        hasHarFile = true;

        RunAnalysis();
    }
    else if (choice == "2" && hasHarFile)
    {
        RunAnalysis();
    }
}

// Hamta schemat
using var authClient = TokenManager.CreateAuthenticatedClient(existingSession!);
var fetcher = new ScheduleFetcher(authClient, baseUrl);
var currentWeek = ScheduleFetcher.GetCurrentWeek();

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== Schema ===");
    Console.Write($"Ange vecka (Enter = v{currentWeek}, 'q' = avsluta): ");
    var weekInput = Console.ReadLine()?.Trim();

    if (weekInput?.ToLower() is "q" or "quit" or "avsluta")
        break;

    int week = currentWeek;
    if (!string.IsNullOrEmpty(weekInput))
    {
        weekInput = weekInput.TrimStart('v', 'V');
        if (!int.TryParse(weekInput, out week) || week < 1 || week > 53)
        {
            Console.WriteLine("Ogiltig vecka. Ange 1-53.");
            continue;
        }
    }

    Console.WriteLine($"Hamtar vecka {week}...");

    try
    {
        var lessons = await fetcher.GetWeekScheduleAsync(week, schoolSlug);

        if (lessons.Count == 0)
        {
            Console.WriteLine("Inga lektioner hittades denna vecka.");
        }
        else
        {
            var sorted = lessons.OrderBy(l => l.Day).ThenBy(l => l.StartTime).ToList();

            Console.WriteLine($"Hittade {sorted.Count} lektioner:");
            Console.WriteLine();
            Console.WriteLine($"{"#",-4} {"Dag",-10} {"Tid",-12} {"Amne",-20} {"Sal",-16} {"Grupp"}");
            Console.WriteLine(new string('-', 84));

            DayOfWeek? lastDay = null;
            for (int i = 0; i < sorted.Count; i++)
            {
                var lesson = sorted[i];
                if (lastDay != null && lesson.Day != lastDay)
                    Console.WriteLine();
                Console.WriteLine($"{i + 1,-4} {lesson}");
                lastDay = lesson.Day;
            }

            // Attendance sub-menu
            var attendanceFetcher = new AttendanceFetcher(authClient, baseUrl);
            while (true)
            {
                Console.WriteLine();
                Console.Write($"Valj lektion (1-{sorted.Count}) for narvaro, eller Enter for ny vecka: ");
                var lessonInput = Console.ReadLine()?.Trim();

                if (string.IsNullOrEmpty(lessonInput))
                    break;

                if (!int.TryParse(lessonInput, out var lessonIdx) || lessonIdx < 1 || lessonIdx > sorted.Count)
                {
                    Console.WriteLine("Ogiltigt val.");
                    continue;
                }

                var selectedLesson = sorted[lessonIdx - 1];
                Console.WriteLine($"Hamtar narvaro for {selectedLesson.Subject} ({selectedLesson.StartTime}-{selectedLesson.EndTime})...");

                try
                {
                    var detail = await attendanceFetcher.GetLessonAttendanceAsync(selectedLesson.LessonId, week, schoolSlug);
                    PrintAttendance(detail);

                    // Status change sub-menu
                    if (detail.Students.Count > 0)
                    {
                        var updater = new AttendanceUpdater(authClient, baseUrl);
                        while (true)
                        {
                            Console.WriteLine();
                            Console.WriteLine("  Andra status:");
                            Console.WriteLine("    0=- (rensa)  1=Franvarande  205=Delvis franv.  206=Sen ankomst  751=Distans");
                            Console.WriteLine("    For 205/206/751 kan du ange minuter: t.ex. '1 206 15' = elev 1, sen 15 min");
                            Console.Write("  Elev-nr + status [+ min] (t.ex. '1 1', '1-5 0', '2 751 30', 'alla 0'), Enter = tillbaka: ");
                            var changeInput = Console.ReadLine()?.Trim();

                            if (string.IsNullOrEmpty(changeInput))
                                break;

                            var changes = ParseStatusChanges(changeInput, detail.Students);
                            if (changes == null || changes.Count == 0)
                            {
                                Console.WriteLine("  Ogiltigt format. Anvand: <elevnr> <status> [minuter] eller 'alla <status>'");
                                continue;
                            }

                            // Show what will change
                            foreach (var (sid, change) in changes)
                            {
                                var student = detail.Students.FirstOrDefault(s => s.StudentId == sid);
                                var name = student?.Name ?? $"Elev {sid}";
                                var extra = string.IsNullOrEmpty(change.LengthMinutes) ? "" : $" ({change.LengthMinutes} min)";
                                Console.WriteLine($"    {name}: {AttendanceUpdater.GetStatusLabel(student?.StatusCode ?? 0)} -> {AttendanceUpdater.GetStatusLabel(change.StatusCode)}{extra}");
                            }

                            Console.Write("  Spara? (j/n): ");
                            if (Console.ReadLine()?.Trim().ToLower() != "j")
                            {
                                Console.WriteLine("  Avbrutet.");
                                continue;
                            }

                            var ok = await updater.UpdateAttendanceAsync(detail, changes, schoolSlug);
                            if (ok)
                            {
                                Console.WriteLine("  Uppdaterat!");
                                // Refresh
                                detail = await attendanceFetcher.GetLessonAttendanceAsync(selectedLesson.LessonId, week, schoolSlug);
                                PrintAttendance(detail);
                            }
                            else
                            {
                                Console.WriteLine("  FEL: Kunde inte uppdatera.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Kunde inte hamta narvaro: {ex.Message}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Kunde inte hamta schemat: {ex.Message}");
    }
}

// --- Hjalpfunktioner ---

void PrintAttendance(LessonDetail detail)
{
    Console.WriteLine();
    Console.WriteLine($"=== {detail.Subject} — {detail.Day} {detail.Time} ({detail.LengthMinutes} min) ===");
    if (!string.IsNullOrEmpty(detail.LessonStatus))
        Console.WriteLine($"    Status: {detail.LessonStatus}");
    Console.WriteLine($"    Elever: {detail.Students.Count}");
    Console.WriteLine();

    if (detail.Students.Count > 0)
    {
        Console.WriteLine($"  {"#",-4} {"Namn",-30} {"Status"}");
        Console.WriteLine($"  {new string('-', 54)}");
        for (int j = 0; j < detail.Students.Count; j++)
        {
            var s = detail.Students[j];
            Console.WriteLine($"  {j + 1,-4} {s.Name,-30} {s.Status}");
        }
    }
    else
    {
        Console.WriteLine("  Inga elever hittades.");
    }
}

Dictionary<int, AttendanceUpdater.StatusChange>? ParseStatusChanges(string input, List<StudentAttendance> students)
{
    var changes = new Dictionary<int, AttendanceUpdater.StatusChange>();
    var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length < 2)
        return null;

    // Check for optional minutes parameter at the end:
    // Format: <selector> <status> [minutes]
    // Examples: "1 206 15", "1-5 751 30", "alla 206 20"
    string lengthMinutes = "";
    int statusCode;

    if (parts.Length >= 3 && int.TryParse(parts[^1], out var lastNum) && int.TryParse(parts[^2], out var secondLast))
    {
        // Could be: "<selector> <status> <minutes>" or "<idx1> <idx2> <status>"
        // Heuristic: if secondLast is a known status code (0, 1, 2, 205, 206, 751), treat lastNum as minutes
        var knownStatuses = new HashSet<int> { 0, 1, 2, 205, 206, 751 };
        if (knownStatuses.Contains(secondLast) && parts.Length == 3 && !parts[0].Contains('-') && !parts[0].Contains(',')
            && !parts[0].Equals("alla", StringComparison.OrdinalIgnoreCase)
            && !parts[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            // "1 206 15" — single student, status, minutes
            statusCode = secondLast;
            lengthMinutes = lastNum.ToString();
        }
        else if (knownStatuses.Contains(secondLast) &&
                 (parts[0].Equals("alla", StringComparison.OrdinalIgnoreCase) ||
                  parts[0].Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            // "alla 206 15" — all students, status, minutes
            statusCode = secondLast;
            lengthMinutes = lastNum.ToString();
        }
        else
        {
            // Fallback: last part is status, no minutes
            if (!int.TryParse(parts[^1], out statusCode))
                return null;
        }
    }
    else
    {
        if (!int.TryParse(parts[^1], out statusCode))
            return null;
    }

    // "alla 0" — apply to all students
    if (parts[0].Equals("alla", StringComparison.OrdinalIgnoreCase) ||
        parts[0].Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        foreach (var s in students)
            changes[s.StudentId] = new AttendanceUpdater.StatusChange(statusCode, lengthMinutes);
        return changes;
    }

    // Determine how many parts form the selector vs status+minutes
    int selectorEndIndex = string.IsNullOrEmpty(lengthMinutes) ? parts.Length - 1 : parts.Length - 2;
    var selector = string.Join(" ", parts[..selectorEndIndex]);

    // Range: "1-5"
    var rangeMatch = System.Text.RegularExpressions.Regex.Match(selector, @"^(\d+)\s*-\s*(\d+)$");
    if (rangeMatch.Success)
    {
        int from = int.Parse(rangeMatch.Groups[1].Value);
        int to = int.Parse(rangeMatch.Groups[2].Value);
        for (int idx = from; idx <= to && idx <= students.Count; idx++)
        {
            if (idx >= 1)
                changes[students[idx - 1].StudentId] = new AttendanceUpdater.StatusChange(statusCode, lengthMinutes);
        }
        return changes.Count > 0 ? changes : null;
    }

    // Comma/space-separated: "1,2,3" or "1 2 3"
    var indices = selector.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    foreach (var idxStr in indices)
    {
        if (int.TryParse(idxStr, out var idx) && idx >= 1 && idx <= students.Count)
            changes[students[idx - 1].StudentId] = new AttendanceUpdater.StatusChange(statusCode, lengthMinutes);
    }

    return changes.Count > 0 ? changes : null;
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

    Console.WriteLine("Analyserar schemat-API:et med AI...");
    Console.WriteLine();

    var analyzeClient = new ApiForgeClient(openAiApiKey: apiKey, model: model, endpoint: endpoint);
    var result = analyzeClient.AnalyzeAsync(
        prompt: "get the schedule / hämta schemat",
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

static string ReadPassword()
{
    var password = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
        {
            password.Length--;
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
            Console.Write('*');
        }
    }
    return password.ToString();
}
