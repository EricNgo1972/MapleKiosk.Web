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
    private readonly ILogger<EmailReceiptSender> _logger;

    public EmailReceiptSender(EmailService email, AppStoreConfig config, ILogger<EmailReceiptSender> logger)
    {
        _email = email;
        _config = config;
        _logger = logger;
    }

    public async Task OnOrderPaidAsync(OrderPaidNotification n, CancellationToken ct = default)
    {
        var inbox = await _config.GetOrderInboxAsync().ConfigureAwait(false);
        var subject = $"Your MapleKiosk order {n.OrderRef}";

        var portal = await _config.GetStripePortalUrlAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(n.CustomerEmail))
            await _email.SendAsync(n.CustomerEmail!, subject, BuildHtml(n, forTeam: false, portal)).ConfigureAwait(false);

        // The team's copy also says who bought, so sales can call to schedule the setup.
        if (!string.IsNullOrWhiteSpace(inbox))
            await _email.SendAsync(inbox!, $"[order] {subject}", BuildHtml(n, forTeam: true, portal)).ConfigureAwait(false);

        _logger.LogInformation("Order receipt emailed for {OrderRef}.", n.OrderRef);
    }

    private static string BuildHtml(OrderPaidNotification n, bool forTeam, string? portalUrl)
    {
        var sb = new StringBuilder();
        sb.Append("<div style='font-family:Arial,Helvetica,sans-serif;color:#111'>");
        sb.Append("<h2>Thank you for your purchase 🍁</h2>");
        sb.Append("<p>Your payment has been received and your order is confirmed.</p>");
        sb.Append("<table style='border-collapse:collapse'>");
        sb.Append(Row("Order reference", n.OrderRef));
        sb.Append(Row("Amount", $"{n.Total:0.##} {n.Currency}"));
        sb.Append(Row("Payment method", n.Method));
        sb.Append(Row("Date", n.PaidAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")));
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
                    Catalog.BillingIntervals.Monthly => " / month",
                    Catalog.BillingIntervals.Yearly => " / year",
                    null => "",
                    _ => " (one-time)"
                };
                var name = l.Quantity > 1 ? $"{l.Name} × {l.Quantity}" : l.Name;
                sb.Append(Row(name, $"{l.LineTotal:0.00} {n.Currency}{cadence}"));
            }
            sb.Append("</table>");
            if (n.Lines.Any(l => Catalog.BillingIntervals.IsRecurring(l.Interval ?? "")))
            {
                var period = n.Lines.Any(l => l.Interval == Catalog.BillingIntervals.Yearly) ? "year" : "month";
                sb.Append($"<p>Your plans renew automatically each {period} on the same card. No long-term contract.</p>");
                if (!string.IsNullOrWhiteSpace(portalUrl))
                    sb.Append($"<p><a href='{System.Net.WebUtility.HtmlEncode(portalUrl)}'>Manage your subscription</a> " +
                              "— change card, download invoices or cancel, any time.</p>");
            }
        }
        sb.Append("<p style='color:#666;margin-top:24px'>MapleKiosk</p></div>");
        return sb.ToString();
    }

    private static string Row(string label, string value)
        => $"<tr><td style='padding:4px 16px 4px 0;color:#666'>{label}</td>" +
           $"<td style='padding:4px 0;font-weight:600'>{System.Net.WebUtility.HtmlEncode(value)}</td></tr>";
}
