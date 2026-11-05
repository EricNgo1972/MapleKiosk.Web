using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>What /voice-phone set: whether the line answers, which Twilio number it is, and its language.</summary>
public sealed record PhoneHookupSetup(bool Enabled, string Number, string NumberSid, string Language)
{
    public bool HasNumber => Number.Length > 0;
}

/// <summary>
/// The website's own phone line: one Twilio number whose voice webhook points at this site, answered by the
/// same voice agent as /voice-test. Its setup is kept in the keyvalue table, partition <c>Assistant</c>,
/// rows PhoneEnabled, PhoneNumber, PhoneNumberSid, PhoneLanguage (edited on /voice-phone).
///
/// <para>The number lives on the platform Twilio account (<c>Twilio/AccountSid</c>, <c>Twilio/AuthToken</c>,
/// the same one the phone-line shop uses): that account's token is what Twilio signs the webhooks with.</para>
/// </summary>
public static class PhoneHookup
{
    private const string Partition = AssistantSettings.Partition;

    public const string IncomingRoute = "/twilio/voice/incoming";
    public const string FallbackRoute = "/twilio/voice/fallback";
    public const string StatusRoute = "/twilio/voice/status";
    public const string StreamRoute = "/twilio/voice/stream";

    /// <summary>The webhooks are always the public site's: that is where Twilio can reach, whichever host saved them.</summary>
    public static string IncomingUrl => SiteHost.Root.TrimEnd('/') + IncomingRoute;
    public static string FallbackUrl => SiteHost.Root.TrimEnd('/') + FallbackRoute;
    public static string StatusUrl => SiteHost.Root.TrimEnd('/') + StatusRoute;

    public static readonly string[] Languages = { "en", "fr", "vi", "ru" };

    public static async Task<PhoneHookupSetup> ReadAsync()
    {
        var lang = (await KeyValueTable.ReadAsync(Partition, "PhoneLanguage")).Trim();
        return new PhoneHookupSetup(
            (await KeyValueTable.ReadAsync(Partition, "PhoneEnabled")).Trim() == "true",
            (await KeyValueTable.ReadAsync(Partition, "PhoneNumber")).Trim(),
            (await KeyValueTable.ReadAsync(Partition, "PhoneNumberSid")).Trim(),
            Languages.Contains(lang) ? lang : "en");
    }

    public static async Task SaveAsync(PhoneHookupSetup setup)
    {
        await KeyValueTable.WriteAsync(Partition, "PhoneEnabled", setup.Enabled ? "true" : "false");
        await KeyValueTable.WriteAsync(Partition, "PhoneNumber", setup.Number);
        await KeyValueTable.WriteAsync(Partition, "PhoneNumberSid", setup.NumberSid);
        await KeyValueTable.WriteAsync(Partition, "PhoneLanguage", Languages.Contains(setup.Language) ? setup.Language : "en");
    }

    /// <summary>The platform Twilio account, env var first — the same resolution as the phone-line shop's gateway.</summary>
    public static async Task<(string Sid, string Token)?> CredentialsAsync()
    {
        var sid = await KeyValueTable.ResolveAsync("TWILIO_ACCOUNT_SID", "Twilio", "AccountSid").ConfigureAwait(false);
        var token = await KeyValueTable.ResolveAsync("TWILIO_AUTH_TOKEN", "Twilio", "AuthToken").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(token) ? null : (sid.Trim(), token.Trim());
    }

    /// <summary>Whether a number's voice webhook is this site's line.</summary>
    public static bool PointsHere(string? voiceUrl) =>
        string.Equals(voiceUrl?.Trim(), IncomingUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>"+1 (604) 555-0123" style keys compared as digits only.</summary>
    public static string Digits(string? number) =>
        new((number ?? "").Where(char.IsAsciiDigit).ToArray());
}
