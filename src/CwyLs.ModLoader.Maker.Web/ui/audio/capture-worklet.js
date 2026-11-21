// AudioWorklet that taps the microphone. It never records by itself: it always reports levels, and only while the page
// has switched recording on does it also hand over the raw samples. Runs in the audio thread (no imports, no DOM).
class CwylsCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.recording = false;
    this.block = new Float32Array(1024);
    this.filled = 0;
    this.port.onmessage = (e) => { if (e.data && e.data.cmd === 'record') this.recording = !!e.data.on; };
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || !input.length) return true;
    const first = input[0];
    for (let i = 0; i < first.length; i++) {
      let v = first[i];
      if (input.length > 1) { for (let c = 1; c < input.length; c++) v += input[c][i]; v /= input.length; }
      this.block[this.filled++] = v;
      if (this.filled === this.block.length) {
        let peak = 0, sum = 0;
        for (let k = 0; k < this.block.length; k++) { const a = Math.abs(this.block[k]); if (a > peak) peak = a; sum += this.block[k] * this.block[k]; }
        const msg = { peak, rms: Math.sqrt(sum / this.block.length) };
        if (this.recording) { const frames = this.block.slice(); msg.frames = frames; this.port.postMessage(msg, [frames.buffer]); } else this.port.postMessage(msg);
        this.filled = 0;
      }
    }
    return true;
  }
}
registerProcessor('cwyls-capture', CwylsCapture);
