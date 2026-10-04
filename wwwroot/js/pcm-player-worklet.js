// Vendored unchanged from the monorepo: MapleKiosk/MK.Chat/SPC.Blazor.Voice/wwwroot/js/pcm-player-worklet.js
// (loaded by voice-test.js as an AudioWorklet module).
// Playback for the voice kiosk: streamed PCM16 in, continuous audio out.
//
// TWO THINGS THIS HAS TO GET RIGHT, both learned the hard way:
//
// 1. A generated reply arrives FASTER THAN REALTIME. The model emits a six-second sentence in about two
//    seconds, while playback drains at 1x. An earlier version kept a fixed two-second ring buffer and
//    dropped the oldest sample when it filled — borrowed from video streaming, where stale frames are
//    worthless. For speech it is catastrophic: it deletes the middle of every sentence. Generated audio
//    is a FINITE utterance that must play in full, so the buffer grows instead of dropping.
//
// 2. `new AudioContext({sampleRate})` is a REQUEST, not a guarantee. If the device runs at 48 kHz and the
//    provider sends 24 kHz, playing the samples untouched halves the speed and drops the pitch an octave.
//    So the read cursor advances by sourceRate/contextRate with linear interpolation between samples.
//
// A plain growable buffer with a fractional read cursor, rather than a circular one: modular arithmetic
// and fractional indices together are easy to get subtly wrong, and this is the part that must not be.

const MAX_SECONDS = 120;

class PcmPlayerProcessor extends AudioWorkletProcessor {
    constructor(options) {
        const opts = (options && options.processorOptions) || {};
        super();

        const contextRate = opts.contextRate || sampleRate;
        const sourceRate = opts.sourceRate || contextRate;

        // How far to advance per output sample. 1 when the rates match.
        this._ratio = sourceRate / contextRate;
        this._max = Math.ceil(contextRate * MAX_SECONDS);

        this._buf = new Float32Array(Math.ceil(contextRate * 8));
        this._head = 0;   // read cursor, fractional
        this._tail = 0;   // write cursor, integer

        // Set once the relay says the goodbye was the last thing it will send. From then on, running out
        // of audio is not an underrun to paper over — it is the end of the conversation, and the page is
        // told so it can close the socket without cutting the last word off.
        this._ending = false;
        this._reportedDrained = false;

        this.port.onmessage = (event) => {
            const data = event.data;
            if (!data) return;

            // Barge-in. Everything queued is audio the guest has just talked over, so it is discarded,
            // not drained — playing it out would be the agent talking over them.
            if (data.flush) {
                this._head = 0;
                this._tail = 0;
                return;
            }

            if (data.ending) {
                this._ending = true;
                return;
            }

            if (data.pcm) this._push(new Int16Array(data.pcm));
        };
    }

    _push(samples) {
        this._ensure(samples.length);

        for (let i = 0; i < samples.length; i++) {
            if (this._tail >= this._buf.length) break;   // at the ceiling; see _ensure
            this._buf[this._tail++] = samples[i] / 32768;
        }
    }

    /** Makes room for `count` more samples: compact what has been played, then grow if still short. */
    _ensure(count) {
        if (this._tail + count <= this._buf.length) return;

        // Compact: drop everything already played and slide the rest to the front.
        const start = Math.floor(this._head);
        if (start > 0) {
            this._buf.copyWithin(0, start, this._tail);
            this._tail -= start;
            this._head -= start;
        }

        if (this._tail + count <= this._buf.length) return;

        let size = this._buf.length;
        while (size < this._tail + count && size < this._max) size *= 2;

        if (size > this._buf.length) {
            const grown = new Float32Array(Math.min(size, this._max));
            grown.set(this._buf.subarray(0, this._tail));
            this._buf = grown;
        }
        // If it is still short we are at the 2-minute ceiling — the tail of an absurdly long reply is
        // dropped rather than growing without limit. _push stops at the boundary.
    }

    process(_inputs, outputs) {
        const output = outputs[0];
        const channel = output[0];

        for (let i = 0; i < channel.length; i++) {
            const index = Math.floor(this._head);

            // Underrun (nothing buffered yet, or the model paused): emit silence rather than throwing or
            // stopping, so a momentary stall is a gap and not a dead session.
            if (index + 1 >= this._tail) {
                channel[i] = 0;
                continue;
            }

            const frac = this._head - index;
            channel[i] = this._buf[index] * (1 - frac) + this._buf[index + 1] * frac;
            this._head += this._ratio;
        }

        for (let c = 1; c < output.length; c++) output[c].set(channel);

        // Empty, and nothing more is coming: the goodbye has been heard in full.
        if (this._ending && !this._reportedDrained && Math.floor(this._head) + 1 >= this._tail) {
            this._reportedDrained = true;
            this.port.postMessage({ drained: true });
        }

        return true;
    }
}

registerProcessor('pcm-player', PcmPlayerProcessor);
