using MapleKiosk.Web.Assistant.Voice;
using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// Short confirmations texted to a guest from MapleKiosk's Twilio number when something was done for them:
/// a demo request (the website form or the assistant), an order request, a message for the team, a callback.
/// On by default; switched off on /admin/assistant (row Assistant/AutoTexts = "off"). Canadian and US numbers
/// only, never to the line itself or the staff phone, and never to someone already texting (the assistant's
/// reply confirms it there). Each is recorded in the SMS inbox (/admin/sms) as "Automatic". Never throws.
/// </summary>
public static class AutoTexts
{
    public static async Task<bool> EnabledAsync() =>
        !string.Equals((await KeyValueTable.ReadAsync(AssistantSettings.Partition, "AutoTexts")).Trim(), "off", StringComparison.OrdinalIgnoreCase);

    public static Task SaveEnabledAsync(bool on) =>
        KeyValueTable.WriteAsync(AssistantSettings.Partition, "AutoTexts", on ? "" : "off");

    /// <summary>"Thanks Marie! We got your demo request…", in the guest's language.</summary>
    public static string Demo(string? name, string lang) => Say(lang,
        $"merci{To(name)}! Votre demande de démo est bien reçue. Notre équipe vous appellera pour fixer l'heure.",
        $"thanks{To(name)}! We got your demo request. Our team will call you to set the time.");

    public static string OrderRequest(string? name, string lang) => Say(lang,
        $"merci{To(name)}! Votre commande est reçue. Notre équipe vous contactera pour la confirmer.",
        $"thanks{To(name)}! We got your order. Our team will contact you to confirm it.");

    public static string MessageTaken(string? name, string lang) => Say(lang,
        $"merci{To(name)}! Votre message est transmis à notre équipe, qui vous recontactera bientôt.",
        $"thanks{To(name)}! Your message is with our team, who will get back to you soon.");

    public static string Callback(string? name, string lang) => Say(lang,
        $"bonjour{To(name)}! Quelqu'un de MapleKiosk vous rappelle sous peu à ce numéro.",
        $"hi{To(name)}! Someone from MapleKiosk will call you back shortly at this number.");

    /// <summary>Texts <paramref name="body"/> to the guest when it should go; false when skipped or refused.</summary>
    public static async Task<bool> SendAsync(string? phone, string body, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            if (Recipient(phone, await PhoneHookup.LineNumberAsync(), await AssistantSettings.StaffPhoneAsync()) is not { } to)
                return false;
            if (!await EnabledAsync()) return false;
            var line = (await PhoneHookup.LineNumberAsync())!;
            return await TwilioApi.SendSmsAsync(line, to, body, logger, ct, SmsKind.Auto);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Sms: the automatic text to {Phone} failed.", phone);
            return false;
        }
    }

    /// <summary>Starts <see cref="SendAsync"/> without waiting: a form or a tool never waits on a text.</summary>
    public static void Queue(string? phone, string body, ILogger logger) =>
        _ = Task.Run(() => SendAsync(phone, body, logger, CancellationToken.None));

    /// <summary>The number to text, or null: a +1 (Canada/US) mobile-shaped number that isn't the line or the staff phone.</summary>
    internal static string? Recipient(string? phone, string? line, string? staff)
    {
        if (line is null || PhoneHookup.E164(phone) is not { } to || !to.StartsWith("+1") || to.Length != 12) return null;
        return to == PhoneHookup.E164(line) || to == PhoneHookup.E164(staff) ? null : to;
    }

    private static string To(string? name) => string.IsNullOrWhiteSpace(name) ? "" : " " + name.Trim().Split(' ')[0];

    /// <summary>French for fr, both for Quebec's bilingual line, English otherwise.</summary>
    private static string Say(string lang, string fr, string en) => lang switch
    {
        "fr" => "MapleKiosk : " + Cap(fr),
        MapleKioskVoicePersona.Bilingual => "MapleKiosk : " + Cap(fr) + " / " + Cap(en),
        _ => "MapleKiosk: " + Cap(en),
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
