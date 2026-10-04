using System.Collections.Concurrent;
using System.Text.Json;

namespace MapleKiosk.Web.Assistant;

/// <summary>A guest's contact, as they gave it to the assistant.</summary>
public sealed record GuestContact(string Name, string? Phone, string? Email, string? Business, string? Interest);

/// <summary>One conversation with the website assistant, as the admin list shows it.</summary>
public sealed record ChatConversation(
    string Id, DateTimeOffset StartedAt, DateTimeOffset LastAt, string Lang, string FirstPage, string LastPage,
    int Messages, string Preview, string? Country, string? Device, GuestContact? Contact);

/// <summary>One message in a conversation.</summary>
public sealed record ChatLine(string Role, string Text, DateTimeOffset At, string? Page);

/// <summary>
/// Every website-assistant conversation (chat and voice), kept so the team can read them and follow up
/// (/chats): one JSON file per conversation in the site's data folder, <c>{data}/chats/{id}.json</c>.
///
/// <para>Files, not Azure Table: a conversation is written after every message and the admin page lists
/// them every few seconds, and the table made both slow. Writes go to a temp file then replace the old one,
/// so a crash never leaves half a conversation; an in-memory index of every conversation (loaded once at
/// start) answers the list without touching the disk.</para>
///
/// <para>The data folder (<see cref="DataFolder"/>) sits OUTSIDE the deployed app on the server, because a
/// deploy replaces the app folder wholesale (rsync --delete). Never allowed to fail a reply: when the folder
/// can't be written, or a write fails, the visitor still gets an answer.</para>
/// </summary>
public sealed class ChatLog
{
    private const int PreviewChars = 140;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly string? _dir;
    private readonly ILogger<ChatLog> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly Lazy<ConcurrentDictionary<string, ChatConversation>> _index;

    public ChatLog(IWebHostEnvironment env, ILogger<ChatLog> logger)
    {
        _logger = logger;
        try
        {
            var dir = Path.Combine(DataFolder(env), "chats");
            Directory.CreateDirectory(dir);
            _dir = dir;
            logger.LogInformation("Assistant conversations are kept in {Dir}.", dir);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Assistant conversations cannot be kept: the data folder can't be created.");
        }
        _index = new(LoadIndex);
    }

    public bool IsConfigured => _dir is not null;

    /// <summary>Where conversations live — for the admin page's "not recorded" notice.</summary>
    public string? Folder => _dir;

    /// <summary>
    /// The site's data folder: <c>DATA_DIR</c> when set; next to the app on the server (the app runs from
    /// <c>/var/www/maplekiosk_www/app</c>, so <c>/var/www/maplekiosk_www/data</c>) — never inside it, as a
    /// deploy wipes the app folder; and <c>App_Data</c> in the project when developing.
    /// </summary>
    public static string DataFolder(IWebHostEnvironment env)
    {
        var configured = Environment.GetEnvironmentVariable("DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var root = new DirectoryInfo(env.ContentRootPath);
        return root.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && root.Parent is not null
            ? Path.Combine(root.Parent.FullName, "data")
            : Path.Combine(root.FullName, "App_Data");
    }

    /// <summary>A new conversation id, or the one the browser sent back when it is one of ours.</summary>
    public static string IdFor(string? sent) =>
        sent is { Length: 32 } s && s.All(Uri.IsHexDigit) ? s.ToLowerInvariant() : Guid.NewGuid().ToString("N");

    public Task<ChatConversation?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_dir is not null && _index.Value.TryGetValue(id, out var c) ? c : null);

    /// <summary>Records one exchange: the guest's message and the reply.</summary>
    public Task RecordAsync(string id, ChatLine said, ChatLine replied, string lang, string? country, string? device,
        CancellationToken ct = default) =>
        UpdateAsync(id, f =>
        {
            f.Lines.Add(said);
            f.Lines.Add(replied);
            f.Lang = lang;
            f.Country ??= country;
            f.Device ??= device;
        }, "an exchange", ct);

