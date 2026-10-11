using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MapleKiosk.Web.Services;

namespace MapleKiosk.Web.Shop.PhoneLines;

/// <summary>
/// <see cref="ITelephonyGateway"/> over Twilio's REST API with the platform account (raw HTTP, no SDK). Leaf
/// only: one Twilio call per method plus JSON parsing — it never decides whether a number may be bought.
/// Credentials resolve env var first, then the keyvalue table (<c>Twilio/AccountSid</c>, <c>Twilio/AuthToken</c>).
/// </summary>
public sealed class TwilioGateway : ITelephonyGateway
{
    public const string HttpClientName = "Twilio";

    private const string ApiBase = "https://api.twilio.com/2010-04-01/";

    // "The phone number you requested is not available": someone else bought it after the search.
    private const string NumberUnavailableCode = "21422";
    private const string ResourceNotFoundCode = "20404";

    private readonly HttpClient _http;
    private readonly ILogger<TwilioGateway> _logger;

    public TwilioGateway(IHttpClientFactory clients, ILogger<TwilioGateway> logger)
    {
        _http = clients.CreateClient(HttpClientName);
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) => await CredentialsAsync().ConfigureAwait(false) is not null;

    public async Task<NumberSearchResult> SearchAsync(string country, string? areaCode, CancellationToken ct = default)
    {
        if (await CredentialsAsync().ConfigureAwait(false) is not { } creds) return new NumberSearchResult([], "Telephony is not configured.");

        var query = "?VoiceEnabled=true&PageSize=20";
        if (!string.IsNullOrWhiteSpace(areaCode)) query += "&AreaCode=" + Uri.EscapeDataString(areaCode.Trim());

        var (ok, body, _, detail) = await SendAsync(creds, new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}Accounts/{creds.Sid}/AvailablePhoneNumbers/{Uri.EscapeDataString(country)}/Local.json{query}"), ct).ConfigureAwait(false);
        return ok ? new NumberSearchResult(ParseAvailable(body)) : new NumberSearchResult([], detail);
    }

    public async Task<NumberPurchase> BuyAsync(string? accountSid, string phoneNumber, VoiceWebhooks webhooks, string friendlyName,
        CancellationToken ct = default)
    {
        if (await CredentialsAsync().ConfigureAwait(false) is not { } creds) return NumberPurchase.Failed("Telephony is not configured.");

        var name = friendlyName.Trim();
        var form = new List<KeyValuePair<string, string>>
        {
            new("PhoneNumber", phoneNumber.Trim()),
            new("VoiceUrl", webhooks.Incoming),
            new("VoiceMethod", "POST"),
            new("VoiceFallbackUrl", webhooks.Fallback),
            new("VoiceFallbackMethod", "POST"),
            new("StatusCallback", webhooks.Status),
            new("StatusCallbackMethod", "POST"),
            new("FriendlyName", name.Length > 64 ? name[..64] : name),
        };

        var (ok, body, code, detail) = await SendAsync(creds, new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}Accounts/{Account(accountSid, creds)}/IncomingPhoneNumbers.json") { Content = new FormUrlEncodedContent(form) }, ct).ConfigureAwait(false);
        if (!ok) return code == NumberUnavailableCode ? NumberPurchase.Taken(detail) : NumberPurchase.Failed(detail);

        using var doc = JsonDocument.Parse(body);
        var sid = Str(doc.RootElement, "sid");
        return string.IsNullOrWhiteSpace(sid)
            ? NumberPurchase.Failed("Twilio did not return the bought number's id.")
            : NumberPurchase.Bought(sid, Str(doc.RootElement, "phone_number") ?? phoneNumber.Trim());
    }

    public async Task<string?> ReleaseAsync(string? accountSid, string numberSid, CancellationToken ct = default)
    {
        if (await CredentialsAsync().ConfigureAwait(false) is not { } creds) return "Telephony is not configured.";
        if (!IsSid(numberSid, "PN")) return $"Not a Twilio number id: {numberSid}";

        var (ok, _, code, detail) = await SendAsync(creds, new HttpRequestMessage(HttpMethod.Delete,
            $"{ApiBase}Accounts/{Account(accountSid, creds)}/IncomingPhoneNumbers/{numberSid}.json"), ct).ConfigureAwait(false);
        // 20404 = no such resource: someone released it already (in the console, say). That's the outcome we want.
        return ok || code == ResourceNotFoundCode ? null : detail;
    }

