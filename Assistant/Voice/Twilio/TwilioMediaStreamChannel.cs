// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Twilio/TwilioMediaStreamChannel.cs
// Kept as close to the original as possible (it is tuned); only the namespace was changed.

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// A phone call as the relay sees it: Twilio's Media Streams protocol on one side, the relay's PCM on the
/// other.
///
/// <para>Twilio speaks JSON text frames only. Inbound: <c>connected</c>, <c>start</c> (carrying the stream
/// id everything outbound must quote), then <c>media</c> every 20 ms with 160 bytes of 8 kHz μ-law in
/// base64, and finally <c>stop</c>. Outbound: <c>media</c> in the same shape, <c>clear</c> to flush what
/// the caller has not heard yet, and <c>mark</c>, which Twilio echoes back once everything queued before
/// it has been played — how this channel knows the goodbye was actually heard before hanging up.</para>
///
/// <para>The relay's control frames are mostly for a screen a caller does not have, so all but two are
/// dropped here: <c>clear</c> becomes Twilio's clear (barge-in), and <c>ending</c> becomes a mark whose
/// echo closes the line.</para>
/// </summary>
internal sealed class TwilioMediaStreamChannel : IVoiceClientChannel
{
    /// <summary>Twilio's rate, both directions, not negotiable.</summary>
    public const int PhoneSampleRate = 8000;

    private const string GoodbyeMark = "goodbye";

    /// <summary>A media frame is ~300 bytes of JSON; anything near this is not Twilio.</summary>
    private const int MaxFrameBytes = 16 * 1024;
    private const int ReceiveBufferBytes = 4 * 1024;

    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly TwilioStreamStart _start;
    private readonly DateTime _openedAt = DateTime.UtcNow;

    private PcmResampler? _inbound;    // 8 kHz → the session's input rate
    private PcmResampler? _outbound;   // the session's output rate → 8 kHz

    /// <summary>
    /// Stream timestamp (ms since the stream began) before which inbound media is discarded. The session
    /// takes a moment to open, and Twilio was sending the whole time; feeding a long backlog to the
    /// provider in one burst would have it transcribing the caller's breathing from before anything was
    /// said. What is KEPT is the last <see cref="PreRollMs"/> before ready: the greeting is played by the
    /// line before the stream is even dialled, so a caller who answers it promptly is already talking
    /// while the session opens — and those first words are the request.
    /// </summary>
    private long _discardBeforeMs;

    /// <summary>How much of what the caller said before the session was ready is still forwarded.</summary>
    private const int PreRollMs = 2000;

    /// <summary>The name prefix on timing marks, so a probe's echo is never mistaken for the goodbye's.</summary>
    private const string ProbeMarkPrefix = "probe:";

    // Inbound timing. Every media frame carries Twilio's own stream clock; comparing it with ours says
    // whether Twilio→server delivery is keeping up. The absolute lag includes a fixed offset (the stream
    // clock started before this object existed), so only its SPREAD over the call means anything: a
    // steady offset is fine, a growing one is frames arriving late — the caller's words reaching the
    // model after they were said.
    private long _inboundFrames;
    private long _inboundLagMinMs = long.MaxValue;
    private long _inboundLagMaxMs = long.MinValue;

    private TwilioMediaStreamChannel(WebSocket socket, ILogger logger, TwilioStreamStart start)
    {
        _socket = socket;
        _logger = logger;
        _start = start;
    }

