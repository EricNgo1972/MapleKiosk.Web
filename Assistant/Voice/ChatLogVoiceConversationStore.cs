namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Files a finished voice conversation in the site's <see cref="ChatLog"/>, so a voice call shows on /chats
/// next to the typed chats. Stands in for the platform's VoiceConversationStore (CSLA/SQLite).
///
/// <para>One per conversation, because it carries what the relay's record does not: where the call came from
/// (country, device) and which page opened it. Best-effort like <see cref="ChatLog"/> itself — the relay
/// already logs and swallows a failed save, and ChatLog never throws for a failed write.</para>
/// </summary>
internal sealed class ChatLogVoiceConversationStore : IVoiceConversationStore
{
    public const string Device = "Voice (browser)";

    private readonly ChatLog _log;
    private readonly string? _country;
    private readonly string _page;

    public ChatLogVoiceConversationStore(ChatLog log, string? country, string page)
    {
        _log = log;
        _country = country;
        _page = page;
    }

    public Task SaveAsync(VoiceConversationRecord record, CancellationToken ct = default)
    {
        // The relay's own id is a fresh 32-hex Guid per call — exactly ChatLog's id shape — so the row on
        // /chats and the relay's log lines share one id. IdFor mints a new one if that ever changes.
        var id = ChatLog.IdFor(record.ConversationId);

        // The relay says "agent"; the chat log (and /chats) says "assistant".
        var lines = record.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => new ChatLine(l.Role == "user" ? "user" : "assistant", l.Text.Trim(),
                new DateTimeOffset(DateTime.SpecifyKind(l.At, DateTimeKind.Utc)), _page))
            .ToList();

        return _log.RecordTranscriptAsync(id, lines, record.Language, _country, Device, ct);
    }
}
