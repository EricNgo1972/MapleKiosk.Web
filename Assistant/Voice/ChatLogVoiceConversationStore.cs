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
    public const string PhoneDevice = "Phone call";

    private readonly ChatLog _log;
    private readonly string? _country;
    private readonly string _page;
    private readonly string _device;
    private readonly string? _callerPhone;

    /// <param name="callerPhone">A phone caller's number (E.164) when the carrier passed it: kept as the
    /// conversation's contact, so every call on /chats can be called back.</param>
    public ChatLogVoiceConversationStore(ChatLog log, string? country, string page,
        string device = Device, string? callerPhone = null)
    {
        _log = log;
        _country = country;
        _page = page;
        _device = device;
        _callerPhone = callerPhone;
    }

    public async Task SaveAsync(VoiceConversationRecord record, CancellationToken ct = default)
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

        await _log.RecordTranscriptAsync(id, lines, record.Language, _country, _device, ct);
        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(_callerPhone))
            await _log.SaveContactAsync(id, new GuestContact("Phone caller", _callerPhone, null, null, null), ct);
    }
}
