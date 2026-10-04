// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Relay/VoiceTurnClock.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using Microsoft.Extensions.Logging;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Times every reply the agent gives, hop by hop, and names the slow one.
///
/// <para>"The assistant went quiet for ten seconds" is the whole complaint, and nothing in the ordinary
/// log can say WHERE those seconds went: the model composing, a tool round-trip, this server's queues, or
/// the phone network buffering. This clock is fed by the relay at each hand-off and prints one line per
/// reply (<c>Voice/turn</c>) and one per conversation (<c>Voice/turns</c>) that attribute the wait:</para>
/// <list type="bullet">
///   <item><b>google</b> (or whichever provider) — from the guest's last words (or the tool's answer) to the
///   first byte of reply audio. Split into "to decide" (before it asked for a tool) and "after the tool".</item>
///   <item><b>mcp</b> — the tool calls themselves, broker in to broker out.</item>
///   <item><b>server</b> — reply audio arriving from the provider to leaving for the client: this process's
///   own queueing.</item>
///   <item><b>twilio</b> (or whichever client) — from the first chunk leaving to the client reporting it
///   played, less that chunk's own length. Only a channel that answers probes can supply this.</item>
/// </list>
/// <para>Provider-agnostic and channel-agnostic by construction: it sees hand-offs, not vendors. Thread-safe,
/// because the hand-offs come from four different loops.</para>
/// </summary>
internal sealed class VoiceTurnClock
{
    private readonly ILogger _logger;
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();

    private string _providerLabel = "provider";
    private string _clientLabel = "client";
    private int _outputBytesPerSecond;

    // Anchors: the most recent thing that could start a reply clock.
    private long _readyAt;
    private long _lastGuestHeardAt;

    private Turn? _turn;
    private int _turnsCompleted;
    private int _probeSeq;

    // Per-conversation aggregates.
    private readonly List<long> _waited = new();
    private readonly List<long> _provider = new();
    private readonly List<long> _mcp = new();
    private readonly List<long> _server = new();
    private readonly List<long> _client = new();
    private int _bargeIns;
    private int _cancelledTools;
    private long _upstreamSendMaxMs;
    private long _upstreamSendSlow;

