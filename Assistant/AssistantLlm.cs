using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// The assistant's chat client, from <see cref="AssistantSettings.ResolveLlmAsync"/>: the admin's own
/// settings (/chats → Settings), else env LLM_*, else the platform's shared LLM-AzureOpenAI rows. Resolved
/// on first use and again after an admin saves (<see cref="Invalidate"/>); an unconfigured result is
/// retried every few minutes so rows added by hand are picked up without a restart.
/// </summary>
public sealed class AssistantLlm
{
    private static readonly TimeSpan RetryUnconfigured = TimeSpan.FromMinutes(5);

    private readonly ILogger<AssistantLlm> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChatClient? _client;
    private DateTime _resolvedAtUtc;

    public AssistantLlm(ILogger<AssistantLlm> logger) => _logger = logger;

    public async Task<IChatClient> GetAsync()
    {
        if (_client is not null && (_client is not StubChatClient || DateTime.UtcNow - _resolvedAtUtc < RetryUnconfigured))
            return _client;

        await _lock.WaitAsync();
        try
        {
            if (_client is not null && (_client is not StubChatClient || DateTime.UtcNow - _resolvedAtUtc < RetryUnconfigured))
                return _client;

            var options = await AssistantSettings.ResolveLlmAsync();
            _client = LlmChatClientFactory.Create(new LlmOptions(options.Provider, options.Endpoint, options.Model, options.ApiKey));
            _resolvedAtUtc = DateTime.UtcNow;
            if (_client is StubChatClient)
                _logger.LogWarning("Assistant: no language model configured (/chats → Settings, env LLM_*, or keyvalue LLM-AzureOpenAI/*) — answering with the stub.");
            else
                _logger.LogInformation("Assistant: using {Provider} model {Model}.", options.Provider, options.Model);
            return _client;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Drop the current client so the next message builds one from the latest settings.</summary>
    public void Invalidate() => _client = null;

    /// <summary>Smoke-test a setup with a tiny prompt (as the platform's LlmChatClientFactory.TestAsync).</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(LlmSetup s, CancellationToken ct)
    {
        var client = LlmChatClientFactory.Create(new LlmOptions(s.Provider, s.Endpoint, s.Model, s.ApiKey));
        if (client is StubChatClient)
            return (false, $"{(s.Provider == "" ? "No provider" : s.Provider)} isn't ready — check the endpoint, model and API key.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with the single word: OK")],
                new ChatOptions { MaxOutputTokens = 5 }, timeout.Token);
            return (true, $"Connected to {s.Provider} — the model \"{s.Model}\" responded.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return (false, $"Couldn't reach {s.Provider} within 15s.");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't reach {s.Provider}: {ex.Message}");
        }
    }
}
