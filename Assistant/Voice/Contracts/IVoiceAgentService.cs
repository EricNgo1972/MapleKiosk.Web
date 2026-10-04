// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/IVoiceAgentService.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// One live speech-to-speech conversation: raw audio in, raw audio out, tool calls surfaced through
/// <see cref="VoiceSessionHandlers.OnToolCall"/>. Disposing ends the conversation and releases the
/// provider connection.
/// </summary>
public interface IVoiceAgentSession : IAsyncDisposable
{
    /// <summary>Correlates logs and audit rows for this conversation.</summary>
    string SessionId { get; }

    /// <summary>The format the provider WILL accept — what callers must send. Read it; never assume it.</summary>
    VoiceAudioFormat InputFormat { get; }

    /// <summary>The format the provider WILL emit — what callers will receive.</summary>
    VoiceAudioFormat OutputFormat { get; }

    bool IsConnected { get; }

    /// <summary>Push captured microphone audio, already in <see cref="InputFormat"/>.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct = default);

    /// <summary>
    /// Have the agent say THESE WORDS now — a greeting, "are you still there?", or "let me check that"
    /// while a slow tool runs. Pass the line itself, never an instruction to produce one: a provider with
    /// no inject-speech primitive wraps it in whatever its model needs, and an instruction would then be
    /// wrapped in an instruction.
    ///
    /// <b>Best-effort by contract:</b> a provider that cannot make its agent talk on demand no-ops rather
    /// than throwing, so callers must never depend on the line actually being spoken.
    /// </summary>
    Task SpeakAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Have the agent ACT on an instruction rather than say it — "the guest went quiet after cutting you
    /// off; pick up where you left off". The words are for the model, never for the guest's ears, which
    /// is what separates this from <see cref="SpeakAsync"/>: that one carries the line itself.
    ///
    /// <b>Best-effort by contract</b>, like <see cref="SpeakAsync"/>: a provider with no way to prompt
    /// its model mid-conversation no-ops rather than throwing.
    /// </summary>
    Task PromptAsync(string instruction, CancellationToken ct = default);

    /// <summary>Keeps an idle provider socket alive. The cadence is the implementation's business.</summary>
    Task KeepAliveAsync(CancellationToken ct = default);

    /// <summary>Graceful close; <see cref="VoiceSessionHandlers.OnClosed"/> still fires.</summary>
    Task CloseAsync(CancellationToken ct = default);
}

/// <summary>A configured provider, able to open sessions. One instance per resolved configuration.</summary>
public interface IVoiceAgentService
{
    /// <summary>For logs and the settings UI — e.g. "GeminiLive", "Deepgram", "Stub".</summary>
    string ProviderName { get; }

    /// <summary>False for providers (or configurations) that cannot call tools; the caller can warn.</summary>
    bool SupportsTools { get; }

    Task<IVoiceAgentSession> StartSessionAsync(
        VoiceSessionOptions options, VoiceSessionHandlers handlers, CancellationToken ct = default);
}

/// <summary>
/// Builds the provider for a profile. Concrete <see cref="IVoiceAgentService"/> implementations are never
/// DI-registered — this picks one by <see cref="VoiceProfileInfo.Kind"/>, the same shape as
/// <c>ILlmServiceFactory</c> and <c>ISmsServiceFactory</c>.
///
/// <para>The profile is passed IN rather than looked up here, so a conversation resolves it exactly once.
/// When the caller resolved a named assistant and this re-read "the first active profile", the two could
/// disagree — and did: opening a named assistant silently ran a different one.</para>
/// </summary>
public interface IVoiceAgentServiceFactory
{
    /// <summary>A null profile means voice is not set up; the caller gets the stub.</summary>
    Task<IVoiceAgentService> CreateAsync(VoiceProfileInfo? profile, CancellationToken ct = default);

    /// <summary>
    /// Opens a real session with this profile and closes it again — the only honest way to answer "is this
    /// key right?", since a credential is not checked until the provider is actually dialled. Offers no
    /// tools and says nothing, so it cannot affect a guest or a booking.
    /// </summary>
    Task<VoiceTestResult> TestAsync(VoiceProfileInfo profile, CancellationToken ct = default);
}

/// <summary>The outcome of <see cref="IVoiceAgentServiceFactory.TestAsync"/>, phrased for an operator.</summary>
public sealed record VoiceTestResult(bool Ok, string Message);
