using System.Net.Http.Headers;
using System.Security;
using System.Text;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The few calls the site makes to Twilio's REST API on the platform account (<see cref="PhoneHookup.CredentialsAsync"/>):
/// send a text, ring a number with a spoken message, and redirect a live call (put it through to a person).
/// Each returns false, logged, when it didn't go — never throws.
/// </summary>
internal static class TwilioApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>One SMS from MapleKiosk's number, recorded for the SMS inbox (/admin/sms) as <paramref name="kind"/>.</summary>
    public static async Task<bool> SendSmsAsync(string from, string to, string body, ILogger logger, CancellationToken ct,
        string kind = Services.SmsKind.Reply)
    {
        var error = await PostAsync("Messages.json", new() { ["From"] = from, ["To"] = to, ["Body"] = body }, $"text to {to}", logger, ct);
        if (Services.SmsStore.Current is { } log) await log.AddAsync(false, to, from, body, kind, error is null, error);
        return error is null;
    }

    /// <summary>Rings <paramref name="to"/> from <paramref name="from"/> and says <paramref name="twiml"/>.</summary>
    public static async Task<bool> CallAsync(string from, string to, string twiml, ILogger logger, CancellationToken ct) =>
        await PostAsync("Calls.json", new() { ["From"] = from, ["To"] = to, ["Twiml"] = twiml }, $"call to {to}", logger, ct) is null;

    /// <summary>Replaces what a live call is doing with <paramref name="twiml"/> — the media stream ends, the TwiML runs.</summary>
    public static async Task<bool> RedirectAsync(string callSid, string twiml, ILogger logger, CancellationToken ct) =>
        await PostAsync($"Calls/{Uri.EscapeDataString(callSid)}.json", new() { ["Twiml"] = twiml }, $"redirect of call {callSid}", logger, ct) is null;

    /// <summary>
    /// Put the caller through to <paramref name="person"/>; if nobody answers in 25 s, apologise (French and
    /// English: MapleKiosk is in Quebec) and hang up. The person sees MapleKiosk's number, so they know it's a transfer.
    /// </summary>
    public static string DialTwiml(string person, string line) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response>"
        + $"<Dial callerId=\"{SecurityElement.Escape(line)}\" timeout=\"25\">{SecurityElement.Escape(person)}</Dial>"
        + "<Say language=\"fr-CA\">Désolé, personne n'est disponible pour le moment. Nous vous rappellerons très bientôt.</Say>"
        + "<Say language=\"en-US\">Sorry, nobody is available right now. We will call you back very soon. Goodbye.</Say>"
        + "<Hangup/></Response>";

    /// <summary>A message read out twice to the person who picks up, then hang up.</summary>
    public static string AnnounceTwiml(string text) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response>"
        + $"<Say language=\"en-US\">{SecurityElement.Escape(text)}</Say><Pause length=\"1\"/>"
        + $"<Say language=\"en-US\">Again: {SecurityElement.Escape(text)}</Say><Hangup/></Response>";

    /// <summary>Null when Twilio accepted it; otherwise why not (also logged).</summary>
    private static async Task<string?> PostAsync(string path, Dictionary<string, string> form, string what, ILogger logger,
        CancellationToken ct)
    {
        if (await PhoneHookup.CredentialsAsync() is not { } creds)
        {
            logger.LogError("Twilio: no {What} — no Twilio/AccountSid or Twilio/AuthToken in the keyvalue table.", what);
            return "Twilio account not set";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(creds.Sid)}/{path}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{creds.Sid}:{creds.Token}")));

        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Twilio: {What} accepted.", what);
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Twilio: refused the {What} ({Status}): {Body}", what, (int)response.StatusCode, body);
            return $"Twilio refused it ({(int)response.StatusCode}): {(body.Length <= 300 ? body : body[..300])}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Twilio: the {What} failed.", what);
            return ex.Message;
        }
    }
}
