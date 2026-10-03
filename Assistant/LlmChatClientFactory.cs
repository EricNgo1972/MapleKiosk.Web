using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MapleKiosk.Web.Assistant;

/// <summary>Which model the assistant talks to. Resolved by <see cref="AssistantLlm"/> from the shared
/// keyvalue table (rows LLM-AzureOpenAI/*), the same rows the platform's chat gateway reads.</summary>
public sealed record LlmOptions(string Provider, string Endpoint, string Model, string ApiKey);

/// <summary>
/// Builds a Microsoft.Extensions.AI <see cref="IChatClient"/> for the selected provider. Copied from the
/// platform's chat gateway (MK.Chat/SPC.Infrastructure.ChatGateway/LlmChatClientFactory.cs), taking plain
/// <see cref="LlmOptions"/> instead of its CSLA LlmSettings. Anything unconfigured/unknown returns the
/// <see cref="StubChatClient"/>.
///
/// Every provider is reached through the OpenAI client (the OpenAI protocol) — no provider-specific SDKs.
/// Azure OpenAI included: Azure exposes an OpenAI-compatible surface at <c>{resource}/openai/v1</c>, which
/// the OpenAI client talks to directly with the API key (Azure.AI.OpenAI 2.1.0 is ABI-broken against the
/// OpenAI package Microsoft.Extensions.AI.OpenAI requires).
/// </summary>
public static class LlmChatClientFactory
{
    // Default OpenAI-compatible base URLs for hosted providers — used when Endpoint is blank.
    private const string OpenRouterEndpoint = "https://openrouter.ai/api/v1";
    private const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai";
    private const string AnthropicEndpoint = "https://api.anthropic.com/v1";

    // Fail fast: a per-attempt network timeout + a single retry, so an unreachable provider surfaces well
    // within typical reverse-proxy limits instead of hanging.
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 1;

    private static OpenAIClientOptions ClientOptions(Uri? endpoint = null)
    {
        var o = new OpenAIClientOptions
        {
            NetworkTimeout = NetworkTimeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: MaxRetries),
        };
        if (endpoint is not null)
            o.Endpoint = endpoint;
        return o;
    }

    public static IChatClient Create(LlmOptions s)
    {
        var provider = (s.Provider ?? string.Empty).Trim().ToLowerInvariant();
        return provider switch
        {
            // Azure OpenAI via its OpenAI-compatible v1 endpoint. Model = the Azure deployment name.
            "azureopenai" or "azure" when Ok(s.Endpoint, s.Model, s.ApiKey) =>
                Compatible(AzureV1Endpoint(s.Endpoint), s.Model, s.ApiKey),

            "openai" when Ok(s.Model, s.ApiKey) =>
                new OpenAIClient(new ApiKeyCredential(s.ApiKey), ClientOptions()).GetChatClient(s.Model).AsIChatClient(),

            // Hosted OpenAI-compatible providers — endpoint optional (sensible default), key + model required.
            // (No tools here, so Gemini needs none of the gateway's thought-signature handling.)
            "openrouter" when Ok(s.Model, s.ApiKey) => Compatible(Or(s.Endpoint, OpenRouterEndpoint), s.Model, s.ApiKey),
            "gemini"     when Ok(s.Model, s.ApiKey) => Compatible(Or(s.Endpoint, GeminiEndpoint), s.Model, s.ApiKey),
            "anthropic"  when Ok(s.Model, s.ApiKey) => Compatible(Or(s.Endpoint, AnthropicEndpoint), s.Model, s.ApiKey),

            // Self-hosted / other OpenAI-protocol server (Ollama, vLLM, …) — endpoint required, key optional.
            "openaicompatible" or "ollama" when Ok(s.Endpoint, s.Model) => Compatible(s.Endpoint, s.Model, s.ApiKey),

            _ => new StubChatClient(),
        };
    }

    private static IChatClient Compatible(string endpoint, string model, string apiKey) =>
        new OpenAIClient(
                new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "not-needed" : apiKey),
                ClientOptions(new Uri(endpoint)))
            .GetChatClient(model).AsIChatClient();

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    // Accept either the bare Azure resource URL (https://my-res.openai.azure.com) or one that already points
    // at the v1 surface; normalize to the OpenAI-compatible base the client expects.
    private static string AzureV1Endpoint(string endpoint)
    {
        var e = endpoint.Trim().TrimEnd('/');
        return e.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase) ? e : $"{e}/openai/v1";
    }

    private static bool Ok(params string[] values) => values.All(v => !string.IsNullOrWhiteSpace(v));
}
