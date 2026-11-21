// Local playback of a take (what the creator hears when pressing Space). Never involves the game.
// Interface (also what the test fake implements):  play(samples, rate, onEnd) / stop() / position() -> seconds since start
export function createPlayer(AudioContextCtor = globalThis.AudioContext) {
  let ctx = null, source = null, startedAt = 0;
  return {
    play(samples, rate, onEnd = () => {}) {
      this.stop();
      ctx = ctx || new AudioContextCtor();
      ctx.resume?.();
      const buffer = ctx.createBuffer(1, samples.length, rate);
      buffer.copyToChannel(samples, 0);
      const src = ctx.createBufferSource();
      src.buffer = buffer;
      src.connect(ctx.destination);
      src.onended = () => { if (source === src) { source = null; onEnd(); } };
      source = src;
      startedAt = ctx.currentTime;
      src.start();
    },
    stop() { const s = source; source = null; if (s) { s.onended = null; try { s.stop(); } catch { /* already stopped */ } } },
    position() { return ctx ? Math.max(0, ctx.currentTime - startedAt) : 0; }
  };
}