    public async Task<string?> PointAsync(string? accountSid, string numberSid, VoiceWebhooks webhooks, CancellationToken ct = default)
    {
        if (await CredentialsAsync().ConfigureAwait(false) is not { } creds) return "Telephony is not configured.";
        if (!IsSid(numberSid, "PN")) return $"Not a Twilio number id: {numberSid}";

        var form = new List<KeyValuePair<string, string>>
        {
            new("VoiceUrl", webhooks.Incoming),
            new("VoiceMethod", "POST"),
            new("VoiceFallbackUrl", webhooks.Fallback),
            new("VoiceFallbackMethod", "POST"),
            new("StatusCallback", webhooks.Status),
            new("StatusCallbackMethod", "POST"),
        };
        var (ok, _, _, detail) = await SendAsync(creds, new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}Accounts/{Account(accountSid, creds)}/IncomingPhoneNumbers/{numberSid}.json") { Content = new FormUrlEncodedContent(form) },
            ct).ConfigureAwait(false);
        return ok ? null : detail;
    }

    public async Task<(int? Minutes, string? Error)> MinutesUsedAsync(string? accountSid, string number, DateTimeOffset since,
        CancellationToken ct = default)
    {
        if (await CredentialsAsync().ConfigureAwait(false) is not { } creds) return (null, "Telephony is not configured.");

        // Calls answered on the line (To) and the ones the salon places or transfers from it (From).
        var start = since.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var minutes = 0;
        foreach (var side in new[] { "To", "From" })
        {
            string? page = $"/2010-04-01/Accounts/{Account(accountSid, creds)}/Calls.json?{side}={Uri.EscapeDataString(number)}" +
                           $"&StartTime%3E={start}&PageSize=1000";
            for (var pages = 0; page is not null && pages < 50; pages++)
            {
                var (ok, body, _, detail) = await SendAsync(creds, new HttpRequestMessage(HttpMethod.Get, "https://api.twilio.com" + page), ct)
                    .ConfigureAwait(false);
                if (!ok) return (null, detail);
                var (sum, next) = ParseCallMinutes(body);
                minutes += sum;
                page = next;
            }
        }
        return (minutes, null);
    }

    // The platform's credentials act on its subaccounts too; the URL names which account owns the resource.
    private static string Account(string? accountSid, (string Sid, string Token) creds) =>
        IsSid(accountSid, "AC") ? accountSid! : creds.Sid;

    private static bool IsSid(string? sid, string prefix) =>
        sid is { Length: 34 } && sid.StartsWith(prefix, StringComparison.Ordinal) && sid[2..].All(char.IsAsciiLetterOrDigit);

    private static async Task<(string Sid, string Token)?> CredentialsAsync()
    {
        var sid = await KeyValueTable.ResolveAsync("TWILIO_ACCOUNT_SID", "Twilio", "AccountSid").ConfigureAwait(false);
        var token = await KeyValueTable.ResolveAsync("TWILIO_AUTH_TOKEN", "Twilio", "AuthToken").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(token) ? null : (sid, token);
    }

    private async Task<(bool Ok, string Body, string? Code, string Detail)> SendAsync(
        (string Sid, string Token) creds, HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{creds.Sid}:{creds.Token}")));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Phone line: Twilio unreachable.");
            return (false, "", null, "Could not reach the phone company. Please try again.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (true, body, null, "");

            var (code, detail) = ParseError(body);
            _logger.LogWarning("Phone line: Twilio answered {Status} {Code}: {Detail}", (int)response.StatusCode, code, detail);
            return (false, body, code, detail);
        }
    }

    // --- Parsing (pure) ---

    public static IReadOnlyList<AvailableNumber> ParseAvailable(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("available_phone_numbers", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return list.EnumerateArray()
            .Select(n => new AvailableNumber(Str(n, "phone_number") ?? "", Str(n, "locality") ?? "", Str(n, "region") ?? ""))
            .Where(n => n.Number.Length > 0)
            .ToList();
    }

    /// <summary>A call-log page: billable minutes (each call's seconds rounded up) and the next page's path, if any.</summary>
    public static (int Minutes, string? NextPage) ParseCallMinutes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var minutes = 0;
        if (root.TryGetProperty("calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            foreach (var call in calls.EnumerateArray())
                if (int.TryParse(Str(call, "duration"), out var seconds) && seconds > 0)
                    minutes += (seconds + 59) / 60;
        var next = Str(root, "next_page_uri");
        return (minutes, string.IsNullOrWhiteSpace(next) ? null : next);
    }

    public static (string? Code, string Detail) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, "The phone company returned an empty answer.");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c)
                ? (c.ValueKind == JsonValueKind.Number ? c.GetInt32().ToString() : c.GetString())
                : null;
            var message = Str(root, "message");
            return (code, string.IsNullOrWhiteSpace(message) ? body : message);
        }
        catch (JsonException)
        {
            return (null, body);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
