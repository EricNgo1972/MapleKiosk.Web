// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/VoiceSessionTools.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The tools the CONVERSATION owns, as opposed to the ones a product exposes over MCP. There is one: the
/// agent's way of saying "we are done here".
///
/// <para>Why a tool and not a rule in the browser. Knowing that a guest has finished is a reading of what
/// they meant — "that's all, thanks", "no, I'm good", "see you Thursday", a sentence in Vietnamese that
/// matches no keyword we would have thought to list. The only participant that understands the whole
/// conversation is the model, so it is the one asked. Matching words client-side would end the call on
/// "thanks" mid-booking and never end it for anyone speaking a language nobody wrote a list for.</para>
///
/// <para>Declared here rather than in the relay because it is part of the contract every provider is given
/// — the same reason <see cref="VoicePrompt"/> lives in BO. The relay only supplies the mechanics of
/// hanging up.</para>
/// </summary>
public static class VoiceSessionTools
{
    public const string EndCall = "end_call";

    /// <summary>
    /// Advertised alongside the product's own tools. The description carries the WHEN, because a model
    /// reads a tool's description at the moment it is deciding whether to call it — the system prompt is
    /// far away by then.
    /// </summary>
    public static readonly VoiceToolDefinition EndCallDefinition = new(
        EndCall,
        "End the conversation. Call this as soon as the person is finished — they have said goodbye, said "
        + "that is all, or thanked you with nothing left outstanding. Say your short goodbye in the same "
        + "turn: the line closes once you stop speaking. Do not call it while anything is unresolved, do "
        + "not call it to escape a question you cannot answer, and never ask the person to keep talking "
        + "just to avoid ending.",
        """
        {"type":"object","properties":{"reason":{"type":"string","description":"A few words on why the conversation is over, for the log — for example 'guest said goodbye' or 'appointment booked and confirmed'."}},"required":[]}
        """);

    public static bool IsEndCall(string? name) =>
        string.Equals(name, EndCall, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the model is told when it calls it. Answers at all — a tool call left unanswered leaves some
    /// providers waiting for a result instead of speaking — and answers with the one instruction that
    /// matters, so the farewell is a farewell and not another round of "is there anything else?".
    /// </summary>
    public const string EndCallAck =
        """{"ending":true,"instruction":"Say one short goodbye now and then stop. Ask nothing further."}""";

    /// <summary>
    /// Reads the model's stated reason out of the call arguments, for the log. Tolerant by design: a
    /// missing, malformed or unparseable argument must never stop a conversation from ending.
    /// </summary>
    public static string? ReadReason(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);

            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("reason", out var reason)
                   && reason.ValueKind == System.Text.Json.JsonValueKind.String
                ? reason.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
