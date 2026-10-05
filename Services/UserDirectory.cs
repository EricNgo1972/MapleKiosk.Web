using Azure;
using Azure.Data.Tables;
using MapleKiosk.Web.Onboarding;

namespace MapleKiosk.Web.Services;

/// <summary>A person on /admin/users: a display name, and whether they're on the team (admin).</summary>
public sealed record UserEntry(string Email, string? Name, bool IsTeam);

/// <summary>
/// The people who sign in, as staff maintain them on /admin/users: display names for anyone
/// (team or customer) and the team list beyond the built-in accounts in <see cref="AppAuthValidator"/>.
/// Azure Table <c>appusers</c>, partition "user", one row per email (RowKey = hex of the canonical
/// email, so any address is a valid key). Kept apart from onboarding records, so editing a person
/// never writes to a customer's setup answers. Read on every sign-in and admin request, so reads
/// are cached briefly (cleared on every write) and a storage hiccup serves the last good list.
/// </summary>
public sealed class UserDirectory
{
    private const string TableName = "appusers";
    private const string Partition = "user";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly TableClient? _table;
    private readonly ILogger<UserDirectory> _logger;
    private IReadOnlyDictionary<string, UserEntry>? _cache;
    private DateTime _cachedAtUtc;

    public UserDirectory(ILogger<UserDirectory> logger)
    {
        _logger = logger;
        var connection = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection)) return;
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

    public bool IsConfigured => _table is not null;

    /// <summary>Everyone saved, by canonical email.</summary>
    public async Task<IReadOnlyDictionary<string, UserEntry>> GetAllAsync(CancellationToken ct = default)
    {
        if (_table is null) return new Dictionary<string, UserEntry>();
        if (_cache is { } hit && DateTime.UtcNow - _cachedAtUtc < CacheTtl) return hit;
        try
        {
            var all = new Dictionary<string, UserEntry>(StringComparer.Ordinal);
            await foreach (var e in _table.QueryAsync<TableEntity>(x => x.PartitionKey == Partition, cancellationToken: ct).ConfigureAwait(false))
                if (e.GetString("Email") is { Length: > 0 } email)
                    all[email] = new UserEntry(email, e.GetString("Name") is { Length: > 0 } n ? n : null, e.GetBoolean("IsTeam") ?? false);
            _cache = all;
            _cachedAtUtc = DateTime.UtcNow;
            return all;
        }
        catch (Exception ex) when (_cache is not null)
        {
            _logger.LogWarning(ex, "User directory read failed; serving the last one.");
            return _cache;
        }
    }

    public async Task<bool> IsTeamAsync(string email, CancellationToken ct = default)
        => (await GetAllAsync(ct).ConfigureAwait(false)).TryGetValue(OnboardingRecord.CanonicalEmail(email), out var u) && u.IsTeam;

    /// <summary>Sets the display name, keeping team membership.</summary>
    public async Task SetNameAsync(string email, string? name, CancellationToken ct = default)
    {
        var canonical = OnboardingRecord.CanonicalEmail(email);
        var current = (await GetAllAsync(ct).ConfigureAwait(false)).GetValueOrDefault(canonical);
        await WriteAsync(canonical, name, current?.IsTeam ?? false, ct).ConfigureAwait(false);
    }

    /// <summary>Adds the email to the team (admin access) or takes it off, with its name.</summary>
    public async Task SetTeamAsync(string email, string? name, bool isTeam, CancellationToken ct = default)
    {
        var canonical = OnboardingRecord.CanonicalEmail(email);
        await WriteAsync(canonical, name, isTeam, ct).ConfigureAwait(false);
        _logger.LogInformation("Team {Change}: {Email}", isTeam ? "member added/updated" : "member removed", canonical);
    }

    private async Task WriteAsync(string canonical, string? name, bool isTeam, CancellationToken ct)
    {
        if (_table is null) throw new InvalidOperationException("Storage is not configured (STORAGE_CONNECTION_STRING).");
        if (string.IsNullOrWhiteSpace(name) && !isTeam)
            await _table.DeleteEntityAsync(Partition, Key(canonical), ETag.All, ct).ConfigureAwait(false);
        else
            await _table.UpsertEntityAsync(new TableEntity(Partition, Key(canonical))
            {
                ["Email"] = canonical,
                ["Name"] = name?.Trim() ?? "",
                ["IsTeam"] = isTeam
            }, TableUpdateMode.Replace, ct).ConfigureAwait(false);
        _cache = null;
    }

    private static string Key(string canonicalEmail) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(canonicalEmail));
}
