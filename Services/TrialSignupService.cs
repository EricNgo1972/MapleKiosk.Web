using System.ComponentModel.DataAnnotations;
using System.Net;
using Azure;
using Azure.Data.Tables;
using MapleKiosk.Web.Assistant;

namespace MapleKiosk.Web.Services;

public class TrialSignup
{
    [Required, StringLength(120)]
    public string CompanyName { get; set; } = "";

    [Required, StringLength(200)]
    public string Address { get; set; } = "";

    [Required, StringLength(80)]
    public string City { get; set; } = "";

    [Required, StringLength(12), PostalCode]
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

    public string Culture { get; set; } = "en";

    // Set when the visitor signed up from a stand-alone product's page (e.g. "AI Voice Agent").
    [StringLength(60)]
    public string? Product { get; set; }

    // Honeypot: hidden from people, so only a bot fills it in.
    public string? Website { get; set; }
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
    public string Culture { get; set; } = "en";
    public string? Product { get; set; }
}

/// <summary>
/// The "Book a demo" form. Each request is kept three ways, in this order, so no single failure loses it:
/// the site's SQLite database (<see cref="LeadStore"/>, read by /admin/leads; local disk, needs no secrets),
/// the <c>clientrequest</c> Azure table, then the email to sales and the visitor. A filled honeypot
/// (<see cref="TrialSignup.Website"/>) is a bot: kept as spam, no email.
/// </summary>
public class TrialSignupService
{
    private const string TableName = "clientrequest";
    private const string SalesInbox = "sales@maplekiosk.ca";

    private readonly ILogger<TrialSignupService> _logger;
    private readonly EmailService _email;
    private readonly DemoLinkStore _demos;
    private readonly TableClient? _table;

