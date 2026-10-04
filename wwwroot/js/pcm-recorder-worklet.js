// Vendored unchanged from the monorepo: MapleKiosk/MK.Chat/SPC.Blazor.Voice/wwwroot/js/pcm-recorder-worklet.js
// (loaded by voice-test.js as an AudioWorklet module).
// Microphone capture for the voice kiosk: Float32 render quanta in, 20 ms mono PCM16 frames out.
//
// An AudioWorklet rather than a ScriptProcessorNode. ScriptProcessor is deprecated AND runs its callback
// on the main thread — which on this page is also servicing the Blazor SignalR circuit, MudBlazor
// re-renders and the layout's attract slideshow. A GC pause there drops capture buffers, and dropped
// INPUT is worse than dropped output: it silently corrupts what the agent hears.
//
// No resampling here. The page builds its AudioContext with the exact sample rate the session asked for,
// so the browser resamples the device for us and `sampleRate` in this scope is already correct.

class PcmRecorderProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const frameSamples = (options && options.processorOptions && options.processorOptions.frameSamples) || 320;
        this._frame = new Int16Array(frameSamples);
        this._filled = 0;
        this._muted = false;

        this.port.onmessage = (event) => {
            if (event.data && typeof event.data.muted === 'boolean') {
                this._muted = event.data.muted;
            }
        };
    }

    process(inputs) {
        const input = inputs[0];
        if (!input || input.length === 0) {
            return true;
        }

        const channels = input.length;
        const first = input[0];
        if (!first) {
            return true;
        }

        // Push-to-talk and mute both land here. Returning true keeps the node alive; we simply emit nothing,
        // so the relay's idle watchdog can still do its job.
        if (this._muted) {
            return true;
        }

        for (let i = 0; i < first.length; i++) {
            // Downmix defensively: the page asks for a mono capture, but a device that ignores the
            // constraint would otherwise have only its left channel heard.
            let sample = 0;
            for (let c = 0; c < channels; c++) {
                sample += input[c][i];
            }
            sample /= channels;

            if (sample > 1) sample = 1;
            else if (sample < -1) sample = -1;

            this._frame[this._filled++] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;

            if (this._filled === this._frame.length) {
                // Copy before transferring — this._frame is reused for the next 20 ms.
                const chunk = this._frame.slice();
                this.port.postMessage(chunk.buffer, [chunk.buffer]);
                this._filled = 0;
            }
        }

        return true;
    }
}

registerProcessor('pcm-recorder', PcmRecorderProcessor);