    public VoiceTurnClock(ILogger logger, Func<long>? nowMs = null)
    {
        _logger = logger;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>The session is open and the client knows its formats; the greeting clock starts here.</summary>
    public void Start(string providerLabel, string clientLabel, VoiceAudioFormat output)
    {
        lock (_lock)
        {
            _providerLabel = Short(providerLabel);
            _clientLabel = Short(clientLabel);
            _outputBytesPerSecond = Math.Max(1, output.BytesPerSecond);
            _readyAt = _nowMs();
            _lastGuestHeardAt = _readyAt;
        }
    }

    /// <summary>A transcript of the guest arrived — the closest thing to "the guest stopped talking".</summary>
    public void GuestHeard()
    {
        lock (_lock)
        {
            var now = _nowMs();
            _lastGuestHeardAt = now;

            // Transcription can trail the model's own reply; once audio is flowing the anchor is fixed.
            if (_turn is { FirstAudioAt: null })
                _turn.AnchorAt = now;
        }
    }

    public void ToolRequested(string callId, string name)
    {
        lock (_lock)
        {
            var turn = Open();
            var now = _nowMs();
            turn.FirstToolRequestedAt ??= now;
            turn.Tools.Add(new ToolTiming(callId, name, now));
        }
    }

    public void ToolAnswered(string callId, bool error)
    {
        lock (_lock)
        {
            if (_turn?.Tools.FirstOrDefault(t => t.CallId == callId && t.AnsweredAt is null) is not { } tool) return;
            tool.AnsweredAt = _nowMs();
            tool.Error = error;
            _turn.LastToolAnsweredAt = tool.AnsweredAt;
        }
    }

    public void ToolCancelled(string callId)
    {
        lock (_lock)
        {
            _cancelledTools++;
            if (_turn?.Tools.FirstOrDefault(t => t.CallId == callId && t.AnsweredAt is null) is { } tool)
            {
                tool.AnsweredAt = _nowMs();
                tool.Cancelled = true;
            }
        }
    }

    /// <summary>Reply audio arrived from the provider.</summary>
    public void AgentAudioArrived(int bytes)
    {
        lock (_lock)
        {
            var turn = Open();
            turn.FirstAudioAt ??= _nowMs();
            turn.AudioBytes += bytes;
        }
    }

    /// <summary>
    /// Reply audio left for the client. Returns the name of a probe to send right behind this chunk when
    /// it was the turn's first — the client's echo of it closes the last hop — or null otherwise.
    /// </summary>
    public string? AgentAudioSent(int bytes)
    {
        lock (_lock)
        {
            if (_turn is null || _turn.FirstSentAt is not null) return null;

            _turn.FirstSentAt = _nowMs();
            _turn.FirstChunkMs = bytes * 1000L / _outputBytesPerSecond;
            _turn.Probe = "t" + (++_probeSeq);
            return _turn.Probe;
        }
    }

    /// <summary>The client reports a probe reached in playback.</summary>
    public void ProbeEchoed(string name)
    {
        lock (_lock)
        {
            // The echo can land after the turn closed (a short reply); the turn is kept until the next opens.
            var turn = _turn ?? _lastClosed;
            if (turn is null || turn.Probe != name || turn.FirstSentAt is null) return;

            turn.ClientMs = Math.Max(0, _nowMs() - turn.FirstSentAt.Value - turn.FirstChunkMs);
            if (turn.Logged) LogClient(turn);
        }
    }

    /// <summary>The guest cut in; <paramref name="discardedBytes"/> of reply audio were thrown away unheard.</summary>
    public void Interrupted(long discardedBytes)
    {
        lock (_lock)
        {
            _bargeIns++;
            var turn = _turn;
            var discardedMs = discardedBytes * 1000L / _outputBytesPerSecond;

            if (turn is null || turn.FirstAudioAt is null)
            {
                // Nothing had been said yet — but a turn that was under way (a tool in flight, typically)
                // is over all the same, and gets its line.
                _logger.LogInformation("Voice/turn: barge-in before any reply audio ({Discarded}ms queued was dropped).", discardedMs);
                if (turn is not null) Close(turn, interrupted: true);
                return;
            }

            var into = _nowMs() - turn.FirstAudioAt.Value;
            _logger.LogInformation(
                "Voice/turn {N}: barge-in {Into}ms into the reply — {Discarded}ms of queued audio dropped unheard.",
                _turnsCompleted + 1, into, discardedMs);

            Close(turn, interrupted: true);
        }
    }

    /// <summary>The provider says the reply is complete.</summary>
    public void TurnComplete()
    {
        lock (_lock)
        {
            if (_turn is null) return;
            Close(_turn, interrupted: false);
        }
    }

    /// <summary>How long one upstream audio send (guest → provider) blocked. Backpressure shows up here.</summary>
    public void UpstreamSent(long elapsedMs)
    {
        lock (_lock)
        {
            if (elapsedMs > _upstreamSendMaxMs) _upstreamSendMaxMs = elapsedMs;
            if (elapsedMs > 50) _upstreamSendSlow++;
        }
    }

    /// <summary>The conversation is over: one line that says where the time went.</summary>
    public void Summarize()
    {
        lock (_lock)
        {
            if (_turn is not null) Close(_turn, interrupted: false);

            var hops = new (string Name, List<long> Values)[]
            {
                (_providerLabel, _provider), ("mcp", _mcp), ("server", _server), (_clientLabel, _client),
            };
            var slowest = hops.Where(h => h.Values.Count > 0).OrderByDescending(h => h.Values.Max())
                              .Select(h => h.Name).FirstOrDefault() ?? "none (no reply was timed)";

            _logger.LogInformation(
                "Voice/turns: {Replies} replies; the guest waited p50 {WaitP50}ms, max {WaitMax}ms — "
                + "{Provider} p50 {ProvP50}ms max {ProvMax}ms; mcp max {McpMax}ms; server max {ServerMax}ms; "
                + "{Client} max {ClientMax}ms; upstream send max {UpMax}ms ({UpSlow} slow); "
                + "{BargeIns} barge-in(s), {Cancelled} cancelled tool(s). Slowest hop: {Slowest}.",
                _waited.Count, P50(_waited), Max(_waited),
                _providerLabel, P50(_provider), Max(_provider), Max(_mcp), Max(_server),
                _clientLabel, Max(_client), _upstreamSendMaxMs, _upstreamSendSlow,
                _bargeIns, _cancelledTools, slowest);
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────────────────────────────

    private Turn? _lastClosed;

    private Turn Open()
    {
        if (_turn is not null) return _turn;

        _turn = new Turn { AnchorAt = Math.Max(_lastGuestHeardAt, _readyAt) };
        return _turn;
    }

    private void Close(Turn turn, bool interrupted)
    {
        _turnsCompleted++;
        turn.Logged = true;
        _lastClosed = turn;
        _turn = null;

        var n = _turnsCompleted;

        if (turn.FirstAudioAt is null)
        {
            _logger.LogInformation(
                "Voice/turn {N}: no reply audio{Why} — {Tools}.",
                n, interrupted ? " (interrupted)" : "", ToolsText(turn));
            return;
        }

        var waited = turn.FirstAudioAt.Value - turn.AnchorAt;
        var mcpMs = turn.Tools.Where(t => t.AnsweredAt is not null).Sum(t => t.AnsweredAt!.Value - t.RequestedAt);
        var providerMs = Math.Max(0, waited - mcpMs);
        var serverMs = turn.FirstSentAt is null ? 0 : turn.FirstSentAt.Value - turn.FirstAudioAt.Value;

        string providerText;
        if (turn.FirstToolRequestedAt is { } toolAt && turn.LastToolAnsweredAt is { } answeredAt)
        {
            var decide = Math.Max(0, toolAt - turn.AnchorAt);
            var after = Math.Max(0, turn.FirstAudioAt.Value - answeredAt);
            providerText = $"{providerMs}ms ({decide}ms to decide, {after}ms after the tool)";
        }
        else
        {
            providerText = $"{providerMs}ms";
        }

        _waited.Add(waited + serverMs);
        _provider.Add(providerMs);
        if (mcpMs > 0) _mcp.Add(mcpMs);
        _server.Add(serverMs);

        _logger.LogInformation(
            "Voice/turn {N}: the guest waited {Waited}ms{Interrupted} — {Provider} {ProviderText}, mcp {Mcp}ms, "
            + "server {Server}ms{Tools}.",
            n, waited + serverMs, interrupted ? " (then interrupted)" : "",
            _providerLabel, providerText, mcpMs, serverMs,
            turn.Tools.Count == 0 ? "" : "; tools " + ToolsText(turn));

        if (turn.ClientMs is not null) LogClient(turn);
    }

    private void LogClient(Turn turn)
    {
        if (turn.ClientMs is not { } ms) return;
        _client.Add(ms);
        _logger.LogInformation("Voice/turn: {Client} played the reply {Ms}ms after it left the server.", _clientLabel, ms);
        turn.ClientMs = null;   // reported once
    }

    private static string ToolsText(Turn turn) =>
        turn.Tools.Count == 0
            ? "no tools"
            : string.Join(", ", turn.Tools.Select(t =>
                t.Cancelled ? $"{t.Name} cancelled"
                : t.AnsweredAt is null ? $"{t.Name} unanswered"
                : $"{t.Name} {t.AnsweredAt.Value - t.RequestedAt}ms{(t.Error ? " (error)" : "")}"));

    private static long P50(List<long> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static long Max(List<long> values) => values.Count == 0 ? 0 : values.Max();

    private static string Short(string label) => label.Replace("Live", "", StringComparison.Ordinal).ToLowerInvariant();

    private sealed class Turn
    {
        public long AnchorAt;
        public long? FirstToolRequestedAt;
        public long? LastToolAnsweredAt;
        public long? FirstAudioAt;
        public long? FirstSentAt;
        public long FirstChunkMs;
        public long AudioBytes;
        public string? Probe;
        public long? ClientMs;
        public bool Logged;
        public readonly List<ToolTiming> Tools = new();
    }

    private sealed class ToolTiming
    {
        public ToolTiming(string callId, string name, long requestedAt)
        {
            CallId = callId;
            Name = name;
            RequestedAt = requestedAt;
        }

        public string CallId { get; }
        public string Name { get; }
        public long RequestedAt { get; }
        public long? AnsweredAt;
        public bool Error;
        public bool Cancelled;
    }
}
