class StarWarsPcm extends AudioWorkletProcessor {
    constructor({ processorOptions: { inputRate } }) {
        super();
        this.samples = new Float32Array(8192);
        this.mask = this.samples.length - 1;
        this.startThreshold = Math.ceil(inputRate * 0.06);
        this.inputPerOutput = inputRate / sampleRate;
        this.underruns = 0;
        this.overflows = 0;
        this.reportAt = 0;
        this.peak = 0;
        this.clear();
        this.port.onmessage = ({ data }) => {
            if (data === "clear") {
                this.clear();
                return;
            }

            if (this.queued + data.length > this.samples.length) {
                this.overflows++;
                this.clear();
                if (data.length > this.samples.length) return;
            }

            let write = (this.read + this.queued) & this.mask;
            for (const sample of data) {
                this.samples[write] = sample;
                write = (write + 1) & this.mask;
            }
            this.queued += data.length;
        };
    }

    clear() {
        this.queued = 0;
        this.read = 0;
        this.phase = 0;
        this.playing = false;
    }

    process(_inputs, outputs) {
        const channel = outputs[0][0];
        if (this.queued >= this.startThreshold) this.playing = true;

        if (this.playing) {
            for (let i = 0; i < channel.length; i++) {
                const advance = Math.floor(this.phase + this.inputPerOutput);
                if (this.queued < Math.max(advance, this.phase === 0 ? 1 : 2)) {
                    this.underruns++;
                    this.playing = false;
                    break;
                }

                const first = this.samples[this.read];
                const second = this.samples[(this.read + 1) & this.mask];
                const sample = first + (second - first) * this.phase;
                channel[i] = sample;
                this.peak = Math.max(this.peak, Math.abs(sample));
                this.phase += this.inputPerOutput - advance;
                this.read = (this.read + advance) & this.mask;
                this.queued -= advance;
            }
        }

        if (currentFrame >= this.reportAt) {
            this.port.postMessage({
                queued: this.queued, underruns: this.underruns, overflows: this.overflows, peak: this.peak
            });
            this.reportAt = currentFrame + sampleRate / 2;
            this.peak = 0;
        }
        return true;
    }
}

registerProcessor("starwars-pcm", StarWarsPcm);
