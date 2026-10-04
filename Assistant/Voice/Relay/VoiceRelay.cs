// Vendored from the monorepo: MapleKiosk/Infrastructure/VoiceAgent/SPC.Infrastructure.VoiceAgent/Relay/VoiceRelay.cs
// Kept as close to the original as possible (it is tuned); only namespaces and dependencies the site
// lacks were changed. See Assistant/Voice/WebsiteVoiceEndpoints.cs for what was stripped.

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MapleKiosk.Web.Assistant.Voice;

/// <summary>
/// Pumps one client channel against one provider session: guest PCM upstream, agent PCM and control
/// frames downstream.
///
/// <para>The client is an <see cref="IVoiceClientChannel"/> — a kiosk browser or a phone call — and the
/// relay never learns which. Everything below is about the CONVERSATION: silence, barge-in, the goodbye,
/// the record. How bytes are framed for a given far end is the channel's business alone.</para>
/// </summary>
internal sealed class VoiceRelay
{
    /// <summary>
    /// Generated speech arrives faster than realtime — a six-second reply can land in about two — so this
    /// has to hold a whole utterance comfortably. Bounded only as a runaway guard, NOT as a pacing
    /// mechanism: dropping a chunk here deletes the middle of a sentence, which is never the right
    /// trade for speech (it is for a live video stream, which is where the smaller figure came from).
    /// </summary>
    private const int AudioQueueDepth = 2048;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    private readonly IVoiceClientChannel _channel;
    private readonly ILogger _logger;
    // Site: the platform's IAuditLog (ChatAudit trail of tool calls) is not vendored — this site's voice
    // agent has no tools to audit. The transcript still goes to IVoiceConversationStore.
    private readonly IVoiceConversationStore? _conversations;

    // The two product seams. The relay knows there IS a persona and an outcome reader; it never learns
    // whether this head sells manicures or support tickets.
    private readonly IVoicePersona _persona;
    private readonly IVoiceOutcomeReader _outcomes;

    /// <summary>
    /// The conversation as it happens, kept SERVER-side. The browser has its own copy for display, but
    /// this is the one that gets recorded — a tab closed mid-booking, a dropped tunnel or a kiosk losing
    /// power are exactly the sessions somebody later asks about.
    /// </summary>
    private readonly List<VoiceTranscriptLine> _transcript = new();
    // UTC throughout: these are instants (session length, a de-dupe window, transcript order),
    // and reading them off the host's zone made a call's stamps disagree with every other time in
    // the suite the moment the host and the shop were in different zones.
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private int _toolCalls;

    /// <summary>
    /// What this conversation booked. Captured as each tool answers rather than reconstructed afterwards:
    /// the result is in hand exactly once, and a session that dies before teardown still has recorded the
    /// booking it already made.
    /// </summary>
    private readonly List<string> _bookingIds = new();

    // The booking the agent gathers as the call proceeds (name/phone/service/time), merged across its
    // note-details calls, so a call that ends before the guest confirms still leaves a usable request (TK).
    private readonly object _draftLock = new();
    private VoiceBookingDraft _draft = VoiceBookingDraft.Empty;

    /// <summary>Correlates every audit row for this conversation. Ours, not the provider's — the audit must
    /// survive a provider that never gives us an id, and must not change if we switch vendor.</summary>
    private readonly string _conversationId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// What the guest was heard to say, recorded against whatever tool call follows it. An audit row
    /// saying a booking was made, without what was said to cause it, answers nothing.
    ///
    /// <para>Accumulated, not replaced: transcripts arrive as deltas, so keeping only the newest message
    /// would file a booking under "just listing" — the tail of a sentence — instead of the sentence.
    /// Reset when the agent finishes a turn, so each tool call is attributed to the request that
    /// actually preceded it.</para>
    /// </summary>
    private readonly StringBuilder _heard = new();

    /// <summary>Bounded so a long conversation cannot grow this without limit.</summary>
    private const int MaxHeardChars = 600;

    private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(AudioQueueDepth)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    /// <summary>Control frames, typed so a channel with no screen can pick the ones that matter to it.</summary>
    private readonly Channel<(string Type, string Json)> _control =
        Channel.CreateUnbounded<(string Type, string Json)>(new UnboundedChannelOptions { SingleReader = true });

    private int _droppedChunks;