    public TrialSignupService(ILogger<TrialSignupService> logger, IConfiguration config, EmailService email, DemoLinkStore demos,
        IWebHostEnvironment env)
    {
        _logger = logger;
        _email = email;
        _demos = demos;

        try
        {
            Leads = new LeadStore(new SiteDb(Path.Combine(ChatLog.DataFolder(env), "maplekiosk.db")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The site database can't be opened — demo requests go to Azure and email only.");
        }

        var connection = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING")
                         ?? config["STORAGE_CONNECTION_STRING"];

        if (string.IsNullOrWhiteSpace(connection))
        {
            _logger.LogWarning("STORAGE_CONNECTION_STRING not set — demo requests are kept in the site database and emailed only.");
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

    /// <summary>The leads in the site database; null when it can't be opened (/admin/leads says so).</summary>
    public LeadStore? Leads { get; }

    public bool HasAzure => _table is not null;

    public async Task CreateAsync(TrialSignup signup)
    {
        signup.SubmittedAt = DateTimeOffset.UtcNow;
        var spam = !string.IsNullOrWhiteSpace(signup.Website);

        _logger.LogInformation("Trial signup{Spam}: {Company} | {Email} | {Type} | {Product}",
            spam ? " (honeypot — spam)" : "", signup.CompanyName, signup.Email, signup.BusinessType, signup.Product);

        // Each channel is on its own: one failing never skips the next.
        var id = LeadStore.NewId(signup.SubmittedAt);
        var saved = Leads is not null && await TryAsync("save lead to the site database", () =>
            Leads.AddAsync(new LeadRequest { Id = id, Form = signup, Status = spam ? LeadStatus.Spam : LeadStatus.New }));
        if (spam) return;

        if (_table is not null) await SaveAsync(signup);

        var error = await SendNotificationEmailsAsync(signup);
        if (saved) await TryAsync("record the sales email", () => Leads!.MarkEmailedAsync(id, error));
    }

    /// <summary>Copies the Azure <c>clientrequest</c> rows into the site database once; returns how many were new.</summary>
    public async Task<int> ImportFromAzureAsync()
    {
        if (_table is null || Leads is null) return 0;
        var added = 0;
        await foreach (var e in _table.QueryAsync<ClientRequestEntity>())
        {
            var lead = new LeadRequest
            {
                Id = "az-" + e.RowKey,
                Form = new TrialSignup
                {
                    SubmittedAt = e.SubmittedAt == default ? e.Timestamp ?? DateTimeOffset.UtcNow : e.SubmittedAt,
                    CompanyName = e.CompanyName, Address = e.Address, City = e.City, PostalCode = e.PostalCode,
                    BusinessType = e.BusinessType, Product = e.Product, ContactName = e.ContactName,
                    Email = e.Email, Phone = e.Phone, Culture = e.Culture,
                },
            };
            if (await Leads.AddAsync(lead)) added++;
        }
        return added;
    }

    /// <summary>Runs <paramref name="action"/>; false (logged) when it throws.</summary>
    private async Task<bool> TryAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {What}.", what);
            return false;
        }
    }

    private async Task SaveAsync(TrialSignup signup)
    {
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
            Culture = signup.Culture,
            Email = signup.Email,
            Phone = signup.Phone,
            SubmittedAt = signup.SubmittedAt,
            Product = string.IsNullOrWhiteSpace(signup.Product) ? null : signup.Product,
        };

        try
        {
            await _table!.AddEntityAsync(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write signup for {Company} to Azure Table.", signup.CompanyName);
        }
    }

    /// <summary>Emails sales and the visitor; returns why the sales email failed, or null.</summary>
    private async Task<string?> SendNotificationEmailsAsync(TrialSignup s)
    {
        // Sales email — always English, internal consumption.
        var salesHtml = $"""
            <h2>New MapleKiosk demo request</h2>
            <table cellpadding="6" style="font-family:Arial,sans-serif;font-size:14px">
              <tr><td><b>Company</b></td><td>{WebUtility.HtmlEncode(s.CompanyName)}</td></tr>
              {(string.IsNullOrWhiteSpace(s.Product) ? "" : $"<tr><td><b>Product</b></td><td>{WebUtility.HtmlEncode(s.Product)} only</td></tr>")}
              <tr><td><b>Business type</b></td><td>{WebUtility.HtmlEncode(s.BusinessType)}</td></tr>
              <tr><td><b>Contact name</b></td><td>{WebUtility.HtmlEncode(s.ContactName)}</td></tr>
              <tr><td><b>Address</b></td><td>{WebUtility.HtmlEncode(s.Address)}</td></tr>
              <tr><td><b>City</b></td><td>{WebUtility.HtmlEncode(s.City)}</td></tr>
              <tr><td><b>Postal code</b></td><td>{WebUtility.HtmlEncode(s.PostalCode)}</td></tr>
              <tr><td><b>Email</b></td><td>{WebUtility.HtmlEncode(s.Email)}</td></tr>
              <tr><td><b>Phone</b></td><td>{WebUtility.HtmlEncode(s.Phone)}</td></tr>
              <tr><td><b>Culture</b></td><td>{s.Culture}</td></tr>
              <tr><td><b>Submitted</b></td><td>{s.SubmittedAt:u}</td></tr>
            </table>
            """;

        // Client email — localized to submitter's culture.
        var culture = Translations.All.ContainsKey(s.Culture) ? s.Culture : "en";
        var t = Translations.All[culture];
        string T(string k) => t.TryGetValue(k, out var v) ? v : Translations.All["en"][k];

        // The demo of the product they picked (set on /onboarding/admin); no link set → no demo paragraph.
        var demo = await _demos.GetAsync(s.BusinessType);
        var url = WebUtility.HtmlEncode(demo.Url);
        var demoHtml = string.IsNullOrEmpty(demo.Url) ? "" : $"""
              <p>{WebUtility.HtmlEncode(T("email.client.p2"))}</p>
              <p><a href="{url}" style="display:inline-block;background:#c0392b;color:#fff;text-decoration:none;padding:12px 22px;border-radius:6px;font-weight:600">{WebUtility.HtmlEncode(string.Format(T("email.client.btn"), demo.Product))}</a></p>
              <p style="color:#666;font-size:13px">{WebUtility.HtmlEncode(T("email.client.alt"))} <a href="{url}">{url}</a></p>
            """;

        var clientHtml = $"""
            <div style="font-family:Arial,sans-serif;font-size:15px;line-height:1.55;color:#222">
              <h2 style="color:#c0392b">{WebUtility.HtmlEncode(string.Format(T("email.client.h1"), s.CompanyName))}</h2>
              <p>{WebUtility.HtmlEncode(T("email.client.p1"))}</p>
              {demoHtml}
              <hr style="border:none;border-top:1px solid #eee;margin:24px 0" />
              <p style="color:#888;font-size:12px">MapleKiosk — Made in Canada 🍁</p>
            </div>
            """;

        var salesTask  = _email.SendAsync(SalesInbox,
            string.IsNullOrWhiteSpace(s.Product) ? $"New demo request: {s.CompanyName}" : $"New demo request ({s.Product}): {s.CompanyName}", salesHtml);
        var clientTask = _email.SendAsync(s.Email, T("email.client.subject"), clientHtml);
        await Task.WhenAll(salesTask, clientTask);
        return await salesTask;
    }
}
