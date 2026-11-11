using System.Net;
using MapleKiosk.Web.Assistant.Voice;
using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// Tells the staff about every request or message, two ways at once:
/// <list type="bullet">
///   <item>a text to the staff phone (row Assistant/StaffPhone) from MapleKiosk's Twilio number, recorded in the
///   SMS inbox as "Staff alert" — skipped when no staff phone is set or the line isn't known yet;</item>
///   <item>an email to the person in charge (<see cref="AssistantSettings.MessageInboxAsync"/>) — skipped when the
///   caller already emails that address itself.</item>
/// </list>
/// Any hour of the day. Never throws; each half goes on its own.
/// </summary>
public static class StaffAlert
{
    /// <summary>Longest staff text: two SMS segments, so a long message isn't split into many.</summary>
    internal const int MaxText = 300;

    /// <param name="text">One plain line: what happened, who, how to reach them. A link is added when <paramref name="path"/> is given.</param>
    /// <param name="path">Where to read the rest on the site, e.g. "/chats/abc" or "/admin/leads".</param>
    /// <param name="email">Null to skip the email (the caller sent it already).</param>
    /// <param name="html">The email body; the text (and link) when null.</param>
    /// <param name="textStaff">False when the caller texted the staff phone itself.</param>
    /// <param name="alreadyEmailed">An address the caller already emailed: no second copy when it is the person in charge.</param>
    public static async Task<(bool Texted, bool Emailed)> SendAsync(string text, string? path, EmailService? email,
        string subject, string? html, ILogger logger, CancellationToken ct = default, string? alreadyEmailed = null,
        bool textStaff = true)
    {
        var link = path is null ? "" : SiteHost.Root.TrimEnd('/') + path;
        var texted = false;
        var emailed = false;

        try
        {
            var staff = await AssistantSettings.StaffPhoneAsync();
            var line = await PhoneHookup.LineNumberAsync();
            if (textStaff && staff is not null && line is not null && staff != PhoneHookup.E164(line))
                texted = await TwilioApi.SendSmsAsync(line, staff, Text(text, link), logger, ct, SmsKind.Alert);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "StaffAlert: the text to the staff phone failed.");
        }

        if (email is not null)
        {
            try
            {
                var body = html ?? $"<p style=\"font-family:Arial,sans-serif;font-size:15px\">{WebUtility.HtmlEncode(text)}</p>"
                    + (link == "" ? "" : $"<p style=\"font-family:Arial,sans-serif\"><a href=\"{link}\">{WebUtility.HtmlEncode(link)}</a></p>");
                var inbox = await AssistantSettings.MessageInboxAsync();
                if (!string.Equals(inbox.Trim(), alreadyEmailed?.Trim(), StringComparison.OrdinalIgnoreCase))
                    emailed = await email.SendAsync(inbox, subject, body) is null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "StaffAlert: the email to the person in charge failed.");
            }
        }

        return (texted, emailed);
    }

    /// <summary>Starts <see cref="SendAsync"/> without waiting: a form or a tool never waits on the staff alert.</summary>
    public static void Queue(string text, string? path, EmailService? email, string subject, string? html, ILogger logger,
        string? alreadyEmailed = null, bool textStaff = true) =>
        _ = Task.Run(() => SendAsync(text, path, email, subject, html, logger, CancellationToken.None, alreadyEmailed, textStaff));

    /// <summary>"MapleKiosk: …" cut to <see cref="MaxText"/>, the link always kept whole at the end.</summary>
    internal static string Text(string text, string link)
    {
        var head = "MapleKiosk: " + string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var room = MaxText - (link.Length == 0 ? 0 : link.Length + 1);
        if (head.Length > room) head = head[..Math.Max(0, room - 1)].TrimEnd() + "…";
        return link.Length == 0 ? head : head + " " + link;
    }
}
