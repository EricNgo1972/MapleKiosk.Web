// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/IVoicePersona.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// What this product's voice agent is — its identity and the rules only its own domain knows.
///
/// <para>Exists so the speech stack can be shared while the thing being said is not. Core supplies the
/// conduct rules every voice agent must follow (<see cref="VoicePrompt.Base"/>); the product supplies who
/// is speaking and what they may do. Neither half can be written by the other: a salon's four-fields-to-book
/// rule means nothing to a ticket desk, and the never-read-an-id-aloud rule must not be a product's to
/// forget.</para>
///
/// <para>Keyed by audience because the same product speaks differently to a guest in a lobby than to a
/// signed-in staff member — and because the tool set differs too, a persona that promised a customer
/// something only staff tools can do would be lying.</para>
/// </summary>
public interface IVoicePersona
{
    /// <summary>
    /// The identity and domain rules for this audience. Returning blank is legitimate — the agent then
    /// runs on the Core conduct rules alone, which is what an unconfigured head gets.
    /// </summary>
    string ForAudience(VoiceToolAudience audience);

    /// <summary>
    /// What to tell the agent about THIS conversation, given what the page already knew — typically that
    /// it need not ask for something the guest has already typed.
    ///
    /// <para>Defaulted to nothing so a product that captures no context is unaffected. Core cannot write
    /// this: it holds the pairs but has no idea that an email means "use it for the ticket" rather than
    /// "greet them by it".</para>
    /// </summary>
    string ForContext(VoiceToolAudience audience, VoiceSessionContext context) => string.Empty;

    /// <summary>
    /// The first thing said, before the person has spoken — used when the operator has not written a
    /// greeting of their own on the voice profile.
    ///
    /// <para>A product owns this for the same reason it owns <see cref="ForAudience"/>: how a business
    /// answers is part of how it sounds, and a lobby kiosk, a support desk and a coffee counter do not
    /// open the same way. Returning blank is legitimate and means the agent waits to be spoken to.</para>
    /// </summary>
    /// <param name="businessName">
    /// The company's own name, or blank when Company Settings has not been filled in — in which case a
    /// greeting must still work, name-free, rather than welcoming anyone to a placeholder.
    /// </param>
    string GreetingFor(VoiceToolAudience audience, string businessName) => string.Empty;

    /// <summary>
    /// What the agent asks when the conversation has gone quiet, before the session gives up on it.
    ///
    /// <para>A conversation ends one of two ways: the person says they are done — see
    /// <see cref="VoiceSessionTools.EndCall"/> — or they simply walk away. The second one deserves asking
    /// about rather than assuming: someone who stepped aside to check their phone comes back to a kiosk
    /// that quietly hung up on them, and has no idea why. One question separates "gone" from "thinking".</para>
    ///
    /// <para>Defaulted rather than abstract, and to a plain line: every product needs this and none of them
    /// needs a different one badly enough to be forced to write it. Returning blank turns the question off,
    /// and the session then simply times out in silence.</para>
    /// </summary>
    string StillThereFor(VoiceToolAudience audience) => "Are you still there?";
}

/// <summary>
/// The persona for a head that has not written one — deliberately plain rather than absent, so an
/// unconfigured install still holds a coherent conversation instead of speaking with no identity at all.
/// Mirrors <c>StubVoiceAgentProvider</c>: the fallback is harmless, not broken.
/// </summary>
public sealed class DefaultVoicePersona : IVoicePersona
{
    public string ForAudience(VoiceToolAudience audience) => audience switch
    {
        VoiceToolAudience.Staff =>
            "You are the voice assistant of this business, talking to a member of staff. "
            + "Help them with whatever the tools you have been given can do, and say plainly when something "
            + "is outside them.",

        _ =>
            "You are the voice assistant of this business, talking to a customer. "
            + "Help them with whatever the tools you have been given can do. If they ask for something you "
            + "have no tool for, say so plainly and suggest they speak to a member of staff.",
    };

    public string GreetingFor(VoiceToolAudience audience, string businessName) =>
        string.IsNullOrWhiteSpace(businessName)
            ? "Hello. How can I help you?"
            : $"Hello, this is {businessName.Trim()}. How can I help you?";
}
