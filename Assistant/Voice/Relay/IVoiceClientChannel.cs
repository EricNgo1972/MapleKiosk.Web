// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Relay/IVoiceClientChannel.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.


namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The relay's view of whoever is on the far end of a conversation: a browser page, or a phone call.
///
/// <para>The relay pumps PCM between the provider and "the client" and never learns which kind it has.
/// Everything wire-specific — how the browser frames a microphone chunk, how Twilio wraps a phone line in
/// base64 μ-law JSON — is a channel's business. What crosses this seam is what the provider speaks: raw
/// 16-bit PCM in the session's negotiated formats, plus the relay's own control vocabulary.</para>
/// </summary>
internal interface IVoiceClientChannel
{
    /// <summary>What to call the far end in a timing line — "twilio", "browser". Never a guest's identity.</summary>
    string Label { get; }

    /// <summary>
    /// Called exactly once, the moment the provider session is open and its formats are known, before any
    /// audio is sent. A browser tells its player the rates; a phone channel sets up its resamplers. Nothing
    /// is sent through <see cref="SendAudioAsync"/> before this.
    /// </summary>
    ValueTask ReadyAsync(VoiceClientReady ready, CancellationToken ct);

    /// <summary>Agent speech, in the session's output format.</summary>
    ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>
    /// A relay control frame — <c>clear</c> (barge-in), <c>ending</c> (the goodbye has been sent),
    /// <c>probe</c> (a timing mark: a channel that can tell when audio was actually PLAYED answers it
    /// with <see cref="VoiceClientInboundKind.Played"/>, others ignore it), and the display-only ones
    /// (<c>state</c>, <c>transcript</c>, <c>tool</c>, <c>ping</c>…). <paramref name="json"/> is the frame
    /// exactly as the browser protocol defines it; a channel with no screen keys off
    /// <paramref name="type"/> and ignores the rest.
    /// </summary>
    ValueTask SendControlAsync(string type, string json, CancellationToken ct);

    /// <summary>
    /// The next thing the client did. Blocks until there is one. Audio is already in the session's INPUT
    /// format. <see cref="VoiceClientInboundKind.Closed"/> is final: the far side is gone or the channel
    /// is unusable, and the relay tears down.
    /// </summary>
    ValueTask<VoiceClientInbound> ReceiveAsync(CancellationToken ct);

    /// <summary>Best-effort orderly close. Never throws.</summary>
    ValueTask CloseAsync();
}

/// <summary>What a channel needs to know once the session exists.</summary>
internal sealed record VoiceClientReady(
    VoiceAudioFormat Input,
    VoiceAudioFormat Output,
    string ProviderName,
    IReadOnlyList<string> Tools);

internal enum VoiceClientInboundKind
{
    /// <summary>Guest audio, in the session's input format.</summary>
    Audio,

    /// <summary>The client asked to end the conversation.</summary>
    Stop,

    /// <summary>The client is gone.</summary>
    Closed,

    /// <summary>
    /// A <c>probe</c> the relay sent has been reached in playback: everything queued before it has now
    /// actually been heard. <see cref="VoiceClientInbound.Mark"/> names which probe. How the relay
    /// measures the far end's own buffering — the one hop no server-side clock can see.
    /// </summary>
    Played,
}

internal readonly record struct VoiceClientInbound(VoiceClientInboundKind Kind, ReadOnlyMemory<byte> Audio, string? Mark = null)
{
    public static readonly VoiceClientInbound Stop = new(VoiceClientInboundKind.Stop, ReadOnlyMemory<byte>.Empty);
    public static readonly VoiceClientInbound Closed = new(VoiceClientInboundKind.Closed, ReadOnlyMemory<byte>.Empty);

    public static VoiceClientInbound Of(ReadOnlyMemory<byte> pcm) => new(VoiceClientInboundKind.Audio, pcm);

    public static VoiceClientInbound PlayedMark(string name) => new(VoiceClientInboundKind.Played, ReadOnlyMemory<byte>.Empty, name);
}
