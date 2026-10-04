// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Entities/VoiceProfileInfo.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// One voice-agent configuration, read-only — what the running app consumes.
///
/// <para>EVERYTHING needed to build the session is on this one object. A provider reads its own profile and
/// nothing else, which is what keeps two vendors independent.</para>
///
/// <para>Site: the platform loads this from its VoiceProfile entity (CSLA, settings table). Here it is built
/// per conversation from <see cref="AssistantSettings.ResolveVoiceAsync"/> (the Gemini key, model and voice
/// an admin sets on /chats → Settings) — see <c>WebsiteVoiceEndpoints.ProfileFor</c>. The Deepgram "think"
/// fields and the entity mapping are dropped.</para>
/// </summary>
public sealed class VoiceProfileInfo
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = VoiceKinds.GeminiLive;
    public bool IsActive { get; init; }
    public int SortOrder { get; init; }

    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public string VoiceName { get; init; } = "";
    public string Language { get; init; } = "en";
    public string Greeting { get; init; } = "";
    public string PromptAddendum { get; init; } = "";

    public int InputSampleRate { get; init; } = 16000;
    public int OutputSampleRate { get; init; } = 24000;
    public int MaxSessionSeconds { get; init; } = 600;
    public int IdleTimeoutSeconds { get; init; } = 45;
    public int MaxConcurrentSessions { get; init; } = 2;
}

/// <summary>The provider kinds (MK.Chat/SPC.BO.Voice/Entities/VoiceProfile.cs). Only Gemini Live is vendored.</summary>
public static class VoiceKinds
{
    public const string GeminiLive = "GeminiLive";
}