    /// <summary>
    /// Reads Twilio's opening handshake off a freshly accepted socket and returns a channel ready for the
    /// relay, or null when what arrived was not a Media Stream (no <c>start</c> within the timeout, a
    /// close, or garbage).
    /// </summary>
    public static async Task<TwilioMediaStreamChannel?> OpenAsync(
        WebSocket socket, ILogger logger, CancellationToken ct)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));

        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        try
        {
            while (true)
            {
                var frame = await ReadTextFrameAsync(socket, buffer, logger, handshake.Token).ConfigureAwait(false);
                if (frame is null) return null;

                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                var kind = root.TryGetProperty("event", out var e) ? e.GetString() : null;

                if (kind == "start" && root.TryGetProperty("start", out var start))
                {
                    var streamSid = start.TryGetProperty("streamSid", out var sid) ? sid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(streamSid))
                        streamSid = root.TryGetProperty("streamSid", out var top) ? top.GetString() : null;

                    if (string.IsNullOrWhiteSpace(streamSid))
                    {
                        logger.LogWarning("Voice/phone: Twilio's start event carried no streamSid.");
                        return null;
                    }

                    var callSid = start.TryGetProperty("callSid", out var c) ? c.GetString() : null;
                    return new TwilioMediaStreamChannel(socket, logger, new TwilioStreamStart(streamSid, callSid));
                }

                if (kind == "stop") return null;
                // "connected" and anything else before start: keep waiting.
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Voice/phone: no start event within the handshake timeout.");
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Voice/phone: the socket did not speak Media Streams.");
            return null;
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Voice/phone: socket closed during the handshake.");
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public string Label => "twilio";
    public string StreamSid => _start.StreamSid;
    public string? CallSid => _start.CallSid;

    public ValueTask ReadyAsync(VoiceClientReady ready, CancellationToken ct)
    {
        if (ready.Input.Channels != 1 || ready.Output.Channels != 1)
            throw new NotSupportedException("The phone line carries mono audio only.");

        _inbound = PcmResampler.For(PhoneSampleRate, ready.Input.SampleRate);
        _outbound = PcmResampler.For(ready.Output.SampleRate, PhoneSampleRate);

        // Everything Twilio sent while the provider was warming up is stale, except the last moment of it.
        _discardBeforeMs = Math.Max(0, (long)(DateTime.UtcNow - _openedAt).TotalMilliseconds - PreRollMs);

        _logger.LogInformation(
            "Voice/phone: stream {StreamSid} ready — 8 kHz μ-law ↔ {In} Hz in / {Out} Hz out ({Provider}).",
            _start.StreamSid, ready.Input.SampleRate, ready.Output.SampleRate, ready.ProviderName);

        return ValueTask.CompletedTask;
    }

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var at8k = _outbound is null ? pcm.Span : _outbound.Resample(pcm.Span);

        // A provider chunk too short to yield one 8 kHz sample, or an empty one: nothing to say, and Twilio
        // has no use for a media frame with no audio in it.
        if (at8k.Length < 2) return ValueTask.CompletedTask;

        var payload = Convert.ToBase64String(G711.EncodeMuLaw(at8k));

        return SendJsonAsync(new
        {
            @event = "media",
            streamSid = _start.StreamSid,
            media = new { payload },
        }, ct);
    }

    public ValueTask SendControlAsync(string type, string json, CancellationToken ct) => type switch
    {
        // Barge-in: drop whatever the caller has not heard yet, so the agent stops mid-word as it should.
        "clear" => SendJsonAsync(new { @event = "clear", streamSid = _start.StreamSid }, ct),

        // A timing probe: Twilio echoes the mark once everything queued before it has been PLAYED, and
        // that echo (see ReceiveAsync) is how the relay learns the phone network's own delay.
        "probe" => SendJsonAsync(new
        {
            @event = "mark",
            streamSid = _start.StreamSid,
            mark = new { name = ProbeMarkPrefix + ProbeName(json) },
        }, ct),

        // The goodbye has been queued. Ask Twilio to tell us when it has been PLAYED; the echo closes the
        // line. Closing now would cut the farewell off — Twilio buffers several seconds ahead.
        "ending" => SendJsonAsync(new
        {
            @event = "mark",
            streamSid = _start.StreamSid,
            mark = new { name = GoodbyeMark },
        }, ct),

        // Transcripts, states, tool progress, pings: for a screen. A caller has none.
        _ => ValueTask.CompletedTask,
    };

    public async ValueTask<VoiceClientInbound> ReceiveAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        try
        {
            while (true)
            {
                var frame = await ReadTextFrameAsync(_socket, buffer, _logger, ct).ConfigureAwait(false);
                if (frame is null) return VoiceClientInbound.Closed;

                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                var kind = root.TryGetProperty("event", out var e) ? e.GetString() : null;

                switch (kind)
                {
                    case "media":
                        if (!root.TryGetProperty("media", out var media)) continue;

                        // Only the caller's own track; on a bidirectional stream Twilio can also echo ours.
                        if (media.TryGetProperty("track", out var track) && track.GetString() is { } t && t != "inbound")
                            continue;

                        if (media.TryGetProperty("timestamp", out var ts)
                            && ts.ValueKind == JsonValueKind.String
                            && long.TryParse(ts.GetString(), out var stamp))
                        {
                            NoteInboundLag(stamp);
                            if (stamp < _discardBeforeMs) continue;
                        }

                        var payload = media.TryGetProperty("payload", out var p) ? p.GetString() : null;
                        if (string.IsNullOrEmpty(payload)) continue;

                        var pcm8k = G711.DecodeMuLaw(Convert.FromBase64String(payload));
                        return VoiceClientInbound.Of(_inbound is null ? pcm8k : _inbound.Resample(pcm8k));

                    case "mark":
                        if (!root.TryGetProperty("mark", out var mark)
                            || !mark.TryGetProperty("name", out var name)
                            || name.GetString() is not { } markName)
                            continue;

                        // Our goodbye mark came back: the caller has heard the last of it. Hang up.
                        if (markName == GoodbyeMark)
                        {
                            _logger.LogInformation("Voice/phone: goodbye played; hanging up {StreamSid}.", _start.StreamSid);
                            return VoiceClientInbound.Closed;
                        }

                        // A timing probe came back: that point in the agent's speech has now been heard.
                        if (markName.StartsWith(ProbeMarkPrefix, StringComparison.Ordinal))
                            return VoiceClientInbound.PlayedMark(markName[ProbeMarkPrefix.Length..]);

                        continue;

                    case "stop":
                        _logger.LogInformation("Voice/phone: caller hung up ({StreamSid}).", _start.StreamSid);
                        return VoiceClientInbound.Closed;

                    default:
                        // connected, dtmf, and whatever Twilio adds next.
                        continue;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Voice/phone: unparseable frame from Twilio; closing.");
            return VoiceClientInbound.Closed;
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Voice/phone: media payload was not base64; closing.");
            return VoiceClientInbound.Closed;
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Voice/phone: socket closed while reading.");
            return VoiceClientInbound.Closed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask CloseAsync()
    {
        if (_inboundFrames > 0)
        {
            _logger.LogInformation(
                "Voice/phone: inbound timing for {StreamSid} — {Frames} frames; Twilio→server lag spread {Spread}ms "
                + "(a spread under ~200ms means the caller's audio reached us as it was said).",
                _start.StreamSid, _inboundFrames, _inboundLagMaxMs - _inboundLagMinMs);
        }

        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "call ended", CancellationToken.None)
                         .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice/phone: could not close the Twilio socket cleanly.");
        }
    }

    /// <summary>One complete text frame, or null on close / oversize / a binary frame Twilio never sends.</summary>
    private static async Task<string?> ReadTextFrameAsync(
        WebSocket socket, byte[] buffer, ILogger logger, CancellationToken ct)
    {
        using var frame = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
            {
                logger.LogWarning("Voice/phone: unexpected binary frame; closing.");
                return null;
            }

            frame.Write(buffer, 0, result.Count);

            if (frame.Length > MaxFrameBytes)
            {
                logger.LogWarning("Voice/phone: oversized frame; closing.");
                return null;
            }
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
    }

    private void NoteInboundLag(long streamStampMs)
    {
        var lag = (long)(DateTime.UtcNow - _openedAt).TotalMilliseconds - streamStampMs;
        _inboundFrames++;
        if (lag < _inboundLagMinMs) _inboundLagMinMs = lag;
        if (lag > _inboundLagMaxMs) _inboundLagMaxMs = lag;
    }

    /// <summary>The probe's name from the relay's frame; tolerant, since a mark with any name still echoes.</summary>
    private static string ProbeName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "?"
                : "?";
        }
        catch (JsonException)
        {
            return "?";
        }
    }

    private ValueTask SendJsonAsync<T>(T message, CancellationToken ct) =>
        _socket.SendAsync(
            new ReadOnlyMemory<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, endOfMessage: true, ct);
}

/// <summary>What Twilio's <c>start</c> event told us.</summary>
internal sealed record TwilioStreamStart(string StreamSid, string? CallSid);