    /// <summary>
    /// Records a whole conversation at once — a voice call, whose transcript the relay hands over when the
    /// call ends (it keeps the record server-side, so a closed tab is still recorded). Shown on /chats like
    /// any other chat.
    /// </summary>
    public Task RecordTranscriptAsync(string id, IReadOnlyList<ChatLine> lines, string lang, string? country,
        string? device, CancellationToken ct = default) =>
        lines.Count == 0
            ? Task.CompletedTask
            : UpdateAsync(id, f =>
            {
                f.Lines.AddRange(lines);
                f.Lang = lang;
                f.Country ??= country;
                f.Device ??= device;
            }, "the transcript", ct);

    /// <summary>Saves the contact the guest gave on the conversation.</summary>
    public Task SaveContactAsync(string id, GuestContact contact, CancellationToken ct = default) =>
        UpdateAsync(id, f =>
        {
            f.Contact = contact;
            f.ContactAt = DateTimeOffset.UtcNow;
        }, "the contact", ct);

    /// <summary>Conversations active in the last <paramref name="days"/> days, newest activity first.</summary>
    public Task<List<ChatConversation>> ListAsync(int days, CancellationToken ct = default)
    {
        if (_dir is null) return Task.FromResult(new List<ChatConversation>());
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        return Task.FromResult(_index.Value.Values.Where(c => c.LastAt >= since).OrderByDescending(c => c.LastAt).ToList());
    }

    public async Task<List<ChatLine>> MessagesAsync(string id, CancellationToken ct = default) =>
        (await ReadAsync(id, ct))?.Lines ?? new List<ChatLine>();

    // ---- files ----

    /// <summary>What a conversation file holds.</summary>
    private sealed class ChatFile
    {
        public string Id { get; set; } = "";
        public string Lang { get; set; } = "en";
        public string? Country { get; set; }
        public string? Device { get; set; }
        public GuestContact? Contact { get; set; }
        public DateTimeOffset? ContactAt { get; set; }
        public List<ChatLine> Lines { get; set; } = new();
    }

    private string FileOf(string id) => Path.Combine(_dir!, id + ".json");

    // One writer per conversation; two conversations never wait on each other.
    private async Task UpdateAsync(string id, Action<ChatFile> change, string what, CancellationToken ct)
    {
        if (_dir is null) return;
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var file = await ReadAsync(id, ct) ?? new ChatFile { Id = id };
            change(file);
            var path = FileOf(id);
            var temp = path + ".tmp";
            await using (var s = File.Create(temp))
                await JsonSerializer.SerializeAsync(s, file, Json, ct);
            File.Move(temp, path, overwrite: true);
            _index.Value[id] = Summary(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ChatLog: could not record {What} of conversation {Id}.", what, id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ChatFile?> ReadAsync(string id, CancellationToken ct)
    {
        if (_dir is null || !File.Exists(FileOf(id))) return null;
        try
        {
            await using var s = File.OpenRead(FileOf(id));
            return await JsonSerializer.DeserializeAsync<ChatFile>(s, Json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ChatLog: could not read conversation {Id}.", id);
            return null;
        }
    }

    private ConcurrentDictionary<string, ChatConversation> LoadIndex()
    {
        var index = new ConcurrentDictionary<string, ChatConversation>();
        if (_dir is null) return index;
        foreach (var path in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var file = JsonSerializer.Deserialize<ChatFile>(File.ReadAllText(path), Json);
                if (file is { Lines.Count: > 0 }) index[file.Id] = Summary(file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ChatLog: skipping an unreadable conversation file {File}.", Path.GetFileName(path));
            }
        }
        return index;
    }

    private static ChatConversation Summary(ChatFile f)
    {
        var first = f.Lines.FirstOrDefault();
        var last = f.Lines.LastOrDefault();
        var said = f.Lines.FirstOrDefault(l => l.Role == "user") ?? first;
        return new ChatConversation(
            f.Id,
            first?.At ?? f.ContactAt ?? DateTimeOffset.UtcNow,
            last?.At ?? f.ContactAt ?? DateTimeOffset.UtcNow,
            f.Lang,
            first?.Page ?? "/",
            f.Lines.LastOrDefault(l => l.Page is not null)?.Page ?? "/",
            f.Lines.Count,
            said is null ? "" : Clip(said.Text, PreviewChars),
            f.Country,
            f.Device,
            f.Contact);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
