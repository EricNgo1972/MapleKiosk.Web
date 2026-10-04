// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/IVoiceConversationStore.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.


namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>One line of a recorded conversation.</summary>
/// <param name="Role">"user" or "agent".</param>
public sealed record VoiceTranscriptLine(string Role, string Text, DateTime At);

/// <summary>Everything worth keeping about one conversation.</summary>
/// <param name="BookingIds">
/// What the conversation created — see <see cref="IVoiceOutcomeReader"/>. Empty for the many conversations
/// that only ask about opening hours.
/// </param>
public sealed record VoiceConversationRecord(
    string ConversationId,
    string ProfileName,
    string Provider,
    string Audience,
    string Language,
    DateTime StartedAt,
    DateTime EndedAt,
    int ToolCalls,
    IReadOnlyList<VoiceTranscriptLine> Lines,
    IReadOnlyList<string> BookingIds,
    // The booking the agent gathered during the call (name/phone/service/time), or null. Kept so the front
    // desk can create the appointment from a call that ended before the guest confirmed. GuestName/Phone
    // are denormalized onto the row from this for the log list; the whole draft is stored as JSON.
    VoiceBookingDraft? Draft = null,
    // How the guest reached the agent. Defaulted so a caller that predates the channel still compiles;
    // the relay always passes what the session context says.
    // Site: the platform's ChatAudit filing fields (AuditPlatform, AuditUserId) are dropped — no audit
    // trail here. ConversationChannel is the site's own (MapleKiosk.Web.Assistant), same values.
    ConversationChannel Channel = ConversationChannel.DesktopVoice);

/// <summary>
/// Persists a finished conversation.
///
/// <para>Written by the SERVER at the end of a session, not posted up by the browser: a conversation must
/// be recorded even when the guest closes the tab, the tunnel drops, or the kiosk loses power mid-booking
/// — precisely the cases where somebody later asks what was agreed.</para>
/// </summary>
public interface IVoiceConversationStore
{
    Task SaveAsync(VoiceConversationRecord record, CancellationToken ct = default);
}
