// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/VoicePrompt.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Assembles what the voice agent is told it is, from three layers with three different owners.
///
/// <para>Kept in the BO layer because it is the behavioural rule for a business capability, not
/// presentation — and because both providers must be given the same one, or switching profile would
/// quietly change how the business talks to its customers.</para>
///
/// <para>The layering exists so that sharing the speech stack across products cannot dilute the rules that
/// keep a conversation honest. Core owns <see cref="Base"/>; a product owns its
/// <see cref="IVoicePersona"/>; an operator owns <c>VoiceProfile.PromptAddendum</c>. A product can add to
/// the conduct rules but has no way to drop them.</para>
/// </summary>
public static class VoicePrompt
{
    /// <summary>
    /// How every Maple voice agent conducts itself, whatever it happens to be selling. Two kinds of rule
    /// live here and nowhere else: how speech differs from text, and what the agent may state as fact.
    ///
    /// <para>The confirm-before-writing rule is enforced HERE and only here, which is a deliberate and
    /// weaker choice than the text assistant makes: <c>/chat</c> suspends every destructive tool behind a
    /// real yes/no gate in <c>DefaultSafetyGate</c>, whereas by voice a record is written whenever the
    /// model decides to call the tool. If that trade ever needs reversing,
    /// <see cref="IVoiceToolBroker.InvokeAsync"/> is the single chokepoint where a two-phase gate would go,
    /// without any vendor or any product knowing.</para>
    /// </summary>
    public const string Base =
        "Speak the way a person speaks out loud: short sentences, no lists, no markdown, no emoji, and "
        + "never read out an internal code or an id. "
        + "Only state as fact what a tool has told you. Never invent a name, a number, a time or any other "
        + "detail, and never act on a placeholder. If you have not checked something, say you will check "
        + "rather than guessing. "
        + "Before you do anything that creates or changes a record, say back what you are about to do and "
        + "wait for the person to agree. Only once it has actually succeeded may you say it is done. "
        + "If something fails, say so honestly rather than covering for it. "
        + "Speak the language the person is speaking to you in. "
        + ToolFiller;

    /// <summary>
    /// Why the agent must talk BEFORE it uses a tool. Every provider here calls tools synchronously: from
    /// the moment the model decides to call one until the answer comes back and a new reply is composed,
    /// it says nothing — and a guest at a counter, or a caller holding a phone, reads a few seconds of
    /// that as the line going dead. Measured on a real phone line, callers hung up inside that gap. The
    /// tool itself answers in well under a second; the silence is the model's, so the model is told to
    /// fill it. Part of <see cref="Base"/> so no product can forget it.
    /// </summary>
    public const string ToolFiller =
        "Whenever you are about to use a tool, first say one short natural phrase out loud — such as "
        + "\"One moment, let me check that\" or \"Let me look\" — and only then call the tool, so the "
        + "person never hears silence while you work. When you must use several tools for one request, "
        + "call them together in the same turn rather than one after another.";

    /// <summary>
    /// How a conversation ends, added only when the session actually offers
    /// <see cref="VoiceSessionTools.EndCall"/> — a provider that cannot call tools must not be told to
    /// call one.
    ///
    /// <para>Both halves are load-bearing. Without the first, the agent keeps a guest who has said goodbye
    /// standing at the kiosk answering "anything else?"; without the second, it hangs up on someone who
    /// paused to think, which is far worse than a session that runs a few seconds long.</para>
    /// </summary>
    public const string Hangup =
        "When the person is finished — they say goodbye, say that is all, or thank you with nothing left "
        + $"outstanding — say a short goodbye and call {VoiceSessionTools.EndCall} in the same turn. "
        + "A pause, a hesitation, or a question you could not answer is NOT the person finishing: keep "
        + "listening. Never end the conversation while anything you were asked for is still unsettled.";

