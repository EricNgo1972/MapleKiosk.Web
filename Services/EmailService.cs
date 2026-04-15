using SendGrid;
using SendGrid.Helpers.Mail;

namespace MapleKiosk.Web.Services;

public class EmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly SendGridClient? _client;
    private readonly EmailAddress _from;

    public EmailService(ILogger<EmailService> logger, IConfiguration config)
    {
        _logger = logger;

        var apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY")
                     ?? config["SENDGRID_API_KEY"];

        _from = ParseFrom(
            Environment.GetEnvironmentVariable("SENDGRID_FROM") ?? config["SENDGRID_FROM"],
            Environment.GetEnvironmentVariable("SENDGRID_FROM_NAME") ?? config["SENDGRID_FROM_NAME"]);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("SENDGRID_API_KEY not set — emails will be skipped.");
            return;
        }

        _client = new SendGridClient(apiKey);
    }

    public bool Enabled => _client is not null;

    // Ultimate fallback:  MapleKiosk <no-reply@maplekiosk.ca>
    private static EmailAddress ParseFrom(string? from, string? name)
    {
        const string FallbackName = "MapleKiosk";
        const string FallbackAddress = "no-reply@maplekiosk.ca";

        if (!string.IsNullOrWhiteSpace(from))
        {
            // Accept "Name <email@host>" format in SENDGRID_FROM.
            var open  = from.IndexOf('<');
            var close = from.IndexOf('>');
            if (open >= 0 && close > open)
            {
                var parsedName    = from[..open].Trim().Trim('"');
                var parsedAddress = from.Substring(open + 1, close - open - 1).Trim();
                if (!string.IsNullOrWhiteSpace(parsedAddress))
                    return new EmailAddress(
                        parsedAddress,
                        !string.IsNullOrWhiteSpace(name) ? name
                            : !string.IsNullOrWhiteSpace(parsedName) ? parsedName
                            : FallbackName);
            }
            return new EmailAddress(from.Trim(), string.IsNullOrWhiteSpace(name) ? FallbackName : name);
        }

        return new EmailAddress(FallbackAddress, string.IsNullOrWhiteSpace(name) ? FallbackName : name);
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        if (_client is null) return;

        try
        {
            var msg = MailHelper.CreateSingleEmail(
                _from,
                new EmailAddress(to),
                subject,
                plainTextContent: null,
                htmlContent: htmlBody);

            var response = await _client.SendEmailAsync(msg);

            if ((int)response.StatusCode >= 400)
            {
                var body = await response.Body.ReadAsStringAsync();
                _logger.LogError("SendGrid send failed to {To}: {Status} — {Body}",
                    to, response.StatusCode, body);
            }
            else
            {
                _logger.LogInformation("Email sent to {To} — {Subject} ({Status})",
                    to, subject, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}", to);
        }
    }
}
