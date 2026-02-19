using System.Text;

namespace SchoolSoftDemo;

/// <summary>
/// Posts attendance status changes to SchoolSoft.
/// </summary>
public class AttendanceUpdater
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public AttendanceUpdater(HttpClient client, string baseUrl)
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Represents a status change for a student, including an optional length (minutes) value.
    /// </summary>
    public record StatusChange(int StatusCode, string LengthMinutes = "");

    /// <summary>
    /// Updates attendance for one or more students in a lesson.
    /// changes: studentId -> StatusChange (status code + optional length in minutes).
    /// </summary>
    public async Task<bool> UpdateAttendanceAsync(
        LessonDetail detail, Dictionary<int, StatusChange> changes, string schoolSlug)
    {
        // Browser POSTs to bare form action URL (no query params — lesson/week are in form data)
        var url = $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp";

        // Start from the EXACT form fields extracted from the HTML page.
        // This includes the action field (insert/update), CSRF tokens, hidden fields, etc.
        // We only override the student status fields we want to change.
        var formPairs = new List<KeyValuePair<string, string>>();

        // Build overrides for only the student fields we're changing
        // NOTE: Don't override "action" — the form already has the correct value
        // (insert for first report, update for edits)
        var overrides = new Dictionary<string, string>();

        foreach (var (studentId, change) in changes)
        {
            overrides[$"status_{studentId}"] = change.StatusCode.ToString();
            if (!string.IsNullOrEmpty(change.LengthMinutes))
                overrides[$"length-{studentId}"] = change.LengthMinutes;
        }

        // Replay all form fields, applying overrides where needed
        foreach (var field in detail.FormFields)
        {
            var value = overrides.TryGetValue(field.Key, out var ov) ? ov : field.Value;
            formPairs.Add(new(field.Key, value));
        }

        // Add the submit button — the server checks this to confirm the save
        // (submit buttons are excluded from FormFields since browsers only send the clicked one)
        formPairs.Add(new("button", "Spara"));

        // If FormFields is empty (shouldn't happen), log warning
        if (formPairs.Count == 0)
        {
            Console.WriteLine("  [POST] FEL: Inga formularfalt extraherades fran sidan!");
            return false;
        }

        // Debug: show key fields
        var actionVal = formPairs.FirstOrDefault(f => f.Key == "action").Value ?? "?";
        Console.WriteLine($"  [POST] lesson={detail.LessonId}, action={actionVal}, {formPairs.Count} formularfalt");
        foreach (var (sid, ch) in changes)
        {
            var extra = string.IsNullOrEmpty(ch.LengthMinutes) ? "" : $", length={ch.LengthMinutes} min";
            Console.WriteLine($"  [POST] status_{sid}={ch.StatusCode}{extra}");
        }

        var content = new FormUrlEncodedContent(formPairs);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        // Match browser headers
        request.Headers.Referrer = new Uri(
            $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp?lesson={detail.LessonId}&teachersubstitute=0&week={detail.Week}");
        request.Headers.Add("Origin", _baseUrl);
        var resp = await _client.SendAsync(request);

        // Check response
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var respHtml = ScheduleFetcher.DecodeHtml(bytes);

        // Always save response for debugging
        var debugPath = Path.Combine(Path.GetTempPath(), "schoolsoft_post_debug.html");
        await File.WriteAllTextAsync(debugPath, respHtml);

        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        Console.WriteLine($"  [POST] HTTP {(int)resp.StatusCode}, {respHtml.Length} tecken");
        if (finalUrl != url)
            Console.WriteLine($"  [POST] Slutlig URL: {finalUrl}");

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  [POST] FEL: HTTP {(int)resp.StatusCode}");
            Console.WriteLine($"  [POST] Svar sparat: {debugPath}");
            return false;
        }

        // Detect SPA shell (server didn't route to JSP backend)
        if (respHtml.Length < 1500 && (respHtml.Contains("main-root") || respHtml.Contains("bundle.js")))
        {
            Console.WriteLine($"  [POST] FEL: Servern returnerade SPA-skalet ({respHtml.Length} tecken) — POST bearbetades inte!");
            Console.WriteLine($"  [POST] Svar sparat: {debugPath}");
            return false;
        }

        // Check for actual error messages (not onerror/JS handlers)
        if (respHtml.Contains("felaktig") || respHtml.Contains("Felaktigt") ||
            respHtml.Contains("saknas") || respHtml.Contains("ej tillåt"))
        {
            Console.WriteLine($"  [POST] Server rapporterade fel i svaret");
            Console.WriteLine($"  [POST] Svar sparat: {debugPath}");
            return false;
        }

        if (respHtml.Contains("har nu sparats"))
            Console.WriteLine($"  [POST] OK — servern bekraftade: 'Din rapportering har nu sparats'");
        else
            Console.WriteLine($"  [POST] VARNING — hittade inte bekraftelsemeddelandet i svaret");

        Console.WriteLine($"  [POST] Debug sparat: {debugPath}");
        return true;
    }

    public static string GetStatusLabel(int code)
        => AttendanceFetcher.StatusLabels.GetValueOrDefault(code, $"Kod {code}");
}
