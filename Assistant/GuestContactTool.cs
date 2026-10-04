using System.ComponentModel;
using System.Net;
using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// The assistant's one tool: keep the name and contact a guest chose to give, on the conversation's
/// record (/chats), and tell sales by email so someone follows up. Like the platform's customer tools it
/// acts only on what the guest actually wrote, and says what happened so the reply cannot claim more.
/// </summary>
public sealed class GuestContactTool
{
    private const string SalesInbox = "sales@maplekiosk.ca";

    private readonly ChatLog _chats;
    private readonly EmailService _email;
    private readonly ILogger<GuestContactTool> _logger;

    public GuestContactTool(ChatLog chats, EmailService email, ILogger<GuestContactTool> logger)
    {
        _chats = chats;
        _email = email;
        _logger = logger;
    }

    /// <summary>The tool for one conversation: it can only ever save to that conversation.</summary>
    public AIFunction For(string conversationId, string lang, string page) =>
        AIFunctionFactory.Create(
            async ([Description("The guest's name, exactly as they wrote it.")] string name,
                   [Description("Their phone number, if they gave one.")] string? phone,
                   [Description("Their email address, if they gave one.")] string? email,
                   [Description("Their business or trade, if they said (e.g. \"nail salon in Laval\").")] string? business,
                   [Description("What they are interested in, in a few words (e.g. \"MapleSPA, pricing, demo\").")] string? interest,
                   CancellationToken ct) =>
                await SaveAsync(conversationId, lang, page, new GuestContact(
                    Trim(name, 80) ?? "", Trim(phone, 40), Trim(email, 120), Trim(business, 120), Trim(interest, 200)), ct),
            name: "save_guest_contact",
            description: "Save the guest's name and how to reach them (phone or email) so the MapleKiosk team can follow up. " +
                         "Call it only with details the guest actually wrote in this conversation — never guess or invent one.");

    private async Task<string> SaveAsync(string id, string lang, string page, GuestContact c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Name))
            return "Not saved: the guest's name is missing. Ask for it.";
        if (string.IsNullOrWhiteSpace(c.Phone) && string.IsNullOrWhiteSpace(c.Email))
            return "Not saved: there is no phone number or email yet. Ask how they would like to be reached.";

        await _chats.SaveContactAsync(id, c, ct);
        _logger.LogInformation("Assistant: a guest left a contact (conversation {Id}).", id);

        try
        {
            await _email.SendAsync(SalesInbox, $"Website chat lead: {c.Name}{(c.Business is null ? "" : $" ({c.Business})")}", Html(id, lang, page, c));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant: the lead email for conversation {Id} could not be sent.", id);
        }

        return $"Saved. The team will contact {c.Name} soon. Thank them by name and keep helping; do not ask for their contact again.";
    }

    private static string Html(string id, string lang, string page, GuestContact c)
    {
        static string Row(string label, string? value) =>
            string.IsNullOrWhiteSpace(value) ? "" : $"<tr><td><b>{label}</b></td><td>{WebUtility.HtmlEncode(value)}</td></tr>";

        return $"""
            <h2>New lead from the website assistant</h2>
            <table cellpadding="6" style="font-family:Arial,sans-serif;font-size:14px">
              {Row("Name", c.Name)}{Row("Phone", c.Phone)}{Row("Email", c.Email)}{Row("Business", c.Business)}{Row("Interested in", c.Interest)}
              {Row("Language", lang)}{Row("Page", page)}
            </table>
            <p style="font-family:Arial,sans-serif;font-size:14px">Read the whole conversation at <a href="https://web.maplekiosk.ca/chats?c={id}">web.maplekiosk.ca/chats</a> (admin sign-in).</p>
            """;
    }

    private static string? Trim(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : v[..max];
    }
}
