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

        if (!string.IsNullOrWhiteSpace(n.CustomerEmail))
            await _email.SendAsync(n.CustomerEmail!, subject, BuildHtml(n, forTeam: false)).ConfigureAwait(false);

        // The team's copy also says who bought, so sales can call to schedule the setup.
        if (!string.IsNullOrWhiteSpace(inbox))
            await _email.SendAsync(inbox!, $"[order] {subject}", BuildHtml(n, forTeam: true)).ConfigureAwait(false);

        _logger.LogInformation("Order receipt emailed for {OrderRef}.", n.OrderRef);
    }

    private static string BuildHtml(OrderPaidNotification n, bool forTeam)
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

        // What was bought: one-time items, then the monthly plans that now renew every month.
        if (n.Lines is { Count: > 0 })
        {
            sb.Append("<table style='border-collapse:collapse;margin-top:16px'>");
            foreach (var l in n.Lines)
            {
                // App-store lines carry no cadence of their own (the order's applies); quote lines do.
                var cadence = l.Interval switch { null => "", Catalog.BillingIntervals.OneTime => " (one-time)", _ => " / month" };
                sb.Append(Row(l.Name, $"{l.LineTotal:0.00} {n.Currency}{cadence}"));
            }
            sb.Append("</table>");
            if (n.Lines.Any(l => Catalog.BillingIntervals.IsRecurring(l.Interval ?? "")))
                sb.Append("<p>Your monthly plans renew automatically each month on the same card. No long-term contract.</p>");
        }
        sb.Append("<p style='color:#666;margin-top:24px'>MapleKiosk</p></div>");
        return sb.ToString();
    }

    private static string Row(string label, string value)
        => $"<tr><td style='padding:4px 16px 4px 0;color:#666'>{label}</td>" +
           $"<td style='padding:4px 0;font-weight:600'>{System.Net.WebUtility.HtmlEncode(value)}</td></tr>";
}
