using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI;
using OpenAI.Chat;

namespace ApiForge.Utilities;

public class LlmService
{
    private ChatClient? _client;
    private string _defaultModel = "gpt-4o";
    private string _alternateModel = "o1-preview";
    private string? _apiKey;
    private Uri? _endpoint;

    /// <summary>
    /// Maximum prompt length in characters. 0 = no limit.
    /// For local models with limited context windows, set to e.g. 24000 (~6K tokens).
    /// </summary>
    public int MaxPromptChars { get; set; } = 0;

    public LlmService(string? apiKey = null, string? endpoint = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _endpoint = !string.IsNullOrEmpty(endpoint)
            ? new Uri(endpoint)
            : Environment.GetEnvironmentVariable("OPENAI_BASE_URL") is string envUrl && !string.IsNullOrEmpty(envUrl)
                ? new Uri(envUrl)
                : null;

        // Auto-set limit for local models
        if (_endpoint != null)
        {
            MaxPromptChars = 8000; // ~2K tokens, safe for small local models
        }
    }

    public void SetDefaultModel(string model)
    {
        _defaultModel = model;
        _client = null;
    }

    private ChatClient GetClient()
    {
        if (_client == null)
        {
            _client = CreateChatClient(_defaultModel);
        }
        return _client;
    }

    public ChatClient GetAlternateClient()
    {
        return CreateChatClient(_alternateModel);
    }

    private ChatClient CreateChatClient(string model)
    {
        if (_endpoint != null)
        {
            var options = new OpenAIClientOptions { Endpoint = _endpoint };
            var key = _apiKey ?? "lm-studio";
            return new ChatClient(model, new System.ClientModel.ApiKeyCredential(key), options);
        }
        return new ChatClient(model, _apiKey);
    }

    /// <summary>
    /// Reverts the alternate model to the default model.
    /// </summary>
    public void RevertToDefaultModel()
    {
        Console.WriteLine($"Reverting to default model: {_defaultModel}. Performance will be degraded as ApiForge is using non O1 model");
        _alternateModel = _defaultModel;
    }

    /// <summary>
    /// Truncates a prompt to fit within MaxPromptChars if set.
    /// </summary>
    private string TruncatePrompt(string prompt)
    {
        if (MaxPromptChars <= 0 || prompt.Length <= MaxPromptChars)
            return prompt;

        Console.WriteLine($"[LlmService] Prompt truncated from {prompt.Length} to {MaxPromptChars} chars");
        return prompt[..MaxPromptChars] + "\n\n[...truncated due to context limit]";
    }

    /// <summary>
    /// Invokes the model with a prompt and a function definition, forcing the model to call the specified function.
    /// Returns the parsed function-call arguments as a JsonElement.
    /// Falls back to text-based JSON extraction if function calling is not supported (e.g. local models).
    /// </summary>
    public async Task<JsonElement> InvokeWithFunctionAsync(
        string prompt,
        string functionName,
        string functionDescription,
        JsonElement parametersSchema)
    {
        prompt = TruncatePrompt(prompt);
        var client = GetClient();

        // Try native function calling first
        try
        {
            var tool = ChatTool.CreateFunctionTool(
                functionName,
                functionDescription,
                BinaryData.FromString(parametersSchema.GetRawText()));

            var options = new ChatCompletionOptions();
            options.Tools.Add(tool);
            options.ToolChoice = ChatToolChoice.CreateFunctionChoice(functionName);

            var messages = new List<ChatMessage>
            {
                new UserChatMessage(prompt)
            };

            ChatCompletion completion = await client.CompleteChatAsync(messages, options);

            var toolCall = completion.ToolCalls.FirstOrDefault();
            if (toolCall != null)
            {
                using var argsDoc = JsonDocument.Parse(toolCall.FunctionArguments);
                return argsDoc.RootElement.Clone();
            }
        }
        catch (Exception ex) when (ex.Message.Contains("400") || ex.Message.Contains("Bad Request") ||
                                    ex.Message.Contains("not supported") || ex.Message.Contains("tool"))
        {
            Console.WriteLine("[LlmService] Function calling not supported, falling back to JSON prompt...");
        }

        // Fallback: ask the model to return JSON in the response text
        return await InvokeWithJsonFallbackAsync(prompt, functionName, parametersSchema);
    }

