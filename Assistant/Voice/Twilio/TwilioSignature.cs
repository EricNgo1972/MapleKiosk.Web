// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Twilio/TwilioSignature.cs
// Kept as close to the original as possible (it is tuned); only the namespace was changed.

using System.Security.Cryptography;
using System.Text;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Proves a webhook came from Twilio: every request carries <c>X-Twilio-Signature</c>, an HMAC-SHA1 over
/// the exact URL Twilio called plus the POST form, keyed with the account's auth token. Anyone else on
/// the internet can POST a form that looks like an incoming call; only Twilio can sign it.
///
/// <para>The algorithm is Twilio's own (docs: "Validating requests"): take the full URL as requested,
/// append each form field's name and value, sorted by name, hash, base64. Reproduced here rather than
/// pulled in as an SDK — it is one HMAC, and this codebase already talks to Twilio's REST API by hand.</para>
/// </summary>
internal static class TwilioSignature
{
    public const string Header = "X-Twilio-Signature";

    /// <summary>The signature Twilio would send for this URL and form.</summary>
    public static string Compute(string authToken, string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var data = new StringBuilder(url);

        foreach (var (key, value) in form.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            data.Append(key).Append(value);

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data.ToString())));
    }

    /// <summary>
    /// True when <paramref name="signature"/> matches. Tries the URL both as reconstructed and with the
    /// default port stripped/added, because a proxy in front of the app can leave <c>Host</c> with or
    /// without <c>:443</c> while Twilio signed whichever form it dialled.
    /// </summary>
    public static bool IsValid(
        string authToken, string url, IEnumerable<KeyValuePair<string, string>> form, string? signature)
    {
        if (string.IsNullOrWhiteSpace(authToken) || string.IsNullOrWhiteSpace(signature))
            return false;

        var fields = form.ToList();

        foreach (var candidate in UrlVariants(url))
        {
            if (FixedTimeEquals(Compute(authToken, candidate, fields), signature))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> UrlVariants(string url)
    {
        yield return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield break;

        // Twilio signs the URL exactly as it dialled it. Behind a proxy the reconstructed Host can carry
        // an explicit default port that Twilio never sent, or lack one it did — offer the other spelling.
        // Uri normalises the port away, so check the raw text for whether one was written.
        var authorityEnd = url.IndexOf('/', uri.Scheme.Length + 3);
        var authority = authorityEnd < 0 ? url[(uri.Scheme.Length + 3)..] : url[(uri.Scheme.Length + 3)..authorityEnd];
        var hadExplicitPort = authority.Contains(':');

        yield return hadExplicitPort
            ? $"{uri.Scheme}://{uri.Host}{uri.PathAndQuery}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.PathAndQuery}";
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual.Trim());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
