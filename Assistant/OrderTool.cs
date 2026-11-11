using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using MapleKiosk.Web.Assistant.Voice;
using MapleKiosk.Web.Services;
using MapleKiosk.Web.Shop.Catalog;
using MapleKiosk.Web.Shop.Checkout;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// <c>place_order</c>: the assistant takes an order for a trade's packages (the same items and prices as the
/// /{trade}/pricing quote), two ways:
/// <list type="bullet">
///   <item><b>Pay now</b> — a real Pending order and a Stripe Checkout link, through the pricing page's own
///   <see cref="QuoteCheckoutService"/>; Stripe's webhook marks it paid and the usual receipts follow. The link
///   goes where the guest is: in the chat reply, in the SMS reply, or texted to a phone caller; and emailed.
///   The assistant never sees a card.</item>
///   <item><b>Order request</b> — no payment: a lead on /admin/leads with the items, and the sales email, for
///   the team to confirm and invoice.</item>
/// </list>
/// Codes and prices come from the catalog, never from the model: an unknown code is refused with the list of
/// valid ones.
/// </summary>
public sealed class OrderTool
{
    public const string Name = "place_order";

    private readonly QuoteCheckoutService _checkout;
    private readonly CatalogStore _catalog;
    private readonly TrialSignupService _signups;
    private readonly ChatLog _chats;
    private readonly EmailService _email;
    private readonly ILogger<OrderTool> _logger;

    /// <summary>One order per conversation: a model that calls twice must not make two payment links.</summary>
    private readonly ConcurrentDictionary<string, string> _ordered = new();

    public OrderTool(QuoteCheckoutService checkout, CatalogStore catalog, TrialSignupService signups, ChatLog chats,
        EmailService email, ILogger<OrderTool> logger)
    {
        _checkout = checkout;
        _catalog = catalog;
        _signups = signups;
        _chats = chats;
        _email = email;
        _logger = logger;
    }

    /// <summary>Where the guest is, which decides how the payment link reaches them.</summary>
    public enum Channel { Chat, Sms, PhoneCall, BrowserVoice }

    /// <param name="callerPhone">The SMS sender or phone caller's number.</param>
    /// <param name="lineNumber">MapleKiosk's Twilio number the caller rang — to text them the link from.</param>
    public AIFunction For(string conversationId, string lang, Channel channel, string? callerPhone = null, string? lineNumber = null) =>
        AIFunctionFactory.Create(
            async ([Description("The trade: coffee, nails, restaurants or garage.")] string product,
                   [Description("The order codes of the items they chose, from the pricing knowledge (\"order code ...\"). At most one per \"pick one\" group.")] string[] items,
                   [Description("The customer's name, exactly as they said it.")] string name,
                   [Description("Their business's name.")] string business,
                   [Description("Their email address, if they gave one. Required to pay now.")] string? email,
                   [Description("Their phone number, if they gave one.")] string? phone,
                   [Description("True if they want to pay now by card (a secure payment link); false to send the order to the sales team, who confirm it and invoice.")] bool payNow,
                   [Description("Anything else about the order (start date, number of locations...), in a few words.")] string? notes,
                   CancellationToken ct) =>
                await PlaceAsync(conversationId, lang, channel, callerPhone, lineNumber, new Request(
                    (product ?? "").Trim().ToLowerInvariant(), items ?? [], Trim(name, 80) ?? "", Trim(business, 80) ?? "",
                    Trim(email, 120), Trim(phone, 40) ?? callerPhone, payNow, Trim(notes, 500)), ct),
            name: Name,
            description: "Take the customer's order for MapleKiosk packages. Before calling: confirm the trade, the items " +
                         "(with their prices) and the total out loud or in writing, and get their name, business, and an " +
                         "email (to pay now) or a phone number or email (for an order request). Use only what the customer " +
                         "said — never guess — and call once per order.");

    internal sealed record Request(string Product, string[] Items, string Name, string Business, string? Email, string? Phone,
        bool PayNow, string? Notes);

    internal async Task<string> PlaceAsync(string id, string lang, Channel channel, string? callerPhone, string? lineNumber,
        Request r, CancellationToken ct)
    {
        var trade = SiteProducts.All.FirstOrDefault(p => p.Group == ProductGroup.Trade && p.Slug == r.Product);
        if (trade is null) return "Not ordered: the trade must be one of coffee, nails, restaurants or garage. Ask which.";
        if (r.Items.Length == 0) return "Not ordered: no items. Ask which package(s) they want.";
        if (r.Name.Length == 0) return "Not ordered: their name is missing. Ask for it.";
        if (r.Business.Length == 0) return "Not ordered: the business's name is missing. Ask for it.";
        if (r.Email is not null && (!r.Email.Contains('@') || r.Email.Contains(' ')))
            return "Not ordered: that email address looks incomplete. Ask them to say it again.";
        if (r.PayNow && r.Email is null)
            return "Not ordered: paying now needs their email (for the receipt). Ask for it, or offer to send the order to the sales team instead.";
        if (r.Email is null && r.Phone is null)
            return "Not ordered: there is no phone number or email. Ask how the team should reach them.";
        // Claimed before anything is done: a model that fires the same call twice at once places one order.
        if (!_ordered.TryAdd(id, Placing))
            return $"Already ordered in this conversation ({_ordered.GetValueOrDefault(id)}). Do not call this again.";
        try
        {
            var result = await PlaceClaimedAsync(id, lang, channel, callerPhone, lineNumber, r, trade, ct);
            if (_ordered.TryGetValue(id, out var now) && now == Placing) _ordered.TryRemove(id, out _); // nothing placed
            return result;
        }
        catch
        {
            _ordered.TryRemove(id, out _);
            throw;
        }
    }

