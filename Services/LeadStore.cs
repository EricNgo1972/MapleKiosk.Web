using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MapleKiosk.Web.Services;

/// <summary>Where the team is with a lead.</summary>
public static class LeadStatus
{
    public const string New = "new";
    public const string Contacted = "contacted";
    public const string Won = "won";
    public const string Lost = "lost";
    public const string Spam = "spam";

    public static readonly string[] All = [New, Contacted, Won, Lost, Spam];
}

/// <summary>A demo request as /admin/leads lists it: the form as sent, the team's follow-up, and the sales email's fate.</summary>
public sealed class LeadRequest
{
    public string Id { get; set; } = "";
    public TrialSignup Form { get; set; } = new();
    public string Status { get; set; } = LeadStatus.New;
    public DateTimeOffset? ContactedAt { get; set; }
    public string? ContactedBy { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? EmailSentAt { get; set; }
    public string? EmailError { get; set; }
}

/// <summary>The <c>leads</c> table of <see cref="SiteDb"/>.</summary>
public sealed class LeadStore(SiteDb db)
{
    /// <summary>A new id that sorts by time: <c>yyyyMMddHHmmssfff-xxxxxxxx</c>.</summary>
    public static string NewId(DateTimeOffset at) => $"{at.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];

    /// <summary>Adds the lead; false when its id is already there (an import seen before).</summary>
    public async Task<bool> AddAsync(LeadRequest lead)
    {
        var f = lead.Form;
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO leads (id, submitted_at, company, address, city, postal_code, business_type, product,
                                         contact_name, email, phone, culture, status, notes)
            VALUES ($id, $at, $company, $address, $city, $postal, $type, $product, $contact, $email, $phone, $culture, $status, $notes);
            """;
        cmd.Parameters.AddWithValue("$id", lead.Id);
        cmd.Parameters.AddWithValue("$at", Text(f.SubmittedAt));
        cmd.Parameters.AddWithValue("$company", f.CompanyName);
        cmd.Parameters.AddWithValue("$address", f.Address);
        cmd.Parameters.AddWithValue("$city", f.City);
        cmd.Parameters.AddWithValue("$postal", f.PostalCode);
        cmd.Parameters.AddWithValue("$type", f.BusinessType);
        cmd.Parameters.AddWithValue("$product", Db(f.Product));
        cmd.Parameters.AddWithValue("$contact", f.ContactName);
        cmd.Parameters.AddWithValue("$email", f.Email);
        cmd.Parameters.AddWithValue("$phone", f.Phone);
        cmd.Parameters.AddWithValue("$culture", f.Culture);
        cmd.Parameters.AddWithValue("$status", lead.Status);
        cmd.Parameters.AddWithValue("$notes", Db(lead.Notes));
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    /// <summary>Leads newest first; only those with <paramref name="status"/> when given.</summary>
    public async Task<List<LeadRequest>> ListAsync(string? status = null)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM leads" + (status is null ? "" : " WHERE status = $status") + " ORDER BY submitted_at DESC;";
        if (status is not null) cmd.Parameters.AddWithValue("$status", status);

        var leads = new List<LeadRequest>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            leads.Add(new LeadRequest
            {
                Id = r.GetString(r.GetOrdinal("id")),
                Status = r.GetString(r.GetOrdinal("status")),
                ContactedAt = Time(r, "contacted_at"),
                ContactedBy = Str(r, "contacted_by"),
                Notes = Str(r, "notes"),
                EmailSentAt = Time(r, "email_sent_at"),
                EmailError = Str(r, "email_error"),
                Form = new TrialSignup
                {
                    SubmittedAt = Time(r, "submitted_at")!.Value,
                    CompanyName = r.GetString(r.GetOrdinal("company")),
                    Address = r.GetString(r.GetOrdinal("address")),
                    City = r.GetString(r.GetOrdinal("city")),
                    PostalCode = r.GetString(r.GetOrdinal("postal_code")),
                    BusinessType = r.GetString(r.GetOrdinal("business_type")),
                    Product = Str(r, "product"),
                    ContactName = r.GetString(r.GetOrdinal("contact_name")),
                    Email = r.GetString(r.GetOrdinal("email")),
                    Phone = r.GetString(r.GetOrdinal("phone")),
                    Culture = r.GetString(r.GetOrdinal("culture")),
                },
            });
        }
        return leads;
    }

    /// <summary>Moves a lead to <paramref name="status"/>; leaving "new" records who and when (the first time).</summary>
    public Task SetStatusAsync(string id, string status, string? by) => ExecAsync("""
        UPDATE leads SET status = $status,
            contacted_at = CASE WHEN $status = 'new' THEN NULL ELSE COALESCE(contacted_at, $now) END,
            contacted_by = CASE WHEN $status = 'new' THEN NULL ELSE COALESCE(contacted_by, $by) END
        WHERE id = $id;
        """, ("$id", id), ("$status", status), ("$now", Text(DateTimeOffset.UtcNow)), ("$by", Db(by)));

    public Task SetNotesAsync(string id, string? notes) =>
        ExecAsync("UPDATE leads SET notes = $notes WHERE id = $id;", ("$id", id), ("$notes", Db(notes?.Trim())));

    /// <summary>Records the sales email: sent (<paramref name="error"/> null) or why not.</summary>
    public Task MarkEmailedAsync(string id, string? error) => ExecAsync(
        "UPDATE leads SET email_sent_at = $at, email_error = $error WHERE id = $id;",
        ("$id", id), ("$at", error is null ? Text(DateTimeOffset.UtcNow) : DBNull.Value), ("$error", Db(error)));

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    // Times are stored as round-trip UTC text, which also sorts in time order.
    private static string Text(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static object Db(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s;

    private static string? Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTimeOffset? Time(SqliteDataReader r, string col) =>
        Str(r, col) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;
}
