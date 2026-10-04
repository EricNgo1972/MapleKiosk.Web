// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/IVoiceOutcomeReader.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Recognises, in a tool result, that the conversation created something durable — an appointment, a
/// support ticket, an order — and names its id.
///
/// <para>The relay invokes tools it knows nothing about; that is the whole point of the MCP broker. So
/// without this it can see that <i>something</i> succeeded but not that a guest now has a booking. This is
/// the one contract that knows a product's tool names and answer shapes, which keeps that knowledge in a
/// single findable place per product rather than sprinkled through the relay.</para>
///
/// <para><b>Implementations must be tolerant.</b> A result that is not JSON, not an object, or not a
/// creation simply yields nothing: a conversation has to be recorded even when a tool answers in a shape
/// nobody expected. Never throw from here — the relay records outcomes on a path that must not be able to
/// fail a call the guest already saw succeed.</para>
///
/// <para>Note that products differ in how success is signalled, and the contract accommodates both. A
/// salon's <c>book_appointment</c> answers with alternatives when the slot was taken, so only an explicit
/// <c>booked:true</c> distinguishes a booking from a refusal; a ticket desk's <c>submit_ticket</c> either
/// answers with an id or errors, so the id's presence is signal enough.</para>
/// </summary>
public interface IVoiceOutcomeReader
{
    /// <summary>
    /// The tools whose results are worth reading — used to narrow an audit query when recovering outcomes
    /// for conversations recorded before the ids were kept on the row itself. Empty means this product
    /// records nothing, and callers can skip the query entirely.
    /// </summary>
    IReadOnlyCollection<string> RecordingToolNames { get; }

    /// <summary>
    /// The id of the record this tool result created, or null when the call created nothing, did not
    /// succeed, or answered in an unreadable shape.
    /// </summary>
    string? ReadRecordId(string? toolName, string? resultJson);

    /// <summary>
    /// The tool the agent calls to NOTE the guest's booking details as it gathers them (name, phone,
    /// service, time), or null if the head has none. The relay watches for calls to this tool and keeps
    /// the running draft on the conversation, so a call that ends before the guest confirms still leaves a
    /// structured request. Default null — heads without such a tool are unaffected.
    /// </summary>
    string? DraftToolName => null;

    /// <summary>
    /// Parse a <see cref="VoiceBookingDraft"/> from that tool's call ARGUMENTS, or null when it is not the
    /// draft tool or the arguments are unreadable. Tolerant like <see cref="ReadRecordId"/>; never throws.
    /// Default null.
    /// </summary>
    VoiceBookingDraft? ReadDraft(string? toolName, string? argumentsJson) => null;
}

/// <summary>
/// The reader for a head that has not written one. Conversations are still transcribed and stored in full;
/// they simply carry no linked record id — which is the right default, because a product that records
/// nothing is far better served than one that guesses at a shape it was never told about.
/// </summary>
public sealed class NullVoiceOutcomeReader : IVoiceOutcomeReader
{
    public IReadOnlyCollection<string> RecordingToolNames => [];

    public string? ReadRecordId(string? toolName, string? resultJson) => null;
}
