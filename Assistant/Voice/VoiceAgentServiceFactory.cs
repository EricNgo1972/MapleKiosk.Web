// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/VoiceAgentServiceFactory.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using Microsoft.Extensions.Logging;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Picks the provider that implements a profile's <see cref="VoiceProfileInfo.Kind"/>. That is the whole
/// job — the profile itself is chosen by the caller (here <c>WebsiteVoiceEndpoints</c>), so there is exactly
/// one place that decides which assistant is running.
///
/// <para>Anything unresolvable — no profile at all, or a kind no provider claims — fails loudly. Site: the
/// platform falls back to its tone-playing stub provider instead; the stub is not vendored, because on this
/// test page a clear error ("set the Gemini key") is the useful answer.</para>
/// </summary>
internal sealed class VoiceAgentServiceFactory : IVoiceAgentServiceFactory
{
    private readonly IEnumerable<IVoiceAgentProvider> _providers;
    private readonly ILogger<VoiceAgentServiceFactory> _logger;

    public VoiceAgentServiceFactory(
        IEnumerable<IVoiceAgentProvider> providers, ILoggerFactory loggerFactory)
    {
        _providers = providers;
        _logger = loggerFactory.CreateLogger<VoiceAgentServiceFactory>();
    }

    public Task<IVoiceAgentService> CreateAsync(VoiceProfileInfo? profile, CancellationToken ct = default)
    {
        if (profile is null)
            throw new InvalidOperationException("Voice is not set up — set the Gemini API key in /chats → Settings.");

        var provider = _providers.FirstOrDefault(
            p => p.Kind.Equals(profile.Kind, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            // Naming the kinds that DO exist turns a typo into a one-line fix instead of a hunt.
            _logger.LogError(
                "Voice: profile '{Name}' asks for kind '{Kind}', which no provider implements. Available: {Kinds}.",
                profile.Name, profile.Kind, string.Join(", ", _providers.Select(p => p.Kind)));
            throw new InvalidOperationException($"No voice provider implements '{profile.Kind}'.");
        }

        _logger.LogInformation("Voice: profile '{Name}' -> {Kind}", profile.Name, provider.Kind);
        return Task.FromResult(provider.Create(profile));
    }

    public async Task<VoiceTestResult> TestAsync(VoiceProfileInfo profile, CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(
            p => p.Kind.Equals(profile.Kind, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
            return new VoiceTestResult(false, $"No provider implements '{profile.Kind}'.");

        // Bounded: a wrong endpoint or a blocked outbound port should answer in seconds, not sit on the
        // provider's own connect timeout while someone watches a spinner.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));

        try
        {
            var service = provider.Create(profile);

            // No tools and no greeting: this proves the credential and the model id, and nothing else.
            var options = new VoiceSessionOptions(
                SystemPrompt: "You are a connection test. Say nothing.",
                Greeting: null,
                LanguageTag: string.IsNullOrWhiteSpace(profile.Language) ? "en" : profile.Language,
                PreferredInput: VoiceAudioFormat.Pcm16Mono(profile.InputSampleRate),
                PreferredOutput: VoiceAudioFormat.Pcm16Mono(profile.OutputSampleRate),
                Tools: Array.Empty<VoiceToolDefinition>());

            await using var session = await service
                .StartSessionAsync(options, new VoiceSessionHandlers(), timeout.Token)
                .ConfigureAwait(false);

            return new VoiceTestResult(true,
                $"Connected to {service.ProviderName}. The key and model are accepted.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new VoiceTestResult(false,
                "Timed out reaching the provider — check the salon's internet connection or firewall.");
        }
        catch (Exception ex)
        {
            // The providers already phrase their failures for a human ("rejected the API key (401)"),
            // so pass that through rather than wrapping it in something vaguer.
            _logger.LogWarning(ex, "Voice: test of profile '{Name}' failed.", profile.Name);
            return new VoiceTestResult(false, ex.Message);
        }
    }
}
