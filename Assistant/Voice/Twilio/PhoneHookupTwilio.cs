using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>A number on the platform Twilio account, and where its voice webhook points now.</summary>
public sealed record OwnedNumber(string Sid, string Number, string FriendlyName, string VoiceUrl)
{
    public bool PointsHere => PhoneHookup.PointsHere(VoiceUrl);
    public bool IsFree => string.IsNullOrWhiteSpace(VoiceUrl);
}

/// <summary>
/// The Twilio calls /voice-phone makes: list the account's numbers, point one at this site, or let it go.
/// Raw REST like the phone-line shop's gateway; thin leaf — the page decides which number may be touched.
/// </summary>
public sealed class PhoneHookupTwilio
{
    private const string ApiBase = "https://api.twilio.com/2010-04-01/";

    private readonly HttpClient _http;
    private readonly ILogger<PhoneHookupTwilio> _logger;

    public PhoneHookupTwilio(IHttpClientFactory clients, ILogger<PhoneHookupTwilio> logger)
    {
        _http = clients.CreateClient("Twilio");
        _logger = logger;
    }

    /// <summary>The account's voice numbers, or an error saying why not.</summary>
    public async Task<(IReadOnlyList<OwnedNumber> Numbers, string? Error)> ListAsync(CancellationToken ct = default)
    {
        if (await PhoneHookup.CredentialsAsync() is not { } creds)
            return ([], "No Twilio account: set Twilio/AccountSid and Twilio/AuthToken in the keyvalue table.");

        var numbers = new List<OwnedNumber>();
        string? next = $"{ApiBase}Accounts/{creds.Sid}/IncomingPhoneNumbers.json?PageSize=100";
        while (next is not null)
        {
            var (ok, body, detail) = await SendAsync(creds, new HttpRequestMessage(HttpMethod.Get, next), ct);
            if (!ok) return (numbers, detail);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("incoming_phone_numbers", out var list))
                numbers.AddRange(list.EnumerateArray().Select(n => new OwnedNumber(
                    Str(n, "sid"), Str(n, "phone_number"), Str(n, "friendly_name"), Str(n, "voice_url"))));

            var uri = Str(doc.RootElement, "next_page_uri");
            next = uri == "" ? null : "https://api.twilio.com" + uri;
        }
        return (numbers.OrderBy(n => n.Number).ToList(), null);
    }

    /// <summary>Points the number's voice, fallback and status webhooks at this site. Null = done, else why not.</summary>
    public Task<string?> ConnectAsync(string numberSid, CancellationToken ct = default) =>
        UpdateAsync(numberSid, new()
        {
            ["VoiceUrl"] = PhoneHookup.IncomingUrl,
            ["VoiceMethod"] = "POST",
            ["VoiceFallbackUrl"] = PhoneHookup.FallbackUrl,
            ["VoiceFallbackMethod"] = "POST",
            ["StatusCallback"] = PhoneHookup.StatusUrl,
            ["StatusCallbackMethod"] = "POST",
        }, ct);

    /// <summary>Clears the webhooks, so the number stops ringing this site. Null = done, else why not.</summary>
    public Task<string?> DisconnectAsync(string numberSid, CancellationToken ct = default) =>
        UpdateAsync(numberSid, new() { ["VoiceUrl"] = "", ["VoiceFallbackUrl"] = "", ["StatusCallback"] = "" }, ct);

    private async Task<string?> UpdateAsync(string numberSid, Dictionary<string, string> fields, CancellationToken ct)
    {
        if (await PhoneHookup.CredentialsAsync() is not { } creds) return "No Twilio account is set up.";
        if (numberSid is not { Length: 34 } || !numberSid.StartsWith("PN", StringComparison.Ordinal)) return "Not a Twilio number id.";

        var (ok, _, detail) = await SendAsync(creds, new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}Accounts/{creds.Sid}/IncomingPhoneNumbers/{numberSid}.json")
        { Content = new FormUrlEncodedContent(fields) }, ct);
        return ok ? null : detail;
    }

    private async Task<(bool Ok, string Body, string Detail)> SendAsync(
        (string Sid, string Token) creds, HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{creds.Sid}:{creds.Token}")));
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) return (true, body, "");

            var detail = $"Twilio answered {(int)response.StatusCode}";
            try
            {
                using var doc = JsonDocument.Parse(body);
                var message = Str(doc.RootElement, "message");
                if (message != "") detail += ": " + message;
            }
            catch (JsonException) { }
            _logger.LogWarning("Voice/phone hookup: {Detail}", detail);
            return (false, body, detail);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Voice/phone hookup: Twilio unreachable.");
            return (false, "", "Could not reach Twilio. Please try again.");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
