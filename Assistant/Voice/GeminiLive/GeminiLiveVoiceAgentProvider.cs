// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/GeminiLive/GeminiLiveVoiceAgentProvider.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using Microsoft.Extensions.Logging;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Google Gemini Live — an audio-native model: it hears the waveform and speaks, with no transcription or
/// synthesis stage in between, so tone and emphasis survive in both directions. Knows nothing about any
/// other provider; selected purely by a profile whose <c>Kind</c> is "GeminiLive".
/// </summary>
internal sealed class GeminiLiveVoiceAgentProvider : IVoiceAgentProvider
{
    private readonly ILoggerFactory _loggerFactory;

    public GeminiLiveVoiceAgentProvider(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public string Kind => "GeminiLive";

    public IVoiceAgentService Create(VoiceProfileInfo profile) =>
        new GeminiLiveVoiceAgentService(profile, _loggerFactory);
}

internal sealed class GeminiLiveVoiceAgentService : IVoiceAgentService
{
    /// <summary>
    /// Current audio-to-audio model. NOT interchangeable with a text "flash" SKU — the Live API only
    /// accepts its own native-audio variants, and pointing it at a text model fails at setup.
    /// </summary>
    public const string DefaultModel = "gemini-3.8-live";

    private readonly VoiceProfileInfo _profile;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GeminiLiveVoiceAgentService> _logger;

    public GeminiLiveVoiceAgentService(VoiceProfileInfo profile, ILoggerFactory loggerFactory)
    {
        _profile = profile;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GeminiLiveVoiceAgentService>();
    }

    public string ProviderName => "GeminiLive";
    public bool SupportsTools => true;

    public Task<IVoiceAgentSession> StartSessionAsync(
        VoiceSessionOptions options, VoiceSessionHandlers handlers, CancellationToken ct = default)
    {
        // Fail here, before a socket is opened, with a message that names the setting to fix. An
        // unconfigured provider that merely goes quiet is indistinguishable from a broken microphone.
        var missing = MissingFields(_profile);
        if (missing.Count > 0)
        {
            // Site: worded for where this site keeps the key (the platform names the profile field).
            throw new InvalidOperationException(
                "Voice is not set up — set the Gemini API key in /chats → Settings.");
        }

        var session = new GeminiLiveSession(
            _profile, options, handlers, _loggerFactory.CreateLogger<GeminiLiveSession>());

        return ConnectAsync(session, ct);
    }

    private async Task<IVoiceAgentSession> ConnectAsync(GeminiLiveSession session, CancellationToken ct)
    {
        try
        {
            await session.ConnectAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// What this profile still needs. Only this provider knows its own requirements — there is no shared
    /// readiness rule to keep in step, which is what lets a new vendor be added without editing anything.
    /// </summary>
    public static IReadOnlyList<string> MissingFields(VoiceProfileInfo profile)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
            missing.Add("ApiKey");
        return missing;
    }

    /// <summary>The model to ask for — blank in the profile means the current default.</summary>
    public static string ModelFor(VoiceProfileInfo profile) =>
        string.IsNullOrWhiteSpace(profile.Model) ? DefaultModel : profile.Model.Trim();
}
