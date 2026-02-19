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
        // URL must include query params — the browser POSTs to the current page URL
        var url = $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp?lesson={detail.LessonId}&teachersubstitute=0&week={detail.Week}";

        // Start from the EXACT form fields extracted from the HTML page.
        // This includes any CSRF tokens, hidden fields, etc. that the server expects.
        // We only override the fields we want to change.
        var formPairs = new List<KeyValuePair<string, string>>();

        // Build a set of field names we need to override
        var overrides = new Dictionary<string, string>();
        overrides["action"] = "update";  // Tell server we're saving

        foreach (var (studentId, change) in changes)
        {
            overrides[$"status_{studentId}"] = change.StatusCode.ToString();
            // status2: 0 for present, 1 for any type of absence
            overrides[$"status2_{studentId}"] = change.StatusCode > 0 ? "1" : "0";
            if (!string.IsNullOrEmpty(change.LengthMinutes))
                overrides[$"length-{studentId}"] = change.LengthMinutes;
        }

        // Replay all form fields, applying overrides where needed
        foreach (var field in detail.FormFields)
        {
            var value = overrides.TryGetValue(field.Key, out var ov) ? ov : field.Value;
            formPairs.Add(new(field.Key, value));
        }

        // If FormFields is empty (shouldn't happen), log warning
        if (formPairs.Count == 0)
        {
            Console.WriteLine("  [POST] FEL: Inga formularfalt extraherades fran sidan!");
            return false;
        }

        // Debug: show key fields
        Console.WriteLine($"  [POST] lesson={detail.LessonId}, {formPairs.Count} formularfalt");
        foreach (var (sid, ch) in changes)
        {
            var extra = string.IsNullOrEmpty(ch.LengthMinutes) ? "" : $", length={ch.LengthMinutes} min";
            Console.WriteLine($"  [POST] status_{sid}={ch.StatusCode}{extra}");
        }

        var content = new FormUrlEncodedContent(formPairs);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Referrer = new Uri(
            $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp?lesson={detail.LessonId}&teachersubstitute=0&week={detail.Week}");
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

        Console.WriteLine($"  [POST] OK — svar sparat: {debugPath}");
        return true;
    }

    public static string GetStatusLabel(int code)
        => AttendanceFetcher.StatusLabels.GetValueOrDefault(code, $"Kod {code}");
}
