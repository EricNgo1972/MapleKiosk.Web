// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/IVoiceToolBroker.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Where a voice session's tools come from and where its tool calls go. Keeping this a seam means the
/// vendor implementations never learn that the tools happen to arrive over MCP — swapping the tool source
/// and swapping the speech provider stay independent decisions.
/// </summary>
public interface IVoiceToolBroker : IAsyncDisposable
{
    /// <summary>
    /// The tools this audience may use, already filtered and with schemas cleaned for the provider.
    /// Implementations must remember what they returned: <see cref="InvokeAsync"/> is required to refuse
    /// anything that was not advertised, because a model can name a tool it was never offered.
    /// </summary>
    Task<IReadOnlyList<VoiceToolDefinition>> ListToolsAsync(VoiceToolAudience audience, CancellationToken ct);

    /// <summary>
    /// Execute a call and return a result fit to hand back to the model. Never throws for an ordinary
    /// failure — a failed tool must still produce a <see cref="VoiceToolResult"/> with
    /// <c>IsError = true</c>, or the conversation stalls on an unanswered call id.
    /// </summary>
    Task<VoiceToolResult> InvokeAsync(VoiceToolCall call, CancellationToken ct);
}

/// <summary>Creates a broker for one conversation. Brokers are per-session, not pooled.</summary>
public interface IVoiceToolBrokerFactory
{
    /// <param name="context">
    /// What the page knew when it opened this conversation. Handed to the broker so it travels with the
    /// conversation's TOOL CALLS, not just its prompt: a tool that attributes a booking to the link the
    /// guest followed cannot ask the guest for a tracking token, and must not be told to guess one.
    /// Omit for a conversation the page knew nothing about.
    /// </param>
    Task<IVoiceToolBroker> CreateAsync(VoiceSessionContext? context = null, CancellationToken ct = default);
}

// Site: IVoiceSessionTicketService is not vendored — the kiosk's anonymous ticket is replaced here by the
// admin's auth cookie on the socket upgrade (see WebsiteVoiceEndpoints).
