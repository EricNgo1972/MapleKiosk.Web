// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/VoiceSessionHandlers.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The callbacks a session raises. Deliberately a record of delegates rather than C# <c>event</c>s:
/// one wiring point at construction, no unsubscribe leak on a long-lived socket, and — the load-bearing
/// reason — <b>every handler is awaitable</b>. An <c>event</c> cannot be awaited, so
/// <see cref="OnAudio"/> could not apply backpressure and a slow consumer would silently accumulate
/// unbounded audio.
/// </summary>
public sealed class VoiceSessionHandlers
{
    /// <summary>
    /// One chunk of agent audio in <see cref="IVoiceAgentSession.OutputFormat"/>. Chunk size is chosen by
    /// the provider — do not assume it aligns to a frame or a sample-count boundary. Awaited, so returning
    /// slowly throttles the read loop rather than queueing without limit.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? OnAudio { get; init; }

    /// <summary>Coarse state for the UI. Purely presentational; no protocol depends on it.</summary>
    public Func<VoiceTurnState, CancellationToken, ValueTask>? OnStateChanged { get; init; }

    /// <summary>
    /// Barge-in. The consumer MUST stop playback AND DISCARD every already-buffered agent byte — audio
    /// that has left the server is beyond recall, so anything still queued downstream is the only thing
    /// we can actually cancel. Failing to discard is what makes an agent appear to talk over the guest
    /// for a second after they interrupt.
    /// </summary>
    public Func<CancellationToken, ValueTask>? OnUserStartedSpeaking { get; init; }

    /// <summary>The agent's audio for this turn is complete (local playout may still be draining).</summary>
    public Func<CancellationToken, ValueTask>? OnAgentAudioDone { get; init; }

    /// <summary>A line of transcript, for on-screen captions and the audit trail.</summary>
    public Func<VoiceTranscriptRole, string, CancellationToken, ValueTask>? OnTranscript { get; init; }

    /// <summary>
    /// Run the tool and return its result. Implementations MUST invoke this off their socket read loop:
    /// awaiting a database round-trip there stalls inbound audio and destroys barge-in latency. Throwing
    /// is equivalent to returning <c>IsError = true</c>; either way a result must reach the provider, or
    /// the conversation hangs waiting on a call id that is never answered.
    /// </summary>
    public Func<VoiceToolCall, CancellationToken, Task<VoiceToolResult>>? OnToolCall { get; init; }

    /// <summary>A recoverable or fatal provider error, already flattened to a human-readable message.</summary>
    public Func<string, Exception?, CancellationToken, ValueTask>? OnError { get; init; }

    /// <summary>The provider closed the session. <c>null</c> reason means a normal close.</summary>
    public Func<string?, CancellationToken, ValueTask>? OnClosed { get; init; }
}
