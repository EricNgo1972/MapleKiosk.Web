// Vendored from the monorepo: MK.Chat/SPC.BO.Voice/Entities/VoiceCatalog.cs (Gemini Live only — the site
// runs no other voice provider).
namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>One thing an operator can pick for the voice agent — a model id or a voice name — with a
/// short note the picker shows beside it. The <see cref="Value"/> is what is saved verbatim.</summary>
public sealed record VoiceOption(string Value, string? Note = null);

/// <summary>
/// The Gemini Live models and voices offered in /chats → Settings.
///
/// <para>Reference data, kept as the platform keeps it: the page shows these as the choices, and a value
/// already saved that isn't in the list (a model Google adds tomorrow, typed into the keyvalue row) is kept
/// and shown rather than silently replaced.</para>
/// </summary>
public static class VoiceCatalog
{
    // Gemini Live models — the ones the Developer API actually accepts for BidiGenerateContent. gemini-3.8-live
    // is the stable Live model Google now recommends for voice agents (its -extended-thinking twin reasons while
    // it talks, covering tool calls with filler, at the same price) and is this site's default when nothing is
    // picked; 3.1-flash-live is the earlier preview; 2.5 native-audio is older and has been flaky mid-call, so
    // it is offered only as a labelled fallback. 3.8 runs tools NON_BLOCKING by default — the session declares
    // every tool BLOCKING (GeminiLiveSession) so it never speaks before a tool has answered.
    public static readonly VoiceOption[] GeminiModels =
    [
        new("gemini-3.8-live", "Latest, stable — recommended, the default"),
        new("gemini-3.8-live-extended-thinking", "Latest, thinks while it talks — for multi-step calls"),
        new("gemini-3.1-flash-live-preview", "Earlier, audio-native (preview)"),
        new("gemini-2.5-flash-native-audio-preview-12-2025", "Older audio-native — can drop mid-call (preview)"),
    ];

    // The prebuilt Gemini Live voices, shared across the live models.
    public static readonly VoiceOption[] GeminiVoices =
    [
        new("Puck"), new("Charon"), new("Kore"), new("Fenrir"),
        new("Aoede"), new("Leda"), new("Orus"), new("Zephyr"),
    ];
}
