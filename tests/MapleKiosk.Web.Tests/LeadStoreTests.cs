using MapleKiosk.Web.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MapleKiosk.Web.Tests;

/// <summary>
/// The demo-request leads in the site's SQLite database: what the form sends comes back intact, newest first,
/// and the team's follow-up (status, who/when, notes) and the sales email's fate are recorded. Each test gets
/// its own temporary database file.
/// </summary>
public sealed class LeadStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"leads-{Guid.NewGuid():N}.db");
    private readonly LeadStore _leads;

    public LeadStoreTests() => _leads = new LeadStore(new SiteDb(_path));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static LeadRequest Lead(string company, DateTimeOffset at, string? product = null) => new()
    {
        Id = LeadStore.NewId(at),
        Form = new TrialSignup
        {
            SubmittedAt = at, CompanyName = company, Address = "123 Main St", City = "Montreal", PostalCode = "H2X 1Y4",
            BusinessType = "Nail Salon", Product = product, ContactName = "Jane Tremblay", Email = "jane@example.com",
            Phone = "+1 514 555 0199", Culture = "fr",
        },
    };

    [Fact]
    public async Task Saved_lead_comes_back_intact_and_new()
    {
        var at = new DateTimeOffset(2026, 10, 17, 12, 30, 0, TimeSpan.Zero);
        var lead = Lead("Acme Nails", at, "AI Voice Agent");
        Assert.True(await _leads.AddAsync(lead));

        var back = Assert.Single(await _leads.ListAsync());
        Assert.Equal(lead.Id, back.Id);
        Assert.Equal(LeadStatus.New, back.Status);
        Assert.Equal(at, back.Form.SubmittedAt);
        Assert.Equal("Acme Nails", back.Form.CompanyName);
        Assert.Equal("AI Voice Agent", back.Form.Product);
        Assert.Equal("fr", back.Form.Culture);
        Assert.Null(back.EmailSentAt);
    }

    [Fact]
    public async Task Same_id_twice_is_kept_once()
    {
        var lead = Lead("Acme", DateTimeOffset.UtcNow);
        Assert.True(await _leads.AddAsync(lead));
        Assert.False(await _leads.AddAsync(lead));
        Assert.Single(await _leads.ListAsync());
    }

    [Fact]
    public async Task Newest_first_and_filtered_by_status()
    {
        var old = Lead("Old", DateTimeOffset.UtcNow.AddDays(-2));
        var recent = Lead("Recent", DateTimeOffset.UtcNow);
        var spam = Lead("Bot", DateTimeOffset.UtcNow.AddDays(-1));
        spam.Status = LeadStatus.Spam;
        foreach (var l in new[] { old, recent, spam }) await _leads.AddAsync(l);

        Assert.Equal(["Recent", "Bot", "Old"], (await _leads.ListAsync()).Select(l => l.Form.CompanyName));
        Assert.Equal(["Recent", "Old"], (await _leads.ListAsync(LeadStatus.New)).Select(l => l.Form.CompanyName));
    }

    [Fact]
    public async Task Follow_up_records_who_and_when_once_and_back_to_new_clears_it()
    {
        var lead = Lead("Acme", DateTimeOffset.UtcNow);
        await _leads.AddAsync(lead);

        await _leads.SetStatusAsync(lead.Id, LeadStatus.Contacted, "minh@spc-technology.com");
        var contacted = Assert.Single(await _leads.ListAsync());
        Assert.Equal(LeadStatus.Contacted, contacted.Status);
        Assert.Equal("minh@spc-technology.com", contacted.ContactedBy);
        Assert.NotNull(contacted.ContactedAt);

        // Closing it later keeps the first follow-up.
        await _leads.SetStatusAsync(lead.Id, LeadStatus.Won, "eric@spc-technology.com");
        var won = Assert.Single(await _leads.ListAsync());
        Assert.Equal(LeadStatus.Won, won.Status);
        Assert.Equal("minh@spc-technology.com", won.ContactedBy);
        Assert.Equal(contacted.ContactedAt, won.ContactedAt);

        await _leads.SetStatusAsync(lead.Id, LeadStatus.New, "eric@spc-technology.com");
        var reopened = Assert.Single(await _leads.ListAsync());
        Assert.Null(reopened.ContactedAt);
        Assert.Null(reopened.ContactedBy);
    }

    [Fact]
    public async Task Notes_and_email_outcome_are_kept()
    {
        var lead = Lead("Acme", DateTimeOffset.UtcNow);
        await _leads.AddAsync(lead);

        await _leads.SetNotesAsync(lead.Id, "  Called, demo Tuesday  ");
        await _leads.MarkEmailedAsync(lead.Id, "SendGrid is not configured");
        var failed = Assert.Single(await _leads.ListAsync());
        Assert.Equal("Called, demo Tuesday", failed.Notes);
        Assert.Equal("SendGrid is not configured", failed.EmailError);
        Assert.Null(failed.EmailSentAt);

        await _leads.MarkEmailedAsync(lead.Id, null);
        var sent = Assert.Single(await _leads.ListAsync());
        Assert.Null(sent.EmailError);
        Assert.NotNull(sent.EmailSentAt);
    }

    [Fact]
    public async Task Reopening_an_existing_database_keeps_its_leads()
    {
        await _leads.AddAsync(Lead("Acme", DateTimeOffset.UtcNow));
        var again = new LeadStore(new SiteDb(_path));
        Assert.Single(await again.ListAsync());
    }
}