    private const string Placing = "being placed";

    private async Task<string> PlaceClaimedAsync(string id, string lang, Channel channel, string? callerPhone, string? lineNumber,
        Request r, SiteProduct trade, CancellationToken ct)
    {
        // Codes resolved against the catalog: what is unknown is said, with what exists.
        var quote = await _catalog.GetGroupsAsync(ct);
        var all = quote.SelectMany(g => g.Items).ToList();
        var codes = r.Items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var picked = new List<AppProduct>();
        foreach (var code in codes)
        {
            var item = all.FirstOrDefault(i => i.Sku.Equals(code, StringComparison.OrdinalIgnoreCase))
                       ?? all.FirstOrDefault(i => i.Name.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return $"Not ordered: \"{code}\" is not an order code. Valid codes: " +
                       string.Join(", ", all.Select(i => $"{i.Sku} ({i.Name})")) + ".";
            picked.Add(item);
        }
        var summary = Summary(picked);

        return r.PayNow
            ? await PayNowAsync(id, lang, channel, callerPhone, lineNumber, r, trade, picked, summary, ct)
            : await RequestAsync(id, lang, channel, r, trade, summary, ct);
    }

    private async Task<string> PayNowAsync(string id, string lang, Channel channel, string? callerPhone, string? lineNumber,
        Request r, SiteProduct trade, List<AppProduct> picked, string summary, CancellationToken ct)
    {
        var culture = lang == "qc" ? "fr" : lang;
        CheckoutResult result;
        try
        {
            result = await _checkout.CreateAsync(new QuoteCheckoutRequest
            {
                Product = trade.Slug,
                Items = picked.Select(p => p.Sku).ToList(),
                Company = r.Business,
                Name = r.Name,
                Phone = r.Phone,
                Email = r.Email,
                Culture = culture,
            }, SiteHost.Root, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant: place_order checkout failed (conversation {Id}).", id);
            return Failed;
        }
        if (!result.Success || result.StripeUrl is null)
            return $"Not ordered: {result.Error ?? "the payment page could not be created"}. Explain it simply; if it is about the " +
                   "items, ask them to choose again, otherwise offer to send the order to the sales team (payNow false).";

        _ordered[id] = $"order {result.OrderRef}, payment link sent";
        var total = $"{result.Total:0.00} {result.Currency}";

        // The link, to wherever the guest can open it.
        var emailed = await SendLinkEmailAsync(r, trade, summary, total, result.StripeUrl, ct);
        var texted = false;
        if (channel == Channel.PhoneCall && callerPhone is not null && lineNumber is not null)
            texted = await WebsiteSmsEndpoints.SendAsync(lineNumber, callerPhone,
                $"MapleKiosk — {trade.Name}: {summary}. Total today: {total}. Pay securely here: {result.StripeUrl}", _logger, ct, Services.SmsKind.Link);

        await Record(id, r, $"Order {result.OrderRef} ({trade.Name}): {summary}. First payment {total}. Payment link sent, awaiting payment.", ct);
        await NotifySalesAsync($"[{channel switch { Channel.PhoneCall => "Phone call", Channel.Sms => "SMS", Channel.BrowserVoice => "Voice", _ => "Chat" }}] Assistant order {result.OrderRef} — awaiting payment: {r.Business}",
            $"Order {result.OrderRef} by {r.Name} ({r.Business}, {r.Email}, {r.Phone}): {trade.Name} — {summary}. " +
            $"First payment {total}. Payment link sent by the assistant; Stripe confirms when paid.", id, ct);
        _logger.LogInformation("Assistant: order {Ref} from conversation {Id}, payment link sent.", result.OrderRef, id);

        var where = channel switch
        {
            Channel.Chat => $"Give them this payment link as a Markdown link: {result.StripeUrl}",
            Channel.Sms => $"Put this payment link in your reply, as is: {result.StripeUrl}",
            Channel.PhoneCall when texted => "The payment link was just texted to the number they are calling from" +
                                             (emailed ? " and emailed to them" : "") + ". Never read the link out.",
            _ when emailed => "The payment link was emailed to them. Never read the link out.",
            _ => $"The link could not be sent to them. Ask them to write to sales@maplekiosk.ca quoting order {result.OrderRef}.",
        };
        return $"Order {result.OrderRef} created: {trade.Name} — {summary}. First payment {total} (before tax). {where} " +
               "Tell them: the order is complete once they pay on that secure page (the link works for 24 hours), and they " +
               "get a receipt by email. Never take card details yourself. Do not call this again.";
    }

    private async Task<string> RequestAsync(string id, string lang, Channel channel, Request r, SiteProduct trade, string summary,
        CancellationToken ct)
    {
        var notes = string.Join("\n", new[]
        {
            $"ORDER REQUEST from the AI assistant ({channel}) — no payment yet: confirm and invoice.",
            $"{trade.Name}: {summary}",
            r.Notes is null ? null : $"Notes: {r.Notes}",
            $"Conversation: {SiteHost.Root.TrimEnd('/')}/chats/{id}",
        }.Where(l => l is not null));

        TrialSignupService.Created created;
        try
        {
            created = await _signups.CreateAsync(new TrialSignup
            {
                CompanyName = r.Business,
                BusinessType = trade.Slug switch { "nails" => "Nail Salon", "garage" => "Garage", _ => "Restaurant" },
                ContactName = r.Name,
                Phone = r.Phone ?? "",
                Email = r.Email ?? "",
                Product = trade.Name,
                Culture = lang == "qc" ? "fr" : lang is "fr" or "vi" or "ru" ? lang : "en",
            }, notes, kind: "order request", clientEmail: false, clientText: channel != Channel.Sms);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant: place_order request failed (conversation {Id}).", id);
            return Failed;
        }
        if (created.LeadId is null && created.SalesEmailError is not null) return Failed;

        _ordered[id] = "order request sent to sales";
        await Record(id, r, $"Order request ({trade.Name}): {summary}. Sent to sales to confirm and invoice.", ct);
        _logger.LogInformation("Assistant: order request from conversation {Id} (lead {Lead}).", id, created.LeadId ?? "email only");
        return $"Order request sent: {trade.Name} — {summary}. The sales team will contact {r.Name} to confirm it and send the " +
               "invoice. Say exactly that — never promise a date — and do not call this again.";
    }

    private const string Failed =
        "FAILED: the order could not be placed. Apologise, and give them sales@maplekiosk.ca. Do not call this again.";

    internal static string Summary(IEnumerable<AppProduct> items) =>
        string.Join(", ", items.Select(i => $"{i.Name} {i.Price:0.##} CAD{i.BillingInterval switch
        {
            BillingIntervals.Monthly => "/month",
            BillingIntervals.Yearly => "/year",
            _ => " one-time",
        }}"));

    private async Task Record(string id, Request r, string text, CancellationToken ct)
    {
        try
        {
            await _chats.SaveMessageAsync(id, new GuestContact(r.Name, r.Phone, r.Email, r.Business, "Order"),
                new GuestMessage("sales", false, text, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant: the order note for conversation {Id} could not be saved.", id);
        }
    }

    private async Task<bool> SendLinkEmailAsync(Request r, SiteProduct trade, string summary, string total, string url, CancellationToken ct)
    {
        if (r.Email is null) return false;
        var link = WebUtility.HtmlEncode(url);
        var html = $"""
            <div style="font-family:Arial,sans-serif;font-size:15px;line-height:1.55;color:#222">
              <h2 style="color:#c0392b">MapleKiosk — {WebUtility.HtmlEncode(trade.Name)}</h2>
              <p>{WebUtility.HtmlEncode(r.Name)}, merci! Thank you for your order:</p>
              <p>{WebUtility.HtmlEncode(summary)}<br/><b>Total today / Total aujourd'hui: {WebUtility.HtmlEncode(total)}</b> (before tax / avant taxes)</p>
              <p><a href="{link}" style="display:inline-block;background:#c0392b;color:#fff;text-decoration:none;padding:12px 22px;border-radius:6px;font-weight:600">Payer / Pay securely</a></p>
              <p style="color:#666;font-size:13px">The link works for 24 hours. Le lien est valide 24 heures. <a href="{link}">{link}</a></p>
            </div>
            """;
        try { return await _email.SendAsync(r.Email, $"MapleKiosk — {trade.Name}: payment link / lien de paiement", html) is null; }
        catch (Exception ex) { _logger.LogWarning(ex, "Assistant: the payment-link email failed."); return false; }
    }

    private async Task NotifySalesAsync(string subject, string text, string id, CancellationToken ct)
    {
        StaffAlert.Queue(text, $"/chats/{id}", null, "", null, _logger);
        try
        {
            await _email.SendAsync(await AssistantSettings.MessageInboxAsync(), subject,
                $"<p style=\"font-family:Arial,sans-serif\">{WebUtility.HtmlEncode(text)}</p>" +
                $"<p style=\"font-family:Arial,sans-serif\"><a href=\"{SiteHost.Root.TrimEnd('/')}/chats/{id}\">The conversation</a></p>");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Assistant: the order email to sales failed."); }
    }

    private static string? Trim(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : v[..max];
    }
}
