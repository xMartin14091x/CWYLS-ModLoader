import test from 'node:test';
import assert from 'node:assert/strict';
import { levelClass, LevelTracker, CLIP_PEAK, CLIP_HOLD_MS } from '../../ui/audio/levels.js';
import { renderSamples, prepareRender, parseWavHeader } from '../../ui/audio/wav.js';

test('level class: clip beats everything, quiet is idle, a loud quietest-moment is noise, otherwise good', () => {
  assert.equal(levelClass({ peak: 0.99, rms: 0.3, floor: 0 }), 'clip');
  assert.equal(levelClass({ peak: CLIP_PEAK, rms: 0.3, floor: 0 }), 'clip');
  assert.equal(levelClass({ peak: 0.2, rms: 0.005, floor: 0.004 }), 'idle');
  assert.equal(levelClass({ peak: 0.4, rms: 0.1, floor: 0.05 }), 'noise');
  assert.equal(levelClass({ peak: 0.4, rms: 0.1, floor: 0.005 }), 'good');
});

test('the tracker: the noise floor is the quietest level of the last 3 s, so speech with pauses is good and a hissing room is noise', () => {
  const speech = new LevelTracker();
  let last;
  for (let t = 0; t < 3000; t += 25) last = speech.push(t, 0.4, t % 500 < 250 ? 0.15 : 0.004); // pauses every half second
  assert.equal(last.cls === 'good' || last.cls === 'idle', true);
  assert.ok(last.floor < 0.01);
  const hiss = new LevelTracker();
  for (let t = 0; t < 3000; t += 25) last = hiss.push(t, 0.2, 0.05);
  assert.equal(last.cls, 'noise');
  assert.ok(last.lvl > 0 && last.lvl <= 1);
});

test('a clip is held for a moment after the peak, then released', () => {
  const t = new LevelTracker();
  assert.equal(t.push(0, 1, 0.3).cls, 'clip');
  assert.equal(t.push(CLIP_HOLD_MS - 10, 0.3, 0.1).cls, 'clip');
  assert.notEqual(t.push(CLIP_HOLD_MS + 50, 0.3, 0.1).cls, 'clip');
});

test('renderSamples: null trim end means to the end of the source; the same processing as the published render', () => {
  const src = { channels: [Float32Array.from({ length: 48000 }, (_, i) => Math.sin(i / 20) * 0.3)], sampleRate: 48000 };
  assert.equal(renderSamples(src).length, 48000);
  assert.equal(renderSamples(src, { trimStartSec: 0.25, trimEndSec: null }).length, 36000);
  assert.equal(renderSamples(src, { trimStartSec: 0.25, trimEndSec: 0.75 }).length, 24000);
  assert.equal(renderSamples(src, { trimEndSec: 99 }).length, 48000, 'an end past the source is clamped');
  const { wav, seconds } = prepareRender(src, { trimStartSec: 0.25, trimEndSec: 0.75, evenOutVolume: true });
  assert.equal(parseWavHeader(wav).sampleRate, 48000);
  assert.equal(seconds, 0.5);
});
