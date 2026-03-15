using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GoogleClassroomDemo;

/// <summary>
/// Google Classroom API client that uses the internal batchexecute RPC protocol.
/// Works for any authenticated user — session tokens are extracted from the initial page load.
/// </summary>
public class GoogleClassroomClient
{
    private const string BaseUrl = "https://classroom.google.com";
    private const string BatchExecutePath = "/u/0/_/ClassroomUi/data/batchexecute";

    // Field masks extracted from real Classroom traffic — these tell the server which fields to return
    private const string CourseFieldMask = "[1,1,null,null,1,1,null,1,[null,1,1,1,null,1,[1,1,1],null,[1],1,1,1,1,[1,1]],1,1,null,null,1,null,null,null,null,null,null,[1,1,1,1,1,0,0],1,null,null,1,null,null,null,[1,1],1,1,null,null,1,1,1,1,null,1,1,[null,null,null,null,1,1,null,null,1,null,null,null,null,null,null,null,null,1,1,1,1,1,0],null,[1,1,1],[1,1,null,1],1,1,1,[[[1,1,1],1],[[[1,1]]],1],null,1,[1,1],null,null,1,null,null,0]";
    private const string UserFieldMask = "[1,1,null,1,null,1,null,null,1,1,1,1,null,null,1]";
    private const string SubmissionFieldMask = "[null,1,null,null,1,null,1,1,1,null,null,null,1,null,[1],null,null,null,1,1,1,null,null,null,[[1,1]],[[1,1]],[1,[[1,1,[],[null,1,1]],1,1,1]],null,null,null,null,1]";
    private const string CommentFieldMask = "[1,1,1,1,null,null,[1],1,null,3,[1]]";
    private const string AssignmentFieldMask = "[[1,1,1,1,1,null,null,[1,1,1,null,1,1,1],1,1,1,1,1,1,[1],null,null,null,1,3,null,null,1,[1],0],[1,1,1,1,1,1,[1],1,null,[1,1],1,1,null,1,[[1,1,[],[null,1]],1,1],null,null,null,1],[null,1],null,[1,1]]";

    private readonly HttpClient _client;
    private string _fsid = "";
    private string _at = "";
    private string _bl = "";
    private int _reqId;

    public bool IsInitialized => !string.IsNullOrEmpty(_at);

