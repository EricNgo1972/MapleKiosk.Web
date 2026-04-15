using System.ComponentModel.DataAnnotations;
using System.Net;
using Azure;
using Azure.Data.Tables;

namespace MapleKiosk.Web.Services;

public class TrialSignup
{
    [Required, StringLength(120)]
    public string CompanyName { get; set; } = "";

    [Required, StringLength(200)]
    public string Address { get; set; } = "";

    [Required, StringLength(80)]
    public string City { get; set; } = "";

    [Required, StringLength(12)]
    public string PostalCode { get; set; } = "";

    [Required]
    public string BusinessType { get; set; } = "";

    [Required, StringLength(120)]
    public string ContactName { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    [Required, Phone, StringLength(32)]
    public string Phone { get; set; } = "";

    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal class ClientRequestEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "signup";
    public string RowKey { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string CompanyName { get; set; } = "";
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string BusinessType { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
}

public class TrialSignupService
{
    private const string TableName = "clientrequest";
    private const string SalesInbox = "sales@maplekiosk.ca";
    private const string DemoUrl = "https://demo.maplekiosk.ca";

    private readonly List<TrialSignup> _signups = new();
    private readonly ILogger<TrialSignupService> _logger;
    private readonly EmailService _email;
    private readonly TableClient? _table;

    public TrialSignupService(ILogger<TrialSignupService> logger, IConfiguration config, EmailService email)
    {
        _logger = logger;
        _email = email;
        var connection = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING")
                         ?? config["STORAGE_CONNECTION_STRING"];

        if (string.IsNullOrWhiteSpace(connection))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — trial signups will only be kept in memory.");
            return;
        }

        try
        {
            _table = new TableClient(connection, TableName);
            _table.CreateIfNotExists();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Azure Table '{Table}'.", TableName);
            _table = null;
        }
    }

    public IReadOnlyList<TrialSignup> All => _signups;

    public async Task CreateAsync(TrialSignup signup)
    {
        signup.SubmittedAt = DateTimeOffset.UtcNow;
        _signups.Add(signup);

        _logger.LogInformation("Trial signup: {Company} | {Email} | {Type}",
            signup.CompanyName, signup.Email, signup.BusinessType);

        if (_table is null) return;

        var entity = new ClientRequestEntity
        {
            PartitionKey = string.IsNullOrWhiteSpace(signup.BusinessType) ? "signup" : signup.BusinessType,
            RowKey = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
            CompanyName = signup.CompanyName,
            Address = signup.Address,
            City = signup.City,
            PostalCode = signup.PostalCode,
            BusinessType = signup.BusinessType,
            ContactName = signup.ContactName,
            Email = signup.Email,
            Phone = signup.Phone,
            SubmittedAt = signup.SubmittedAt,
        };

        try
        {
            await _table.AddEntityAsync(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write signup for {Company} to Azure Table.", signup.CompanyName);
        }

        await SendNotificationEmailsAsync(signup);
    }

    private async Task SendNotificationEmailsAsync(TrialSignup s)
    {
        var salesHtml = $"""
            <h2>New MapleKiosk trial signup</h2>
            <table cellpadding="6" style="font-family:Arial,sans-serif;font-size:14px">
              <tr><td><b>Company</b></td><td>{WebUtility.HtmlEncode(s.CompanyName)}</td></tr>
              <tr><td><b>Business type</b></td><td>{WebUtility.HtmlEncode(s.BusinessType)}</td></tr>
              <tr><td><b>Contact name</b></td><td>{WebUtility.HtmlEncode(s.ContactName)}</td></tr>
              <tr><td><b>Address</b></td><td>{WebUtility.HtmlEncode(s.Address)}</td></tr>
              <tr><td><b>City</b></td><td>{WebUtility.HtmlEncode(s.City)}</td></tr>
              <tr><td><b>Postal code</b></td><td>{WebUtility.HtmlEncode(s.PostalCode)}</td></tr>
              <tr><td><b>Email</b></td><td>{WebUtility.HtmlEncode(s.Email)}</td></tr>
              <tr><td><b>Phone</b></td><td>{WebUtility.HtmlEncode(s.Phone)}</td></tr>
              <tr><td><b>Submitted</b></td><td>{s.SubmittedAt:u}</td></tr>
            </table>
            """;

        var clientHtml = $"""
            <div style="font-family:Arial,sans-serif;font-size:15px;line-height:1.55;color:#222">
              <h2 style="color:#c0392b">Welcome to MapleKiosk, {WebUtility.HtmlEncode(s.CompanyName)}!</h2>
              <p>Thank you for registering for your free trial. Your account is being set up — our team will reach out shortly to help you get started.</p>
              <p>In the meantime, you can try our demo app right now:</p>
              <p><a href="{DemoUrl}" style="display:inline-block;background:#c0392b;color:#fff;text-decoration:none;padding:12px 22px;border-radius:6px;font-weight:600">Open the demo app</a></p>
              <p style="color:#666;font-size:13px">Or paste this link into your browser: <a href="{DemoUrl}">{DemoUrl}</a></p>
              <hr style="border:none;border-top:1px solid #eee;margin:24px 0" />
              <p style="color:#888;font-size:12px">MapleKiosk — Made in Canada 🍁</p>
            </div>
            """;

        var salesTask  = _email.SendAsync(SalesInbox, $"New trial signup: {s.CompanyName}", salesHtml);
        var clientTask = _email.SendAsync(s.Email, "Thank you for registering with MapleKiosk", clientHtml);
        await Task.WhenAll(salesTask, clientTask);
    }
}
