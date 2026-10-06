using System.Text;
using MapleKiosk.Web.Services;
using MapleKiosk.Web.Shop.Config;

namespace MapleKiosk.Web.Shop.Notifications;

/// <summary>
/// Signal (b, email half): emails a receipt to the buyer (and a copy to the
/// internal order inbox) via the site's existing <see cref="EmailService"/>. The
/// in-process C# event half is <c>AppOrderService.OrderPaid</c>.
/// </summary>
public sealed class EmailReceiptSender : IOrderPaidSink
{
    private readonly EmailService _email;
    private readonly AppStoreConfig _config;
    private readonly Catalog.CatalogStore _catalog;
    private readonly ILogger<EmailReceiptSender> _logger;

    public EmailReceiptSender(EmailService email, AppStoreConfig config, Catalog.CatalogStore catalog, ILogger<EmailReceiptSender> logger)
    {
        _email = email;
        _config = config;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task OnOrderPaidAsync(OrderPaidNotification n, CancellationToken ct = default)
    {
        var inbox = await _config.GetOrderInboxAsync().ConfigureAwait(false);
        var portal = await _config.GetStripePortalUrlAsync().ConfigureAwait(false);

        // The buyer reads it in their language, with the catalog's translated item names; the order itself
        // keeps the English names (as Stripe has them), which the team's copy shows.
        if (!string.IsNullOrWhiteSpace(n.CustomerEmail))
        {
            var t = Words(n.Culture);
            var names = await LocalNamesAsync(n, ct).ConfigureAwait(false);
            await _email.SendAsync(n.CustomerEmail!, string.Format(t("rcpt.subject"), n.OrderRef),
                BuildHtml(n, forTeam: false, portal, t, names)).ConfigureAwait(false);
        }

        // The team's copy also says who bought, so sales can call to schedule the setup.
        if (!string.IsNullOrWhiteSpace(inbox))
        {
            var en = Words("en");
            await _email.SendAsync(inbox!, $"[order] {string.Format(en("rcpt.subject"), n.OrderRef)}",
                BuildHtml(n, forTeam: true, portal, en, null)).ConfigureAwait(false);
        }

        _logger.LogInformation("Order receipt emailed for {OrderRef}.", n.OrderRef);
    }

    // The receipt's wording in a language (Translations rcpt.*), English when a key is missing.
    private static Func<string, string> Words(string culture) => key =>
        Translations.All.TryGetValue(culture, out var d) && d.TryGetValue(key, out var v) ? v
        : Translations.All["en"].TryGetValue(key, out var en) ? en : key;

    // SKU → the catalog's name in the buyer's language, for lines that carry the catalog's English name
    // (shop orders). Quote lines are already worded in the buyer's language, so they're left alone.
    private async Task<Dictionary<string, string>?> LocalNamesAsync(OrderPaidNotification n, CancellationToken ct)
    {
        if (n.Culture == "en" || n.Lines is not { Count: > 0 }) return null;
        try
        {
            var products = (await _catalog.GetAllAsync(ct).ConfigureAwait(false)).ToDictionary(p => p.Sku);
            return n.Lines.Where(l => products.TryGetValue(l.Sku, out var p) && p.Name == l.Name)
                          .ToDictionary(l => l.Sku, l => products[l.Sku].NameIn(n.Culture));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Receipt for {OrderRef}: catalog names unavailable; using the order's.", n.OrderRef);
            return null;
        }
    }

    private static string BuildHtml(OrderPaidNotification n, bool forTeam, string? portalUrl,
        Func<string, string> t, Dictionary<string, string>? names)
    {
        var sb = new StringBuilder();
        sb.Append("<div style='font-family:Arial,Helvetica,sans-serif;color:#111'>");
        sb.Append($"<h2>{Enc(t("rcpt.title"))} 🍁</h2>");
        sb.Append($"<p>{Enc(t("rcpt.body"))}</p>");
        sb.Append("<table style='border-collapse:collapse'>");
        sb.Append(Row(t("rcpt.ref"), n.OrderRef));
        sb.Append(Row(t("rcpt.amount"), $"{n.Total:0.##} {n.Currency}"));
        sb.Append(Row(t("rcpt.method"), n.Method));
        sb.Append(Row(t("rcpt.date"), n.PaidAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")));
        if (forTeam)
        {
            foreach (var (label, value) in new[] { ("Source", n.Source), ("Business", n.Company), ("Contact", n.CustomerName),
                                                   ("Phone", n.CustomerPhone), ("Email", n.CustomerEmail) })
                if (!string.IsNullOrWhiteSpace(value)) sb.Append(Row(label, value!));
        }
        sb.Append("</table>");

        // What was bought: one-time items, and the plans that now renew every month (or year).
        if (n.Lines is { Count: > 0 })
        {
            sb.Append("<table style='border-collapse:collapse;margin-top:16px'>");
            foreach (var l in n.Lines)
            {
                // App-store lines carry no cadence of their own (the order's applies); quote lines do.
                var cadence = l.Interval switch
                {
                    Catalog.BillingIntervals.Monthly => " " + t("rcpt.month"),
                    Catalog.BillingIntervals.Yearly => " " + t("rcpt.year"),
                    null => "",
                    _ => " " + t("rcpt.once")
                };
                var lineName = names?.GetValueOrDefault(l.Sku) ?? l.Name;
                var name = l.Quantity > 1 ? $"{lineName} × {l.Quantity}" : lineName;
                sb.Append(Row(name, $"{l.LineTotal:0.00} {n.Currency}{cadence}"));
            }
            sb.Append("</table>");
            if (n.Lines.Any(l => Catalog.BillingIntervals.IsRecurring(l.Interval ?? "")))
            {
                var yearly = n.Lines.Any(l => l.Interval == Catalog.BillingIntervals.Yearly);
                sb.Append($"<p>{Enc(t(yearly ? "rcpt.renew.year" : "rcpt.renew.month"))}</p>");
                if (!string.IsNullOrWhiteSpace(portalUrl))
                    sb.Append($"<p><a href='{Enc(portalUrl)}'>{Enc(t("rcpt.manage"))}</a> {Enc(t("rcpt.manage.d"))}</p>");
            }
        }
        sb.Append("<p style='color:#666;margin-top:24px'>MapleKiosk</p></div>");
        return sb.ToString();
    }

    private static string Enc(string v) => System.Net.WebUtility.HtmlEncode(v);

    private static string Row(string label, string value)
        => $"<tr><td style='padding:4px 16px 4px 0;color:#666'>{label}</td>" +
           $"<td style='padding:4px 0;font-weight:600'>{System.Net.WebUtility.HtmlEncode(value)}</td></tr>";
}
