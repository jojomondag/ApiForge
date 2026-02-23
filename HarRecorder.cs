using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace ApiForge;

public class HarRecorder
{
    /// <summary>
    /// Opens a browser for the user to perform actions, records HAR and cookies.
    /// When userProfilePath is set, launches Chrome directly (not through Playwright)
    /// and captures traffic via CDP — all extensions and logins are preserved.
    /// </summary>
    public async Task RecordAsync(
        string harFilePath = "network_requests.har",
        string cookieFilePath = "cookies.json",
        Func<Task>? waitForInput = null,
        string? startUrl = null,
        IEnumerable<Cookie>? cookies = null,
        string? userProfilePath = null,
        string? browserExecutablePath = null,
        string? profileDirectory = null)
    {
        if (!string.IsNullOrEmpty(userProfilePath))
        {
            await RecordWithCdpAsync(harFilePath, cookieFilePath, waitForInput, startUrl,
                userProfilePath, profileDirectory, browserExecutablePath);
        }
        else
        {
            await RecordWithPlaywrightAsync(harFilePath, cookieFilePath, waitForInput, startUrl, cookies);
        }
    }

    /// <summary>
    /// CDP mode: launches YOUR Chrome directly via Process.Start, then connects
    /// via Chrome DevTools Protocol to passively observe network traffic.
    /// Chrome runs 100% normally — all extensions, cookies, and logins work.
    /// </summary>
    private async Task RecordWithCdpAsync(
        string harFilePath,
        string cookieFilePath,
        Func<Task>? waitForInput,
        string? startUrl,
        string userProfilePath,
        string? profileDirectory,
        string? browserExecutablePath)
    {
        // 1. Find Chrome
        var chromePath = browserExecutablePath ?? FindChromePath();
        if (chromePath == null)
            throw new InvalidOperationException(
                "Chrome hittades inte. Ange browserExecutablePath eller installera Chrome.");

        // 2. Build arguments — Chrome launches as a NORMAL window, we just add debugging port
        var argParts = new List<string>
        {
            "--remote-debugging-port=9222",
            $"--user-data-dir=\"{userProfilePath}\"",
            "--disable-blink-features=AutomationControlled"
        };
        if (!string.IsNullOrEmpty(profileDirectory))
            argParts.Add($"--profile-directory=\"{profileDirectory}\"");
        if (!string.IsNullOrEmpty(startUrl))
            argParts.Add($"\"{startUrl}\"");

        // 3. Launch Chrome directly — NOT through Playwright
        Console.WriteLine($"[Recorder] Startar din Chrome: {chromePath}");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = string.Join(" ", argParts),
            UseShellExecute = false
        });
        if (process == null)
            throw new InvalidOperationException("Kunde inte starta Chrome.");

        // 4. Wait for CDP to be ready
        string? browserWsUrl = null;
        using var http = new HttpClient();
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            try
            {
                var versionJson = await http.GetStringAsync("http://localhost:9222/json/version");
                using var doc = JsonDocument.Parse(versionJson);
                browserWsUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                break;
            }
            catch { }
        }
        if (browserWsUrl == null)
            throw new InvalidOperationException("Chrome CDP svarade inte inom 15 sekunder.");

        Console.WriteLine("[Recorder] CDP-anslutning klar.");

        // 5. Track network requests from all targets
        var requests = new ConcurrentDictionary<string, CdpNetworkRequest>();
        var cdpClients = new List<(CdpClient client, string targetType, string targetUrl)>();
        var connectedTargetIds = new HashSet<string>();

        // 6. Connect to all available targets (pages, service workers, etc.)
        await ConnectToTargets(http, cdpClients, connectedTargetIds, requests);

        // 7. Poll for new targets (service workers may start later)
        var pollCts = new CancellationTokenSource();
        var pollTask = PollForNewTargets(http, cdpClients, connectedTargetIds, requests, pollCts.Token);

        // 8. Wait for user to perform actions
        Console.WriteLine();
        Console.WriteLine("Webbläsaren är öppen. Utför dina åtgärder, tryck sedan Enter för att spara och stänga...");

        if (waitForInput != null)
            await waitForInput();
        else
            Console.ReadLine();

        // 9. Stop polling
        pollCts.Cancel();
        try { await pollTask; } catch { }

        // 10. Collect response bodies for completed requests
        Console.WriteLine("[Recorder] Hämtar response-data...");
        await CollectResponseBodies(requests);

        // 11. Build and save HAR
        var completedRequests = requests.Values
            .Where(r => r.Status.HasValue)
            .OrderBy(r => r.WallTime)
            .ToList();

        var har = BuildHarJson(completedRequests);
        await File.WriteAllTextAsync(harFilePath, har);
        Console.WriteLine($"[Recorder] {completedRequests.Count} requests sparade till {harFilePath}");

        // 12. Save cookies via CDP
        await SaveCookiesViaCdp(cdpClients, cookieFilePath);

        // 13. Disconnect (does NOT close Chrome)
        foreach (var (client, _, _) in cdpClients)
            await client.DisposeAsync();

        Console.WriteLine("[Recorder] Klar! Chrome körs fortfarande.");
    }

    private async Task ConnectToTargets(
        HttpClient http,
        List<(CdpClient client, string targetType, string targetUrl)> cdpClients,
        HashSet<string> connectedTargetIds,
        ConcurrentDictionary<string, CdpNetworkRequest> requests)
    {
        try
        {
            var targetsJson = await http.GetStringAsync("http://localhost:9222/json");
            using var doc = JsonDocument.Parse(targetsJson);

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                var id = target.GetProperty("id").GetString()!;
                var type = target.GetProperty("type").GetString()!;
                var url = target.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var wsUrl = target.TryGetProperty("webSocketDebuggerUrl", out var ws) ? ws.GetString() : null;

                if (wsUrl == null || connectedTargetIds.Contains(id)) continue;
                if (type is not ("page" or "service_worker" or "background_page")) continue;

                try
                {
                    var client = new CdpClient();
                    await client.ConnectAsync(wsUrl);
                    await client.SendAsync("Network.enable");

                    SetupNetworkEvents(client, requests);
                    cdpClients.Add((client, type, url));
                    connectedTargetIds.Add(id);

                    Console.WriteLine($"[Recorder] Ansluten till {type}: {TruncateUrl(url)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Recorder] Kunde inte ansluta till {type}: {ex.Message}");
                }
            }
        }
        catch { }
    }

    private async Task PollForNewTargets(
        HttpClient http,
        List<(CdpClient client, string targetType, string targetUrl)> cdpClients,
        HashSet<string> connectedTargetIds,
        ConcurrentDictionary<string, CdpNetworkRequest> requests,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch { break; }
            await ConnectToTargets(http, cdpClients, connectedTargetIds, requests);
        }
    }

    private void SetupNetworkEvents(CdpClient client, ConcurrentDictionary<string, CdpNetworkRequest> requests)
    {
        client.EventReceived += (method, data) =>
        {
            try
            {
                var key = $"{client.Id}:";
                switch (method)
                {
                    case "Network.requestWillBeSent":
                    {
                        var requestId = data.GetProperty("requestId").GetString()!;
                        var request = data.GetProperty("request");
                        var entry = new CdpNetworkRequest
                        {
                            RequestId = requestId,
                            Url = request.GetProperty("url").GetString()!,
                            Method = request.GetProperty("method").GetString()!,
                            Timestamp = data.GetProperty("timestamp").GetDouble(),
                            WallTime = data.TryGetProperty("wallTime", out var wt) ? wt.GetDouble() : 0,
                            SourceClient = client
                        };

                        if (request.TryGetProperty("headers", out var headers))
                            entry.RequestHeaders = ParseHeaders(headers);
                        if (request.TryGetProperty("postData", out var pd))
                            entry.PostData = pd.GetString();

                        requests[key + requestId] = entry;
                        break;
                    }
                    case "Network.responseReceived":
                    {
                        var requestId = data.GetProperty("requestId").GetString()!;
                        if (requests.TryGetValue(key + requestId, out var entry))
                        {
                            var response = data.GetProperty("response");
                            entry.Status = response.GetProperty("status").GetInt32();
                            entry.StatusText = response.TryGetProperty("statusText", out var st)
                                ? st.GetString() ?? "" : "";
                            entry.MimeType = response.TryGetProperty("mimeType", out var mt)
                                ? mt.GetString() : null;
                            entry.ResponseTimestamp = data.GetProperty("timestamp").GetDouble();

                            if (response.TryGetProperty("headers", out var headers))
                                entry.ResponseHeaders = ParseHeaders(headers);
                        }
                        break;
                    }
                    case "Network.loadingFinished":
                    {
                        var requestId = data.GetProperty("requestId").GetString()!;
                        if (requests.TryGetValue(key + requestId, out var entry))
                        {
                            entry.EndTimestamp = data.GetProperty("timestamp").GetDouble();
                            entry.IsComplete = true;
                        }
                        break;
                    }
                    case "Network.loadingFailed":
                    {
                        var requestId = data.GetProperty("requestId").GetString()!;
                        if (requests.TryGetValue(key + requestId, out var entry))
                        {
                            entry.IsComplete = true;
                        }
                        break;
                    }
                }
            }
            catch { }
        };
    }

    private async Task CollectResponseBodies(ConcurrentDictionary<string, CdpNetworkRequest> requests)
    {
        var textMimeTypes = new[] { "application/json", "text/", "application/xml",
            "application/javascript", "application/graphql", "application/x-www-form-urlencoded" };

        var tasks = requests.Values
            .Where(r => r.IsComplete && r.Status.HasValue && r.SourceClient?.IsConnected == true)
            .Where(r => r.MimeType != null && textMimeTypes.Any(t => r.MimeType!.Contains(t)))
            .Select(async entry =>
            {
                try
                {
                    var result = await entry.SourceClient!.SendAsync("Network.getResponseBody",
                        new { requestId = entry.RequestId });

                    if (result.HasValue)
                    {
                        entry.ResponseBody = result.Value.TryGetProperty("body", out var body)
                            ? body.GetString() : null;
                        entry.IsBase64 = result.Value.TryGetProperty("base64Encoded", out var b64)
                            && b64.GetBoolean();
                    }
                }
                catch { }
            });

        await Task.WhenAll(tasks);
    }

    private string BuildHarJson(List<CdpNetworkRequest> requests)
    {
        var entries = new List<object>();

        foreach (var req in requests)
        {
            var startTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(req.WallTime * 1000));
            var totalTime = req.EndTimestamp.HasValue
                ? (req.EndTimestamp.Value - req.Timestamp) * 1000 : 0;

            var requestHeaders = req.RequestHeaders
                .Select(h => new { name = h.Key, value = h.Value }).ToList();
            var responseHeaders = req.ResponseHeaders
                .Select(h => new { name = h.Key, value = h.Value }).ToList();

            var requestObj = new Dictionary<string, object?>
            {
                ["method"] = req.Method,
                ["url"] = req.Url,
                ["httpVersion"] = "HTTP/1.1",
                ["headers"] = requestHeaders,
                ["queryString"] = new List<object>(),
                ["cookies"] = new List<object>(),
                ["headersSize"] = -1,
                ["bodySize"] = req.PostData?.Length ?? -1
            };
            if (req.PostData != null)
                requestObj["postData"] = new { mimeType = "application/x-www-form-urlencoded", text = req.PostData };

            var entry = new Dictionary<string, object>
            {
                ["startedDateTime"] = startTime.ToString("o"),
                ["time"] = totalTime,
                ["request"] = requestObj,
                ["response"] = new Dictionary<string, object>
                {
                    ["status"] = req.Status ?? 0,
                    ["statusText"] = req.StatusText ?? "",
                    ["httpVersion"] = "HTTP/1.1",
                    ["headers"] = responseHeaders,
                    ["cookies"] = new List<object>(),
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["size"] = req.ResponseBody?.Length ?? 0,
                        ["mimeType"] = req.MimeType ?? "application/octet-stream",
                        ["text"] = req.ResponseBody,
                        ["encoding"] = req.IsBase64 ? "base64" : null
                    },
                    ["redirectURL"] = "",
                    ["headersSize"] = -1,
                    ["bodySize"] = -1
                },
                ["cache"] = new Dictionary<string, object>(),
                ["timings"] = new Dictionary<string, object>
                {
                    ["send"] = 0,
                    ["wait"] = totalTime,
                    ["receive"] = 0
                }
            };

            entries.Add(entry);
        }

        var har = new
        {
            log = new
            {
                version = "1.2",
                creator = new { name = "ApiForge", version = "1.0" },
                entries
            }
        };

        return JsonSerializer.Serialize(har, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private async Task SaveCookiesViaCdp(
        List<(CdpClient client, string targetType, string targetUrl)> cdpClients,
        string cookieFilePath)
    {
        var pageClient = cdpClients
            .Where(c => c.targetType == "page" && c.client.IsConnected)
            .Select(c => c.client)
            .FirstOrDefault();

        if (pageClient == null) return;

        try
        {
            var result = await pageClient.SendAsync("Network.getAllCookies");
            if (result == null) return;

            var cookies = new List<object>();
            if (result.Value.TryGetProperty("cookies", out var cookieArray))
            {
                foreach (var c in cookieArray.EnumerateArray())
                {
                    cookies.Add(new
                    {
                        name = c.GetProperty("name").GetString(),
                        value = c.GetProperty("value").GetString(),
                        domain = c.GetProperty("domain").GetString(),
                        path = c.GetProperty("path").GetString(),
                        expires = c.TryGetProperty("expires", out var exp) ? exp.GetDouble() : -1,
                        httpOnly = c.TryGetProperty("httpOnly", out var ho) && ho.GetBoolean(),
                        secure = c.TryGetProperty("secure", out var sec) && sec.GetBoolean(),
                        sameSite = c.TryGetProperty("sameSite", out var ss) ? ss.GetString() : "None"
                    });
                }
            }

            var json = JsonSerializer.Serialize(cookies, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cookieFilePath, json);
            Console.WriteLine($"[Recorder] {cookies.Count} cookies sparade till {cookieFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Recorder] Kunde inte spara cookies: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseHeaders(JsonElement headers)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in headers.EnumerateObject())
            dict[prop.Name] = prop.Value.GetString() ?? "";
        return dict;
    }

    private static string? FindChromePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string TruncateUrl(string url)
    {
        return url.Length > 80 ? url[..77] + "..." : url;
    }

    // ─── Playwright mode (standard, clean Chromium, no extensions) ───

    private async Task RecordWithPlaywrightAsync(
        string harFilePath,
        string cookieFilePath,
        Func<Task>? waitForInput,
        string? startUrl,
        IEnumerable<Cookie>? cookies)
    {
        var playwright = await Playwright.CreateAsync();

        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            RecordHarPath = harFilePath,
            RecordHarContent = HarContentPolicy.Embed
        });

        if (cookies != null)
        {
            var cookieList = cookies.ToList();
            if (cookieList.Count > 0)
            {
                await context.AddCookiesAsync(cookieList);
                Console.WriteLine($"[Recorder] {cookieList.Count} cookies injicerade i webbläsaren.");
            }
        }

        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

        if (!string.IsNullOrEmpty(startUrl))
            await page.GotoAsync(startUrl);

        Console.WriteLine("Browser is open. Perform your actions, then press Enter to save and close...");

        if (waitForInput != null)
            await waitForInput();
        else
            Console.ReadLine();

        var savedCookies = await context.CookiesAsync();
        var cookieJson = JsonSerializer.Serialize(savedCookies, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(cookieFilePath, cookieJson);

        await context.CloseAsync();
        await browser.CloseAsync();
        playwright.Dispose();
    }

    // ─── Data class for CDP network capture ───

    private class CdpNetworkRequest
    {
        public string RequestId { get; set; } = "";
        public string Url { get; set; } = "";
        public string Method { get; set; } = "";
        public Dictionary<string, string> RequestHeaders { get; set; } = new();
        public string? PostData { get; set; }
        public double Timestamp { get; set; }
        public double WallTime { get; set; }

        public int? Status { get; set; }
        public string? StatusText { get; set; }
        public Dictionary<string, string> ResponseHeaders { get; set; } = new();
        public string? MimeType { get; set; }
        public double? ResponseTimestamp { get; set; }

        public string? ResponseBody { get; set; }
        public bool IsBase64 { get; set; }

        public double? EndTimestamp { get; set; }
        public bool IsComplete { get; set; }

        public CdpClient? SourceClient { get; set; }
    }
}
