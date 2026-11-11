using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Assistant;

/// <summary>
/// After a phone call, a browser-voice conversation or an SMS reply, the chat model reads the transcript and
/// writes a two-or-three-sentence summary for /chats. It also picks out the guest's name (and business, email)
/// when they said it, so a row reads "Eric" rather than "Phone caller" even if no tool saved a contact. The
/// caller's number stays the one the carrier gave. Background and best-effort: a failure is logged, nothing else.
/// </summary>
public sealed class ConversationSummarizer
{
    /// <summary>The names the recorders put on a contact before the guest says theirs.</summary>
    public static bool IsPlaceholder(string? name) => name is null or "" or "Phone caller" or "SMS sender" or "Guest";

    private const int MaxTranscriptChars = 12_000;

    private const string Prompt =
        "You summarise a conversation between MapleKiosk's assistant (a salesperson and receptionist for MapleKiosk, " +
        "business software for cafés, restaurants, nail salons/spas and garages) and a guest, for the MapleKiosk team.\n" +
        "Reply with ONE JSON object only, no Markdown:\n" +
        "{\"name\": the guest's own name exactly as they said it, or null; \"business\": their business's name, or null; " +
        "\"email\": their email address, or null; \"summary\": 2–3 short sentences in English — who they are, what they " +
        "wanted, what was done (demo booked, order, message, put through to a person) and anything the team must follow up}.\n" +
        "Only what the guest actually said — never guess a name or invent a detail. Transcription can garble words: " +
        "if a name is unclear, give your best reading only if the assistant repeated or used it.";

    private readonly AssistantLlm _llm;
    private readonly ChatLog _chats;
    private readonly ILogger<ConversationSummarizer> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public ConversationSummarizer(AssistantLlm llm, ChatLog chats, ILogger<ConversationSummarizer> logger)
    {
        _llm = llm;
        _chats = chats;
        _logger = logger;
    }

    /// <summary>Summarise in the background; returns at once.</summary>
    public void Queue(string id) => _ = Task.Run(() => SummarizeAsync(id, CancellationToken.None));

    public async Task SummarizeAsync(string id, CancellationToken ct)
    {
        // One at a time per conversation: SMS replies can queue several in a row.
        var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var lines = await _chats.MessagesAsync(id, ct);
            if (lines.Count(l => l.Role == "user") == 0) return;

            var client = await _llm.GetAsync();
            if (client is StubChatClient) return;

            var transcript = string.Join("\n", lines.Select(l => $"{(l.Role == "user" ? "Guest" : "Assistant")}: {l.Text}"));
            if (transcript.Length > MaxTranscriptChars) transcript = "…" + transcript[^MaxTranscriptChars..];

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompt + "\nThe conversation started " +
                    LocalClock.Now(lines[0].At).ToString("dddd, MMMM d, yyyy, h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US")) +
                    " (Quebec time): write any day they mentioned as a real date, not \"tomorrow\"."),
                 new ChatMessage(ChatRole.User, transcript)],
                new ChatOptions { MaxOutputTokens = 400 }, ct);
            if (Parse(response.Text) is not { } found) return;

            if (found.Summary is { Length: > 0 } summary) await _chats.SaveSummaryAsync(id, summary, ct);

            // A name the guest said, on a contact that has none of its own yet (the caller's number is kept).
            var contact = (await _chats.GetAsync(id, ct))?.Contact;
            if (found.Name is { Length: > 0 } name && IsPlaceholder(contact?.Name))
                await _chats.SaveContactAsync(id, new GuestContact(name, contact?.Phone,
                    contact?.Email ?? found.Email, contact?.Business ?? found.Business, contact?.Interest), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Assistant: could not summarise conversation {Id}.", id);
        }
        finally
        {
            gate.Release();
        }
    }

    internal sealed record Found(string? Name, string? Business, string? Email, string? Summary);

    /// <summary>The model's JSON, tolerating a code fence or text around it; null when there is none.</summary>
    internal static Found? Parse(string? text)
    {
        var m = Regex.Match(text ?? "", @"\{[\s\S]*\}");
        if (!m.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            var root = doc.RootElement;
            string? Get(string key, int max) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Trim() is { Length: > 0 } s
                && !s.Equals("null", StringComparison.OrdinalIgnoreCase)
                    ? (s.Length <= max ? s : s[..max])
                    : null;
            var email = Get("email", 160);
            return new Found(Get("name", 120), Get("business", 120), email?.Contains('@') == true ? email : null, Get("summary", 800));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
