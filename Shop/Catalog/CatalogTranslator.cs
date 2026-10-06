using System.Text.Json;
using MapleKiosk.Web.Assistant;
using Microsoft.Extensions.AI;

namespace MapleKiosk.Web.Shop.Catalog;

/// <summary>
/// Drafts an item's (or a category's) French, Vietnamese and Russian wording from its English, with the
/// language model the website assistant uses (<see cref="AssistantLlm"/>). Only /shop/admin calls it,
/// and only to fill the dialog: the admin reviews and saves, and the website shows saved text only.
/// </summary>
public sealed class CatalogTranslator
{
    private readonly AssistantLlm _llm;
    private readonly ILogger<CatalogTranslator> _logger;

    public CatalogTranslator(AssistantLlm llm, ILogger<CatalogTranslator> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public sealed record Result(Dictionary<string, CatalogText>? Texts, string? Error);

    private const string Instructions = """
        You translate the catalog of MapleKiosk, point-of-sale and front-desk software sold to small businesses
        in Canada (coffee shops, restaurants, nail salons and spas, garages). Translate the English item into
        French (Canadian), Vietnamese and Russian, for the website's shop.
        - Natural, short shop wording a business owner would use; keep the tone plain and friendly.
        - Keep brand and plan names as they are: MapleKiosk, MapleSPA, MapleCoffee, MaplePOS, MapleGarage, plan
          names like "Pro Growth" or "SMS 2", and product words like POS, SMS, AI, Google Maps, Facebook,
          Instagram, TikTok. Translate a name only when it's a plain description (e.g. "Website").
        - Keep numbers, prices and units exactly.
        - "details" is one point per line: return the same number of lines, in the same order.
        - An empty field stays empty.
        Reply with JSON only, no code fences:
        {"fr":{"name":"","description":"","details":""},"vi":{...},"ru":{...}}
        """;

    /// <summary>Translations of the item's English (name, description, details) into fr / vi / ru.</summary>
    public async Task<Result> TranslateAsync(AppProduct item, CancellationToken ct = default)
    {
        var input = JsonSerializer.Serialize(new { name = item.Name, description = item.Description ?? "", details = item.Details ?? "" });
        var (json, error) = await AskAsync(input, ct);
        if (json is null) return new(null, error);

        var source = CatalogText.SourceOf(item);
        var texts = new Dictionary<string, CatalogText>();
        foreach (var lang in CatalogLanguages.Translated)
        {
            if (!json.Value.TryGetProperty(lang, out var t) || t.ValueKind != JsonValueKind.Object) continue;
            texts[lang] = new CatalogText
            {
                Name = Field(t, "name"),
                Description = string.IsNullOrWhiteSpace(item.Description) ? null : Field(t, "description"),
                Details = string.IsNullOrWhiteSpace(item.Details) ? null : Field(t, "details"),
                Source = source
            };
        }
        return texts.Count == CatalogLanguages.Translated.Length ? new(texts, null) : new(null, "The model's reply was incomplete. Try again.");
    }

    /// <summary>A category's name in fr / vi / ru.</summary>
    public async Task<(Dictionary<string, string>? Names, string? Error)> TranslateNameAsync(string name, CancellationToken ct = default)
    {
        var input = JsonSerializer.Serialize(new { name, description = "", details = "" });
        var (json, error) = await AskAsync(input, ct);
        if (json is null) return (null, error);

        var names = CatalogLanguages.Translated
            .Select(l => (l, json.Value.TryGetProperty(l, out var t) && t.ValueKind == JsonValueKind.Object ? Field(t, "name") : null))
            .Where(x => x.Item2 is not null)
            .ToDictionary(x => x.l, x => x.Item2!);
        return names.Count == CatalogLanguages.Translated.Length ? (names, null) : (null, "The model's reply was incomplete. Try again.");
    }

    private async Task<(JsonElement? Json, string? Error)> AskAsync(string input, CancellationToken ct)
    {
        var client = await _llm.GetAsync();
        if (client is StubChatClient)
            return (null, "No AI model is set up (Chats → Settings), so translations can't be drafted. You can still type them.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, input)],
                new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 2000 }, timeout.Token);

            var text = response.Text.Trim();
            // Some models wrap JSON in a fence anyway.
            if (text.StartsWith("```")) text = text.Trim('`').Replace("json", "", StringComparison.OrdinalIgnoreCase).Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return (null, "The model didn't reply with translations. Try again.");
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return (doc.RootElement.Clone(), null);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return (null, "The AI model took too long. Try again.");
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Catalog translation failed.");
            return (null, $"Translation failed: {ex.Message}");
        }
    }

    private static string? Field(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;
}
