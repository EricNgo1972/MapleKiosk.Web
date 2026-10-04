// The hidden admin voice test page (Components/Pages/VoiceTest.razor): talk to the website's assistant by
// voice over the relay at /assistant/voice/ws (Assistant/Voice/).
//
// The audio half is vendored from the monorepo — MapleKiosk/MK.Chat/SPC.Blazor.Voice/wwwroot/js/voice-agent.js
// — kept as close to it as possible (start/open/begin/deliverAudio/finishWhenHeard/finish/stop are its code,
// comments included). What changed: no session ticket (the socket rides the admin's auth cookie), no Blazor
// interop (the page is static, so `notify` calls the page's own handlers below instead of a .NET object), and
// the worklets load from js/. The page half at the bottom mirrors the kiosk page's states and its rule for
// merging transcript deltas.
//
// Deliberately vendor-agnostic: this file knows nothing about which speech provider is running. It sends
// PCM and receives PCM plus a handful of JSON control frames, all defined by our own relay — which is
// where the vendor lock-in is absorbed, and why swapping providers needs no change here.
(function () {
  // Loaded once per document; a second copy (a re-inserted <script>) would double-wire the page.
  if (window.__mkVoiceTest) return;
  window.__mkVoiceTest = true;

  const BASE = 'js/';
  const WORKLET_VERSION = '1';
  const FRAME_MS = 20;

  /**
   * How much agent speech to hold while the player is still being built.
   *
   * The agent can greet the moment the session opens, and building playback is slow — two AudioContexts,
   * two worklet modules to fetch, and the microphone to open. All of that happens AFTER the relay says
   * "ready", so the whole greeting can arrive before there is anywhere to put it. Dropped, it becomes a
   * kiosk that visibly says hello in the transcript and is silent out loud.
   *
   * A cap rather than an unbounded queue: if begin() fails or stalls, this must not grow forever. ~500
   * chunks is far more than any greeting and still bounded.
   */
  const MAX_PENDING_CHUNKS = 512;

  /**
   * How long to wait for the goodbye to finish playing before closing anyway.
   *
   * The player reports the moment it runs dry, so this is only a backstop — for a suspended context, a
   * worklet that never got the message, or a device that stopped rendering audio when the screen locked.
   * Generous, because cutting a farewell short is worse than a kiosk that lingers a few seconds.
   */
  const DRAIN_TIMEOUT_MS = 12000;

  // ── Audio + relay (voice-agent.js) ──────────────────────────────────────────────────────────────

  async function start(ui, options) {
    const handle = {
      ui: ui,
      socket: null,
      capture: null,
      playback: null,
      recorder: null,
      player: null,
      stream: null,
      closed: false,
      // The relay has said the goodbye is the last of it; we are waiting for playback to catch up.
      ending: false,
      finished: false,
      drainTimer: null,
      muted: false,
      // Agent speech that arrived before the player existed. Null once it has been handed over — see
      // deliverAudio, which uses null (not empty) to mean "play straight through from now on".
      pending: [],
    };

    try {
      // Before the socket and before any AudioContext exists. Otherwise the first thing to fail is
      // addModule on an undefined audioWorklet — a TypeError that names none of this.
      if (!canRecord()) throw new Error('insecure-context');

      // Opening the microphone is the slowest step by far — permission, device open, driver warm-up —
      // and it does NOT depend on anything the server says. Start it here, in the same user gesture
      // that permitted it, so it runs WHILE the socket connects instead of after.
      handle.micRequest = requestMicrophone();
      // Site: a session that fails before "ready" never awaits this, so its stream would stay open (and
      // the browser's recording dot on). Release it if it lands after teardown.
      handle.micRequest.then((s) => { if (handle.closed) s.getTracks().forEach((t) => t.stop()); }, () => { });

      await open(handle, options.url);
    } catch (error) {
      await notify(handle, 'OnError', String((error && error.message) || error));
      await stop(handle);
      return handle;
    }

    return handle;
  }

  function open(handle, url) {
    return new Promise((resolve, reject) => {
      let settled = false;

      const socket = new WebSocket(url);
      socket.binaryType = 'arraybuffer';
      handle.socket = socket;

      socket.onmessage = async (event) => {
        if (typeof event.data !== 'string') {
          deliverAudio(handle, event.data);
          return;
        }

        let message;
        try {
          message = JSON.parse(event.data);
        } catch {
          return;
        }

        switch (message.type) {
          case 'ready':
            try {
              await begin(handle, message);
              if (!settled) { settled = true; resolve(); }
            } catch (error) {
              if (!settled) { settled = true; reject(error); }
            }
            break;

          case 'clear':
            // Barge-in: drop everything queued, immediately. No awaiting anything first.
            // Includes anything still waiting for the player — a guest who talks over the
            // greeting must not hear the rest of it once playback finally opens.
            if (handle.pending) handle.pending.length = 0;
            if (handle.player) handle.player.port.postMessage({ flush: true });
            break;

          case 'state':
            await notify(handle, 'OnState', message.value);
            break;

          case 'transcript':
            await notify(handle, 'OnTranscript', message.role, message.text);
            break;

          case 'tool':
            await notify(handle, 'OnToolActivity', message.name, message.status);
            break;

          // End of the agent's turn. Transcripts arrive as deltas, so this is what tells the page
          // the current line is finished and the next one starts a new bubble.
          case 'agentAudioDone':
            await notify(handle, 'OnTurnEnd');
            break;

          // The conversation is over — the guest said so, and the agent has just said goodbye. The
          // relay leaves the closing to us because only this side knows when the goodbye has been
          // HEARD: it arrives faster than realtime, so there are usually still seconds of it queued.
          case 'ending':
            finishWhenHeard(handle);
            break;

          case 'error':
            await notify(handle, 'OnError', message.message);
            break;

          case 'ping':
          case 'probe':
          default:
            break;
        }
      };

      socket.onerror = () => {
        if (!settled) { settled = true; reject(new Error('Could not reach the voice service.')); }
      };

      socket.onclose = async () => {
        // No auto-reconnect. Unlike a camera feed, a conversation cannot resume where it left off —
        // silently reopening would drop the guest into a fresh session mid-sentence with no idea why.
        if (!settled) { settled = true; reject(new Error('The voice service closed the connection.')); }
        await notify(handle, 'OnClosed');
        await stop(handle);
      };
    });
  }

  async function begin(handle, ready) {
    // Constructing each context at the session's own rate makes the browser do the resampling — no
    // hand-rolled converter, and no drift between what we capture and what the provider expects.
    handle.playback = new AudioContext({ sampleRate: ready.outputRate, latencyHint: 'interactive' });
    handle.capture = new AudioContext({ sampleRate: ready.inputRate, latencyHint: 'interactive' });

    // Autoplay policy parks a fresh context in "suspended". start() is called from the tap handler, so
    // this resume is still inside the user gesture that permits it.
    await handle.playback.resume();
    await handle.capture.resume();

    await handle.playback.audioWorklet.addModule(`${BASE}pcm-player-worklet.js?v=${WORKLET_VERSION}`);
    await handle.capture.audioWorklet.addModule(`${BASE}pcm-recorder-worklet.js?v=${WORKLET_VERSION}`);

    // The requested rate is not guaranteed — a device may hand back its own. Tell the worklet BOTH rates
    // so it can resample rather than play the reply at the wrong speed and pitch.
    if (handle.playback.sampleRate !== ready.outputRate) {
      console.warn(
        `voice: playback context runs at ${handle.playback.sampleRate}Hz, provider sends ` +
        `${ready.outputRate}Hz — resampling.`);
    }

    handle.player = new AudioWorkletNode(handle.playback, 'pcm-player', {
      numberOfInputs: 0,
      outputChannelCount: [1],
      processorOptions: {
        sourceRate: ready.outputRate,
        contextRate: handle.playback.sampleRate,
      },
    });
    handle.player.connect(handle.playback.destination);

    // The only thing the player ever says back: the farewell has finished playing. See finishWhenHeard.
    handle.player.port.onmessage = (event) => {
      if (event.data && event.data.drained) void finish(handle);
    };

    // Hand over whatever the agent said while this was being built — typically the whole greeting.
    //
    // Drained HERE rather than at the end of begin(), so the greeting starts playing without waiting on
    // the microphone. Clearing `pending` first and posting in the same synchronous block is what keeps
    // the order right: nothing else can run in between, so no later chunk can overtake the backlog.
    const backlog = handle.pending;
    handle.pending = null;
    for (const pcm of backlog) {
      handle.player.port.postMessage({ pcm }, [pcm]);
    }

    // Already in flight since the tap — see start().
    handle.stream = await handle.micRequest;

    handle.recorder = new AudioWorkletNode(handle.capture, 'pcm-recorder', {
      numberOfOutputs: 0,
      processorOptions: { frameSamples: Math.round(ready.inputRate * FRAME_MS / 1000) },
    });

    handle.recorder.port.onmessage = (event) => {
      if (handle.socket && handle.socket.readyState === WebSocket.OPEN) {
        handle.socket.send(event.data);
      }
    };

    if (handle.muted) {
      handle.recorder.port.postMessage({ muted: true });
    }

    handle.capture.createMediaStreamSource(handle.stream).connect(handle.recorder);

    await notify(handle, 'OnReady', ready.provider, (ready.tools || []).join(', '));
  }

  /**
   * Agent speech, to the player if there is one and to the holding queue if there is not yet.
   *
   * `pending === null` is the steady state and means playback is open — checked BEFORE the player so that
   * a chunk arriving mid-handover still queues behind the backlog instead of jumping ahead of it.
   */
  function deliverAudio(handle, buffer) {
    if (handle.pending) {
      if (handle.pending.length < MAX_PENDING_CHUNKS) handle.pending.push(buffer);
      return;
    }

    // Transfer the buffer rather than copying it — this runs for every chunk of speech.
    if (handle.player) handle.player.port.postMessage({ pcm: buffer }, [buffer]);
  }

  /**
   * Ends the session once the guest has actually HEARD the goodbye.
   *
   * Asking the player rather than timing it: the queue holds however much of the farewell arrived ahead of
   * realtime, which depends on the reply's length and the network, so any fixed wait is either a cut-off
   * word or a kiosk sitting on a dead conversation. The timer is only there for a player that never answers.
   */
  function finishWhenHeard(handle) {
    if (handle.closed || handle.ending) return;
    handle.ending = true;

    if (!handle.player) {
      void finish(handle);
      return;
    }

    handle.player.port.postMessage({ ending: true });
    handle.drainTimer = setTimeout(() => { void finish(handle); }, DRAIN_TIMEOUT_MS);
  }

  async function finish(handle) {
    if (handle.finished) return;
    handle.finished = true;

    if (handle.drainTimer) {
      clearTimeout(handle.drainTimer);
      handle.drainTimer = null;
    }

    // Told BEFORE teardown, because stop() closes the socket and its onclose reports a bare "closed" —
    // which is what a dropped connection looks like too. A conversation that ended because the guest was
    // finished should read that way on screen, not like something that failed.
    await notify(handle, 'OnFinished');
    await stop(handle);
  }

  async function stop(handle) {
    if (!handle || handle.closed) return;
    handle.closed = true;

    if (handle.drainTimer) {
      clearTimeout(handle.drainTimer);
      handle.drainTimer = null;
    }

    try {
      if (handle.socket && handle.socket.readyState === WebSocket.OPEN) {
        handle.socket.send(JSON.stringify({ type: 'stop' }));
        handle.socket.close();
      } else if (handle.socket && handle.socket.readyState === WebSocket.CONNECTING) {
        handle.socket.close();
      }
    } catch { /* already gone */ }

    // Releasing the tracks is what turns the browser's recording indicator off. Skipping it leaves a kiosk
    // looking like it is still listening after the conversation ended.
    try { if (handle.stream) handle.stream.getTracks().forEach((t) => t.stop()); } catch { }
    try { if (handle.recorder) handle.recorder.disconnect(); } catch { }
    try { if (handle.player) handle.player.disconnect(); } catch { }
    try { if (handle.capture) await handle.capture.close(); } catch { }
    try { if (handle.playback) await handle.playback.close(); } catch { }

    handle.pending = null;
    handle.socket = null;
    handle.stream = null;
    handle.recorder = null;
    handle.player = null;
    handle.capture = null;
    handle.playback = null;
  }

  /// Can this browser record at all? Both halves of the audio path are secure-context only: served over a
  /// plain private IP (http://192.168.x.x) navigator.mediaDevices is undefined, and so is
  /// BaseAudioContext.audioWorklet. (localhost counts as secure.)
  function canRecord() {
    return !!(window.isSecureContext
      && navigator.mediaDevices
      && navigator.mediaDevices.getUserMedia
      && typeof AudioContext !== 'undefined'
      && 'audioWorklet' in AudioContext.prototype);
  }

  function requestMicrophone() {
    if (!canRecord()) {
      return Promise.reject(new Error('insecure-context'));
    }

    return navigator.mediaDevices.getUserMedia({
      audio: {
        // Echo cancellation is load-bearing, not polish: with the speaker open it is the only thing
        // stopping the agent from hearing its own voice and interrupting itself in a loop.
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
        channelCount: 1,
      },
    });
  }

  // Site: the page's handlers, where voice-agent.js invoked [JSInvokable] methods on the Blazor page.
  async function notify(handle, method, ...args) {
    const fn = handle.ui && handle.ui[method];
    if (typeof fn !== 'function') return;
    try { await fn(...args); } catch (e) { console.error('voice:', e); }
  }

  // ── The page ────────────────────────────────────────────────────────────────────────────────────

  /** How close to the bottom still counts as "following along", in pixels (voice-agent.js scrollToEnd). */
  const STICK_THRESHOLD_PX = 80;

  function wire() {
    const root = document.querySelector('[data-voice-test]');
    if (!root || root.dataset.wired === '1') return;
    root.dataset.wired = '1';

    const button = root.querySelector('[data-vt-start]');
    const stateEl = root.querySelector('[data-vt-state]');
    const log = root.querySelector('[data-vt-log]');
    const errorEl = root.querySelector('[data-vt-error]');
    const langs = Array.from(root.querySelectorAll('[data-vt-lang]'));
    const configured = root.dataset.ready === 'true';

    const LABELS = {
      idle: 'Ready', connecting: 'Connecting…', listening: 'Listening', thinking: 'Thinking…',
      speaking: 'Speaking', ended: 'Call ended',
    };

    let lang = root.dataset.lang || 'en';
    let handle = null;
    let state = 'idle';
    let finished = false;
    let lines = [];   // { role, text, closed, el }

    function setState(next) {
      state = next;
      root.dataset.state = next;
      stateEl.textContent = LABELS[next] || next;
      const live = next !== 'idle' && next !== 'ended';
      button.textContent = live ? 'Stop' : (next === 'ended' ? 'Start again' : 'Start talking');
      button.classList.toggle('is-live', live);
      // Not cancellable while connecting: start() hands back its handle only once the session is open.
      button.disabled = !configured || next === 'connecting';
      langs.forEach((b) => { b.disabled = live; });
    }

    function showError(message) {
      errorEl.hidden = !message;
      errorEl.textContent = message || '';
    }

    function stick() {
      const distance = log.scrollHeight - log.scrollTop - log.clientHeight;
      if (distance <= STICK_THRESHOLD_PX) log.scrollTop = log.scrollHeight;
    }

    // Transcripts arrive as deltas: merged into the speaker's open line, as the kiosk page does, until the
    // other party speaks or the agent's turn ends.
    function append(role, text) {
      const empty = log.querySelector('[data-vt-empty]');
      if (empty) empty.remove();

      const last = lines.length ? lines[lines.length - 1] : null;
      if (last && last.role === role && !last.closed) {
        last.text = join(last.text, text);
        last.el.querySelector('p').textContent = last.text;
      } else {
        if (last) last.closed = true;
        const el = document.createElement('div');
        el.className = 'vt-line vt-line--' + (role === 'user' ? 'me' : 'bot');
        el.innerHTML = '<small></small><p></p>';
        el.querySelector('small').textContent = role === 'user' ? 'You' : 'Assistant';
        const line = { role: role, text: text.trimStart(), closed: false, el: el };
        el.querySelector('p').textContent = line.text;
        lines.push(line);
        log.appendChild(el);
      }
      stick();
    }

    function join(a, b) {
      if (a.length && !/\s$/.test(a) && !/^[\s.,!?;:…)]/.test(b)) return a + ' ' + b;
      return a + b;
    }

    const ui = {
      OnReady: () => setState('listening'),
      OnState: (value) => {
        if (state === 'ended') return;
        if (value === 'listening' || value === 'thinking' || value === 'speaking') setState(value);
      },
      OnTranscript: (role, text) => { if (text) append(role, text); },
      OnTurnEnd: () => { lines.forEach((l) => { l.closed = true; }); },
      OnError: (message) => {
        // The first error of an attempt is the one that says why ("set the Gemini API key…"); the socket
        // closing right after it reports a generic one that must not overwrite it.
        if (!errorEl.hidden) return;
        showError(message === 'insecure-context'
          ? 'This browser cannot record here — the page must be served over HTTPS (or localhost).'
          : (message || 'Something went wrong with the voice service.'));
      },
      OnFinished: () => { finished = true; setState('ended'); },
      OnClosed: () => {
        handle = null;
        setState(lines.length || finished ? 'ended' : 'idle');
      },
    };

    async function toggle() {
      if (handle) {
        const h = handle;
        handle = null;
        await stop(h);
        setState(lines.length ? 'ended' : 'idle');
        return;
      }

      showError('');
      finished = false;
      lines = [];
      log.innerHTML = '';
      setState('connecting');

      const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
      const url = `${scheme}://${location.host}/assistant/voice/ws?lang=${encodeURIComponent(lang)}`;
      handle = await start(ui, { url: url });
      // start() already reported and tore down a session that failed to open.
      if (handle && handle.closed) { handle = null; if (state === 'connecting') setState('idle'); }
    }

    button.addEventListener('click', () => { void toggle(); });
    langs.forEach((b) => b.addEventListener('click', () => {
      if (handle) return;
      lang = b.dataset.vtLang;
      langs.forEach((x) => x.classList.toggle('is-on', x === b));
    }));

    // Leaving the page (Blazor enhanced navigation keeps the script alive) must not leave the mic open.
    active = { root: root, stop: () => { if (handle) { const h = handle; handle = null; void stop(h); } } };

    setState('idle');
    if (!canRecord()) ui.OnError('insecure-context');
  }

  let active = null;   // the wired page: { root, stop }

  function navigated() {
    if (active && !document.body.contains(active.root)) { active.stop(); active = null; }
    wire();
  }

  window.addEventListener('pagehide', () => { if (active) active.stop(); });

  wire();
  // Blazor's enhanced navigation swaps the page without reloading the script.
  if (window.Blazor && typeof window.Blazor.addEventListener === 'function') window.Blazor.addEventListener('enhancedload', navigated);
  else document.addEventListener('enhancedload', navigated);
})();
