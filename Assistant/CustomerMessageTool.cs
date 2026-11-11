using System.ComponentModel;
using System.Net;
using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// The assistant's take_message tool: a customer's message for the person in charge. It is kept on the
/// conversation (/chats) first, so a failed email can't lose it, then emailed to
/// <see cref="AssistantSettings.MessageInboxAsync"/>. What it returns says what really happened, so the reply
/// cannot claim more than that. Acts only on what the customer actually wrote.
/// </summary>
public sealed class CustomerMessageTool
{
    private static readonly string[] Topics = ["sales", "setup", "billing", "support"];

    private readonly ChatLog _chats;
    private readonly EmailService _email;
    private readonly ILogger<CustomerMessageTool> _logger;

    public CustomerMessageTool(ChatLog chats, EmailService email, ILogger<CustomerMessageTool> logger)
    {
        _chats = chats;
        _email = email;
        _logger = logger;
    }

    /// <summary>The tool for one conversation: it can only ever save to that conversation.</summary>
    public AIFunction For(string conversationId, string lang, string page) =>
        AIFunctionFactory.Create(
            async ([Description("The message to pass on, in the customer's own words and language — with any relative day (\"tomorrow\", \"Monday\") written as the real date.")] string message,
                   [Description("The customer's name, exactly as they wrote it.")] string name,
                   [Description("Their phone number, if they gave one.")] string? phone,
                   [Description("Their email address, if they gave one.")] string? email,
                   [Description("Their business, if they said.")] string? business,
                   [Description("One of: sales, setup, billing, support.")] string topic,
                   [Description("True only if their business can't operate right now (e.g. they can't take payments or orders).")] bool urgent,
                   CancellationToken ct) =>
                await SaveAsync(conversationId, lang, page,
                    new GuestContact(Trim(name, 80) ?? "", Trim(phone, 40), Trim(email, 120), Trim(business, 120), null),
                    new GuestMessage(Topic(topic), urgent, Trim(message, 2000) ?? "", DateTimeOffset.UtcNow), ct),
            name: "take_message",
            description: "Pass a customer's message to the person in charge at MapleKiosk, along with who they are and how to " +
                         "reach them. Call it only with what the customer actually wrote in this conversation — never guess " +
                         "or invent a value — and only once for a message.");

    private async Task<string> SaveAsync(string id, string lang, string page, GuestContact c, GuestMessage m, CancellationToken ct)
    {
        if (m.Text.Length == 0) return "Not passed on: there is no message yet. Ask what they would like to say.";
        if (c.Name.Length == 0) return "Not passed on: their name is missing. Ask for it.";
        if (c.Phone is null && c.Email is null)
            return "Not passed on: there is no phone number or email yet. Ask how they would like to be reached.";

        var saved = await _chats.SaveMessageAsync(id, c, m, ct);

        string? emailError;
        try
        {
            var inbox = await AssistantSettings.MessageInboxAsync();
            var subject = $"{ChatChannel.SubjectTag(page)}{(m.Urgent ? "URGENT — " : "")}Customer message ({m.Topic}): {c.Name}{(c.Business is null ? "" : $" ({c.Business})")}";
            emailError = await _email.SendAsync(inbox, subject, Html(id, lang, page, c, m));
        }
        catch (Exception ex)
        {
            emailError = ex.Message;
        }

        if (emailError is not null)
            _logger.LogError("Assistant: the message email for conversation {Id} failed (kept on the record: {Saved}): {Error}", id, saved, emailError);
        else
            _logger.LogInformation("Assistant: a customer left a message (conversation {Id}).", id);

        // The staff phone gets a text (the email above went to the person in charge).
        if (saved || emailError is null)
            StaffAlert.Queue($"{(m.Urgent ? "URGENT " : "")}message from {c.Name}{(c.Business is null ? "" : $" ({c.Business})")}"
                             + $"{(c.Phone is null ? "" : $", {c.Phone}")}{(c.Email is null ? "" : $", {c.Email}")}: {m.Text}",
                $"/chats/{id}", null, "", null, _logger);

        // A confirmation text, unless they are texting already (then the assistant's reply says it).
        if ((saved || emailError is null) && page != "sms" && c.Phone is not null)
            AutoTexts.Queue(c.Phone, AutoTexts.MessageTaken(c.Name, lang), _logger);

        return Outcome(saved, emailError is null, c.Name);
    }

    /// <summary>What the model is told. The customer is only ever told what actually happened.</summary>
    public static string Outcome(bool saved, bool emailed, string name) => (saved, emailed) switch
    {
        (false, false) => "FAILED: the message was neither saved nor sent. Tell the customer it could not be passed on, and " +
                          "give them sales@maplekiosk.ca, or support@maplekiosk.ca for a problem with their system. Do not call this again.",
        (true, false) => $"Saved on the conversation, where the team will see it, but the notification email failed. Tell {name} their " +
                         "message is recorded for the team. Do not promise when they'll hear back, and do not call this again.",
        _ => $"Passed on. Tell {name} their message has been passed to the person in charge. Do not promise when they'll hear " +
             "back, do not call this again for this message, and do not ask for their contact again.",
    };

    private static string Topic(string? topic)
    {
        var t = topic?.Trim().ToLowerInvariant();
        return Topics.Contains(t) ? t! : "general";
    }

    private static string Html(string id, string lang, string page, GuestContact c, GuestMessage m)
    {
        static string Row(string label, string? value) =>
            string.IsNullOrWhiteSpace(value) ? "" : $"<tr><td><b>{label}</b></td><td>{WebUtility.HtmlEncode(value)}</td></tr>";

        return $"""
            <h2>{(m.Urgent ? "URGENT: " : "")}Message from a customer</h2>
            <p style="font-family:Arial,sans-serif;font-size:15px;white-space:pre-wrap">{WebUtility.HtmlEncode(m.Text)}</p>
            <table cellpadding="6" style="font-family:Arial,sans-serif;font-size:14px">
              {Row("Name", c.Name)}{Row("Phone", c.Phone)}{Row("Email", c.Email)}{Row("Business", c.Business)}{Row("Topic", m.Topic)}
              {Row("Language", lang)}{Row("Page", page)}
            </table>
            <p style="font-family:Arial,sans-serif;font-size:14px">Read the whole conversation at <a href="https://www.maplekiosk.ca/chats/{id}">www.maplekiosk.ca/chats</a> (admin sign-in).</p>
            """;
    }

    private static string? Trim(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : v[..max];
    }
}