    /// <summary>
    /// Creates client from a stored session (cookies from HeadlessAuthenticator).
    /// Call InitializeAsync() after construction.
    /// </summary>
    public GoogleClassroomClient(TokenManager.StoredSession session)
    {
        var cookieContainer = new CookieContainer();
        foreach (var cookie in session.Cookies)
        {
            try
            {
                var domain = cookie.Domain.TrimStart('.');
                cookieContainer.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, domain)
                {
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly
                });
            }
            catch { }
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true
        };

        _client = new HttpClient(handler);
        _client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Add("Accept-Language", "en-GB,en;q=0.9,sv-SE;q=0.8");

        _reqId = Random.Shared.Next(1000, 9999);
    }

    /// <summary>
    /// Loads the Classroom page and extracts session tokens (f.sid, at, bl).
    /// Must be called before any RPC methods.
    /// </summary>
    public async Task InitializeAsync(string classroomUrl = "https://classroom.google.com")
    {
        Console.WriteLine("[Client] Laddar Classroom-sida för session-tokens...");
        var resp = await _client.GetAsync(classroomUrl);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync();

        // Extract f.sid (session ID) - found in WIZ_global_data: "FdrFJe":"<sid>"
        var sidMatch = Regex.Match(html, @"""FdrFJe""\s*:\s*""(-?\d+)""");
        if (sidMatch.Success)
            _fsid = sidMatch.Groups[1].Value;
        else
        {
            sidMatch = Regex.Match(html, @"f\.sid\s*=\s*['""](-?\d+)['""]");
            if (sidMatch.Success) _fsid = sidMatch.Groups[1].Value;
        }

        // Extract at (CSRF/XSRF token) - "SNlM0e":"token_value"
        var atMatch = Regex.Match(html, @"""SNlM0e""\s*:\s*""([^""]+)""");
        if (atMatch.Success)
            _at = atMatch.Groups[1].Value;

        // Extract bl (build label) - "cfb2h":"boq_apps-edu-..."
        var blMatch = Regex.Match(html, @"""cfb2h""\s*:\s*""([^""]+)""");
        if (blMatch.Success)
            _bl = blMatch.Groups[1].Value;

        if (string.IsNullOrEmpty(_at))
            throw new InvalidOperationException("Kunde inte extrahera CSRF-token (at) från sidan. Session kan vara ogiltig.");

        Console.WriteLine($"[Client] f.sid: {_fsid}");
        Console.WriteLine($"[Client] at:    {_at[..Math.Min(20, _at.Length)]}...");
        Console.WriteLine($"[Client] bl:    {_bl}");
        Console.WriteLine("[Client] Redo!");
    }

    // ========== RPC Methods ==========

    /// <summary>
    /// Gets all courses for the current user.
    /// RPC: gXtzob
    /// </summary>
    public async Task<JsonElement> GetCoursesAsync(int pageSize = 100)
    {
        var rpcParams = $"[[{pageSize},null,1,0],{CourseFieldMask},[[null,[[1]]],null,null,[1,2,3],null,null,[1,2]]]";
        return await CallRpcAsync("gXtzob", rpcParams, "/u/0");
    }

    /// <summary>
    /// Gets a single course by ID (continuation/lookup).
    /// RPC: gXtzob
    /// </summary>
    public async Task<JsonElement> GetCourseAsync(long courseId)
    {
        var rpcParams = $"[[null,null,1,0],{CourseFieldMask},[null,[[{courseId}]],null,null,null,null,[1,2]]]";
        return await CallRpcAsync("gXtzob", rpcParams, "/u/0");
    }

    /// <summary>
    /// Gets user profiles by IDs.
    /// RPC: UG41I
    /// </summary>
    public async Task<JsonElement> GetUsersAsync(params long[] userIds)
    {
        var userArrayItems = string.Join(",", userIds.Select(id => $"[null,[{id}]]"));
        var rpcParams = $"[[null,null,1,0],{UserFieldMask},[[null,[{userArrayItems}]]]]";
        return await CallRpcAsync("UG41I", rpcParams, "/u/0");
    }

    /// <summary>
    /// Gets assignment/coursework details.
    /// RPC: tQShAc
    /// </summary>
    public async Task<JsonElement> GetAssignmentAsync(long assignmentId, long courseId)
    {
        var rpcParams = $"[[[{assignmentId},[{courseId}]]],[{AssignmentFieldMask},{AssignmentFieldMask.Split("],[null,1]")[0]}],{AssignmentFieldMask.Split(",[null,1]")[0]},[1]],null,null,[{AssignmentFieldMask.Split("],[null,1]")[0]}]]]";
        // Use the exact params from HAR
        var exactParams = $"[[[[{assignmentId},[{courseId}]]]],[{AssignmentFieldMask},[[1,1,1,1,1,null,null,[1,1,1,null,1,1,1],1,1,1,1,1,1,[1],null,null,null,1,3,null,null,1,[1],0]],[[1,1,1,1,1,null,null,[1,1,1,null,1,1,1],1,1,1,1,1,1,[1],null,null,null,1,3,null,null,1,[1],0],[1,1,1,1,1,1,[1],1,null,[1,1],1,1,null,1,[[1,1,[],[null,1]],1,1],null,null,null,1],[1]],null,null,[[1,1,1,1,1,null,null,[1,1,1,null,1,1,1],1,1,1,1,1,1,[1],null,null,null,1,3,null,null,1,[1],0]]]]";
        return await CallRpcAsync("tQShAc", exactParams, $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}");
    }

    /// <summary>
    /// Gets all submissions for an assignment.
    /// RPC: Zj93ge
    /// </summary>
    public async Task<JsonElement> GetSubmissionsAsync(long assignmentId, long courseId, int pageSize = 100)
    {
        var rpcParams = $"[[{pageSize},null,1,0],{SubmissionFieldMask},[null,[[{assignmentId},[{courseId}]]]],0]";
        return await CallRpcAsync("Zj93ge", rpcParams,
            $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}/submissions");
    }

    /// <summary>
    /// Gets a specific student's submission.
    /// RPC: Zj93ge
    /// </summary>
    public async Task<JsonElement> GetStudentSubmissionAsync(long studentId, long assignmentId, long courseId)
    {
        var rpcParams = $"[[null,null,1,0],[null,1,null,1],[null,null,[[{studentId},[{assignmentId},[{courseId}]]]]],1]";
        return await CallRpcAsync("Zj93ge", rpcParams,
            $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}/submissions/by-status/and-sort-last-name/student/{Base64EncodeId(studentId)}");
    }

    /// <summary>
    /// Gets private comments on a student's submission.
    /// RPC: sLc6hf
    /// </summary>
    public async Task<JsonElement> GetCommentsAsync(long studentId, long assignmentId, long courseId, int pageSize = 100)
    {
        var rpcParams = $"[[{pageSize},null,1,0],{CommentFieldMask},[[null,null,[2,3]],null,null,[[{studentId},[{assignmentId},[{courseId}]]]],[[1]]]]";
        return await CallRpcAsync("sLc6hf", rpcParams,
            $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}/submissions/by-status/and-sort-last-name/student/{Base64EncodeId(studentId)}");
    }

    /// <summary>
    /// Posts a private comment on a student's submission.
    /// RPC: jOFnxd
    /// </summary>
    public async Task<JsonElement> PostCommentAsync(long studentId, long assignmentId, long courseId, long authorId, string commentText)
    {
        var escaped = commentText.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var rpcParams = $"[[2],[[[null,null,[{studentId},[{assignmentId},[{courseId}]]],3],[[null,null,[{studentId},[{assignmentId},[{courseId}]]],3],null,null,null,[{authorId}],null,null,null,[1],null,null,[\"edu.rt\",\"{escaped}\",null,null,[null,\"{escaped}\"]]]]],{CommentFieldMask}]";
        return await CallRpcAsync("jOFnxd", rpcParams,
            $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}/submissions/by-status/and-sort-last-name/student/{Base64EncodeId(studentId)}");
    }

    /// <summary>
    /// Gets submission timestamps / grade history for an assignment.
    /// RPC: fp5B0d
    /// </summary>
    public async Task<JsonElement> GetSubmissionTimestampsAsync(long assignmentId, long courseId)
    {
        var rpcParams = $"[[{assignmentId},[{courseId}]]]";
        return await CallRpcAsync("fp5B0d", rpcParams,
            $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}/submissions");
    }

    /// <summary>
    /// Gets permissions/capabilities for the current user on an assignment.
    /// RPC: cCzVwf
    /// </summary>
    public async Task<JsonElement> GetAssignmentPermissionsAsync(long assignmentId, long courseId)
    {
        var rpcParams = $"[[100,null,1,0],null,[1,1,1,1,1,1,1,1,1,1,1,1,[1,1,1,1],1,1,[1],1],[[{assignmentId},[{courseId}]]]]";
        return await CallRpcAsync("cCzVwf", rpcParams,
            $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}/submissions");
    }

    /// <summary>
    /// Gets the teacher's Drive folder for an assignment.
    /// RPC: O1Xqee
    /// </summary>
    public async Task<JsonElement> GetTeacherFolderAsync(long courseId, long teacherId)
    {
        var rpcParams = $"[[null,null,1,0],[1,[null,1],[1],1,1,1,1,1,1,1,1,1],[null,[[[{courseId}],[{teacherId}]]]]]";
        return await CallRpcAsync("O1Xqee", rpcParams, "/u/0");
    }

    /// <summary>
    /// Gets rubric for an assignment.
    /// RPC: Bypadc
    /// </summary>
    public async Task<JsonElement> GetRubricAsync(long courseId, long assignmentId)
    {
        var rpcParams = $"[[null,[[{courseId},{assignmentId}]]],[10,\"\"]]";
        return await CallRpcAsync("Bypadc", rpcParams,
            $"/u/0/c/{Base64EncodeId(courseId)}/a/{Base64EncodeId(assignmentId)}/submissions");
    }

    // ========== Core RPC Engine ==========

    /// <summary>
    /// Calls a Google batchexecute RPC and returns the parsed response data.
    /// </summary>
    private async Task<JsonElement> CallRpcAsync(string rpcId, string rpcParams, string sourcePath)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Client ej initialiserad. Anropa InitializeAsync() först.");

        var reqId = _reqId;
        _reqId += 100000;

        // Build f.req payload: [[["rpcId","<escaped_params>",null,"generic"]]]
        var fReq = $"[[[\"{rpcId}\",\"{EscapeJsonString(rpcParams)}\",null,\"generic\"]]]";

        // Build query string
        var qs = new StringBuilder();
        qs.Append($"rpcids={rpcId}");
        qs.Append($"&source-path={Uri.EscapeDataString(sourcePath)}");
        qs.Append($"&f.sid={_fsid}");
        qs.Append($"&bl={Uri.EscapeDataString(_bl)}");
        qs.Append("&hl=en-GB&soc-app=1&soc-platform=1&soc-device=1");
        qs.Append($"&_reqid={reqId}&rt=c");

        var url = $"{BaseUrl}{BatchExecutePath}?{qs}";

        // Build form body
        var body = $"f.req={Uri.EscapeDataString(fReq)}&at={Uri.EscapeDataString(_at)}";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.Add("X-Same-Domain", "1");

        var resp = await _client.SendAsync(request);
        var responseText = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[RPC] {rpcId} → HTTP {(int)resp.StatusCode}");
            resp.EnsureSuccessStatusCode();
        }

        return ParseBatchResponse(responseText, rpcId);
    }

    /// <summary>
    /// Parses the batchexecute response format:
    /// )]}'              ← anti-XSSI prefix
    /// <length>
    /// [["wrb.fr","rpcId","<json_data>",null,...]]
    /// </summary>
    private static JsonElement ParseBatchResponse(string responseText, string rpcId)
    {
        // Remove anti-XSSI prefix
        var lines = responseText.Split('\n');
        var dataLines = new List<string>();
        bool pastPrefix = false;

        foreach (var line in lines)
        {
            if (!pastPrefix)
            {
                if (line.TrimStart().StartsWith(")]}'"))
                {
                    pastPrefix = true;
                    continue;
                }
                continue;
            }

            // Skip length-prefix lines (just digits)
            if (long.TryParse(line.Trim(), out _))
                continue;

            if (!string.IsNullOrWhiteSpace(line))
                dataLines.Add(line);
        }

        // Find the wrb.fr chunk containing our RPC response
        foreach (var dataLine in dataLines)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(dataLine);
                if (parsed.ValueKind != JsonValueKind.Array) continue;

                foreach (var outerItem in parsed.EnumerateArray())
                {
                    if (outerItem.ValueKind != JsonValueKind.Array) continue;
                    var arr = outerItem;

                    if (arr.GetArrayLength() >= 3 &&
                        arr[0].ValueKind == JsonValueKind.String &&
                        arr[0].GetString() == "wrb.fr" &&
                        arr[1].ValueKind == JsonValueKind.String &&
                        arr[1].GetString() == rpcId &&
                        arr[2].ValueKind == JsonValueKind.String)
                    {
                        var jsonData = arr[2].GetString()!;
                        return JsonSerializer.Deserialize<JsonElement>(jsonData);
                    }
                }
            }
            catch { }
        }

        return JsonSerializer.Deserialize<JsonElement>("null");
    }

    // ========== Helpers ==========

    /// <summary>
    /// Escapes a JSON string for embedding inside another JSON string.
    /// The f.req payload has stringified JSON inside JSON.
    /// </summary>
    private static string EscapeJsonString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    /// <summary>
    /// Google Classroom uses base64url-encoded IDs in URLs.
    /// Converts a numeric ID to the base64 form used in Classroom URLs.
    /// </summary>
    public static string Base64EncodeId(long id)
    {
        var bytes = Encoding.UTF8.GetBytes(id.ToString());
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
