using Microsoft.Data.Sqlite;

namespace MapleKiosk.Web.Services;

/// <summary>
/// The site's own SQLite database, <c>{data}/maplekiosk.db</c> in the data folder beside the app
/// (<see cref="Assistant.ChatLog.DataFolder"/>) — local disk, no secrets, survives deploys. WAL mode, so the
/// admin page reads while the form writes. The schema is created on first open; <c>user_version</c> marks
/// which version it is at, for when a column is added later.
/// </summary>
public sealed class SiteDb
{
    private const int SchemaVersion = 2;

    private readonly string _connection;

    public SiteDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using var db = Open();
        Exec(db, "PRAGMA journal_mode=WAL;");
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version;"));
        if (version < 1)
        {
            Exec(db, """
                CREATE TABLE IF NOT EXISTS leads (
                    id             TEXT PRIMARY KEY,
                    submitted_at   TEXT NOT NULL,
                    company        TEXT NOT NULL,
                    address        TEXT NOT NULL,
                    city           TEXT NOT NULL,
                    postal_code    TEXT NOT NULL,
                    business_type  TEXT NOT NULL,
                    product        TEXT,
                    contact_name   TEXT NOT NULL,
                    email          TEXT NOT NULL,
                    phone          TEXT NOT NULL,
                    culture        TEXT NOT NULL,
                    status         TEXT NOT NULL DEFAULT 'new',
                    contacted_at   TEXT,
                    contacted_by   TEXT,
                    notes          TEXT,
                    email_sent_at  TEXT,
                    email_error    TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_leads_submitted ON leads(submitted_at);
                """);
        }
        if (version < 2)
        {
            // Every text on MapleKiosk's Twilio number, in and out (/admin/sms).
            Exec(db, """
                CREATE TABLE IF NOT EXISTS sms (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    at         TEXT NOT NULL,
                    direction  TEXT NOT NULL,
                    number     TEXT NOT NULL,
                    line       TEXT NOT NULL,
                    body       TEXT NOT NULL,
                    kind       TEXT NOT NULL,
                    ok         INTEGER NOT NULL DEFAULT 1,
                    error      TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_sms_number ON sms(number, at);
                CREATE INDEX IF NOT EXISTS ix_sms_at ON sms(at);
                """);
        }
        if (version < SchemaVersion) Exec(db, $"PRAGMA user_version={SchemaVersion};");
    }

    public SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        Exec(db, "PRAGMA busy_timeout=5000;");
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
