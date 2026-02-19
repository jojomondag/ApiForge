using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Browser;

namespace SchoolSoftDemo;

public class StudentAttendance
{
    public int StudentId { get; set; }
    public string Name { get; set; } = "";
    public int StatusCode { get; set; }
    public string Status { get; set; } = "Narvarande";

    public override string ToString()
        => $"  {Name,-30} {Status}";
}

public class LessonDetail
{
    public int LessonId { get; set; }
    public string Subject { get; set; } = "";
    public int SubjectId { get; set; }
    public string Time { get; set; } = "";
    public int LengthMinutes { get; set; }
    public string Day { get; set; } = "";
    public int DayIndex { get; set; }
    public string LessonStatus { get; set; } = "";
    public int LessonStatusCode { get; set; }
    public int Week { get; set; }
    public int TeacherId { get; set; }
    public List<StudentAttendance> Students { get; set; } = new();
}

public class AttendanceFetcher
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public static readonly Dictionary<int, string> StatusLabels = new()
    {
        [0] = "-",
        [1] = "Franvarande",
        [205] = "Delvis franvarande",
        [206] = "Sen ankomst",
        [751] = "Distans (deltagit)"
    };

    public AttendanceFetcher(HttpClient client, string baseUrl)
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Fetches attendance/student list for a specific lesson.
    /// </summary>
    public async Task<LessonDetail> GetLessonAttendanceAsync(int lessonId, int week, string schoolSlug)
    {
        var url = $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp?lesson={lessonId}&teachersubstitute=0&week={week}";
        var bytes = await _client.GetByteArrayAsync(url);
        var html = ScheduleFetcher.DecodeHtml(bytes);
        return ParseLessonHtml(html, lessonId, week);
    }

    /// <summary>
    /// Parses the lesson detail HTML to extract students and attendance.
    /// </summary>
    public static LessonDetail ParseLessonHtml(string html, int lessonId, int week)
    {
        var config = Configuration.Default.Without<EncodingMetaHandler>();
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(html)).Result;

        var detail = new LessonDetail
        {
            LessonId = lessonId,
            Week = week
        };

        // Extract lesson metadata from hidden fields
        var lessonInput = document.QuerySelector("input[name='lesson']");
        var weekInput = document.QuerySelector("input[name='week']");
        var timeInput = document.QuerySelector("input[name='time']");
        var lengthInput = document.QuerySelector("input[name='length']");
        var dayInput = document.QuerySelector("input[name='day']");
        var subjectInput = document.QuerySelector("input[name='subject']");

        if (timeInput != null)
            detail.Time = timeInput.GetAttribute("value") ?? "";
        if (lengthInput != null && int.TryParse(lengthInput.GetAttribute("value"), out var len))
            detail.LengthMinutes = len;
        if (subjectInput != null && int.TryParse(subjectInput.GetAttribute("value"), out var subId))
            detail.SubjectId = subId;

        var dayNames = new[] { "Mandag", "Tisdag", "Onsdag", "Torsdag", "Fredag" };
        if (dayInput != null && int.TryParse(dayInput.GetAttribute("value"), out var dayIdx) && dayIdx >= 0 && dayIdx < 5)
        {
            detail.DayIndex = dayIdx;
            detail.Day = dayNames[dayIdx];
        }

        // Extract subject from page title or header
        var titleEl = document.QuerySelector("td.LessonTeacher b");
        if (titleEl != null)
            detail.Subject = titleEl.TextContent.Trim();

        // Fallback: look for subject in the page heading area
        if (string.IsNullOrEmpty(detail.Subject))
        {
            var headerCells = document.QuerySelectorAll("td.tab_dark_top b");
            foreach (var cell in headerCells)
            {
                var text = cell.TextContent.Trim();
                if (text.Length > 2 && !text.Contains("Schema") && !text.Contains("Vecka"))
                {
                    detail.Subject = text;
                    break;
                }
            }
        }

        // Extract lesson status (Genomford/Ej genomford)
        var statusSelect = document.QuerySelector("select[name='status']")
                           ?? document.QuerySelector("select[name='lectionstatus']");
        if (statusSelect != null)
        {
            var selected = statusSelect.QuerySelector("option[selected]");
            if (selected != null)
            {
                detail.LessonStatus = selected.TextContent.Trim();
                if (int.TryParse(selected.GetAttribute("value"), out var lsc))
                    detail.LessonStatusCode = lsc;
            }
        }

        // Extract teacher ID from hidden fields (teacher-{studentId})
        var teacherInput = document.QuerySelector("input[name^='teacher-']");
        if (teacherInput != null)
        {
            var teacherVal = teacherInput.GetAttribute("value") ?? "";
            if (int.TryParse(teacherVal, out var tid))
                detail.TeacherId = tid;
        }

        // Find all student status dropdowns: name pattern "status_{studentId}"
        var statusSelects = document.QuerySelectorAll("select[name^='status_']");
        foreach (var select in statusSelects)
        {
            var name = select.GetAttribute("name") ?? "";
            var match = Regex.Match(name, @"status_(\d+)");
            if (!match.Success)
                continue;

            var studentId = int.Parse(match.Groups[1].Value);

            // Get selected status
            int statusCode = 0;
            var selectedOption = select.QuerySelector("option[selected]");
            if (selectedOption != null)
            {
                int.TryParse(selectedOption.GetAttribute("value"), out statusCode);
            }

            // Find student name - look for the table row containing this select
            var studentName = FindStudentName(select, studentId, document);

            detail.Students.Add(new StudentAttendance
            {
                StudentId = studentId,
                Name = studentName,
                StatusCode = statusCode,
                Status = StatusLabels.GetValueOrDefault(statusCode, $"Kod {statusCode}")
            });
        }

        return detail;
    }

    private static string FindStudentName(AngleSharp.Dom.IElement select, int studentId, AngleSharp.Dom.IDocument document)
    {
        // Walk up to find the containing <tr>, then look for the student name cell
        var row = select.Closest("tr");
        if (row != null)
        {
            // Student name is typically in an anchor or text in the first cells
            var links = row.QuerySelectorAll("a");
            foreach (var link in links)
            {
                var text = link.TextContent.Trim();
                if (!string.IsNullOrEmpty(text) && text.Length > 2 && !text.Contains("status"))
                {
                    return text;
                }
            }

            // Fallback: look for text in td cells
            var cells = row.QuerySelectorAll("td");
            foreach (var cell in cells)
            {
                var text = cell.TextContent.Trim();
                // Student names are typically "Lastname Firstname"
                if (text.Length > 3 && text.Contains(' ') &&
                    !text.All(c => char.IsDigit(c) || c == ':'))
                {
                    return text;
                }
            }
        }

        return $"Elev {studentId}";
    }
}
