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
        var html = BuildHtml(n);
        var subject = $"Your MapleKiosk order {n.OrderRef}";

        if (!string.IsNullOrWhiteSpace(n.CustomerEmail))
            await _email.SendAsync(n.CustomerEmail!, subject, html).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(inbox))
            await _email.SendAsync(inbox!, $"[order] {subject}", html).ConfigureAwait(false);

        _logger.LogInformation("Order receipt emailed for {OrderRef}.", n.OrderRef);
    }

    private static string BuildHtml(OrderPaidNotification n)
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
        sb.Append("</table><p style='color:#666;margin-top:24px'>MapleKiosk</p></div>");
        return sb.ToString();
    }

    private static string Row(string label, string value)
        => $"<tr><td style='padding:4px 16px 4px 0;color:#666'>{label}</td>" +
           $"<td style='padding:4px 0;font-weight:600'>{System.Net.WebUtility.HtmlEncode(value)}</td></tr>";
}
