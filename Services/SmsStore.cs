using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MapleKiosk.Web.Services;

/// <summary>What a text was, for the inbox's labels.</summary>
public static class SmsKind
{
    public const string Guest = "guest";   // a text someone sent to the line
    public const string Reply = "reply";   // the assistant's answer
    public const string Hold = "hold";     // "one moment, I'm sending your request"
    public const string Auto = "auto";     // an automatic confirmation (demo, order, message, callback)
    public const string Link = "link";     // a payment link
    public const string Alert = "alert";   // to the staff phone: a guest wants a person

    public static string Label(string kind, string agentName) => kind switch
    {
        Guest => "Received",
        Reply => agentName,
        Hold => $"{agentName} · please wait",
        Auto => "Automatic",
        Link => "Payment link",
        Alert => "Staff alert",
        _ => kind,
    };
}

/// <summary>One text on the line.</summary>
public sealed record SmsMessage(long Id, DateTimeOffset At, bool Incoming, string Number, string Line, string Body,
    string Kind, bool Ok, string? Error);

/// <summary>One person's texts with the line, as the inbox lists them.</summary>
public sealed record SmsThread(string Number, DateTimeOffset LastAt, string LastBody, bool LastIncoming, int Count, int Failed);

/// <summary>
/// The <c>sms</c> table of <see cref="SiteDb"/>: every text on MapleKiosk's Twilio number, in and out, for /admin/sms.
/// Written from static code paths (the webhook, the Twilio helper), so the one instance is reachable as
/// <see cref="Current"/>. Best-effort: a failed write is logged and never stops a text.
/// </summary>
public sealed class SmsStore
{
    public static SmsStore? Current { get; private set; }

    private readonly SiteDb _db;
    private readonly ILogger<SmsStore> _logger;

    public SmsStore(IWebHostEnvironment env, ILogger<SmsStore> logger)
    {
        _logger = logger;
        _db = new SiteDb(Path.Combine(Assistant.ChatLog.DataFolder(env), "maplekiosk.db"));
        Current = this;
    }

    /// <summary>Records a text; <paramref name="number"/> is the other side (the guest, or the staff phone).</summary>
    public async Task AddAsync(bool incoming, string number, string line, string body, string kind, bool ok = true, string? error = null)
    {
        try
        {
            await using var conn = _db.Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sms (at, direction, number, line, body, kind, ok, error)
                VALUES ($at, $dir, $number, $line, $body, $kind, $ok, $error);
                """;
            cmd.Parameters.AddWithValue("$at", Text(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$dir", incoming ? "in" : "out");
            cmd.Parameters.AddWithValue("$number", number);
            cmd.Parameters.AddWithValue("$line", line);
            cmd.Parameters.AddWithValue("$body", body);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
            cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sms: could not record a text with {Number}.", number);
        }
    }

    /// <summary>One row per number, newest activity first, over the last <paramref name="days"/> days.</summary>
    public async Task<List<SmsThread>> ThreadsAsync(int days = 90)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.number, s.at, s.body, s.direction, t.n, t.failed
            FROM (SELECT number, MAX(id) AS last, COUNT(*) AS n, SUM(CASE WHEN ok = 0 THEN 1 ELSE 0 END) AS failed
                  FROM sms WHERE at >= $since GROUP BY number) t
            JOIN sms s ON s.id = t.last
            ORDER BY s.id DESC;
            """;
        cmd.Parameters.AddWithValue("$since", Text(DateTimeOffset.UtcNow.AddDays(-days)));
        var list = new List<SmsThread>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new SmsThread(r.GetString(0), Parse(r.GetString(1)), r.GetString(2), r.GetString(3) == "in",
                r.GetInt32(4), r.GetInt32(5)));
        return list;
    }

    /// <summary>All texts with one number, oldest first.</summary>
    public async Task<List<SmsMessage>> MessagesAsync(string number)
    {
        await using var conn = _db.Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, at, direction, number, line, body, kind, ok, error FROM sms WHERE number = $number ORDER BY id;";
        cmd.Parameters.AddWithValue("$number", number);
        var list = new List<SmsMessage>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new SmsMessage(r.GetInt64(0), Parse(r.GetString(1)), r.GetString(2) == "in", r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetInt32(7) == 1, r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    private static string Text(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