    /// <summary>
    /// Fallback for models that don't support function calling.
    /// Asks the model to return a JSON object matching the schema directly in text.
    /// </summary>
    private async Task<JsonElement> InvokeWithJsonFallbackAsync(
        string prompt,
        string functionName,
        JsonElement parametersSchema)
    {
        var schemaText = parametersSchema.GetRawText();
        var client = GetClient();

        // Use system message to enforce JSON output + user prompt
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are a JSON API. You ONLY output valid JSON objects. No text, no markdown, no explanation. Just the JSON object."),
            new UserChatMessage($@"{prompt}

Respond with ONLY a JSON object matching this schema:
{schemaText}")
        };

        ChatCompletion completion = await client.CompleteChatAsync(messages);
        var responseText = completion.Content[0].Text;

        var json = ExtractJson(responseText);
        if (json != null)
        {
            return json.Value;
        }

        // Retry: send the response back and ask for just JSON
        Console.WriteLine("[LlmService] First response wasn't JSON, retrying...");
        messages.Add(new AssistantChatMessage(responseText));
        messages.Add(new UserChatMessage("That was not valid JSON. Reply with ONLY the JSON object, nothing else. Example: {\"url\": \"https://example.com\"}"));

        completion = await client.CompleteChatAsync(messages);
        responseText = completion.Content[0].Text;

        json = ExtractJson(responseText);
        if (json != null)
        {
            return json.Value;
        }

        throw new InvalidOperationException(
            $"Failed to extract JSON from model response for '{functionName}'. Response was: {responseText[..Math.Min(200, responseText.Length)]}");
    }

    /// <summary>
    /// Extracts a JSON object from text that may contain markdown fences or extra text.
    /// </summary>
    private static JsonElement? ExtractJson(string text)
    {
        text = text.Trim();

        // Strip markdown code fences
        if (text.StartsWith("```json"))
            text = text["```json".Length..];
        else if (text.StartsWith("```"))
            text = text["```".Length..];
        if (text.EndsWith("```"))
            text = text[..^"```".Length];
        text = text.Trim();

        // Try direct parse
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch { }

        // Try to find JSON object in the text
        var match = Regex.Match(text, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}");
        if (match.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(match.Value);
                return doc.RootElement.Clone();
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Invokes the default model with a simple text prompt and returns the response text.
    /// </summary>
    public async Task<string> InvokeAsync(string prompt)
    {
        prompt = TruncatePrompt(prompt);
        var client = GetClient();

        var messages = new List<ChatMessage>
        {
            new UserChatMessage(prompt)
        };

        try
        {
            ChatCompletion completion = await client.CompleteChatAsync(messages);
            return completion.Content[0].Text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LlmService] API error: {ex.Message}");
            if (ex.GetType().GetMethod("GetRawResponse") != null)
            {
                try
                {
                    dynamic dynEx = ex;
                    var raw = dynEx.GetRawResponse();
                    Console.WriteLine($"[LlmService] Response body: {raw?.Content}");
                }
                catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Invokes the alternate model with a text prompt. Falls back to the default model on failure.
    /// Used primarily for code generation tasks.
    /// </summary>
    public async Task<string> InvokeWithAlternateModelAsync(string prompt)
    {
        prompt = TruncatePrompt(prompt);
        var altClient = GetAlternateClient();

        var messages = new List<ChatMessage>
        {
            new UserChatMessage(prompt)
        };

        try
        {
            ChatCompletion completion = await altClient.CompleteChatAsync(messages);
            return completion.Content[0].Text;
        }
        catch
        {
            Console.WriteLine("Switching to default model");
            return await InvokeAsync(prompt);
        }
    }
}
