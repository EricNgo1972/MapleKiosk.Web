using System.Collections.Concurrent;
using System.ComponentModel;
using MapleKiosk.Web.Services;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// <c>book_demo</c>: the assistant (chat, SMS, voice and the phone line) books a demo the same way the
/// "Book a demo" form does — a lead on /admin/leads, the sales email, and the client's confirmation email when
/// they gave an address. It does not pick a time slot: the team calls back to set one, and the guest's
/// preferred time goes on the lead. Like the other tools it acts only on what the guest actually said, and
/// tells the model what really happened so the reply can't claim more.
/// </summary>
public sealed class DemoRequestTool
{
    public const string Name = "book_demo";

    /// <summary>The form's business types; anything else is "Other".</summary>
    private static readonly string[] BusinessTypes = ["Nail Salon", "Restaurant", "Retail", "Garage", "Startup", "Other"];

    private readonly TrialSignupService _signups;
    private readonly ChatLog _chats;
    private readonly ILogger<DemoRequestTool> _logger;

    /// <summary>Conversations that already booked: one demo per conversation, whatever the model does.</summary>
    private readonly ConcurrentDictionary<string, string> _booked = new();

    public DemoRequestTool(TrialSignupService signups, ChatLog chats, ILogger<DemoRequestTool> logger)
    {
        _signups = signups;
        _chats = chats;
        _logger = logger;
    }

    /// <summary>The tool for one conversation: it can only ever book for that conversation.</summary>
    /// <param name="channel">Where the guest is: "website chat", "SMS", "phone call", "voice (browser)".</param>
    public AIFunction For(string conversationId, string lang, string channel, string? callerPhone = null) =>
        AIFunctionFactory.Create(
            async ([Description("The guest's name, exactly as they said it.")] string name,
                   [Description("Their business's name.")] string business,
                   [Description("One of: Nail Salon, Restaurant, Retail, Garage, Startup, Other. A café or coffee shop is Restaurant; a spa is Nail Salon.")] string businessType,
                   [Description("Their phone number, if they gave one.")] string? phone,
                   [Description("Their email address, if they gave one.")] string? email,
                   [Description("Their city, if they said.")] string? city,
                   [Description("When they would like the demo, as a real date (e.g. \"Thursday, November 12, afternoon\") — work out \"tomorrow\" or \"next week\" from today's date — if they said.")] string? preferredTime,
                   [Description("What they want to see or anything else useful for the demo, in a few words.")] string? notes,
                   CancellationToken ct) =>
                await BookAsync(conversationId, lang, channel, new Request(
                    Trim(name, 120) ?? "", Trim(business, 120) ?? "", BusinessType(businessType),
                    Trim(phone, 32) ?? callerPhone, Trim(email, 160), Trim(city, 80),
                    Trim(preferredTime, 120), Trim(notes, 500)), ct),
            name: Name,
            description: "Book a MapleKiosk demo for the guest: records the request for the sales team, who call back to set the " +
                         "time. Needs their name, their business and a phone number or email. Call it only once they asked for a " +
                         "demo and gave those details in this conversation — never guess or invent a value — and only once.");

    internal sealed record Request(string Name, string Business, string BusinessType, string? Phone, string? Email,
        string? City, string? PreferredTime, string? Notes);

    internal async Task<string> BookAsync(string id, string lang, string channel, Request r, CancellationToken ct)
    {
        if (r.Name.Length == 0) return "Not booked: their name is missing. Ask for it.";
        if (r.Business.Length == 0) return "Not booked: the business's name is missing. Ask for it.";
        if (r.Phone is null && r.Email is null)
            return "Not booked: there is no phone number or email. Ask how the team should reach them.";
        if (r.Email is not null && !r.Email.Contains('@'))
            return "Not booked: that email address looks incomplete. Ask them to say it again.";
        // Claimed before anything is saved: a model that fires the same call twice at once books one demo.
        if (!_booked.TryAdd(id, "booking"))
            return "Already booked in this conversation. Do not call this again; tell them the team will call to set the time.";

        var notes = string.Join("\n", new[]
        {
            $"Booked by the AI assistant ({channel}).",
            r.PreferredTime is null ? null : $"Preferred time: {r.PreferredTime}",
            r.Notes is null ? null : $"Notes: {r.Notes}",
            $"Conversation: {SiteHost.Root.TrimEnd('/')}/chats/{id}",
        }.Where(l => l is not null));

        var signup = new TrialSignup
        {
            CompanyName = r.Business,
            BusinessType = r.BusinessType,
            ContactName = r.Name,
            Phone = r.Phone ?? "",
            Email = r.Email ?? "",
            City = r.City ?? "",
            Culture = lang is "fr" or "vi" or "ru" ? lang : lang == "qc" ? "fr" : "en",
        };

        TrialSignupService.Created created;
        try
        {
            created = await _signups.CreateAsync(signup, notes, clientText: channel != "SMS");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant: book_demo failed for conversation {Id}.", id);
            created = new TrialSignupService.Created(null, ex.Message);
        }

        // On the conversation too, so /chats shows who it was.
        try
        {
            await _chats.SaveContactAsync(id, new GuestContact(r.Name, r.Phone, r.Email, r.Business, "Demo"), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant: the contact for conversation {Id} could not be saved.", id);
        }

        var outcome = Outcome(created.LeadId is not null, created.SalesEmailError is null, r.Name, r.Email is not null);
        if (created.LeadId is not null || created.SalesEmailError is null) _booked[id] = created.LeadId ?? "emailed";
        else _booked.TryRemove(id, out _); // nothing went through: they may try again
        _logger.LogInformation("Assistant: demo request from conversation {Id} — lead {Lead}, sales email {Email}.",
            id, created.LeadId ?? "not saved", created.SalesEmailError ?? "sent");
        return outcome;
    }

    /// <summary>What the model is told: only what actually happened.</summary>
    public static string Outcome(bool saved, bool emailed, string name, bool hasEmail) => (saved, emailed) switch
    {
        (false, false) => "FAILED: the demo request was neither saved nor sent. Apologise, and give them sales@maplekiosk.ca " +
                          "(or the \"Book a demo\" form on the website). Do not call this again.",
        _ => $"Booked. The demo request is with the sales team, who will contact {name} to set the time. " +
             (hasEmail ? "A confirmation email is on its way. " : "") +
             "Say exactly that — never promise a specific date or time — and do not call this again.",
    };

    private static string BusinessType(string? value)
    {
        var v = value?.Trim() ?? "";
        return BusinessTypes.FirstOrDefault(t => t.Equals(v, StringComparison.OrdinalIgnoreCase)) ?? "Other";
    }

    private static string? Trim(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : v[..max];
    }
}
