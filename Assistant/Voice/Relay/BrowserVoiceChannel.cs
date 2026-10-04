// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Relay/BrowserVoiceChannel.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// The kiosk page's side of the relay: one raw WebSocket, binary frames are PCM in both directions, text
/// frames are JSON control messages. This is the ORIGINAL wire protocol, unchanged — the seam was cut
/// beneath it so a phone line could share the relay, not to alter what the browser sees.
///
/// <para>Nothing vendor-specific crosses this socket — no vendor message types, no vendor names — which is
/// what keeps the browser code free of the lock-in the server side absorbs.</para>
/// </summary>
internal sealed class BrowserVoiceChannel : IVoiceClientChannel
{
    /// <summary>Reject absurd frames rather than allocating whatever a hostile client claims to be sending.</summary>
    private const int MaxTextFrameBytes = 4 * 1024;
    private const int MaxBinaryFrameBytes = 64 * 1024;
    private const int ReceiveBufferBytes = 8 * 1024;

    private readonly WebSocket _socket;
    private readonly ILogger _logger;

    public BrowserVoiceChannel(WebSocket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
    }

    public string Label => "browser";

    /// <summary>
    /// The browser cannot capture or play a single sample until it knows the rates, and only the live
    /// session knows them — a provider may impose its own regardless of what we asked for.
    /// </summary>
    public ValueTask ReadyAsync(VoiceClientReady ready, CancellationToken ct) =>
        SendTextAsync(JsonSerializer.Serialize(new
        {
            type = "ready",
            inputRate = ready.Input.SampleRate,
            outputRate = ready.Output.SampleRate,
            channels = ready.Output.Channels,
            provider = ready.ProviderName,
            tools = ready.Tools,
        }), ct);

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) =>
        _socket.SendAsync(pcm, WebSocketMessageType.Binary, endOfMessage: true, ct);

    public ValueTask SendControlAsync(string type, string json, CancellationToken ct) => SendTextAsync(json, ct);

    public async ValueTask<VoiceClientInbound> ReceiveAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        try
        {
            while (true)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return VoiceClientInbound.Closed;

                    frame.Write(buffer, 0, result.Count);

                    var limit = result.MessageType == WebSocketMessageType.Text
                        ? MaxTextFrameBytes
                        : MaxBinaryFrameBytes;

                    if (frame.Length > limit)
                    {
                        _logger.LogWarning("Voice: client sent an oversized {Kind} frame; closing.", result.MessageType);
                        return VoiceClientInbound.Closed;
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                    return VoiceClientInbound.Of(frame.ToArray());

                if (IsStop(frame))
                    return VoiceClientInbound.Stop;

                // Any other text frame is a control message the relay has no use for; keep listening.
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Voice: client socket closed while reading.");
            return VoiceClientInbound.Closed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool IsStop(MemoryStream frame)
    {
        try
        {
            using var document = JsonDocument.Parse(frame.ToArray());
            return document.RootElement.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                   && type.GetString() == "stop";
        }
        catch (JsonException)
        {
            _logger.LogDebug("Voice: ignoring an unparseable control frame from the client.");
            return false;
        }
    }

    public async ValueTask CloseAsync()
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "voice session ended", CancellationToken.None)
                         .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: could not close the client socket cleanly.");
        }
    }

    private ValueTask SendTextAsync(string message, CancellationToken ct) =>
        _socket.SendAsync(new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, endOfMessage: true, ct);
}
