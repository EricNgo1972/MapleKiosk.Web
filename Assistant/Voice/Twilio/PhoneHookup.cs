using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The website's own phone line: a Twilio number whose voice webhook is set, by hand in the Twilio console, to
/// <see cref="IncomingUrl"/> is answered by the same voice agent as /voice-test. The steps are on
/// /admin/assistant (section 3). The only setting is the language a call opens in: keyvalue
/// <c>Assistant/PhoneLanguage</c>.
///
/// <para>The number must be on the platform Twilio account (<c>Twilio/AccountSid</c>, <c>Twilio/AuthToken</c>,
/// the same one the phone-line shop uses): that account's token is what Twilio signs the webhooks with.</para>
/// </summary>
public static class PhoneHookup
{
    private const string Partition = AssistantSettings.Partition;

    public const string IncomingRoute = "/twilio/voice/incoming";
    public const string FallbackRoute = "/twilio/voice/fallback";
    public const string StatusRoute = "/twilio/voice/status";
    public const string StreamRoute = "/twilio/voice/stream";

    /// <summary>The webhooks are always the public site's: that is where Twilio can reach.</summary>
    public static string IncomingUrl => SiteHost.Root.TrimEnd('/') + IncomingRoute;
    public static string FallbackUrl => SiteHost.Root.TrimEnd('/') + FallbackRoute;
    public static string StatusUrl => SiteHost.Root.TrimEnd('/') + StatusRoute;

    /// <summary>Bilingual first: MapleKiosk is in Quebec, so a call opens "Bonjour, hi!" unless set otherwise.</summary>
    public static readonly string[] Languages = { MapleKioskVoicePersona.Bilingual, "fr", "en", "vi", "ru" };

    public const string DefaultLanguage = MapleKioskVoicePersona.Bilingual;

    public static async Task<string> LanguageAsync()
    {
        var lang = (await KeyValueTable.ReadAsync(Partition, "PhoneLanguage")).Trim();
        return Languages.Contains(lang) ? lang : DefaultLanguage;
    }

    public static Task SaveLanguageAsync(string lang) =>
        KeyValueTable.WriteAsync(Partition, "PhoneLanguage", Languages.Contains(lang) ? lang : DefaultLanguage);

    /// <summary>What a phone call opens with when /admin/assistant sets nothing: Quebec, French first.</summary>
    public const string DefaultGreeting = "Bonjour, Hi. This is Lisa from MapleKiosk, how can I help you today?";

    /// <summary>The default greeting with the assistant's name in it.</summary>
    public static string DefaultGreetingFor(string agentName) => AssistantSettings.Named(DefaultGreeting, agentName);

    /// <summary>The phone greeting: row Assistant/PhoneGreeting (/admin/assistant), else the default with the assistant's name.</summary>
    public static async Task<string> GreetingAsync()
    {
        var own = (await KeyValueTable.ReadAsync(Partition, "PhoneGreeting")).Trim();
        return own == "" ? DefaultGreetingFor(await AssistantSettings.AgentNameAsync()) : own;
    }

    /// <summary>Saves the phone greeting; blank (or the default itself) goes back to the default, which follows the name.</summary>
    public static Task SaveGreetingAsync(string greeting, string agentName)
    {
        var g = greeting.Trim();
        return KeyValueTable.WriteAsync(Partition, "PhoneGreeting",
            g == DefaultGreeting || g == DefaultGreetingFor(agentName) ? "" : g.Length <= 400 ? g : g[..400]);
    }

    /// <summary>The platform Twilio account, env var first — the same resolution as the phone-line shop's gateway.</summary>
    public static async Task<(string Sid, string Token)?> CredentialsAsync()
    {
        var sid = await KeyValueTable.ResolveAsync("TWILIO_ACCOUNT_SID", "Twilio", "AccountSid").ConfigureAwait(false);
        var token = await KeyValueTable.ResolveAsync("TWILIO_AUTH_TOKEN", "Twilio", "AuthToken").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(token) ? null : (sid.Trim(), token.Trim());
    }

    /// <summary>
    /// MapleKiosk's own Twilio number, learned from the webhooks (the "To" of the last call or text): what
    /// call_person rings the staff phone from when the guest is on the chat. Row Assistant/LineNumber.
    /// </summary>
    public static async Task<string?> LineNumberAsync() =>
        E164((await KeyValueTable.ReadAsync(Partition, "LineNumber")).Trim());

    private static string? _knownLine;

    /// <summary>Remembers the line a call or text came in on; writes only when it changes.</summary>
    public static async Task LearnLineAsync(string? to)
    {
        if (E164(to) is not { } line || line == _knownLine) return;
        _knownLine = line;
        if (await LineNumberAsync() != line) await KeyValueTable.WriteAsync(Partition, "LineNumber", line);
    }

    /// <summary>"418 717-3979", "+1 (418) 717-3979" → "+14187173979"; null when it isn't a phone number.</summary>
    public static string? E164(string? number)
    {
        var digits = Digits(number);
        if ((number ?? "").TrimStart().StartsWith('+')) return digits.Length is >= 8 and <= 15 ? "+" + digits : null;
        return digits.Length switch
        {
            10 => "+1" + digits,
            11 when digits[0] == '1' => "+" + digits,
            _ => null,
        };
    }

    /// <summary>"+1 (604) 555-0123" style keys compared as digits only.</summary>
    public static string Digits(string? number) =>
        new((number ?? "").Where(char.IsAsciiDigit).ToArray());
}
