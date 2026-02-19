using ApiForge.Agents;
using ApiForge.Models;
using ApiForge.Utilities;

namespace ApiForge;

/// <summary>
/// The main public API surface for the ApiForge library.
/// Provides methods to analyze HAR files, build dependency graphs,
/// and record browser sessions.
/// </summary>
public class ApiForgeClient
{
    private readonly LlmService _llmService;

    /// <summary>
    /// Creates a new ApiForgeClient instance.
    /// </summary>
    /// <param name="openAiApiKey">
    /// Optional API key. If null, falls back to the OPENAI_API_KEY environment variable.
    /// </param>
    /// <param name="model">
    /// The LLM model to use (default is "gpt-4o").
    /// </param>
    /// <param name="endpoint">
    /// Optional custom API endpoint (e.g. "http://127.0.0.1:1234/v1" for LM Studio).
    /// Falls back to OPENAI_BASE_URL environment variable.
    /// </param>
    public ApiForgeClient(string? openAiApiKey = null, string model = "gpt-4o", string? endpoint = null)
    {
        _llmService = new LlmService(openAiApiKey, endpoint);
        _llmService.SetDefaultModel(model);
    }

    /// <summary>
    /// Analyzes a HAR file and builds a dependency graph (DAG) representing the
    /// integration flow for the described action.
    /// </summary>
    /// <param name="prompt">A description of the action to analyze (e.g., "send a message").</param>
    /// <param name="harFilePath">Path to the HAR file containing recorded network requests.</param>
    /// <param name="cookiePath">Path to the cookies JSON file.</param>
    /// <param name="inputVariables">Optional dictionary of input variables to seed the analysis.</param>
    /// <param name="maxSteps">Maximum total node executions (default 20).</param>
    /// <param name="generateCode">Whether to generate integration code from the resulting DAG.</param>
    /// <returns>An <see cref="ApiForgeResult"/> containing the DAG and master node ID.</returns>
    public async Task<ApiForgeResult> AnalyzeAsync(
        string prompt,
        string harFilePath = "network_requests.har",
        string cookiePath = "cookies.json",
        Dictionary<string, string>? inputVariables = null,
        int maxSteps = 20,
        bool generateCode = false)
    {
        var (dag, agent) = await GraphBuilder.BuildAndRunAsync(
            prompt,
            harFilePath,
            cookiePath,
            _llmService,
            inputVariables,
            maxSteps,
            generateCode);

        return new ApiForgeResult
        {
            DagManager = dag,
            MasterNodeId = agent.GlobalMasterNodeId
        };
    }

    /// <summary>
    /// Opens a browser window for the user to perform actions, then records the
    /// network traffic as a HAR file and saves cookies.
    /// </summary>
    /// <param name="harFilePath">Path to save the HAR file (default: network_requests.har).</param>
    /// <param name="cookieFilePath">Path to save the cookies JSON file (default: cookies.json).</param>
    /// <param name="startUrl">Optional URL to navigate to when the browser opens.</param>
    /// <param name="cookies">Optional Playwright cookies to inject into the browser context.</param>
    public async Task RecordHarAsync(
        string harFilePath = "network_requests.har",
        string cookieFilePath = "cookies.json",
        string? startUrl = null,
        IEnumerable<Microsoft.Playwright.Cookie>? cookies = null)
    {
        var recorder = new HarRecorder();
        await recorder.RecordAsync(harFilePath, cookieFilePath, startUrl: startUrl, cookies: cookies);
    }
}

/// <summary>
/// Contains the results of an ApiForge analysis run.
/// </summary>
public class ApiForgeResult
{
    /// <summary>
    /// The DAG manager containing the complete dependency graph.
    /// </summary>
    public DagManager DagManager { get; set; } = null!;

    /// <summary>
    /// The ID of the master (root) node in the DAG, or null if no master node was identified.
    /// </summary>
    public string? MasterNodeId { get; set; }
}
