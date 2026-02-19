using Microsoft.Playwright;
using System.Text.Json;

namespace ApiForge;

public class HarRecorder
{
    /// <summary>
    /// Opens a browser for the user to perform actions, records HAR and cookies.
    /// </summary>
    /// <param name="harFilePath">Path to save the HAR file (default: network_requests.har)</param>
    /// <param name="cookieFilePath">Path to save cookies JSON (default: cookies.json)</param>
    /// <param name="waitForInput">Action to call that blocks until user is ready. Default reads console line.</param>
    /// <param name="startUrl">Optional URL to navigate to when the browser opens.</param>
    /// <param name="cookies">Optional cookies to inject into the browser context before navigation.</param>
    public async Task RecordAsync(
        string harFilePath = "network_requests.har",
        string cookieFilePath = "cookies.json",
        Func<Task>? waitForInput = null,
        string? startUrl = null,
        IEnumerable<Cookie>? cookies = null)
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

        // Inject session cookies before navigating
        if (cookies != null)
        {
            var cookieList = cookies.ToList();
            if (cookieList.Count > 0)
            {
                await context.AddCookiesAsync(cookieList);
                Console.WriteLine($"[Recorder] {cookieList.Count} cookies injicerade i webblasaren.");
            }
        }

        var page = await context.NewPageAsync();

        if (!string.IsNullOrEmpty(startUrl))
        {
            await page.GotoAsync(startUrl);
        }

        Console.WriteLine("Browser is open. Perform your actions, then press Enter to save and close...");

        if (waitForInput != null)
            await waitForInput();
        else
            Console.ReadLine();

        // Save cookies
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
}
