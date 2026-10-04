// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/GeminiLive/GeminiLiveSession.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// One Gemini Live conversation over a raw <see cref="ClientWebSocket"/>.
///
/// <para>No SDK: Google ships no C# client for the Live API, and the protocol is a handful of JSON
/// messages over one socket. Owning it keeps every Gemini-ism inside this file — the same place it would
/// have been anyway — and means nothing above <see cref="IVoiceAgentSession"/> can tell which vendor is
/// talking.</para>
/// </summary>
internal sealed class GeminiLiveSession : IVoiceAgentSession
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>Fixed by the API, not negotiable — which is exactly why the contract reports formats per session.</summary>
    private static readonly VoiceAudioFormat ApiInput = VoiceAudioFormat.Pcm16Mono(16000);
    private static readonly VoiceAudioFormat ApiOutput = VoiceAudioFormat.Pcm16Mono(24000);

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(20);

    private readonly VoiceProfileInfo _profile;
    private readonly VoiceSessionOptions _options;
    private readonly VoiceSessionHandlers _handlers;
    private readonly ILogger _logger;
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _setupComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task _readLoop = Task.CompletedTask;
    private bool _speaking;

    /// <summary>
    /// Tool calls the model is waiting on, by call id, so a <c>toolCallCancellation</c> can stop the work
    /// rather than merely be logged. Left running, a cancelled <c>book_appointment</c> still books — and
    /// the model, having cancelled it, calls it again: two tickets for one guest. Seen on a live line.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingCalls = new(StringComparer.Ordinal);

    public GeminiLiveSession(
        VoiceProfileInfo profile, VoiceSessionOptions options, VoiceSessionHandlers handlers, ILogger logger)
    {
        _profile = profile;
        _options = options;
        _handlers = handlers;
        _logger = logger;
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public VoiceAudioFormat InputFormat => ApiInput;
    public VoiceAudioFormat OutputFormat => ApiOutput;
    public bool IsConnected => _socket.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken ct)
    {
        // The key goes in the query string: the Live API has no header-based auth for the socket.
        var uri = new Uri($"{Endpoint}?key={Uri.EscapeDataString(_profile.ApiKey)}");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(SetupTimeout);

        await _socket.ConnectWithClearErrorsAsync(uri, "Gemini Live", connectCts.Token).ConfigureAwait(false);
        await SendAsync(BuildSetup(), connectCts.Token).ConfigureAwait(false);

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), CancellationToken.None);

        // Don't report the session as open until the model has accepted the configuration — otherwise a
        // rejected model id or a bad key surfaces as silence a few seconds into the conversation.
        var completed = await Task.WhenAny(
            _setupComplete.Task, Task.Delay(SetupTimeout, connectCts.Token)).ConfigureAwait(false);

        if (completed != _setupComplete.Task)
            throw new TimeoutException("Gemini Live did not acknowledge the session setup within 20s.");

        await _setupComplete.Task.ConfigureAwait(false);
        _logger.LogInformation("Gemini Live session {Session} ready ({Model}).",
            SessionId, GeminiLiveVoiceAgentService.ModelFor(_profile));

        // Sent from here, still inside connect, because the relay writes its "ready" frame only after
        // this returns — so the browser learns the audio format before the first greeting sample can
        // reach it. See the note above VoiceRelay's "ready".
        await GreetAsync(connectCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the agent take the FIRST turn, so a guest walks up to a salon that says hello rather than to
    /// a machine waiting to be spoken to.
    ///
    /// <para>Unlike Deepgram, Gemini Live has no greeting field: the only way to make the model talk before
    /// it has heard anything is to complete a turn on the guest's behalf. That turn carries an INSTRUCTION
    /// to say the greeting rather than the greeting itself — sent verbatim, the words would be on record as
    /// something the guest said, and the model would reply to its own welcome.</para>
    /// </summary>
    private Task GreetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Greeting)) return Task.CompletedTask;

        return InstructAsync(
            "Begin the conversation now — the guest has not spoken yet. Say exactly this, and nothing "
            + $"else, then stop and listen: \"{_options.Greeting!.Trim()}\"", ct);
    }

    /// <summary>
    /// One tool as Gemini Live declares it. BLOCKING, always: the model waits for the result before it speaks
    /// again. From gemini-3.8-live the API's default became NON_BLOCKING — the model talks on while the tool
    /// runs — and a voice agent that says "you're booked" before book_appointment has answered tells a guest
    /// something that may be false. BLOCKING was the older models' default, so for them nothing changes.
    /// </summary>
    internal static JsonObject Declare(VoiceToolDefinition tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        // Reshaped to Gemini's Schema subset — a stray JSON Schema keyword fails the whole setup,
        // not just this tool. See GeminiSchemaAdapter.
        ["parameters"] = GeminiSchemaAdapter.Adapt(JsonNode.Parse(tool.ParametersJson)),
        ["behavior"] = "BLOCKING",
    };

    private JsonObject BuildSetup()
    {
        var functions = new JsonArray();
        foreach (var tool in _options.Tools)
            functions.Add(Declare(tool));

        var setup = new JsonObject
        {
            ["model"] = $"models/{GeminiLiveVoiceAgentService.ModelFor(_profile)}",
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray { "AUDIO" },

                // Pinned low, and this is a correctness setting rather than a matter of taste. Left
                // unset, Live samples at its own default, and a flash model at that temperature will
                // reach for the socially expected reply — "I have added it to your cart" — instead of
                // emitting the function call that would make it true. It is a probabilistic behaviour,
                // not a guarantee, so the way to get it reliably is to stop rewarding invention. A
                // café assistant has nothing to gain from a creative sampler: it reads a menu back and
                // calls tools. Observed failure: three items narrated in Vietnamese, no call made, and
                // hold_cart then correctly refusing an empty cart.
                ["temperature"] = 0.4,
            },
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = _options.SystemPrompt } },
            },
            // Transcription is requested for BOTH directions: the kiosk shows captions, and the audit
            // trail is worthless if it records that a booking happened but not what was said to cause it.
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject(),

            // Turn-taking, tuned to the room the guest is in — see VoiceAcoustics and ActivityDetection.
            ["realtimeInputConfig"] = new JsonObject
            {
                ["automaticActivityDetection"] = ActivityDetection(_options.Acoustics),
            },
        };

        if (!string.IsNullOrWhiteSpace(_profile.VoiceName))
        {
            setup["generationConfig"]!["speechConfig"] = new JsonObject
            {
                ["voiceConfig"] = new JsonObject
                {
                    ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = _profile.VoiceName.Trim() },
                },
            };
        }

        if (functions.Count > 0)
            setup["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = functions } };

        return new JsonObject { ["setup"] = setup };
    }

    /// <summary>
    /// Gemini's voice-activity settings for one room.
    ///
    /// <para><b>Kiosk.</b> Left at the API default, the model waits for a clear ~800ms silence — which a
    /// salon floor never provides: chatter, dryers, and the microphone's own auto-gain raising the noise
    /// floor between words mean the guest is never "silent". Measured on a real kiosk, the model took
    /// 6.9 SECONDS to accept that a one-word greeting had ended. So end-of-speech is made deliberately
    /// eager, and start-of-speech sensitive so a quiet guest is heard. The stated risk of a short silence
    /// window is splitting one sentence into fragments at natural pauses; 600ms is above the ~500ms the
    /// docs warn below, and a guest at a counter says short things ("gel manicure", "two o'clock") where a
    /// prompt answer matters more than perfectly joined clauses.</para>
    ///
    /// <para><b>Telephone.</b> The opposite problem. The inbound track of a phone call is narrowband and
    /// quiet, and the loudest thing on it between the caller's sentences is the handset's echo of the
    /// AGENT's own voice. With start-of-speech on HIGH the model hears that echo as the caller barging in,
    /// abandons its reply mid-word (we flush the rest — see the relay's <c>clear</c>), and then waits for
    /// the end of a sentence nobody is saying. To the caller: the salon cut itself off and went quiet.
    /// Google's own guidance for phone audio is START_SENSITIVITY_LOW; the silence window is a touch
    /// longer than the kiosk's because a caller's pauses are not covered by lobby noise, and the padding
    /// wider because narrowband onsets are softer.</para>
    /// </summary>
    internal static JsonObject ActivityDetection(VoiceAcoustics acoustics) => acoustics switch
    {
        VoiceAcoustics.Telephone => new JsonObject
        {
            ["startOfSpeechSensitivity"] = "START_SENSITIVITY_LOW",
            ["endOfSpeechSensitivity"] = "END_SENSITIVITY_HIGH",
            ["silenceDurationMs"] = 700,
            ["prefixPaddingMs"] = 200,
        },
        _ => new JsonObject
        {
            ["startOfSpeechSensitivity"] = "START_SENSITIVITY_HIGH",
            ["endOfSpeechSensitivity"] = "END_SENSITIVITY_HIGH",
            ["silenceDurationMs"] = 600,
            // Keeps the first syllable — without padding, "hi" can lose its consonant.
            ["prefixPaddingMs"] = 120,
        },
    };

    // ── Sending ─────────────────────────────────────────────────────────────────────────────────────

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct = default) =>
        SendAsync(new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject
                {
                    ["mimeType"] = $"audio/pcm;rate={ApiInput.SampleRate}",
                    ["data"] = Convert.ToBase64String(pcm.Span),
                },
            },
        }, ct);

    /// <summary>
    /// Says these words out loud — the shared contract, honoured here by ASKING for them.
    ///
    /// <para>Gemini Live has no inject-speech primitive: the only way to make the model talk is to complete
    /// a turn on the guest's behalf, and a turn carrying the words themselves would put them on record as
    /// something the GUEST said — the model then answers its own line. So the words travel wrapped in an
    /// instruction. Deepgram, which does have the primitive, simply speaks them; callers see one contract
    /// and neither provider's quirk.</para>
    /// </summary>
    public Task SpeakAsync(string text, CancellationToken ct = default) =>
        InstructAsync(
            $"Say exactly this out loud now, and nothing else, then stop and listen: \"{text.Trim()}\"", ct);

    /// <summary>The instruction IS the turn here — see <see cref="InstructAsync"/>.</summary>
    public Task PromptAsync(string instruction, CancellationToken ct = default) =>
        InstructAsync(instruction.Trim(), ct);

    /// <summary>
    /// A turn completed on the guest's behalf, carrying an instruction the model acts on rather than
    /// answers. Private: it is how this provider is driven, not something a caller should have to know.
    /// </summary>
    private Task InstructAsync(string instruction, CancellationToken ct) =>
        SendAsync(new JsonObject
        {
            ["clientContent"] = new JsonObject
            {
                ["turns"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = instruction } },
                    },
                },
                ["turnComplete"] = true,
            },
        }, ct);

    private Task SendToolResponseAsync(VoiceToolResult result, CancellationToken ct)
    {
        // The response field is an object, so a tool's JSON string is wrapped rather than spliced —
        // splicing arbitrary tool output into the message would let a malformed result corrupt the frame.
        var payload = new JsonObject { ["result"] = result.Content };
        if (result.IsError)
            payload["error"] = true;

        return SendAsync(new JsonObject
        {
            ["toolResponse"] = new JsonObject
            {
                ["functionResponses"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = result.CallId,
                        ["name"] = result.Name,
                        ["response"] = payload,
                    },
                },
            },
        }, ct);
    }

    public Task KeepAliveAsync(CancellationToken ct = default) => Task.CompletedTask;

    private async Task SendAsync(JsonNode message, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

        // ClientWebSocket.SendAsync is not thread-safe and audio, tool results and greetings all send from
        // different tasks.
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ── Receiving ───────────────────────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];

        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var reason = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                            ? result.CloseStatus?.ToString() ?? "the connection was closed"
                            : result.CloseStatusDescription;

                        // A close BEFORE setup completes is nearly always a rejected API key or an
                        // unrecognised model id. Reporting it as such matters: left to time out, the
                        // failure reads as a network problem and sends whoever is debugging it to the
                        // wrong place entirely.
                        _setupComplete.TrySetException(
                            new InvalidOperationException($"Gemini Live rejected the session: {reason}"));

                        await RaiseClosedAsync(reason).ConfigureAwait(false);
                        return;
                    }
                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                await HandleAsync(frame.ToArray(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _setupComplete.TrySetException(ex);
            await RaiseErrorAsync("The voice service connection failed.", ex).ConfigureAwait(false);
            await RaiseClosedAsync(ex.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(byte[] payload, CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Gemini Live: unparseable frame ignored.");
            return;
        }

        if (root is not JsonObject message) return;

        if (message["setupComplete"] is not null)
        {
            _setupComplete.TrySetResult(true);
            await Raise(_handlers.OnStateChanged, VoiceTurnState.Listening).ConfigureAwait(false);
            return;
        }

        if (message["serverContent"] is JsonObject content)
        {
            await HandleServerContentAsync(content).ConfigureAwait(false);
            return;
        }

        if (message["toolCall"]?["functionCalls"] is JsonArray calls)
        {
            HandleToolCalls(calls, ct);
            return;
        }

        if (message["toolCallCancellation"] is JsonObject cancellation)
        {
            // The model gave up waiting — usually because the guest interrupted. The service will ignore
            // any result we send now, so the only thing that matters is to STOP the work: a booking that
            // completes after this lands as an appointment the model does not know it made.
            HandleToolCallCancellation(cancellation);
            return;
        }

        if (message["error"] is JsonObject error)
            await RaiseErrorAsync(error["message"]?.GetValue<string>() ?? "The voice service reported an error.", null)
                .ConfigureAwait(false);
    }

    private async Task HandleServerContentAsync(JsonObject content)
    {
        // Barge-in. The model has detected the guest talking over it and abandoned the rest of the turn;
        // everything already buffered downstream is now wrong and must be discarded, not played out.
        if (content["interrupted"]?.GetValue<bool>() == true)
        {
            _speaking = false;
            if (_handlers.OnUserStartedSpeaking is not null)
                await _handlers.OnUserStartedSpeaking(_cts.Token).ConfigureAwait(false);
            await Raise(_handlers.OnStateChanged, VoiceTurnState.Listening).ConfigureAwait(false);
            return;
        }

        if (content["inputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } heard)
            await RaiseTranscript(VoiceTranscriptRole.User, heard).ConfigureAwait(false);

        if (content["outputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } said)
            await RaiseTranscript(VoiceTranscriptRole.Agent, said).ConfigureAwait(false);

        if (content["modelTurn"]?["parts"] is JsonArray parts)
        {
            foreach (var part in parts)
            {
                var data = part?["inlineData"]?["data"]?.GetValue<string>();
                if (string.IsNullOrEmpty(data)) continue;

                if (!_speaking)
                {
                    _speaking = true;
                    await Raise(_handlers.OnStateChanged, VoiceTurnState.Speaking).ConfigureAwait(false);
                }

                if (_handlers.OnAudio is not null)
                    await _handlers.OnAudio(Convert.FromBase64String(data), _cts.Token).ConfigureAwait(false);
            }
        }

        if (content["turnComplete"]?.GetValue<bool>() == true)
        {
            _speaking = false;
            if (_handlers.OnAgentAudioDone is not null)
                await _handlers.OnAgentAudioDone(_cts.Token).ConfigureAwait(false);
            await Raise(_handlers.OnStateChanged, VoiceTurnState.Listening).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatched off the read loop deliberately: a booking round-trip takes seconds, and awaiting it here
    /// would stall inbound audio — the guest's next words, and their interruption, would both be missed.
    /// </summary>
    private void HandleToolCalls(JsonArray calls, CancellationToken ct)
    {
        foreach (var node in calls)
        {
            if (node is not JsonObject call) continue;

            var id = call["id"]?.GetValue<string>() ?? string.Empty;
            var name = call["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var args = call["args"]?.ToJsonString() ?? "{}";

            // One token per call, so a cancellation from the model stops THIS call and nothing else.
            var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (id.Length > 0) _pendingCalls[id] = callCts;

            _ = Task.Run(async () =>
            {
                var toolCall = new VoiceToolCall(id, name, args);
                VoiceToolResult result;

                try
                {
                    await Raise(_handlers.OnStateChanged, VoiceTurnState.Thinking).ConfigureAwait(false);

                    result = _handlers.OnToolCall is null
                        ? new VoiceToolResult(id, name, "No tools are available.", IsError: true)
                        : await _handlers.OnToolCall(toolCall, callCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (callCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Cancelled by the model, and stopped in time. Nothing to answer: the service has
                    // already discarded the call id.
                    _logger.LogInformation("Gemini Live: {Tool} was cancelled by the model and stopped.", name);
                    Forget(id, callCts);
                    return;
                }
                catch (Exception ex)
                {
                    result = new VoiceToolResult(id, name, $"That didn't work: {ex.Message}", IsError: true);
                }

                var cancelledLate = callCts.IsCancellationRequested && !ct.IsCancellationRequested;
                Forget(id, callCts);

                if (cancelledLate)
                {
                    // The tool had already finished when the cancellation arrived, so its effect stands
                    // (the relay recorded it). The service would ignore our reply; a WARNING because for
                    // a writing tool this is precisely the case the head must make idempotent.
                    _logger.LogWarning(
                        "Gemini Live: {Tool} was cancelled by the model AFTER it completed — its effect stands, "
                        + "the model does not know.", name);
                    return;
                }

                try
                {
                    // A call left unanswered leaves the model waiting indefinitely, which the guest
                    // experiences as the salon going silent mid-sentence. Always reply.
                    await SendToolResponseAsync(result, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Gemini Live: could not return the result of {Tool}.", name);
                }
            }, ct);
        }
    }

    private void HandleToolCallCancellation(JsonObject cancellation)
    {
        var stopped = new List<string>();
        var unknown = 0;

        if (cancellation["ids"] is JsonArray ids)
        {
            foreach (var node in ids)
            {
                var id = node?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;

                if (_pendingCalls.TryRemove(id, out var cts))
                {
                    stopped.Add(id);
                    try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
                }
                else unknown++;
            }
        }

        _logger.LogInformation(
            "Gemini Live: the model cancelled {Stopped} pending tool call(s){Unknown}.",
            stopped.Count, unknown == 0 ? "" : $" (+{unknown} already answered)");
    }

    private void Forget(string id, CancellationTokenSource callCts)
    {
        if (id.Length > 0) _pendingCalls.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, callCts));
        callCts.Dispose();
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────────────────────────

    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None)
                             .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Gemini Live: could not close the provider socket cleanly.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        try { await _readLoop.ConfigureAwait(false); } catch { /* teardown */ }
        _socket.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }

    private ValueTask Raise<T>(Func<T, CancellationToken, ValueTask>? handler, T value) =>
        handler is null ? ValueTask.CompletedTask : handler(value, _cts.Token);

    private ValueTask RaiseTranscript(VoiceTranscriptRole role, string text) =>
        _handlers.OnTranscript is null ? ValueTask.CompletedTask : _handlers.OnTranscript(role, text, _cts.Token);

    private ValueTask RaiseErrorAsync(string message, Exception? ex) =>
        _handlers.OnError is null ? ValueTask.CompletedTask : _handlers.OnError(message, ex, CancellationToken.None);

    private ValueTask RaiseClosedAsync(string? reason) =>
        _handlers.OnClosed is null ? ValueTask.CompletedTask : _handlers.OnClosed(reason, CancellationToken.None);
}
