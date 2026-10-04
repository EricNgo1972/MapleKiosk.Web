using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Assistant;

/// <summary>The assistant's language model, as resolved (or as an admin is editing it).</summary>
public sealed record LlmSetup(string Provider, string Endpoint, string Model, string ApiKey);

/// <summary>The voice agent's Gemini Live setup.</summary>
public sealed record VoiceSetup(string GeminiApiKey, string Model, string Voice);

/// <summary>
/// What an admin sets for the assistant on /chats → Settings, kept in the shared keyvalue table under the
/// partition <c>Assistant</c> (rows LlmProvider, LlmEndpoint, LlmModel, LlmApiKey, GeminiApiKey, GeminiModel,
/// GeminiVoice, Knowledge). The site's own rows win; anything left blank falls back to env vars
/// (LLM_*, GEMINI_API_KEY) and then to the platform's shared LLM-AzureOpenAI rows, so the assistant works
/// before anyone opens the page.
/// </summary>
public static class AssistantSettings
{
    public const string Partition = "Assistant";
    private const string SharedLlm = "LLM-AzureOpenAI";

    public const string DefaultGeminiModel = "gemini-3.8-live";
    public const string DefaultGeminiVoice = "Aoede";

    /// <summary>Providers the factory knows, as the admin picks them.</summary>
    public static readonly string[] Providers = { "AzureOpenAI", "OpenAI", "Gemini", "Anthropic", "OpenRouter", "OpenAICompatible" };

    /// <summary>Only the admin's own rows — what the settings page shows and edits.</summary>
    public static async Task<(LlmSetup Llm, VoiceSetup Voice, string Knowledge)> ReadOwnAsync() =>
        (new LlmSetup(
            await KeyValueTable.ReadAsync(Partition, "LlmProvider"),
            await KeyValueTable.ReadAsync(Partition, "LlmEndpoint"),
            await KeyValueTable.ReadAsync(Partition, "LlmModel"),
            await KeyValueTable.ReadAsync(Partition, "LlmApiKey")),
         new VoiceSetup(
            await KeyValueTable.ReadAsync(Partition, "GeminiApiKey"),
            await KeyValueTable.ReadAsync(Partition, "GeminiModel"),
            await KeyValueTable.ReadAsync(Partition, "GeminiVoice")),
         await KeyValueTable.ReadAsync(Partition, "Knowledge"));

    /// <summary>
    /// Saves what the admin entered. A secret left blank keeps the stored one (the page never shows it);
    /// <paramref name="clearLlmKey"/>/<paramref name="clearGeminiKey"/> remove it.
    /// </summary>
    public static async Task SaveAsync(LlmSetup llm, VoiceSetup voice, string knowledge, bool clearLlmKey, bool clearGeminiKey)
    {
        await KeyValueTable.WriteAsync(Partition, "LlmProvider", llm.Provider);
        await KeyValueTable.WriteAsync(Partition, "LlmEndpoint", llm.Endpoint);
        await KeyValueTable.WriteAsync(Partition, "LlmModel", llm.Model);
        if (clearLlmKey) await KeyValueTable.WriteAsync(Partition, "LlmApiKey", null);
        else if (!string.IsNullOrWhiteSpace(llm.ApiKey)) await KeyValueTable.WriteAsync(Partition, "LlmApiKey", llm.ApiKey);

        if (clearGeminiKey) await KeyValueTable.WriteAsync(Partition, "GeminiApiKey", null);
        else if (!string.IsNullOrWhiteSpace(voice.GeminiApiKey)) await KeyValueTable.WriteAsync(Partition, "GeminiApiKey", voice.GeminiApiKey);
        await KeyValueTable.WriteAsync(Partition, "GeminiModel", voice.Model);
        await KeyValueTable.WriteAsync(Partition, "GeminiVoice", voice.Voice);
        await KeyValueTable.WriteAsync(Partition, "Knowledge", knowledge);
    }

    /// <summary>The language model the assistant uses: the admin's rows, else env, else the shared rows.</summary>
    public static async Task<LlmSetup> ResolveLlmAsync()
    {
        var own = (await ReadOwnAsync()).Llm;
        if (!string.IsNullOrWhiteSpace(own.Provider) && !string.IsNullOrWhiteSpace(own.ApiKey + own.Endpoint))
            return own with { Model = FirstModel(own.Model) };

        var provider = await KeyValueTable.ResolveAsync("LLM_PROVIDER", SharedLlm, "Provider");
        var apiKey = await KeyValueTable.ResolveAsync("LLM_API_KEY", SharedLlm, "APIKey");
        // Azure Table row keys are case-sensitive; the platform's row is "EndPoint".
        var endpoint = await KeyValueTable.ResolveAsync("LLM_ENDPOINT", SharedLlm, "EndPoint");
        if (endpoint == "") endpoint = await KeyValueTable.ResolveAsync("LLM_ENDPOINT", SharedLlm, "Endpoint");
        var models = await KeyValueTable.ResolveAsync("LLM_MODELS", SharedLlm, "Models");
        if (models == "") models = await KeyValueTable.ResolveAsync("LLM_MODELS", SharedLlm, "Model");
        return new LlmSetup(provider == "" ? "AzureOpenAI" : provider, endpoint, FirstModel(models), apiKey);
    }

    /// <summary>The voice agent's Gemini setup: the admin's rows, else env GEMINI_API_KEY, with defaults.</summary>
    public static async Task<VoiceSetup> ResolveVoiceAsync()
    {
        var own = (await ReadOwnAsync()).Voice;
        var key = own.GeminiApiKey != "" ? own.GeminiApiKey : Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim() ?? "";
        return new VoiceSetup(key,
            own.Model != "" ? own.Model : DefaultGeminiModel,
            own.Voice != "" ? own.Voice : DefaultGeminiVoice);
    }

    // "gpt-4.1-mini|gpt-5.4" → "gpt-4.1-mini": the platform's rows list models, first one is the default.
    private static string FirstModel(string models) =>
        models.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    /// <summary>"sk-…a1b2" — enough to recognise a stored key, never enough to use it.</summary>
    public static string Mask(string secret) =>
        string.IsNullOrWhiteSpace(secret) ? "" : secret.Length <= 8 ? "••••" : $"{secret[..3]}…{secret[^4..]}";
}