    /// <summary>
    /// When anything was last SAID — by either party — as opposed to when a frame last arrived. The
    /// microphone streams continuously whether or not anyone is in front of it, so inbound traffic says
    /// only that the browser is alive; it cannot tell a conversation from an empty lobby.
    /// </summary>
    private long _lastSpokenTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// When the GUEST was last heard, specifically. The close is decided on this and not on
    /// <see cref="_lastSpokenTicks"/>, because the question the relay asks — "are you still there?" — is
    /// itself speech, and answering the timer with the agent's own voice would keep an empty lobby's
    /// session open indefinitely.
    /// </summary>
    private long _lastHeardGuestTicks = DateTime.UtcNow.Ticks;

    /// <summary>When "are you still there?" was asked, or 0 while nobody has been asked anything.</summary>
    private long _askedIfPresentTicks;

    /// <summary>
    /// How long the guest has to answer that question. Short on purpose: it is asked only once the
    /// operator's own idle timeout has already elapsed in silence, so this is the second silence, not the
    /// first — and somebody who is actually there answers a direct question within a few seconds.
    /// </summary>
    private static readonly TimeSpan AnswerGrace = TimeSpan.FromSeconds(10);

    /// <summary>When the agent last emitted audio — how the hang-up watchdog knows the goodbye is over.</summary>
    private long _lastAgentAudioTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// When the guest last cut the agent off, or 0 once either party has spoken since. A barge-in tells
    /// the model to stop and listen; if what it heard was the handset's echo or a cough, nobody says
    /// anything next — the model is waiting for words that never come, the guest is waiting for the
    /// reply that was thrown away. On a phone that standoff ends with a hang-up (28 s, live call,
    /// 2026-09-04), well inside the idle timeout that would otherwise have asked whether anyone was there.
    /// </summary>
    private long _interruptedTicks;

    /// <summary>
    /// How long an interrupted reply may stay unresumed before the model is told to pick it up. Phone
    /// only: a kiosk barge-in with nobody following up is usually someone walking past the microphone,
    /// and a kiosk that then addresses the empty lobby is worse than one that waits.
    /// </summary>
    private static readonly TimeSpan ResumeAfterInterruption = TimeSpan.FromSeconds(6);

    /// <summary>How often the stall watchdog looks — fine enough that 6 s means 6 s, not 6–26.</summary>
    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>The model has called <c>end_call</c>; what is left is letting the goodbye finish.</summary>
    private volatile bool _endRequested;

    /// <summary>An agent turn has completed since then — usually the goodbye itself.</summary>
    private volatile bool _farewellSpoken;

    /// <summary>Guards against a model that calls <c>end_call</c> twice; only the first hangs up.</summary>
    private int _hangingUp;

    /// <summary>Silence after the farewell that means the agent has genuinely stopped talking.</summary>
    private static readonly TimeSpan FarewellQuiet = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// How long to wait for a goodbye that may never come. A model can call <c>end_call</c> AFTER it has
    /// already said goodbye, in which case there is no further turn to wait for — this is what stops the
    /// watchdog waiting for one.
    /// </summary>
    private static readonly TimeSpan FarewellSilentGrace = TimeSpan.FromSeconds(3);

    /// <summary>Absolute cap on the goodbye, however talkative the model turns out to be.</summary>
    private static readonly TimeSpan FarewellCap = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the browser gets to finish PLAYING the goodbye after it has all arrived. Generated speech
    /// lands faster than realtime, so at the moment the provider stops sending there can still be several
    /// seconds queued in the player. The browser normally closes the socket itself the instant playback
    /// drains; this is only the backstop for one that never answers.
    /// </summary>
    private static readonly TimeSpan DrainBackstop = TimeSpan.FromSeconds(20);

    // Cold-start diagnostics: a guest saying "hello" and waiting is either the browser not sending, or
    // the provider not answering. These separate the two instead of leaving it to guesswork.
    private readonly System.Diagnostics.Stopwatch _sinceReady = new();
    private bool _sawInboundAudio;
    private bool _sawFirstReply;
    private long _inboundBytes;

    /// <summary>Where each reply's wait went — provider, tools, this server, or the client. See the class.</summary>
    private readonly VoiceTurnClock _turns;

    /// <param name="persona">Who this product's agent is. Never null: an unconfigured head gets
    /// <see cref="DefaultVoicePersona"/> rather than an agent with no identity.</param>
    /// <param name="outcomes">Reads created records out of tool results. Never null: a head that records
    /// nothing gets <see cref="NullVoiceOutcomeReader"/>, and its conversations are still transcribed.</param>
    public VoiceRelay(
        IVoiceClientChannel channel,
        ILogger logger,
        IVoiceConversationStore? conversations,
        IVoicePersona persona,
        IVoiceOutcomeReader outcomes)
    {
        _channel = channel;
        _logger = logger;
        _conversations = conversations;
        _persona = persona;
        _outcomes = outcomes;
        _turns = new VoiceTurnClock(logger);
    }

