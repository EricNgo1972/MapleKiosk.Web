// Vendored from the monorepo: MapleKiosk/MK.Chat/SPC.BO.Voice/Contracts/VoiceSessionContext.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.


namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Facts the page already knows about this conversation before a word is spoken — typically something the
/// guest typed, which is worth far more than the same thing said aloud.
///
/// <para>The motivating case is an email address. A support desk's <c>submit_ticket</c> finds-or-creates a
/// customer by email and sends them a confirmation, so a misheard address opens a ticket under a
/// stranger's account and emails it to them. Typed once on a keyboard, it is exactly what the guest meant;
/// spelled out letter by letter over a lobby microphone, it is a coin toss.</para>
///
/// <para><b>Free-form key/value on purpose.</b> Core has no business knowing that "email" matters to a
/// ticket desk and "table" might matter to a coffee counter — it only carries the pairs from the page to
/// the prompt. What they MEAN is the product's answer, given by
/// <see cref="IVoicePersona.ForContext"/>.</para>
///
/// <para><b>Trust:</b> these values ride inside the signed, single-use session ticket rather than the
/// socket's query string, so they cannot be edited between the page issuing them and the relay reading
/// them. They are still guest-supplied — the guest typed them — so a product must treat them as claims,
/// not as verified identity.</para>
/// </summary>
public sealed record VoiceSessionContext(IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The conventional key for a guest's email address. Shared so page and product agree.</summary>
    public const string EmailKey = "email";

    /// <summary>The conventional key for a guest's display name.</summary>
    public const string NameKey = "name";

    /// <summary>
    /// The conventional key for a guest's phone number. On a phone call it is the caller id — known before
    /// a word is said, and worth more than the same digits read out over a trunk line.
    /// </summary>
    public const string PhoneKey = "phone";   // SPC.BO.Abstractions.AssistantContext.PhoneKey

    /// <summary>
    /// The conventional key for HOW the guest reached the agent, when it is not the default kiosk. A
    /// persona written for someone standing in the lobby says different things to someone on the phone.
    /// </summary>
    public const string ChannelKey = "channel";   // SPC.BO.Abstractions.AssistantContext.ChannelKey

    /// <summary>The telephone network's id for THIS call, when the channel is a phone line. The one fact a
    /// tool acting on the live call — putting it through to a person — needs.</summary>
    public const string CallSidKey = "callSid";   // SPC.BO.Abstractions.AssistantContext.CallSidKey

    /// <summary>The <see cref="ChannelKey"/> value for a telephone call.</summary>
    public const string PhoneChannel = "phone";   // SPC.BO.Abstractions.AssistantContext.PhoneCallChannel

    /// <summary>
    /// The <see cref="ChannelKey"/> value for voice with a transcript on screen — the lobby kiosk, or the
    /// voice page on a PC. Also what an ABSENT key means, so a session ticket issued before the page named
    /// its channel keeps working.
    /// </summary>
    public const string DesktopVoiceChannel = "desktop";

    /// <summary>
    /// The greeting the CHANNEL already spoke before the session opened, when it did. A phone line answers
    /// the call with the greeting itself (a canned line plays the instant the call connects) rather than
    /// waiting several seconds for the model to compose one — so the model must be told the words were
    /// said, and must not greet again. Absent on a kiosk, where the model still opens the conversation.
    /// </summary>
    public const string GreetedKey = "greeted";

    /// <summary>
    /// The conventional key for the page's own id for this conversation, minted fresh each time a
    /// conversation is opened and never shown or spoken. It is how a product keeps state PER CONVERSATION
    /// on the server — a cart being built, say — where the tools that write it run in an MCP request and
    /// the page that shows it runs in a Blazor circuit, and neither can reach the other's scope. The
    /// ticket carries it to the relay, the relay puts it on every tool call of the conversation (see the
    /// assistant-context header), and the page hands it to whatever it renders beside the transcript.
    /// </summary>
    public const string SessionIdKey = "session";

    /// <summary>A conversation the page knew nothing about — the ordinary case.</summary>
    public static readonly VoiceSessionContext Empty =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public bool IsEmpty => Values.Count == 0;

    /// <summary>The value for a key, or null when the page did not supply it.</summary>
    public string? Get(string key) =>
        Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>Convenience for the common case.</summary>
    public string? Email => Get(EmailKey);

    public string? Phone => Get(PhoneKey);

    /// <summary>The caller's name when it is already known — the guest typed it, or a phone lookup found
    /// it (see <c>IVoiceCallerDirectory</c>). Null means the agent should ask.</summary>
    public string? Name => Get(NameKey);

    /// <summary>
    /// How the guest reached the agent. Every voice session is one of the two voice channels: a phone call
    /// when the opener said so, desktop voice otherwise (including when nothing was said at all).
    /// </summary>
    public ConversationChannel Channel => IsPhoneCall ? ConversationChannel.Phone : ConversationChannel.DesktopVoice;

    /// <summary>True when this conversation is a telephone call rather than a kiosk session.</summary>
    public bool IsPhoneCall =>
        string.Equals(Get(ChannelKey), PhoneChannel, StringComparison.OrdinalIgnoreCase);

    /// <summary>The greeting already spoken by the channel, or null when the model opens the conversation.</summary>
    public string? SpokenGreeting => Get(GreetedKey);

    /// <summary>
    /// Set when this session opened because a transfer to a person FAILED — nobody answered, and the call
    /// came back to the assistant. A product's persona says what to do about it (the salon takes a name and
    /// number for a callback); Core only carries the fact.
    /// </summary>
    public const string TransferFailedKey = "transferFailed";

    /// <summary>Whether the guest is back because nobody answered the transfer.</summary>
    public bool TransferFailed => Get(TransferFailedKey) is not null;

    /// <summary>
    /// Set with <see cref="TransferFailedKey"/> when the line has ALREADY texted the staff to phone this
    /// caller back (see <c>AssistantMonitoring.MissedTransferAsync</c>). Absent when it could not —
    /// the number was withheld, or nobody could be texted — and the assistant must take the callback itself.
    /// </summary>
    public const string CallbackSentKey = "callbackSent";

    /// <summary>Whether the staff have already been texted to call this caller back.</summary>
    public bool CallbackSent => Get(CallbackSentKey) is not null;

    /// <summary>The page's id for this conversation (see <see cref="SessionIdKey"/>), or null for a session
    /// opened by something that mints none — a phone call, or a ticket issued before the key existed.</summary>
    public string? SessionId => Get(SessionIdKey);

    /// <summary>
    /// How the guest's audio reaches the agent, for the provider's turn-taking. A phone call is the only
    /// channel that says so; everything else is the kiosk, which is what the defaults were tuned on.
    /// </summary>
    public VoiceAcoustics Acoustics => IsPhoneCall ? VoiceAcoustics.Telephone : VoiceAcoustics.Kiosk;

    /// <summary>Builds a context from pairs, dropping blanks so a supplied-but-empty field reads as absent.</summary>
    public static VoiceSessionContext From(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                values[key] = value.Trim();
        }

        return values.Count == 0 ? Empty : new VoiceSessionContext(values);
    }
}
