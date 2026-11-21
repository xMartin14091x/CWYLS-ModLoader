// Small helpers shared by the zones. No state.
export const $ = (selector, root = document) => root.querySelector(selector);

/** Escapes text for use inside HTML. Every interpolated string (reference text, subtitles, pack names) goes through this. */
export function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export const fmtN = (n) => Number(n).toLocaleString('en-US');
export const sec = (n) => (Math.round(n * 100) / 100).toFixed(2).replace(/0$/, '');
export const rem = () => parseFloat(getComputedStyle(document.documentElement).fontSize) || 14;

/** Deterministic pseudo-random numbers (the sky and the fake waveforms must not jump around between renders). */
export function mulberry(seed) {
  return function next() {
    seed |= 0; seed = seed + 0x6D2B79F5 | 0;
    let t = Math.imul(seed ^ seed >>> 15, 1 | seed);
    t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
    return ((t ^ t >>> 14) >>> 0) / 4294967296;
  };
}

/** "m:ss.s" */
export function fmtTime(s) {
  const m = Math.floor(s / 60), r = s - m * 60;
  return `${m < 10 ? '0' : ''}${m}:${r < 10 ? '0' : ''}${r.toFixed(1)}`;
}
