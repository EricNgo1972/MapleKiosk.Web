using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// The assistant's chat client, built from the same keyvalue rows the platform's chat gateway reads
/// (partition LLM-AzureOpenAI: APIKey, EndPoint, Models — "gpt-4.1-mini|gpt-5.4", first one wins), with
/// env vars first (LLM_PROVIDER, LLM_API_KEY, LLM_ENDPOINT, LLM_MODELS). Resolved on first use; an
/// unconfigured result is retried every few minutes so rows added later are picked up without a restart.
/// </summary>
public sealed class AssistantLlm
{
    private const string Partition = "LLM-AzureOpenAI";
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

            var options = await ResolveAsync();
            _client = LlmChatClientFactory.Create(options);
            _resolvedAtUtc = DateTime.UtcNow;
            if (_client is StubChatClient)
                _logger.LogWarning("Assistant: no language model configured (env LLM_* or keyvalue {Partition}/APIKey, EndPoint, Models) — answering with the stub.", Partition);
            else
                _logger.LogInformation("Assistant: using {Provider} model {Model}.", options.Provider, options.Model);
            return _client;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<LlmOptions> ResolveAsync()
    {
        var provider = await KeyValueTable.ResolveAsync("LLM_PROVIDER", Partition, "Provider");
        var apiKey = await KeyValueTable.ResolveAsync("LLM_API_KEY", Partition, "APIKey");
        // Azure Table row keys are case-sensitive; the platform's row is "EndPoint".
        var endpoint = await KeyValueTable.ResolveAsync("LLM_ENDPOINT", Partition, "EndPoint");
        if (endpoint == "") endpoint = await KeyValueTable.ResolveAsync("LLM_ENDPOINT", Partition, "Endpoint");
        var models = await KeyValueTable.ResolveAsync("LLM_MODELS", Partition, "Models");
        if (models == "") models = await KeyValueTable.ResolveAsync("LLM_MODELS", Partition, "Model");

        var model = models.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return new LlmOptions(provider == "" ? "AzureOpenAI" : provider, endpoint, model, apiKey);
    }
}
