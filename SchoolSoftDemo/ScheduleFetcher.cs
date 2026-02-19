using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Browser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace SchoolSoftDemo;

public class Lesson
{
    public int LessonId { get; set; }
    public string Subject { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string Room { get; set; } = "";
    public string Group { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Weeks { get; set; } = "";
    public DayOfWeek Day { get; set; }
    public int Week { get; set; }

    public override string ToString()
        => $"{Day,-10} {StartTime}-{EndTime}  {Subject,-20} {Room,-16} {Group}";
}

public class ScheduleFetcher
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public ScheduleFetcher(HttpClient client, string baseUrl)
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Fetches lessons for a specific week.
    /// </summary>
    public async Task<List<Lesson>> GetWeekScheduleAsync(int week, string schoolSlug)
    {
        var url = $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp?teachersubstitute=0&week={week}";
        var bytes = await _client.GetByteArrayAsync(url);
        var html = DecodeHtml(bytes);
        return ParseScheduleHtml(html, week);
    }

    /// <summary>
    /// Fetches lessons for multiple weeks.
    /// </summary>
    public async Task<List<Lesson>> GetScheduleRangeAsync(int fromWeek, int toWeek, string schoolSlug)
    {
        var allLessons = new List<Lesson>();
        for (int w = fromWeek; w <= toWeek; w++)
        {
            var lessons = await GetWeekScheduleAsync(w, schoolSlug);
            allLessons.AddRange(lessons);
        }
        return allLessons;
    }

    /// <summary>
    /// Parses the lesson status HTML and extracts all lessons with day/time/subject/room.
    /// </summary>
    public static List<Lesson> ParseScheduleHtml(string html, int week)
    {
        var lessons = new List<Lesson>();

        var config = Configuration.Default.Without<EncodingMetaHandler>();
        var context = BrowsingContext.New(config);
        var document = context.OpenAsync(req => req.Content(html)).Result;

        var table = document.QuerySelector("table.tab_dark");
        if (table == null)
            return lessons;

        var rows = table.QuerySelectorAll("tr");

        // Track occupied cells: grid[row][col] = true if occupied by a rowspan from above
        var occupied = new Dictionary<int, HashSet<int>>();

        for (int rowIdx = 0; rowIdx < rows.Length; rowIdx++)
        {
            if (!occupied.ContainsKey(rowIdx))
                occupied[rowIdx] = new HashSet<int>();

            var cells = rows[rowIdx].QuerySelectorAll("td");
            int col = 0;

            foreach (var cell in cells)
            {
                // Skip columns occupied by rowspan from previous rows
                while (occupied[rowIdx].Contains(col))
                    col++;

                int colspan = GetIntAttr(cell, "colspan", 1);
                int rowspan = GetIntAttr(cell, "rowspan", 1);

                // Mark cells as occupied for rowspan
                for (int r = 0; r < rowspan; r++)
                {
                    for (int c = 0; c < colspan; c++)
                    {
                        int futureRow = rowIdx + r;
                        if (!occupied.ContainsKey(futureRow))
                            occupied[futureRow] = new HashSet<int>();
                        occupied[futureRow].Add(col + c);
                    }
                }

                // Check if this cell contains a lesson
                var div = cell.QuerySelector("div[title]");
                if (div != null)
                {
                    var lesson = ParseLessonFromDiv(div, col, week);
                    if (lesson != null)
                        lessons.Add(lesson);
                }

                col += colspan;
            }
        }

        return lessons;
    }

    private static Lesson? ParseLessonFromDiv(IElement div, int column, int week)
    {
        var title = div.GetAttribute("title") ?? "";

        // Parse: header=[ 8:30-9:50 SUBJECT] body=[Personal: JN<br />Sal: ROOM<br />Grupp: GROUP<br />Veckor: ...]
        var headerMatch = Regex.Match(title, @"header=\[\s*(\d+:\d+)-(\d+:\d+)\s+(.+?)\]");
        if (!headerMatch.Success)
            return null;

        var lesson = new Lesson
        {
            StartTime = headerMatch.Groups[1].Value,
            EndTime = headerMatch.Groups[2].Value,
            Subject = headerMatch.Groups[3].Value.Trim(),
            Week = week,
            Day = ColumnToDay(column)
        };

        // Parse body fields
        var bodyMatch = Regex.Match(title, @"body=\[(.+)\]", RegexOptions.Singleline);
        if (bodyMatch.Success)
        {
            var body = bodyMatch.Groups[1].Value;

            var personal = Regex.Match(body, @"Personal:\s*(.+?)(?:<br\s*/?>|$)");
            if (personal.Success)
                lesson.Teacher = personal.Groups[1].Value.Trim();

            var room = Regex.Match(body, @"Sal:\s*(.+?)(?:<br\s*/?>|$)");
            if (room.Success)
                lesson.Room = room.Groups[1].Value.Trim();

            var group = Regex.Match(body, @"Grupp:\s*(.+?)(?:<br\s*/?>|$)");
            if (group.Success)
                lesson.Group = group.Groups[1].Value.Trim();

            var weeks = Regex.Match(body, @"Veckor:\s*(.+?)(?:<br\s*/?>|$)");
            if (weeks.Success)
                lesson.Weeks = weeks.Groups[1].Value.Trim();
        }

        // Extract lesson ID from link
        var link = div.QuerySelector("a[href]");
        if (link != null)
        {
            var href = link.GetAttribute("href") ?? "";
            var idMatch = Regex.Match(href, @"lesson=(\d+)");
            if (idMatch.Success)
                lesson.LessonId = int.Parse(idMatch.Groups[1].Value);
        }

        return lesson;
    }

    /// <summary>
    /// Maps column index to day of week.
    /// Columns: 0=time, 1-4=Mon, 5-8=Tue, 9-12=Wed, 13-16=Thu, 17-20=Fri
    /// </summary>
    private static DayOfWeek ColumnToDay(int col) => col switch
    {
        >= 1 and <= 4 => DayOfWeek.Monday,
        >= 5 and <= 8 => DayOfWeek.Tuesday,
        >= 9 and <= 12 => DayOfWeek.Wednesday,
        >= 13 and <= 16 => DayOfWeek.Thursday,
        >= 17 and <= 20 => DayOfWeek.Friday,
        _ => DayOfWeek.Monday
    };

    private static int GetIntAttr(IElement el, string attr, int defaultVal)
    {
        var val = el.GetAttribute(attr);
        return val != null && int.TryParse(val, out var n) ? n : defaultVal;
    }

    /// <summary>
    /// Gets the current ISO week number.
    /// </summary>
    public static int GetCurrentWeek()
    {
        return ISOWeek.GetWeekOfYear(DateTime.Now);
    }

    /// <summary>
    /// Decodes raw HTML bytes: tries UTF-8 first, falls back to Windows-1252.
    /// </summary>
    internal static string DecodeHtml(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        if (!utf8.Contains('\uFFFD'))
            return utf8;
        return Encoding.GetEncoding(1252).GetString(bytes);
    }
}
