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

    public static readonly string[] Languages = { "en", "fr", "vi", "ru" };

    public static async Task<string> LanguageAsync()
    {
        var lang = (await KeyValueTable.ReadAsync(Partition, "PhoneLanguage")).Trim();
        return Languages.Contains(lang) ? lang : "en";
    }

    public static Task SaveLanguageAsync(string lang) =>
        KeyValueTable.WriteAsync(Partition, "PhoneLanguage", Languages.Contains(lang) ? lang : "en");

    /// <summary>The platform Twilio account, env var first — the same resolution as the phone-line shop's gateway.</summary>
    public static async Task<(string Sid, string Token)?> CredentialsAsync()
    {
        var sid = await KeyValueTable.ResolveAsync("TWILIO_ACCOUNT_SID", "Twilio", "AccountSid").ConfigureAwait(false);
        var token = await KeyValueTable.ResolveAsync("TWILIO_AUTH_TOKEN", "Twilio", "AuthToken").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(token) ? null : (sid.Trim(), token.Trim());
    }

    /// <summary>"+1 (604) 555-0123" style keys compared as digits only.</summary>
    public static string Digits(string? number) =>
        new((number ?? "").Where(char.IsAsciiDigit).ToArray());
}
