import test from 'node:test';
import assert from 'node:assert/strict';
import { downmixToMono, resample, trim, softLimit, evenOut, encodeWavPcm16Mono, parseWavHeader, prepareRender, TARGET_RATE } from '../../ui/audio/wav.js';
import { peaks } from '../../ui/audio/peaks.js';

const sine = (n, rate, hz, amp = 0.5) => Float32Array.from({ length: n }, (_, i) => amp * Math.sin(2 * Math.PI * hz * i / rate));

test('WAV header: RIFF/WAVE, PCM, mono, 16-bit, 48 kHz, exact sizes', () => {
  const wav = encodeWavPcm16Mono(new Float32Array(480), 48000);
  assert.equal(wav.length, 44 + 960);
  const v = new DataView(wav.buffer);
  assert.equal(String.fromCharCode(...wav.slice(0, 4)), 'RIFF');
  assert.equal(v.getUint32(4, true), 36 + 960);
  assert.equal(String.fromCharCode(...wav.slice(8, 16)), 'WAVEfmt ');
  assert.equal(v.getUint32(16, true), 16);
  assert.equal(v.getUint16(20, true), 1, 'PCM');
  assert.equal(v.getUint16(22, true), 1, 'mono');
  assert.equal(v.getUint32(24, true), 48000);
  assert.equal(v.getUint32(28, true), 96000, 'byte rate');
  assert.equal(v.getUint16(32, true), 2, 'block align');
  assert.equal(v.getUint16(34, true), 16);
  assert.equal(String.fromCharCode(...wav.slice(36, 40)), 'data');
  assert.equal(v.getUint32(40, true), 960);
});

test('WAV samples: full scale maps to 32767/-32768 and out-of-range input is clamped', () => {
  const wav = encodeWavPcm16Mono(Float32Array.of(1, -1, 0, 2, -2, 0.5), 48000);
  const v = new DataView(wav.buffer);
  assert.deepEqual([0, 1, 2, 3, 4, 5].map((i) => v.getInt16(44 + i * 2, true)), [32767, -32768, 0, 32767, -32768, 16384]);
});

test('parseWavHeader reads what the encoder wrote, and refuses other formats', () => {
  const wav = encodeWavPcm16Mono(new Float32Array(24000), 48000);
  assert.deepEqual(parseWavHeader(wav), { channels: 1, sampleRate: 48000, dataBytes: 48000, seconds: 0.5 });
  assert.equal(parseWavHeader(wav.slice(0, 20)), null);
  const bad = wav.slice(); bad[20] = 3; // IEEE float
  assert.equal(parseWavHeader(bad), null);
  const junk = wav.slice(); junk[0] = 0x58;
  assert.equal(parseWavHeader(junk), null);
});

test('resample 44.1 kHz to 48 kHz gives round(n * 48000 / 44100) samples and keeps the tone', () => {
  const src = sine(44100, 44100, 440);
  const out = resample(src, 44100, 48000);
  assert.equal(out.length, 48000);
  // peak amplitude is preserved within interpolation error
  const peak = Math.max(...out.map(Math.abs));
  assert.ok(peak > 0.49 && peak < 0.51, String(peak));
});

test('resample 96 kHz to 48 kHz halves the length; equal rates return the same array', () => {
  assert.equal(resample(new Float32Array(96000), 96000, 48000).length, 48000);
  const same = new Float32Array(10);
  assert.equal(resample(same, 48000, 48000), same);
});

test('downmix averages channels', () => {
  const out = downmixToMono([Float32Array.of(1, 0), Float32Array.of(0, 1)]);
  assert.deepEqual(Array.from(out), [0.5, 0.5]);
  const mono = Float32Array.of(1);
  assert.equal(downmixToMono([mono]), mono);
});

test('trim keeps the requested span and clamps the bounds', () => {
  const s = Float32Array.from({ length: 100 }, (_, i) => i);
  assert.equal(trim(s, 100, 0.1, 0.5).length, 40);
  assert.equal(trim(s, 100, 0.1, 0.5)[0], 10);
  assert.equal(trim(s, 100, -1, 99).length, 100);
  assert.equal(trim(s, 100, 0.8, 0.2).length, 0, 'reversed bounds give an empty clip, not a negative length');
});

test('soft limiter is the identity below the knee and never reaches full scale', () => {
  assert.equal(softLimit(0.5), 0.5);
  assert.equal(softLimit(-0.8), -0.8);
  for (const x of [0.81, 1, 2, 50, -50]) assert.ok(Math.abs(softLimit(x)) < 1.0000001);
  assert.ok(Math.abs(softLimit(100)) <= 1);
  assert.ok(Math.abs(softLimit(0.8001) - 0.8001) < 0.001, 'continuous at the knee');
});

test('even out: a quiet take comes up to the target RMS, peaks stay inside full scale, silence is untouched', () => {
  const quiet = sine(48000, 48000, 200, 0.02);
  const out = evenOut(quiet);
  const rms = (a) => Math.sqrt(a.reduce((s, x) => s + x * x, 0) / a.length);
  assert.ok(rms(out) > rms(quiet) * 3);
  assert.ok(Math.max(...out.map(Math.abs)) <= 1);
  const loud = sine(48000, 48000, 200, 0.99);
  assert.ok(Math.max(...evenOut(loud).map(Math.abs)) <= 1);
  const silence = new Float32Array(1000);
  assert.deepEqual(Array.from(evenOut(silence)), Array.from(silence));
});

test('prepareRender: stereo 44.1 kHz in, mono 48 kHz PCM16 out, trimmed to the requested span', () => {
  const n = 44100 * 2; // 2 s
  const decoded = { channels: [sine(n, 44100, 300), sine(n, 44100, 300)], sampleRate: 44100 };
  const { wav, seconds } = prepareRender(decoded, { trimStartSec: 0.5, trimEndSec: 1.5 });
  const h = parseWavHeader(wav);
  assert.equal(h.channels, 1);
  assert.equal(h.sampleRate, TARGET_RATE);
  assert.ok(Math.abs(seconds - 1) < 0.001, String(seconds));
  assert.ok(Math.abs(h.seconds - 1) < 0.001);
});

test('prepareRender: no trim end keeps the whole clip', () => {
  const decoded = { channels: [new Float32Array(48000)], sampleRate: 48000 };
  assert.equal(prepareRender(decoded).seconds, 1);
});

test('peaks: bucket maxima in [0,1], empty input gives zeros', () => {
  const p = peaks(Float32Array.of(0, 0.5, -1, 0.25), 2);
  assert.deepEqual(p, [0.5, 1]);
  assert.deepEqual(peaks(new Float32Array(0), 3), [0, 0, 0]);
  assert.equal(peaks(Float32Array.of(2), 1)[0], 1, 'clamped');
});
