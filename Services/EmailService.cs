using SendGrid;
using SendGrid.Helpers.Mail;

namespace MapleKiosk.Web.Services;

public class EmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly IConfiguration _config;
    private readonly Task<(SendGridClient? Client, EmailAddress From)> _setup;

    public EmailService(ILogger<EmailService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _setup = ResolveAsync();
    }

    // Env var → keyvalue table → appsettings. The site's own SendGrid/maplekiosk
    // row wins; SendGrid/AppleWallet and Email/* are the rows the
    // monorepo's SendGridEmailService and email intake read.
    private async Task<(SendGridClient?, EmailAddress)> ResolveAsync()
    {
        var apiKey = await KeyValueTable.ResolveAsync("SENDGRID_API_KEY", "SendGrid", "maplekiosk");
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = await Resolve("SENDGRID_API_KEY", "SendGrid", "AppleWallet");
        var from = ParseFrom(
            await Resolve("SENDGRID_FROM", "Email", "FromAddress"),
            await Resolve("SENDGRID_FROM_NAME", "Email", "FromName"));

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("SendGrid API key not found (SENDGRID_API_KEY or keyvalue SendGrid/maplekiosk, SendGrid/AppleWallet) — emails will be skipped.");
            return (null, from);
        }

        return (new SendGridClient(apiKey), from);
    }

    private async Task<string?> Resolve(string envVar, string partition, string row)
    {
        var value = await KeyValueTable.ResolveAsync(envVar, partition, row);
        return string.IsNullOrWhiteSpace(value) ? _config[envVar] : value;
    }

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
        var (client, from) = await _setup;
        if (client is null) return;

        try
        {
            var msg = MailHelper.CreateSingleEmail(
                from,
                new EmailAddress(to),
                subject,
                plainTextContent: null,
                htmlContent: htmlBody);

            var response = await client.SendEmailAsync(msg);

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
