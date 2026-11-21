// WAV maths for the creator's own takes. Pure functions on Float32Array / Uint8Array (no Web Audio), so Node can test
// the exact bytes. The loader accepts PCM 16-bit WAV, 8-48 kHz, at most 60 s and 16 MiB; Studio's prepared ("render")
// take is PCM16, 48 kHz, mono (W2 PLAN §3).
export const TARGET_RATE = 48000;
export const MAX_SECONDS = 60;
export const MAX_BYTES = 16 * 1024 * 1024;

/** Average several channels into one. */
export function downmixToMono(channels) {
  if (channels.length === 1) return channels[0];
  const n = channels[0].length, out = new Float32Array(n);
  for (const ch of channels) for (let i = 0; i < n; i++) out[i] += ch[i] / channels.length;
  return out;
}

/**
 * Linear-interpolation resampler. Downsampling averages the source span of each output sample (a box filter) to keep
 * aliasing down; this is plenty for speech and keeps the code testable. Output length = round(n * to / from).
 */
export function resample(samples, fromRate, toRate) {
  if (fromRate === toRate) return samples;
  const outLen = Math.round(samples.length * toRate / fromRate), out = new Float32Array(outLen), ratio = fromRate / toRate;
  for (let i = 0; i < outLen; i++) {
    const a = i * ratio, b = Math.min(samples.length, (i + 1) * ratio);
    if (ratio > 1) {
      let sum = 0, count = 0;
      for (let j = Math.floor(a); j < Math.ceil(b); j++) { sum += samples[j]; count++; }
      out[i] = count ? sum / count : 0;
    } else {
      const j = Math.floor(a), f = a - j, s0 = samples[Math.min(j, samples.length - 1)], s1 = samples[Math.min(j + 1, samples.length - 1)];
      out[i] = s0 + (s1 - s0) * f;
    }
  }
  return out;
}

/** The part between two times (seconds); the bounds are clamped to the clip. */
export function trim(samples, rate, startSec, endSec) {
  const a = Math.max(0, Math.min(samples.length, Math.round(startSec * rate)));
  const b = Math.max(a, Math.min(samples.length, Math.round(endSec * rate)));
  return samples.slice(a, b);
}

/** Soft limiter: identity up to `threshold`, then tanh towards (never reaching) full scale. Continuous at the knee. */
export function softLimit(x, threshold = 0.8) {
  const a = Math.abs(x);
  if (a <= threshold) return x;
  return Math.sign(x) * (threshold + (1 - threshold) * Math.tanh((a - threshold) / (1 - threshold)));
}

/**
 * "Even out volume": RMS normalisation towards `targetRms` (about -20 dBFS) with a soft limiter, so quiet takes come
 * up and peaks never clip. This is level matching, not loudness (LUFS) measurement. Silence is returned unchanged.
 */
export function evenOut(samples, { targetRms = 0.1, maxGain = 8, minGain = 0.25 } = {}) {
  let sum = 0;
  for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i];
  const rms = Math.sqrt(sum / Math.max(1, samples.length));
  if (rms < 1e-5) return samples.slice();
  const gain = Math.max(minGain, Math.min(maxGain, targetRms / rms)), out = new Float32Array(samples.length);
  for (let i = 0; i < samples.length; i++) out[i] = softLimit(samples[i] * gain);
  return out;
}

/** PCM16 mono WAV bytes (44-byte header). Samples are clamped to [-1, 1]. */
export function encodeWavPcm16Mono(samples, sampleRate = TARGET_RATE) {
  const dataBytes = samples.length * 2, bytes = new Uint8Array(44 + dataBytes), v = new DataView(bytes.buffer);
  const str = (at, s) => { for (let i = 0; i < s.length; i++) v.setUint8(at + i, s.charCodeAt(i)); };
  str(0, 'RIFF'); v.setUint32(4, 36 + dataBytes, true); str(8, 'WAVE'); str(12, 'fmt ');
  v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, sampleRate, true); v.setUint32(28, sampleRate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  str(36, 'data'); v.setUint32(40, dataBytes, true);
  for (let i = 0; i < samples.length; i++) {
    const n = Math.max(-1, Math.min(1, samples[i]));
    v.setInt16(44 + i * 2, Math.round(n * (n < 0 ? 32768 : 32767)), true);
  }
  return bytes;
}

/** Reads a canonical WAV header; returns null when it is not a PCM16 WAV this app understands. */
export function parseWavHeader(bytes) {
  if (bytes.length < 44) return null;
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const tag = (at) => String.fromCharCode(v.getUint8(at), v.getUint8(at + 1), v.getUint8(at + 2), v.getUint8(at + 3));
  if (tag(0) !== 'RIFF' || tag(8) !== 'WAVE' || tag(12) !== 'fmt ' || tag(36) !== 'data') return null;
  if (v.getUint16(20, true) !== 1 || v.getUint16(34, true) !== 16) return null;
  const channels = v.getUint16(22, true), sampleRate = v.getUint32(24, true), dataBytes = v.getUint32(40, true);
  return { channels, sampleRate, dataBytes, seconds: dataBytes / (2 * channels * sampleRate) };
}

/**
 * The samples of a take as it will be published: mono, 48 kHz, trimmed, optionally evened out. This is also what the creator
 * hears when listening locally, so "play take" and the published clip are the same processing.
 * @param {{channels:Float32Array[], sampleRate:number}} decoded
 * @param {{trimStartSec?:number, trimEndSec?:number|null, evenOutVolume?:boolean}} [opts] a null/undefined end means "to the end of the source"
 */
export function renderSamples(decoded, { trimStartSec = 0, trimEndSec = null, evenOutVolume = false } = {}) {
  let s = resample(downmixToMono(decoded.channels), decoded.sampleRate, TARGET_RATE);
  const total = s.length / TARGET_RATE;
  s = trim(s, TARGET_RATE, trimStartSec ?? 0, trimEndSec == null || trimEndSec === Infinity ? total : Math.min(trimEndSec, total));
  return evenOutVolume ? evenOut(s) : s;
}

/** The whole preparation of one take for upload: {@link renderSamples} as a PCM16 48 kHz mono WAV. */
export function prepareRender(decoded, opts = {}) {
  const s = renderSamples(decoded, opts);
  return { wav: encodeWavPcm16Mono(s, TARGET_RATE), seconds: s.length / TARGET_RATE };
}

/**
 * Decodes a canonical PCM16 WAV (what this app and the loader store) to mono float samples, without Web Audio.
 * Used for the waveform and trim of takes read back from the pack; imports of other formats go through Web Audio instead.
 * @returns {{samples:Float32Array, sampleRate:number, channels:number}|null}
 */
export function decodePcm16Wav(bytes) {
  const h = parseWavHeader(bytes);
  if (!h) return null;
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const frames = Math.floor(Math.min(h.dataBytes, bytes.length - 44) / (2 * h.channels)), out = new Float32Array(frames);
  for (let i = 0; i < frames; i++) {
    let sum = 0;
    for (let c = 0; c < h.channels; c++) sum += v.getInt16(44 + (i * h.channels + c) * 2, true) / 32768;
    out[i] = sum / h.channels;
  }
  return { samples: out, sampleRate: h.sampleRate, channels: h.channels };
}