    public async Task RunAsync(
        IVoiceAgentServiceFactory serviceFactory,
        IVoiceToolBrokerFactory brokerFactory,
        VoiceProfileInfo? profile,
        VoiceToolAudience audience,
        VoiceSessionContext sessionContext,
        string languageTag,
        string businessName,
        string? grounding,
        CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, profile?.MaxSessionSeconds ?? 600)));
        var ct = cts.Token;

        // Startup is timed because the guest experiences it as dead air: the microphone does not open
        // until "ready" reaches the browser, so every millisecond here is a word they said that nobody
        // heard. Logged per step so a slow session names its own culprit instead of being guessed at.
        var startedAt = System.Diagnostics.Stopwatch.StartNew();

        // The context goes to the broker as well as the persona: the prompt tells the agent who it is
        // talking to, the broker makes the same facts reachable from the tools it calls.
        await using var broker = await brokerFactory.CreateAsync(sessionContext, ct).ConfigureAwait(false);
        var brokerMs = startedAt.ElapsedMilliseconds;

        var tools = await broker.ListToolsAsync(audience, ct).ConfigureAwait(false);
        var toolsMs = startedAt.ElapsedMilliseconds - brokerMs;

        var service = await serviceFactory.CreateAsync(profile, ct).ConfigureAwait(false);

        // The agent's own way of saying the guest is done — see VoiceSessionTools. Offered only where the
        // provider can call tools at all, and offered LAST so it never displaces a product tool in a
        // provider that truncates the list.
        var sessionTools = service.SupportsTools
            ? tools.Append(VoiceSessionTools.EndCallDefinition).ToArray()
            : Array.Empty<VoiceToolDefinition>();

        // A channel that greeted the guest itself (the phone line plays the greeting the instant the
        // call connects) says so in the context; the model is then told the words were said and not
        // asked to say them again. Otherwise the model opens, with the same resolution the line uses.
        var spokenGreeting = sessionContext.SpokenGreeting;

        var sessionOptions = new VoiceSessionOptions(
            SystemPrompt: VoicePrompt.Compose(
                _persona.ForAudience(audience),
                _persona.ForContext(audience, sessionContext),
                profile?.PromptAddendum,
                businessName,
                canEndCall: service.SupportsTools,
                spokenGreeting: spokenGreeting,
                grounding: grounding),
            // The operator's own words win; blank falls through to how this product answers. Null only
            // when the product supplies nothing either, and null is what means "wait to be spoken to".
            Greeting: spokenGreeting is null
                ? VoicePrompt.ResolveGreeting(profile?.Greeting, _persona, audience, businessName)
                : null,
            LanguageTag: languageTag,
            PreferredInput: VoiceAudioFormat.Pcm16Mono(profile?.InputSampleRate ?? 16000),
            PreferredOutput: VoiceAudioFormat.Pcm16Mono(profile?.OutputSampleRate ?? 24000),
            Tools: sessionTools,
            // The room the guest is in decides the provider's turn-taking — see VoiceAcoustics.
            Acoustics: sessionContext.Acoustics);

        IVoiceAgentSession session;
        try
        {
            session = await service.StartSessionAsync(sessionOptions, BuildHandlers(broker, cts), ct)
                                   .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The channel is already open, so say WHY. From a silently-closed connection, "no API key
            // configured" and "the network is down" look identical — to the guest AND to whoever is
            // trying to fix it.
            _logger.LogError(ex, "Voice: could not start a {Provider} session.", service.ProviderName);
            await _channel.SendControlAsync("error", Json(new { type = "error", message = ex.Message }), CancellationToken.None)
                .ConfigureAwait(false);
            await CloseQuietlyAsync().ConfigureAwait(false);
            return;
        }

        await using var _session = session;

        _logger.LogInformation(
            "Voice: ready in {Total}ms (mcp connect {Broker}ms, tools {Tools}ms, {Provider} session {Session}ms).",
            startedAt.ElapsedMilliseconds, brokerMs, toolsMs, service.ProviderName,
            startedAt.ElapsedMilliseconds - brokerMs - toolsMs);

        // The client cannot capture or play a single sample until it knows the rates, and only the live
        // session knows them — a provider may impose its own regardless of what we asked for.
        //
        // Sent DIRECTLY rather than queued, because a session can start talking the moment it opens (a
        // greeting), and those frames are already sitting in the control queue by the time we get here.
        // Queueing "ready" behind them would put audio on the wire before the client knew its format.
        // Safe to write here: the writer loop below is the only other sender and has not started yet.
        await _channel.ReadyAsync(new VoiceClientReady(
            session.InputFormat,
            session.OutputFormat,
            service.ProviderName,
            sessionTools.Select(t => t.Name).ToArray()), ct).ConfigureAwait(false);

        _sinceReady.Start();
        _turns.Start(service.ProviderName, _channel.Label, session.OutputFormat);

        var writer = WriteLoopAsync(ct);
        var reader = ReadLoopAsync(session, cts);
        var keepAlive = KeepAliveLoopAsync(session, profile, audience, cts);
        var stall = sessionContext.Acoustics == VoiceAcoustics.Telephone
            ? MindTheStallAsync(session, cts.Token)
            : Task.CompletedTask;
        MarkSpoken();

        try
        {
            // The stall watchdog is deliberately not in here: it only ever prompts, it never decides the
            // conversation is over, so its finishing (or being absent on a kiosk) must not end the call.
            await Task.WhenAny(writer, reader, keepAlive).ConfigureAwait(false);
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(Quiet(writer), Quiet(reader), Quiet(keepAlive), Quiet(stall)).ConfigureAwait(false);

            if (_droppedChunks > 0)
                _logger.LogWarning("Voice: dropped {Count} audio chunk(s) — the client could not keep up.", _droppedChunks);

            _turns.Summarize();

            await RecordConversationAsync(profile, service.ProviderName, audience, languageTag, sessionContext.Channel)
                .ConfigureAwait(false);

            await CloseQuietlyAsync().ConfigureAwait(false);
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Provider → browser ──────────────────────────────────────────────────────────────────────────

    private VoiceSessionHandlers BuildHandlers(IVoiceToolBroker broker, CancellationTokenSource cts) => new()
    {
        OnAudio = (pcm, _) =>
        {
            Volatile.Write(ref _lastAgentAudioTicks, DateTime.UtcNow.Ticks);
            Volatile.Write(ref _interruptedTicks, 0);   // the agent is talking again — no stall to mind
            _turns.AgentAudioArrived(pcm.Length);

            // The channel drops the OLDEST on overflow, so a struggling client hears current audio rather
            // than an ever-growing delay. TryWrite therefore never fails here; a false means it dropped.
            if (!_audio.Writer.TryWrite(pcm))
                Interlocked.Increment(ref _droppedChunks);
            return ValueTask.CompletedTask;
        },

        OnStateChanged = (state, _) =>
        {
            Enqueue("state", new { type = "state", value = state.ToString().ToLowerInvariant() });
            return ValueTask.CompletedTask;
        },

        OnUserStartedSpeaking = _cancellation =>
        {
            MarkSpoken(byGuest: true);

            // Drain BEFORE telling the client to flush. Reversed, we would flush the client and then
            // immediately re-feed it the stale audio still sitting in this queue — which is exactly what
            // "the agent talked over me for another second" looks like.
            long discarded = 0;
            while (_audio.Reader.TryRead(out var stale)) discarded += stale.Length;
            _turns.Interrupted(discarded);
            Volatile.Write(ref _interruptedTicks, DateTime.UtcNow.Ticks);
            Enqueue("clear", new { type = "clear" });
            return ValueTask.CompletedTask;
        },

        OnAgentAudioDone = _ =>
        {
            // The turn is over: the next thing the guest says is a new request, so it should not be
            // filed against the one just handled.
            _heard.Clear();

            // If the goodbye was the turn that just ended, the hang-up watchdog can stop waiting for it.
            if (_endRequested) _farewellSpoken = true;

            _turns.TurnComplete();
            Enqueue("agentAudioDone", new { type = "agentAudioDone" });
            return ValueTask.CompletedTask;
        },

        OnTranscript = (role, text, _) =>
        {
            MarkSpoken(byGuest: role == VoiceTranscriptRole.User);
            if (role == VoiceTranscriptRole.User)
            {
                _turns.GuestHeard();
                // Words followed the barge-in, so it was a real one: the model now has a turn to answer
                // and needs no reminder. Only an interruption that nothing follows is a stall.
                if (!string.IsNullOrWhiteSpace(text)) Volatile.Write(ref _interruptedTicks, 0);
            }

            if (!_sawFirstReply)
            {
                _sawFirstReply = true;
                _logger.LogInformation(
                    "Voice: first provider transcript after {Ms}ms ({Bytes} bytes of mic audio sent).",
                    _sinceReady.ElapsedMilliseconds, _inboundBytes);
            }

            if (role == VoiceTranscriptRole.User && !string.IsNullOrEmpty(text) && _heard.Length < MaxHeardChars)
            {
                if (_heard.Length > 0 && !char.IsWhiteSpace(_heard[^1]) && !char.IsWhiteSpace(text[0])
                    && !char.IsPunctuation(text[0]))
                    _heard.Append(' ');

                _heard.Append(text);
            }

            RecordLine(role.ToString().ToLowerInvariant(), text);

            Enqueue("transcript", new { type = "transcript", role = role.ToString().ToLowerInvariant(), text });
            return ValueTask.CompletedTask;
        },

        OnToolCall = async (call, toolCt) =>
        {
            MarkSpoken();

            // The one tool the relay answers itself. It reaches no broker and writes no record — it is the
            // model telling US something, not asking the salon for anything.
            if (VoiceSessionTools.IsEndCall(call.Name))
            {
                BeginHangUp(call, cts);
                return new VoiceToolResult(call.CallId, call.Name, VoiceSessionTools.EndCallAck);
            }

            Interlocked.Increment(ref _toolCalls);
            _turns.ToolRequested(call.CallId, call.Name);
            Enqueue("tool", new { type = "tool", name = call.Name, status = "running" });

            VoiceToolResult result;
            try
            {
                result = await broker.InvokeAsync(call, toolCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (toolCt.IsCancellationRequested)
            {
                // The model withdrew the call (the guest interrupted). Nothing to record: whatever the
                // tool was going to do, it did not — and if it had already, the broker would have returned.
                _turns.ToolCancelled(call.CallId);
                Enqueue("tool", new { type = "tool", name = call.Name, status = "cancelled" });
                throw;
            }

            _turns.ToolAnswered(call.CallId, result.IsError);
            Enqueue("tool", new { type = "tool", name = call.Name, status = result.IsError ? "error" : "done" });

            RecordBooking(call, result);
            CaptureDraft(call);
            return result;
        },

        OnError = (message, ex, _) =>
        {
            _logger.LogWarning(ex, "Voice: provider error — {Message}", message);
            Enqueue("error", new { type = "error", message });
            return ValueTask.CompletedTask;
        },

        OnClosed = (reason, _) =>
        {
            Enqueue("closed", new { type = "closed", reason });
            _control.Writer.TryComplete();
            return ValueTask.CompletedTask;
        },
    };

    /// <summary>
    /// The ONLY writer to the channel — a socket send is not thread-safe, and audio, control frames and
    /// keep-alives all want to write. Control frames win every round: they are tiny, and losing
    /// a barge-in "clear" behind a queue of audio defeats the interruption it was announcing.
    /// </summary>
    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (_control.Reader.TryRead(out var message))
                    await _channel.SendControlAsync(message.Type, message.Json, ct).ConfigureAwait(false);

                if (_audio.Reader.TryRead(out var pcm))
                {
                    await _channel.SendAudioAsync(pcm, ct).ConfigureAwait(false);

                    // The first chunk of a reply is followed by a timing probe: a channel that can say
                    // when it was PLAYED answers it, and that answer is the last hop's measurement.
                    // Sent from here, not enqueued, so it sits right behind the chunk it measures.
                    if (_turns.AgentAudioSent(pcm.Length) is { } probe)
                        await _channel.SendControlAsync("probe", Json(new { type = "probe", name = probe }), ct)
                                      .ConfigureAwait(false);
                    continue;
                }

                var control = _control.Reader.WaitToReadAsync(ct).AsTask();
                var audio = _audio.Reader.WaitToReadAsync(ct).AsTask();
                var ready = await Task.WhenAny(control, audio).ConfigureAwait(false);

                // A completed channel yields false — nothing more will ever arrive on it.
                if (ready == control && !await control.ConfigureAwait(false) && !_audio.Reader.TryPeek(out _))
                    return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: client channel closed while writing.");
        }
    }

    // ── Client → provider ───────────────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(IVoiceAgentSession session, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var inbound = await _channel.ReceiveAsync(cts.Token).ConfigureAwait(false);

                if (inbound.Kind == VoiceClientInboundKind.Played)
                {
                    _turns.ProbeEchoed(inbound.Mark ?? "");
                    continue;
                }

                if (inbound.Kind != VoiceClientInboundKind.Audio)
                    return;

                // First guest audio off the client. If this is late, the client is the problem (context
                // suspended, worklet not connected); if it is prompt but the reply is not, the wait is the
                // provider's.
                if (!_sawInboundAudio)
                {
                    _sawInboundAudio = true;
                    _logger.LogInformation("Voice: first mic audio after {Ms}ms.", _sinceReady.ElapsedMilliseconds);
                }

                _inboundBytes += inbound.Audio.Length;

                // Timed: a send that blocks is the provider socket pushing back, and the guest's words
                // queueing here would reach the model late — a cause of silence this server would own.
                var sendStarted = Environment.TickCount64;
                await session.SendAudioAsync(inbound.Audio, cts.Token).ConfigureAwait(false);
                _turns.UpstreamSent(Environment.TickCount64 - sendStarted);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice: client channel closed while reading.");
        }
        finally
        {
            cts.Cancel();
        }
    }

    /// <summary>
    /// Two jobs on one timer: keep the provider socket alive, and close a conversation nobody is having.
    /// The ping also keeps the browser socket off Cloudflare's idle-connection guillotine, which is the
    /// same hazard the Blazor circuit already tunes for.
    ///
    /// <para>"Nobody is having" is measured in SPEECH, not in traffic. The kiosk streams the microphone
    /// continuously, so inbound frames never stop while the tab is open — measured against those, this
    /// watchdog could not fire at all, and a guest who wandered off left a paid provider session running
    /// until the session cap. What is watched instead is <see cref="_lastSpokenTicks"/>: the last time
    /// either party actually said something.</para>
    /// </summary>
    private async Task KeepAliveLoopAsync(
        IVoiceAgentSession session, VoiceProfileInfo? profile, VoiceToolAudience audience,
        CancellationTokenSource cts)
    {
        var idleLimit = TimeSpan.FromSeconds(Math.Max(10, profile?.IdleTimeoutSeconds ?? 45));

        // Resolved once, before any silence: the product's words, and blank is a legitimate answer meaning
        // "just time out". See IVoicePersona.StillThereFor.
        var stillThere = NullIfBlank(_persona.StillThereFor(audience));

        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, cts.Token).ConfigureAwait(false);

                // A conversation that is on its way out is not an idle one — let the goodbye finish.
                if (!_endRequested && !await MindTheSilenceAsync(session, idleLimit, stillThere, cts)
                        .ConfigureAwait(false))
                    return;

                Enqueue("ping", new { type = "ping" });
                await session.KeepAliveAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Watches for a reply that was cut off and never resumed. Once <see cref="ResumeAfterInterruption"/>
    /// has passed with no agent audio and no words from the guest, the model is told to pick up where it
    /// left off — once per interruption; if that prompt is itself interrupted, the next stall is a new one.
    /// The prompt is best-effort like everything else the relay asks of a session: a provider that cannot
    /// take it leaves the idle timeout to do what it always did.
    /// </summary>
    private async Task MindTheStallAsync(IVoiceAgentSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(StallCheckInterval, ct).ConfigureAwait(false);

                var interrupted = Volatile.Read(ref _interruptedTicks);
                if (interrupted == 0 || _endRequested) continue;

                var stalled = DateTime.UtcNow - AsUtc(interrupted);
                if (stalled < ResumeAfterInterruption) continue;

                // Cleared BEFORE prompting, so a prompt that takes a while (or throws) is not repeated
                // every second — a second barge-in re-arms it.
                Volatile.Write(ref _interruptedTicks, 0);
                _logger.LogInformation(
                    "Voice/turn: interrupted reply not resumed after {Stalled:0.0}s of silence — asking the model to pick it up.",
                    stalled.TotalSeconds);

                try
                {
                    await session.PromptAsync(
                        "The guest cut you off and then said nothing — it may have been noise on the line. "
                        + "In one short sentence, pick up where you left off: repeat the question you were asking "
                        + "or the answer you were giving, then stop and listen.", ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Voice: could not ask the model to resume an interrupted reply.");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Decides what a silence means. Nobody is hung up on without being asked first: the agent asks whether
    /// the guest is still there, and only an unanswered question ends the conversation. A kiosk that quietly
    /// dropped the line while someone was reading their calendar would be indistinguishable, to them, from
    /// one that crashed.
    /// </summary>
    /// <param name="stillThere">The question to ask, or null where the product asks nothing.</param>
    /// <returns>False once the conversation has been closed and the loop should stop.</returns>
    private async Task<bool> MindTheSilenceAsync(
        IVoiceAgentSession session, TimeSpan idleLimit, string? stillThere, CancellationTokenSource cts)
    {
        var now = DateTime.UtcNow;
        var asked = Volatile.Read(ref _askedIfPresentTicks);

        if (asked != 0)
        {
            // Answered — they were there all along, and the conversation carries on as normal.
            if (Volatile.Read(ref _lastHeardGuestTicks) > asked)
            {
                Volatile.Write(ref _askedIfPresentTicks, 0);
                return true;
            }

            if (now - AsUtc(asked) < AnswerGrace) return true;

            _logger.LogInformation("Voice: nobody answered — closing the conversation.");
            cts.Cancel();
            return false;
        }

        var quiet = now - AsUtc(Volatile.Read(ref _lastSpokenTicks));
        var guestQuiet = now - AsUtc(Volatile.Read(ref _lastHeardGuestTicks));

        // Both, so that a long answer from the agent is never interrupted to ask whether anyone is
        // listening — mid-sentence is exactly when they are.
        if (quiet <= idleLimit || guestQuiet <= idleLimit) return true;

        if (stillThere is null)
        {
            _logger.LogInformation(
                "Voice: closing a conversation nobody has spoken in for {Quiet:0}s.", quiet.TotalSeconds);
            cts.Cancel();
            return false;
        }

        _logger.LogInformation(
            "Voice: {Quiet:0}s of silence — asking whether the guest is still there.", quiet.TotalSeconds);

        // Recorded BEFORE asking, so a provider that blocks or throws still starts the clock. Otherwise a
        // speech call that never returns would leave the session open on a silence it had already noticed.
        Volatile.Write(ref _askedIfPresentTicks, now.Ticks);

        try
        {
            await session.SpeakAsync(stillThere, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Best-effort by contract. A provider that will not say it does not get to keep an abandoned
            // session running either — the grace above expires regardless and the conversation closes.
            _logger.LogDebug(ex, "Voice: could not ask whether the guest is still there.");
        }

        return true;
    }

    private static DateTime AsUtc(long ticks) => new(ticks, DateTimeKind.Utc);

    /// <summary>
    /// Notes that the conversation is alive — somebody just spoke, or a tool was just called.
    /// </summary>
    /// <param name="byGuest">
    /// True only for the guest's own voice. What separates a conversation from an agent talking to an empty
    /// lobby, and what tells the "are you still there?" question that it was answered.
    /// </param>
    private void MarkSpoken(bool byGuest = false)
    {
        var now = DateTime.UtcNow.Ticks;

        Volatile.Write(ref _lastSpokenTicks, now);
        if (byGuest) Volatile.Write(ref _lastHeardGuestTicks, now);
    }

    // ── Hanging up ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The model has decided the guest is finished. Nothing is cut off here: the tool answers first, the
    /// agent says its goodbye, and only then does the line close.
    /// </summary>
    private void BeginHangUp(VoiceToolCall call, CancellationTokenSource cts)
    {
        // Treated as fresh agent activity so the watchdog below waits at least one interval, even when
        // end_call lands in a lull — otherwise "the agent has been quiet" is already true before the
        // goodbye has been spoken, and we would hang up on it.
        Volatile.Write(ref _lastAgentAudioTicks, DateTime.UtcNow.Ticks);
        _endRequested = true;

        _logger.LogInformation(
            "Voice: conversation {Id} is ending — {Reason}.",
            _conversationId, VoiceSessionTools.ReadReason(call.ArgumentsJson) ?? "the guest is done");

        // A model that repeats the call must not start a second watchdog racing the first.
        if (Interlocked.Exchange(ref _hangingUp, 1) != 0) return;

        _ = HangUpAsync(cts);
    }

    /// <summary>
    /// Waits for the goodbye, then tells the browser this was the last of it.
    ///
    /// <para>The socket is NOT closed here. Closing it the moment the provider stops sending would cut the
    /// farewell off mid-word, because the browser is still playing audio that arrived faster than it can be
    /// heard. So the relay says "ending" and lets the page close when its playback drains, with
    /// <see cref="DrainBackstop"/> covering a page that never does.</para>
    /// </summary>
    private async Task HangUpAsync(CancellationTokenSource cts)
    {
        try
        {
            var deadline = DateTime.UtcNow + FarewellCap;

            while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(150, cts.Token).ConfigureAwait(false);

                var quiet = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastAgentAudioTicks), DateTimeKind.Utc);

                // Two conditions, checked together on every pass so that speech arriving late simply
                // restarts the wait.
                //
                // The agent has stopped talking: either the goodbye has been said and gone quiet, or
                // there was never going to be one — a model that called end_call after already saying it.
                //
                // AND the queue to the browser is empty. Control frames deliberately overtake audio in
                // the writer loop — that is what makes barge-in immediate — so an "ending" sent while the
                // goodbye is still queued would reach the page BEFORE the words it announces, and the page
                // would close on a player that had not been handed them yet.
                if (quiet >= (_farewellSpoken ? FarewellQuiet : FarewellSilentGrace) && _audio.Reader.Count == 0)
                    break;
            }

            Enqueue("ending", new { type = "ending" });

            await Task.Delay(DrainBackstop, cts.Token).ConfigureAwait(false);

            _logger.LogDebug("Voice: the page did not close after the goodbye; closing from here.");
            cts.Cancel();
        }
        catch (OperationCanceledException) { }
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Notes a booking the conversation just made, so the recorded session says what came of it.
    ///
    /// <para>Never allowed to fail the call: an appointment that exists must not be reported to the guest
    /// as failed because bookkeeping threw.</para>
    /// </summary>
    private void RecordBooking(VoiceToolCall call, VoiceToolResult result)
    {
        if (result.IsError) return;

        try
        {
            var bookingId = _outcomes.ReadRecordId(call.Name, result.Content);
            if (bookingId is null) return;

            lock (_bookingIds)
            {
                // A model that repeats a call it already made must not double-list the same appointment.
                if (!_bookingIds.Contains(bookingId, StringComparer.OrdinalIgnoreCase))
                    _bookingIds.Add(bookingId);
            }

            _logger.LogInformation(
                "Voice: conversation {Id} booked {BookingId}.", _conversationId, bookingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice: could not read the booking id from the {Tool} result.", call.Name);
        }
    }

    /// <summary>
    /// Merges the guest's booking details from a note-details call into the running draft (TK). Which tool
    /// carries the draft, and how to read it, is the head's business — the relay asks <see cref="_outcomes"/>,
    /// exactly as it does for booking ids — so this stays product-agnostic. Best-effort and swallowing: a
    /// draft is a convenience, never worth failing a call the guest already saw succeed.
    /// </summary>
    private void CaptureDraft(VoiceToolCall call)
    {
        try
        {
            var noted = _outcomes.ReadDraft(call.Name, call.ArgumentsJson);
            if (noted is null) return;
            lock (_draftLock) _draft = _draft.MergedWith(noted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice: could not read the booking draft from the {Tool} call.", call.Name);
        }
    }

    /// <summary>
    /// Adds a transcript delta to the recorded conversation, merging into the speaker's open line — the
    /// same rule the page uses. Stored unmerged, this would be a list of fragments ("No problem", "at
    /// all.") that reads as gibberish months later when somebody is checking what was agreed.
    /// </summary>
    private void RecordLine(string role, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_transcript)
        {
            var last = _transcript.Count > 0 ? _transcript[^1] : null;

            if (last is not null && last.Role == role && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(30))
            {
                var joined = last.Text;
                if (joined.Length > 0 && !char.IsWhiteSpace(joined[^1])
                    && !char.IsWhiteSpace(text[0]) && !char.IsPunctuation(text[0]))
                    joined += " ";

                _transcript[^1] = last with { Text = joined + text };
                return;
            }

            _transcript.Add(new VoiceTranscriptLine(role, text.TrimStart(), DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Records the conversation. Called from teardown, so it runs whether the guest tapped End, walked
    /// away and timed out, or the connection dropped — never only on the tidy path.
    /// </summary>
    private async Task RecordConversationAsync(
        VoiceProfileInfo? profile, string providerName, VoiceToolAudience audience, string language,
        ConversationChannel channel)
    {
        if (_conversations is null) return;

        List<VoiceTranscriptLine> lines;
        lock (_transcript) lines = _transcript.ToList();

        List<string> bookingIds;
        lock (_bookingIds) bookingIds = _bookingIds.ToList();

        VoiceBookingDraft draft;
        lock (_draftLock) draft = _draft;

        try
        {
            await _conversations.SaveAsync(new VoiceConversationRecord(
                ConversationId: _conversationId,
                ProfileName: profile?.Name ?? string.Empty,
                Provider: providerName,
                Audience: audience.ToString(),
                Language: language,
                StartedAt: _startedAt,
                EndedAt: DateTime.UtcNow,
                ToolCalls: Volatile.Read(ref _toolCalls),
                Lines: lines,
                BookingIds: bookingIds,
                Draft: draft.IsEmpty ? null : draft,
                Channel: channel), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Losing the record must never look like a failed conversation to the guest.
            _logger.LogError(ex, "Voice: could not record conversation {Id}.", _conversationId);
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";

    private void Enqueue(string type, object payload) => _control.Writer.TryWrite((type, Json(payload)));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static Task Quiet(Task task) => task.ContinueWith(
        _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private Task CloseQuietlyAsync() => _channel.CloseAsync().AsTask();
}
