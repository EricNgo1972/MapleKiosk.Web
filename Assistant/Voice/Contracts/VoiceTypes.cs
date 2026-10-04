// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/VoiceTypes.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>Wire encoding of one audio direction. Only raw PCM today; every realtime provider speaks it.</summary>
public enum VoiceAudioEncoding
{
    /// <summary>Signed 16-bit little-endian PCM ("linear16").</summary>
    Pcm16,
}

/// <summary>
/// The format of one audio direction.
///
/// <para>Providers differ and do NOT all let us choose: Gemini Live is fixed at 16 kHz in / 24 kHz out,
/// while Deepgram's agent negotiates whatever we ask for. So a session REPORTS the formats it actually
/// settled on rather than merely accepting the ones we requested — see
/// <see cref="IVoiceAgentSession.InputFormat"/>. Callers (the relay, and through it the browser) must
/// read those, never assume.</para>
/// </summary>
public readonly record struct VoiceAudioFormat(VoiceAudioEncoding Encoding, int SampleRate, int Channels)
{
    public static VoiceAudioFormat Pcm16Mono(int sampleRate) => new(VoiceAudioEncoding.Pcm16, sampleRate, 1);

    /// <summary>Useful for pacing and for sizing buffers in terms of milliseconds of audio.</summary>
    public int BytesPerSecond => SampleRate * Channels * 2;
}

/// <summary>
/// One callable tool as advertised to the provider.
///
/// <para><see cref="ParametersJson"/> is a JSON Schema object — <c>{"type":"object","properties":{…},
/// "required":[…]}</c> — carried as raw JSON text on purpose. A <c>JsonElement</c> would tie the value's
/// validity to the lifetime of the <c>JsonDocument</c> it was cut from (a disposed document makes every
/// read throw), and a <c>JsonNode</c> cannot be attached to two parents without cloning. A string has
/// neither trap, is trivially cacheable, and keeps the BO layer free of JSON DOM semantics; each vendor
/// implementation parses it once while composing its session-open message.</para>
/// </summary>
public sealed record VoiceToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>A tool invocation the model asked for. <see cref="CallId"/> correlates the answer.</summary>
public sealed record VoiceToolCall(string CallId, string Name, string ArgumentsJson);

/// <summary>
/// The answer to a <see cref="VoiceToolCall"/>. <see cref="Content"/> is a string because every provider's
/// tool-result field is text (Deepgram's <c>output</c>, Gemini Live's <c>functionResponse</c> payload) —
/// the model reads it, so it may be JSON or prose, but it is never a typed object on the wire.
/// </summary>
public sealed record VoiceToolResult(string CallId, string Name, string Content, bool IsError = false);

/// <summary>Who is on the other end of the microphone — decides which tools are even advertised.</summary>
public enum VoiceToolAudience
{
    /// <summary>A guest at a lobby kiosk. Sees only tools marked <c>CustomerSafe</c>.</summary>
    Customer,

    /// <summary>A signed-in staff member. May see the full tool set.</summary>
    Staff,
}

/// <summary>Coarse conversational state, for the UI to render. Not a protocol concept.</summary>
public enum VoiceTurnState { Idle, Listening, Thinking, Speaking }

/// <summary>Who said a line of transcript.</summary>
public enum VoiceTranscriptRole { User, Agent }

/// <summary>
/// Everything needed to open a session. Note what is absent: no model id, no voice name, no STT model.
/// Those are provider-specific and live in <see cref="VoiceProfileInfo"/>, so that an audio-native
/// provider (one model that hears and speaks, with no STT/TTS to configure) is not forced to invent
/// values for fields that mean nothing to it.
/// </summary>
public sealed record VoiceSessionOptions(
    string SystemPrompt,
    string? Greeting,
    string LanguageTag,
    VoiceAudioFormat PreferredInput,
    VoiceAudioFormat PreferredOutput,
    IReadOnlyList<VoiceToolDefinition> Tools,
    VoiceAcoustics Acoustics = VoiceAcoustics.Kiosk);

/// <summary>
/// What the guest's audio sounds like — the ROOM, not the wire format. A provider's turn-taking (when has
/// the guest stopped talking? is that a barge-in or the line's own noise?) is tuned per vendor, and the
/// right tuning differs between the two rooms this stack serves.
///
/// <para>A kiosk microphone sits in a busy lobby: chatter, dryers, and auto-gain lifting the floor between
/// words, so end-of-speech has to be eager or the model waits seconds for a silence that never comes. A
/// telephone is the opposite: a narrow, quiet trunk where the loudest thing on the inbound track is the
/// caller's handset echoing the AGENT's own voice — and a start-of-speech detector set for the lobby hears
/// that echo as the caller interrupting, cuts the reply off mid-word, and then waits for a sentence
/// nobody is saying. Which is the silence the caller hangs up on.</para>
/// </summary>
public enum VoiceAcoustics
{
    /// <summary>A microphone in the open — a lobby kiosk. Eager end-of-speech, sensitive start.</summary>
    Kiosk,

    /// <summary>A telephone call — 8 kHz narrowband with handset echo. Conservative start-of-speech.</summary>
    Telephone,
}
