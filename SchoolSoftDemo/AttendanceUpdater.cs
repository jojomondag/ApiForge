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
        // URL must include query params — the browser POSTs to the current page URL (form has
        // no explicit action), and Varnish uses the full URL to route to JSP backend vs SPA.
        var url = $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp?lesson={detail.LessonId}&teachersubstitute=0&week={detail.Week}";

        // Lesson status: default to 2 (Genomförd) if not extracted
        var lessonStatus = detail.LessonStatusCode > 0 ? detail.LessonStatusCode : 2;

        var teacherId = detail.TeacherId > 0 ? detail.TeacherId.ToString() : "0";

        // Use a list of pairs to preserve field order (matching the browser form exactly)
        var formPairs = new List<KeyValuePair<string, string>>
        {
            new("action", "update"),
            new("lesson", detail.LessonId.ToString()),
            new("week", detail.Week.ToString()),
            new("teachersubstitute", "0"),
            new("dayreport", "0"),
            new("old_status", lessonStatus.ToString()),
            new("current_lesson", detail.LessonId.ToString()),
            new("status", lessonStatus.ToString()),
            new("subject", detail.SubjectId.ToString()),
            new("day", detail.DayIndex.ToString()),
            new("time", detail.Time),
            new("length", detail.LengthMinutes.ToString()),
            new("send_messages", "0"),
            new("sendmail", "0"),
            new("recipients", "0"),
            new("sortorder", "0"),
        };

        // Build per-student fields in the exact order from the browser form
        foreach (var student in detail.Students)
        {
            var sid = student.StudentId.ToString();

            // Apply change if present, otherwise keep current status
            var change = changes.TryGetValue(student.StudentId, out var ch)
                ? ch
                : new StatusChange(student.StatusCode);

            var lengthVal = change.LengthMinutes;

            // status2: 0 for present, 1 for any type of absence (matches browser behavior)
            var status2 = change.StatusCode > 0 ? "1" : "0";

            formPairs.Add(new("status_" + sid, change.StatusCode.ToString()));
            formPairs.Add(new("subject-" + sid, detail.SubjectId.ToString()));
            formPairs.Add(new("status2_" + sid, status2));
            formPairs.Add(new("subject2-" + sid, detail.SubjectId.ToString()));
            formPairs.Add(new("teacher-" + sid, teacherId));
            formPairs.Add(new("student", sid));
            formPairs.Add(new("length-" + sid, lengthVal));
            formPairs.Add(new("length2-" + sid, ""));
            formPairs.Add(new("absencetype_unreported_" + sid, "1"));
        }

        // Debug: show key fields
        Console.WriteLine($"  [POST] lesson={detail.LessonId}, status={lessonStatus}, old_status={lessonStatus}");
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
