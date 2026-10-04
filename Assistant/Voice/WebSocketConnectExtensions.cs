// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/WebSocketConnectExtensions.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using System.Net;
using System.Net.WebSockets;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Turns a failed WebSocket handshake into something a human can act on.
///
/// <para>.NET reports a rejected upgrade as <c>"The server returned status code '401' when status code
/// '101' was expected"</c> — accurate, and useless to the operator staring at a kiosk. Since this message
/// travels all the way to the screen, it needs to say which credential was refused.</para>
///
/// <para>Generic transport knowledge, not vendor knowledge, so sharing it between providers costs them no
/// independence.</para>
/// </summary>
internal static class WebSocketConnectExtensions
{
    public static async Task ConnectWithClearErrorsAsync(
        this ClientWebSocket socket, Uri uri, string providerName, CancellationToken ct)
    {
        // Required for HttpStatusCode to be populated on a failed handshake.
        socket.Options.CollectHttpResponseDetails = true;

        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new InvalidOperationException(Describe(providerName, socket.HttpStatusCode, ex), ex);
        }
    }

    private static string Describe(string provider, HttpStatusCode status, WebSocketException ex) => status switch
    {
        HttpStatusCode.Unauthorized =>
            $"{provider} rejected the API key (401 Unauthorized). Check the key on the active voice profile.",
        HttpStatusCode.Forbidden =>
            $"{provider} refused the request (403 Forbidden) — the key may lack access to the voice agent product.",
        HttpStatusCode.NotFound =>
            $"{provider} returned 404 for the voice endpoint — the service URL may have changed.",
        HttpStatusCode.TooManyRequests =>
            $"{provider} is rate limiting this account (429). Try again shortly.",
        0 => $"Could not reach {provider}: {ex.Message}",
        _ => $"{provider} refused the connection ({(int)status} {status}).",
    };
}