    /// <summary>
    /// Identity first, then conduct, then the operator's own words — the order a person would introduce
    /// themselves in, and the order that keeps a model's attention on who it is before what it may do.
    /// The business's name leads even that: it is the one part of the identity that is the install's own.
    /// </summary>
    /// <param name="persona">This product's identity and domain rules; blank falls through to conduct alone.</param>
    /// <param name="sessionContext">
    /// What the product wants said about THIS conversation — see <see cref="IVoicePersona.ForContext"/>.
    /// Placed after the conduct rules so it reads as "and for this one", and before the operator's words
    /// so an operator can still override it.
    /// </param>
    /// <param name="addendum">The operator's per-install instructions, from the active voice profile.</param>
    /// <param name="businessName">
    /// The company's own name. Core's to supply, not a product's: every Maple head has one, in the same
    /// setting. Without it an agent that opens with "this is Element Nails speaking" cannot answer "who am
    /// I talking to?" a minute later — it was told the words, never the name. Blank is fine and simply
    /// leaves the agent nameless, which is what an install with empty Company Settings honestly is.
    /// </param>
    /// <param name="canEndCall">
    /// Whether this session advertises <see cref="VoiceSessionTools.EndCall"/>. False leaves
    /// <see cref="Hangup"/> out entirely rather than telling an agent to call a tool it was never given.
    /// </param>
    /// <param name="grounding">
    /// What the product sells, from <c>IAssistantGrounding</c> — the menu a customer conversation is held
    /// to. Placed right after the persona: the persona says "you can describe the services", this says
    /// which. Null for a staff conversation, or a head that registered none.
    /// </param>
    /// <param name="spokenGreeting">
    /// The greeting the channel already played before this session opened (see
    /// <see cref="VoiceSessionContext.SpokenGreeting"/>), or null when the model greets. Told to the model
    /// as something IT said: without this it opens with a second hello, and the guest — who has just been
    /// asked how they can be helped — is asked again.
    /// </param>
    public static string Compose(string? persona, string? sessionContext, string? addendum,
                                 string? businessName = null, bool canEndCall = false,
                                 string? spokenGreeting = null, string? grounding = null)
    {
        var parts = new List<string>(8);

        if (!string.IsNullOrWhiteSpace(businessName))
            parts.Add($"You speak for {businessName.Trim()}. That is the name you introduce yourself by.");

        if (!string.IsNullOrWhiteSpace(persona)) parts.Add(persona.Trim());
        if (!string.IsNullOrWhiteSpace(grounding)) parts.Add(grounding.Trim());
        parts.Add(Base);
        if (canEndCall) parts.Add(Hangup);
        if (!string.IsNullOrWhiteSpace(sessionContext)) parts.Add(sessionContext.Trim());
        if (!string.IsNullOrWhiteSpace(spokenGreeting)) parts.Add(AlreadyGreeted(spokenGreeting));
        if (!string.IsNullOrWhiteSpace(addendum)) parts.Add(addendum.Trim());

        return string.Join("\n\n", parts);
    }

    /// <summary>What the model is told when the channel has already said hello on its behalf.</summary>
    public static string AlreadyGreeted(string spokenGreeting) =>
        $"You have ALREADY answered and greeted the person by saying: \"{spokenGreeting.Trim()}\" "
        + "Do not greet them again and do not introduce yourself again — they are about to tell you what "
        + "they need. Wait for them to speak, then help.";

    /// <summary>
    /// The greeting a conversation opens with: the operator's own words from the profile when set, else
    /// the product's, and null when neither says anything — which means "wait to be spoken to". One
    /// resolution shared by the relay (where the model speaks it) and the phone line (where the channel
    /// plays it before the model is even connected), so the two can never greet differently.
    /// </summary>
    public static string? ResolveGreeting(
        string? operatorGreeting, IVoicePersona persona, VoiceToolAudience audience, string businessName)
    {
        if (!string.IsNullOrWhiteSpace(operatorGreeting)) return operatorGreeting.Trim();

        var product = persona.GreetingFor(audience, businessName);
        return string.IsNullOrWhiteSpace(product) ? null : product.Trim();
    }
}
