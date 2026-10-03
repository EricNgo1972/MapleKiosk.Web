namespace MapleKiosk.Web.Assistant;

/// <summary>
/// HOW a visitor reached the assistant — the medium changes what it should say (a caller sees nothing,
/// a chat visitor reads links). Decided by the server from the endpoint, never from the client.
/// Same values as the platform's SPC.BO.Chat.ConversationChannel.
/// </summary>
public enum ConversationChannel
{
    /// <summary>A telephone call: no screen.</summary>
    Phone,

    /// <summary>Voice with a transcript on a screen.</summary>
    DesktopVoice,

    /// <summary>Typed text in the website chat.</summary>
    Chat,
}

/// <summary>
/// The facts the assistant is grounded in BEFORE the visitor says a word. Same seam as the platform's
/// SPC.BO.Chat.IAssistantGrounding: a model that is not told what the business sells writes it from the
/// name. Read once per message here (the site keeps no session), and a read that fails must degrade to
/// "no grounding", never to no answer.
/// </summary>
public interface IAssistantGrounding
{
    /// <summary>The text for a customer conversation reaching the assistant by <paramref name="channel"/>, or null for none.</summary>
    Task<string?> ForCustomerAsync(ConversationChannel channel, CancellationToken ct = default);
}
