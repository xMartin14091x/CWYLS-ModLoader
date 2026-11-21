import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { parseWavHeader, encodeWavPcm16Mono, TARGET_RATE } from '../../ui/audio/wav.js';
import { featureOn, canEditText, canDub, canPlay, missingFeature, canCreatePacks } from '../../ui/selectors.js';
import { renderHero } from '../../ui/zones/hero.js';
import { makeT } from '../../ui/i18n/i18n.js';
import { MIC_IDLE_MS } from '../../ui/dub.js';

const P = 'satone-th-dub', LANG = 'th', BRIEF = 'clickheroine_rest_010', EMPTY = 'morning_wake_006';
const tick = () => new Promise((r) => setImmediate(r));
const settleAll = async () => { for (let i = 0; i < 14; i++) await tick(); };
const sine = (n, rate, hz, amp = 0.3) => Float32Array.from({ length: n }, (_, i) => amp * Math.sin(2 * Math.PI * hz * i / rate));

function clock() {
  let t = 0, n = 0;
  const pending = new Map();
  return {
    setTimeout: (fn, ms) => { const id = ++n; pending.set(id, { at: t + ms, fn }); return id; },
    clearTimeout: (id) => { pending.delete(id); },
    async advance(ms) {
      const end = t + ms;
      for (;;) {
        const due = [...pending.entries()].filter(([, v]) => v.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        pending.delete(due[0]); t = due[1].at; due[1].fn();
        await settleAll();
      }
      t = end; await settleAll();
    },
    get time() { return t; }
  };
}

function fakeMic({ supported = true, fail = null, seconds = 1.5, rate = 44100 } = {}) {
  const calls = []; let level = null;
  return {
    calls, supported: () => supported,
    async setup(id) { calls.push(['setup', id]); if (fail) { const e = new Error(fail); e.name = fail; throw e; } return { devices: [{ id: 'a', label: 'Mic A' }, { id: 'b', label: 'Mic B' }], deviceId: id || 'a' }; },
    onLevel(cb) { level = cb; return () => { level = null; }; },
    emit(peak, rms) { level?.({ peak, rms }); },
    start() { calls.push(['start']); },
    async stop() { calls.push(['stop']); return { channels: [sine(Math.round(seconds * rate), rate, 220)], sampleRate: rate }; },
    release() { calls.push(['release']); }
  };
}
function fakePlayer() {
  const played = []; let end = null;
  return { played, play(samples, rate, onEnd) { played.push({ n: samples.length, rate }); end = onEnd; }, stop() { end = null; }, position: () => 0, finish() { end?.(); } };
}

async function setup(o = {}) {
  const dev = createDevBackend({ lines: 120, ...o.dev });
  const store = createStore({
    ready: false, conn: { connected: false, unauthorized: false }, status: null, packs: [], packId: null, packLang: 'th', groups: [], overall: {}, lines: [],
    byId: new Map(), loadingLines: true, view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, history: null, saveNote: null, menu: null,
    selTakeId: null, peaks: {}, last: null, scrollTo: null, mic: 'none', micError: null, devices: [], deviceIds: [], rec: 'idle', level: 'idle', lvl: 0, count: 3,
    recElapsed: 0, livePeaks: [], features: null, consentVersion: null, trimDraft: null, playing: false, playingTake: false, speaking: null, waiting: null
  });
  const toasts = [], clk = clock(), mic = o.mic || fakeMic(), player = o.player || fakePlayer();
  const decodeFile = o.decodeFile || (async (f) => { if (f.bad) throw new Error('not audio'); return f.decoded; });
  let seq = 0;
  const actions = createActions({ store, api: createApi(dev), mode: 'dev', toast: (k, kind, v) => toasts.push([k, kind, v]), timers: clk, reconnectMs: 100, selectDelayMs: 0,
    newOp: () => (++seq).toString(16).padStart(32, 'e'), now: () => clk.time, mic, player, decodeFile, micIdleMs: o.micIdleMs, reasonText: (e) => `reason:${e.code}` });
  await actions.start({});
  await settleAll();
  return { dev, store, actions, toasts, clk, mic, player };
}
const S = (c) => c.store.get();
const log = (c, ...names) => c.dev.dev.log.filter((l) => names.includes(l.method)).map((l) => l.method);
const onLine = async (c, id) => { await c.actions.selectLine(id); await settleAll(); };
const ready = async (c) => { await c.actions.setupMic(); await settleAll(); };
async function record(c, ms = 1500) {
  c.actions.toggleRecord();
  await c.clk.advance(3000);          // the silent 3-2-1
  await c.clk.advance(ms);
  c.actions.toggleRecord();           // stop
  await settleAll();
}
const lineOnServer = (c, id) => c.dev.studioLine(P, LANG, id);
const takesOnServer = (c, id) => c.dev.studioTakes(P, LANG, id);

// ---------------- microphone ----------------
test('the browser is asked for the microphone only when the creator sets it up, never at load', async () => {
  const c = await setup();
  assert.deepEqual(c.mic.calls, []);
  assert.equal(S(c).mic, 'none');
  await c.actions.setupMic();
  assert.deepEqual(c.mic.calls[0], ['setup', undefined]);
  assert.deepEqual([S(c).mic, S(c).devices, S(c).device], ['ready', ['Mic A', 'Mic B'], 'Mic A']);
});

test('mic problems are explained: denied, no microphone, browser cannot record', async () => {
  for (const [fail, key] of [['NotAllowedError', 'mic_denied'], ['NotFoundError', 'mic_none'], ['NotSupportedError', 'mic_unsupported']]) {
    const c = await setup({ mic: fakeMic({ fail }) });
    await c.actions.setupMic();
    assert.deepEqual([S(c).mic, S(c).micError], ['none', key], fail);
  }
  const c = await setup({ mic: fakeMic({ supported: false }) });
  await c.actions.setupMic();
  assert.deepEqual([S(c).mic, S(c).micError], ['none', 'mic_unsupported']);
});

test('input levels become the halo class: idle, good, noise, clip', async () => {
  const c = await setup();
  await ready(c);
  c.mic.emit(0.01, 0.002); assert.equal(S(c).level, 'idle');
  c.clk.advance(0);
  c.mic.emit(0.5, 0.12); assert.equal(S(c).level, 'good');
  c.mic.emit(0.995, 0.4); assert.equal(S(c).level, 'clip');
});

test('device change sets the mic up again on that device', async () => {
  const c = await setup();
  await ready(c);
  await c.actions.selectDevice('Mic B');
  assert.deepEqual(c.mic.calls.filter((x) => x[0] === 'setup').at(-1), ['setup', 'b']);
  assert.equal(S(c).device, 'Mic B');
});

// ---------------- recording ----------------
test('recording: a silent 3-2-1 first (nothing reaches the recorder), then the take is created, uploaded and selected', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  c.actions.toggleRecord();
  assert.deepEqual([S(c).rec, S(c).count], ['countdown', 3]);
  await c.clk.advance(1000); assert.equal(S(c).count, 2);
  await c.clk.advance(1000); assert.equal(S(c).count, 1);
  assert.ok(!c.mic.calls.some((x) => x[0] === 'start'), 'nothing is recorded during the count-in');
  await c.clk.advance(1000);
  assert.equal(S(c).rec, 'recording');
  assert.ok(c.mic.calls.some((x) => x[0] === 'start'));
  await c.clk.advance(1500);
  assert.ok(S(c).recElapsed >= 1.4, String(S(c).recElapsed));
  c.actions.toggleRecord();
  await settleAll();
  assert.equal(S(c).rec, 'idle');
  const takes = (await takesOnServer(c, EMPTY)).items;
  assert.equal(takes.length, 1);
  assert.deepEqual([takes[0].origin, takes[0].consent.confirmed, takes[0].sourcePresent, takes[0].renderReady], ['own', false, true, false]);
  assert.equal(S(c).selTakeId, takes[0].id);
  assert.equal(S(c).line.takes.length, 1);
  assert.ok(S(c).peaks[takes[0].id].length === 96, 'a waveform of the creator\'s own take');
  const wav = new Uint8Array(await (await c.dev.studioGetTakeAudio(P, LANG, EMPTY, takes[0].id, 'source')).arrayBuffer());
  const h = parseWavHeader(wav);
  assert.deepEqual([h.channels, h.sampleRate], [1, 44100], 'the source keeps the microphone rate (8-48 kHz is accepted)');
  assert.ok(Math.abs(h.seconds - 1.5) < 0.01);
  assert.ok(c.toasts.some((t) => t[0] === 't_take'));
});

test('cancelling during the count-in (Esc) records nothing and creates nothing', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  c.actions.toggleRecord();
  await c.clk.advance(1500);
  c.actions.cancelRecord();
  await c.clk.advance(5000);
  assert.equal(S(c).rec, 'idle');
  assert.ok(!c.mic.calls.some((x) => x[0] === 'start'));
  assert.equal((await takesOnServer(c, EMPTY)).items.length, 0);
});

test('recording needs a set-up mic, a writable pack, and a free slot', async () => {
  const c = await setup();
  await onLine(c, EMPTY);
  c.actions.toggleRecord();
  assert.deepEqual(c.toasts.at(-1).slice(0, 2), ['t_needMic', 'warn']);
  await ready(c);
  await onLine(c, BRIEF);                              // three takes already
  c.actions.toggleRecord();
  assert.deepEqual(c.toasts.at(-1).slice(0, 2), ['take_limit', 'warn']);
  assert.equal(S(c).rec, 'idle');
});

test('the recorder stops itself just before the 60 s limit', async () => {
  const c = await setup({ mic: fakeMic({ seconds: 59.4, rate: 8000 }) });
  await onLine(c, EMPTY); await ready(c);
  c.actions.toggleRecord();
  await c.clk.advance(3000);
  await c.clk.advance(60000);
  assert.equal(S(c).rec, 'idle');
  assert.ok(c.mic.calls.some((x) => x[0] === 'stop'));
  assert.equal((await takesOnServer(c, EMPTY)).items.length, 1);
});

test('a take that is too short is not kept', async () => {
  const c = await setup({ mic: fakeMic({ seconds: 0.05 }) });
  await onLine(c, EMPTY); await ready(c);
  await record(c, 100);
  assert.equal((await takesOnServer(c, EMPTY)).items.length, 0);
  assert.ok(c.toasts.some((t) => t[0] === 't_tooShort'));
});

// ---------------- import ----------------
test('import: a stereo 96 kHz file becomes a mono 48 kHz source; a file that is not audio is refused kindly', async () => {
  const c = await setup();
  await onLine(c, EMPTY);
  const file = { name: 'take.mp3', decoded: { channels: [sine(96000, 96000, 200), sine(96000, 96000, 300)], sampleRate: 96000 } };
  assert.equal(await c.actions.importFile(file), true);
  const t = (await takesOnServer(c, EMPTY)).items[0];
  const h = parseWavHeader(new Uint8Array(await (await c.dev.studioGetTakeAudio(P, LANG, EMPTY, t.id, 'source')).arrayBuffer()));
  assert.deepEqual([h.channels, h.sampleRate], [1, 48000]);
  assert.ok(Math.abs(h.seconds - 1) < 0.01);
  assert.ok(c.toasts.some((x) => x[0] === 't_import' && x[2].f === 'take.mp3'));
  assert.equal(await c.actions.importFile({ name: 'x.txt', bad: true }), false);
  assert.ok(c.toasts.some((x) => x[0] === 't_importBad'));
});

test('import: over 60 s is refused before anything is sent; a fourth take is refused', async () => {
  const c = await setup();
  await onLine(c, EMPTY);
  const before = log(c, 'createTake').length;
  assert.equal(await c.actions.importFile({ name: 'long.wav', decoded: { channels: [new Float32Array(61 * 8000)], sampleRate: 8000 } }), false);
  assert.equal(log(c, 'createTake').length, before);
  assert.ok(c.toasts.some((t) => t[0] === 'e_too_large'));
  await onLine(c, BRIEF);
  assert.equal(await c.actions.importFile({ name: 'x.wav', decoded: { channels: [sine(8000, 8000, 200)], sampleRate: 8000 } }), false);
  assert.equal(c.toasts.at(-1)[0], 'take_limit');
});

test('a take whose upload failed is removed again instead of staying as an empty slot', async () => {
  const c = await setup();
  await onLine(c, EMPTY);
  c.dev.dev.fail('putTakeAudio', 'storage_limit', { times: 1 });
  assert.equal(await c.actions.importFile({ name: 'x.wav', decoded: { channels: [sine(8000, 8000, 200)], sampleRate: 8000 } }), false);
  assert.equal((await takesOnServer(c, EMPTY)).items.length, 0);
  assert.ok(c.toasts.some((t) => t[0] === 'e_storage_limit'));
});

// ---------------- take editing ----------------
test('M1-04: an end handle stepped 10 ms before the source end sticks instead of snapping back to the end', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  const t = S(c).line.takes[1];
  const step = +(t.sourceDurationSec - 0.01).toFixed(3);
  await c.actions.commitTrim(0, step);
  const server = (await takesOnServer(c, BRIEF)).items.find((x) => x.id === t.id);
  assert.equal(server.trimEndSec, step, 'a one-step keyboard trim is kept');
});

test('trim: dragging only moves the screen; letting go sends one update; the end of the source clears the trim; the render is invalidated', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  const t = S(c).line.takes[1];
  const patches = () => c.dev.dev.log.filter((l) => l.method === 'patchTake').length;
  const before = patches();
  c.actions.dragTrim(0.3, 1.2); c.actions.dragTrim(0.4, 1.1);
  assert.deepEqual(S(c).trimDraft, { takeId: t.id, start: 0.4, end: 1.1 });
  assert.equal(patches(), before, 'nothing is sent while the handle moves');
  await c.actions.commitTrim(0.4, 1.1);
  assert.equal(patches(), before + 1);
  assert.equal(S(c).trimDraft, null);
  let server = (await takesOnServer(c, BRIEF)).items.find((x) => x.id === t.id);
  assert.deepEqual([server.trimStartSec, server.trimEndSec, server.renderReady], [0.4, 1.1, false]);
  await c.actions.commitTrim(0.4, t.sourceDurationSec);
  server = (await takesOnServer(c, BRIEF)).items.find((x) => x.id === t.id);
  assert.equal(server.trimEndSec, null, 'an end at the end of the source is sent as null');
});

test('even-out is source-affecting too: it invalidates the render', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  const t = S(c).line.takes[1];
  await c.actions.setEven(false);
  let s = (await takesOnServer(c, BRIEF)).items.find((x) => x.id === t.id);
  assert.deepEqual([s.evenOutVolume, s.renderReady], [false, false]);
});

test('origin and consent: a new origin withdraws the consent; confirming sends the statement version; un-ticking withdraws it', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  await record(c);
  const id = S(c).selTakeId;
  const get = async () => (await takesOnServer(c, EMPTY)).items.find((x) => x.id === id);
  await c.actions.setConsent(true);
  assert.deepEqual([(await get()).consent.confirmed, (await get()).consent.statementVersion], [true, 'consent-1']);
  await c.actions.setOrigin('ai-assisted');
  assert.deepEqual([(await get()).origin, (await get()).consent.confirmed], ['ai-assisted', false], 'the old consent no longer applies');
  await c.actions.setConsent(true);
  await c.actions.setConsent(false);
  assert.equal((await get()).consent.confirmed, false);
});

test('favourite is only a preference: it does not publish; removing a published take is refused until it is taken out of the pack', async () => {
  const c = await setup();
  await onLine(c, 'morning_wake_002');                 // two takes, the second published
  const [t1, t2] = S(c).line.takes;
  assert.equal(S(c).line.publishedTakeId, t2.id);
  await c.actions.toggleFavourite(t1.id);
  assert.equal((await takesOnServer(c, 'morning_wake_002')).favouriteTakeId, t1.id);
  assert.equal((await takesOnServer(c, 'morning_wake_002')).publishedTakeId, t2.id, 'still the published one');
  await c.actions.toggleFavourite(t1.id);
  assert.equal((await takesOnServer(c, 'morning_wake_002')).favouriteTakeId, null);
  assert.equal(await c.actions.removeTake(t2.id), false);
  assert.equal(c.toasts.at(-1)[0], 'take_in_use');
  assert.equal(await c.actions.removeTake(t1.id), true);
  assert.equal((await takesOnServer(c, 'morning_wake_002')).items.length, 1);
  await c.actions.selectTake(t2.id);
  assert.equal(await c.actions.unpublish(), true);
  assert.equal(S(c).line.publishedTakeId, null);
  assert.equal(await c.actions.removeTake(t2.id), true);
});

test('local playback plays the trimmed, evened-out take at 48 kHz; the same key stops it; the end resets the state', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  await c.actions.playTake(); await settleAll();
  assert.equal(c.player.played.length, 1);
  assert.equal(c.player.played[0].rate, TARGET_RATE);
  assert.ok(S(c).playingTake);
  const t = S(c).line.takes[1];
  assert.ok(Math.abs(c.player.played[0].n / TARGET_RATE - (t.trimEndSec - t.trimStartSec)) < 0.02, 'the trim is applied');
  await c.actions.playTake();
  assert.equal(S(c).playingTake, false, 'pressing again stops');
  await c.actions.playTake(); await settleAll();
  c.player.finish();
  assert.equal(S(c).playingTake, false);
  assert.equal(log(c, 'play').length, 0, 'the game is not involved');
});

// ---------------- Play ----------------
test('Play original: only the original is requested; no consent, no save, no publish', async () => {
  const c = await setup();
  await onLine(c, EMPTY);
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playOriginal(), true);
  assert.deepEqual(log(c, 'saveSubtitle', 'putTakeAudio', 'publish', 'play'), ['play']);
  assert.equal(c.dev.dev.log.find((l) => l.method === 'play').args[1], 'original');
  assert.equal(S(c).speaking, 'orig');
  assert.equal(S(c).waiting, null);
  await c.clk.advance(10000);
  assert.equal(S(c).speaking, null, 'the "speaking" state ends by itself');
});

test('Play my version, in W2\'s order: save the text, upload the render, publish, then play', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  await record(c);
  await c.actions.setConsent(true);
  c.actions.editText('ซับที่ยังไม่ได้บันทึก');            // dirty text
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), true);
  assert.deepEqual(log(c, 'saveSubtitle', 'putTakeAudio', 'publish', 'play'), ['saveSubtitle', 'putTakeAudio', 'publish', 'play']);
  const srv = await lineOnServer(c, EMPTY);
  assert.equal(srv.ownedSubtitle, 'ซับที่ยังไม่ได้บันทึก');
  assert.equal(srv.ownedDub, true);
  assert.equal(srv.publishedTakeId, S(c).selTakeId);
  assert.equal(c.dev.dev.log.find((l) => l.method === 'play').args[1], 'dub');
  assert.equal(S(c).speaking, 'mine');
});

test('Play my version without consent: nothing is saved, uploaded, published or played; the statement opens', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  await record(c);
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), false);
  assert.deepEqual(log(c, 'putTakeAudio', 'publish', 'play'), []);
  assert.equal(c.toasts.at(-1)[0], 'need_consent');
  assert.equal(S(c).menu, 'consent');
});

test('the first failure stops the sequence: a failed render upload never publishes, a failed publish never plays', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  await record(c); await c.actions.setConsent(true);
  c.dev.dev.fail('putTakeAudio', 'storage_limit', { times: 1 });
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), false);
  assert.deepEqual(log(c, 'publish', 'play'), []);
  assert.equal(c.toasts.at(-1)[0], 'e_storage_limit');
  c.dev.dev.fail('publish', 'recovery_required', { times: 1 });
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), false);
  assert.deepEqual(log(c, 'play'), [], 'publish failed, so the game is never asked');
  assert.equal(c.toasts.at(-1)[0], 'e_recovery_required');
  assert.equal(S(c).playing, false, 'the buttons come back');
});

test('an already published take with a current render is not uploaded or published again: only play', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), true);
  assert.deepEqual(log(c, 'saveSubtitle', 'putTakeAudio', 'publish', 'play'), ['play']);
});

test('after a trim the old render is stale: it is prepared again and published again before the game plays it', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  await c.actions.commitTrim(0.2, 1.0);
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), true);
  assert.deepEqual(log(c, 'putTakeAudio', 'publish', 'play'), ['putTakeAudio', 'publish', 'play']);
  const kinds = c.dev.dev.log.filter((l) => l.method === 'putTakeAudio').map((l) => l.args[4]);
  assert.deepEqual(kinds, ['render']);
});

test('studioPlay is never repeated automatically, not even after an uncertain failure', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  c.dev.dev.fail('play', 'unavailable', { times: 1, uncertain: true });
  c.dev.dev.log.length = 0;
  await c.actions.playOriginal();
  await c.clk.advance(120000);
  assert.equal(log(c, 'play').length, 1);
  assert.equal(S(c).playing, false);
});

test('a second Play press while one is running is ignored', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  const original = c.dev.studioPlay;
  let release; const gate = new Promise((r) => { release = r; });
  c.dev.studioPlay = async (...a) => { await gate; return original(...a); };
  c.dev.dev.log.length = 0;
  const first = c.actions.playOriginal();
  await tick();
  assert.equal(S(c).playing, true);
  assert.equal(await c.actions.playOriginal(), false);
  release(); await first;
  assert.equal(log(c, 'play').length, 1);
});

test('a paused game is explained, the take stays published, and the buttons come back', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  await record(c); await c.actions.setConsent(true);
  c.dev.dev.setStatus({ paused: true, focused: false });
  assert.equal(await c.actions.playMine(), false);
  assert.equal(c.toasts.at(-1)[0], 't_in_pack_hint', 'the creator is told the take is already in the pack and only playing failed');
  assert.equal((await lineOnServer(c, EMPTY)).ownedDub, true, 'publishing happened; only the playing failed');
  assert.deepEqual([S(c).playing, S(c).waiting], [false, null]);
});

// ---------------- features ----------------
test('capabilities decide what the screen offers: no dubs, no playback, no workflow, no subtitles', async () => {
  const none = await setup({ dev: { features: ['catalog'] } });
  assert.deepEqual(S(none).features, ['catalog']);
  assert.equal(missingFeature(S(none)), 'subtitles');
  assert.equal(canEditText(S(none)), false);
  assert.equal(canDub(S(none)), false);
  assert.equal(canPlay(S(none)), false);
  none.mic.calls.length = 0;
  await none.actions.setupMic();
  assert.deepEqual(none.mic.calls, [], 'no microphone prompt for a loader without dubs');
  none.dev.dev.log.length = 0;
  assert.equal(await none.actions.playOriginal(), false);
  assert.deepEqual(log(none, 'play'), []);
  assert.equal(none.toasts.at(-1)[0], 'feat_playback');
  assert.equal(await none.actions.importFile({ name: 'x', decoded: { channels: [sine(8000, 8000, 200)], sampleRate: 8000 } }), false);
});

test('a loader without projects cannot create packs or open history (and says so), but can still edit text', async () => {
  const c = await setup({ dev: { features: ['catalog', 'subtitles', 'workflow', 'dubs', 'playback'] } });
  assert.equal(missingFeature(S(c)), 'projects');
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.createPack({ name: 'Nope', lang: 'th' }), false);
  assert.deepEqual(log(c, 'createPack'), []);
  assert.equal(c.toasts.at(-1)[0], 'feat_newpacks');
  c.actions.editText('ยังแก้ซับได้');
  await c.clk.advance(900);
  assert.equal((await lineOnServer(c, BRIEF)).ownedSubtitle, 'ยังแก้ซับได้');
});

test('W2 v2.1: "projects" (history, glossary, operations) does not enable pack creation; only "newPacks" does', async () => {
  const c = await setup({ dev: { features: ['catalog', 'subtitles', 'workflow', 'dubs', 'projects', 'playback'] } }); // what the real and fake loaders advertise today
  assert.equal(missingFeature(S(c)), null, 'nothing the page needs is missing: no banner for the absent pack creation');
  assert.equal(canCreatePacks(S(c)), false);
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.createPack({ name: 'Nope', lang: 'th' }), false);
  assert.deepEqual(log(c, 'createPack'), [], 'the loader is never asked');
  assert.equal(c.toasts.at(-1)[0], 'feat_newpacks');
  await c.actions.openHistory();
  assert.equal(log(c, 'history').length, 1, 'history is still offered');
  const withNew = await setup({ dev: { features: ['catalog', 'subtitles', 'workflow', 'dubs', 'projects', 'newPacks', 'playback'] } });
  assert.equal(canCreatePacks(S(withNew)), true);
});

test('a loader without the catalogue is "too old"; unknown capabilities count as available', async () => {
  await assert.rejects(setup({ dev: { features: ['subtitles'] } }), /contract or catalogue missing|not_found/);
  assert.equal(featureOn({ features: null }, 'dubs'), true);
  assert.equal(featureOn({ features: ['dubs'] }, 'dubs'), true);
  assert.equal(featureOn({ features: ['dubs'] }, 'playback'), false);
  const full = await setup();
  assert.equal(missingFeature(S(full)), null);
});

test('moving to another line stops local playback and cancels a recording in progress', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  c.actions.toggleRecord(); await c.clk.advance(3500);
  assert.equal(S(c).rec, 'recording');
  await c.actions.selectLine('morning_wake_005'); await settleAll();
  assert.equal(S(c).rec, 'idle');
  assert.equal((await takesOnServer(c, EMPTY)).items.length, 0, 'a cancelled recording leaves no take');
});

test('the waveforms of takes already in the pack are read back from the creator\'s own uploads', async () => {
  const c = await setup();
  await onLine(c, BRIEF);
  await settleAll();
  for (const t of S(c).line.takes) assert.equal(S(c).peaks[t.id]?.length, 96, t.id);
});

// ---------------- M4b: the microphone lets go, and Play says what really failed ----------------
test('the microphone is released after 60 s with no recording and no count-in (and the screen says it is off)', async () => {
  assert.equal(MIC_IDLE_MS, 60000);
  const c = await setup();
  await ready(c);
  await c.clk.advance(MIC_IDLE_MS - 100);
  assert.equal(S(c).mic, 'ready');
  assert.ok(!c.mic.calls.some((x) => x[0] === 'release'));
  await c.clk.advance(200);
  assert.equal(S(c).mic, 'off');
  assert.ok(c.mic.calls.some((x) => x[0] === 'release'), 'the tracks are let go, so the browser\'s recording indicator goes out');
  assert.deepEqual([S(c).level, S(c).lvl], ['idle', 0]);
});

test('a count-in or a recording keeps the microphone; finishing or cancelling a recording releases the microphone immediately', async () => {
  const c = await setup({ micIdleMs: 5000 });
  await onLine(c, EMPTY); await ready(c);
  await c.clk.advance(4000);
  c.actions.toggleRecord();                          // the count-in takes 3 s, the recording 1.5 s: well past 5 s since set-up
  await c.clk.advance(3000); await c.clk.advance(1500);
  assert.equal(S(c).mic, 'ready', 'never let go while recording');
  c.actions.toggleRecord(); await settleAll();
  assert.equal(S(c).mic, 'off', 'released as soon as the recording finishes');
  assert.equal(c.mic.calls.filter((x) => x[0] === 'release').length, 1);

  await ready(c);
  c.actions.toggleRecord();
  await c.clk.advance(1500);
  c.actions.cancelRecord();
  assert.equal(S(c).mic, 'off', 'released as soon as a count-in or recording is cancelled');
  assert.equal(c.mic.calls.filter((x) => x[0] === 'release').length, 2);
});

test('hiding the page releases an idle microphone but never one that is counting in or recording', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  assert.equal(c.actions.releaseMic(), true, 'idle');
  assert.equal(S(c).mic, 'off');
  assert.equal(c.actions.releaseMic(), false, 'already off');
  await ready(c);
  c.actions.toggleRecord();
  assert.equal(c.actions.releaseMic(), false, 'count-in');
  await c.clk.advance(3000);
  assert.equal(c.actions.releaseMic(), false, 'recording');
  c.actions.toggleRecord(); await settleAll();
  assert.equal(S(c).mic, 'off', 'released when recording ended');
});

test('R wakes a released microphone on the same device (no new prompt is needed) and then starts the count-in', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c);
  await c.actions.selectDevice('Mic B'); await settleAll();
  c.actions.releaseMic();
  const setups = () => c.mic.calls.filter((x) => x[0] === 'setup');
  const before = setups().length;
  await c.actions.toggleRecord();
  assert.equal(setups().length, before + 1);
  assert.deepEqual(setups().at(-1), ['setup', 'b'], 'the same device as before');
  assert.deepEqual([S(c).mic, S(c).rec], ['ready', 'countdown']);
  c.actions.cancelRecord();
});

test('"Set up mic" also wakes it; if waking fails the screen says why instead of pretending', async () => {
  const c = await setup();
  await ready(c); c.actions.releaseMic();
  assert.equal(await c.actions.setupMic(), true);
  assert.equal(S(c).mic, 'ready');
  c.actions.releaseMic();
  c.mic.setup = async () => { const e = new Error('x'); e.name = 'NotAllowedError'; throw e; };
  await c.actions.toggleRecord();
  assert.deepEqual([S(c).mic, S(c).micError, S(c).rec], ['none', 'mic_denied', 'idle']);
});

test('Play my version: when the game step fails after the publish, the creator is told the take is already in the pack', async () => {
  // game paused
  const a = await setup();
  await onLine(a, EMPTY); await ready(a); await record(a); await a.actions.setConsent(true);
  a.dev.dev.setStatus({ paused: true, focused: false });
  assert.equal(await a.actions.playMine(), false);
  assert.deepEqual(a.toasts.at(-1).slice(0, 2), ['t_in_pack_hint', 'warn']);
  assert.equal((await lineOnServer(a, EMPTY)).ownedDub, true);
  // any other game failure: in the pack, with the reason
  const b = await setup();
  await onLine(b, EMPTY); await ready(b); await record(b); await b.actions.setConsent(true);
  b.dev.dev.fail('play', 'not_ready', { times: 1 });
  assert.equal(await b.actions.playMine(), false);
  assert.equal(b.toasts.at(-1)[0], 't_in_pack_play_failed');
  assert.equal(b.toasts.at(-1)[2].reason, 'reason:not_ready');
  // a take that was already in the pack
  const d = await setup();
  await onLine(d, BRIEF);
  d.dev.dev.setStatus({ paused: true, focused: false });
  assert.equal(await d.actions.playMine(), false);
  assert.equal(d.toasts.at(-1)[0], 't_in_pack_hint');
});

test('a failure BEFORE the take is in the pack is not described as "in the pack"', async () => {
  const c = await setup();
  await onLine(c, EMPTY); await ready(c); await record(c); await c.actions.setConsent(true);
  c.dev.dev.fail('publish', 'recovery_required', { times: 1 });
  assert.equal(await c.actions.playMine(), false);
  assert.equal(c.toasts.at(-1)[0], 'e_recovery_required');
  assert.equal((await lineOnServer(c, EMPTY)).ownedDub, false);
});

test('when the loader offers dubs without playback, T still renders and publishes the take into the pack without calling play', async () => {
  const c = await setup({ dev: { features: ['catalog', 'workflow', 'subtitles', 'dubs', 'projects', 'newPacks'] } });
  await onLine(c, EMPTY); await ready(c); await record(c); await c.actions.setConsent(true);
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), true);
  assert.deepEqual(log(c, 'putTakeAudio', 'publish', 'play'), ['putTakeAudio', 'publish']);
  assert.equal((await lineOnServer(c, EMPTY)).ownedDub, true);
  assert.equal(S(c).line.publishedTakeId, S(c).selTakeId);
});

// Subtitle-only creators use the same settle/queue/play path, without making a take.
test('subtitle-only Play my version saves current text then plays sub with no audio writes', async () => {
  const c = await setup(); await onLine(c, EMPTY);
  c.actions.editText('ซับของฉัน ไม่มีเสียงพากย์');
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), true);
  assert.deepEqual(log(c, 'saveSubtitle', 'putTakeAudio', 'publish', 'play'), ['saveSubtitle', 'play']);
  assert.equal(c.dev.dev.log.find(l => l.method === 'play').args[1], 'sub');
  assert.equal((await lineOnServer(c, EMPTY)).ownedSubtitle, 'ซับของฉัน ไม่มีเสียงพากย์');
  assert.equal(S(c).speaking, 'mine');
  assert.equal(S(c).waiting, null);
  assert.equal(c.toasts.at(-1)[0], 't_mine_sub');
  assert.equal(S(c).line.takes.length, 0);
  await c.clk.advance(10000); assert.equal(S(c).speaking, null);
});

test('subtitle-only Play works when dubbing feature is absent and refuses an empty contribution', async () => {
  const c = await setup({dev: {features: ['catalog', 'workflow', 'subtitles', 'projects', 'playback']}}); await onLine(c, EMPTY);
  assert.equal(canDub(S(c)), false);
  c.actions.editText('Translation only');
  assert.equal(await c.actions.playMine(), true);
  assert.equal(c.dev.dev.log.findLast(l => l.method === 'play').args[1], 'sub');
  c.actions.editText('   '); c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), false);
  assert.equal(log(c, 'play').length, 0);
  assert.equal(c.toasts.at(-1)[0], 'mine_empty');
});

test('subtitle-only Play stops after failed save and after already-speaking refusal without retry', async () => {
  const c = await setup(); await onLine(c, EMPTY);
  c.actions.editText('Unsaved translation'); c.dev.dev.fail('saveSubtitle', 'read_only_pack');
  c.dev.dev.log.length = 0;
  assert.equal(await c.actions.playMine(), false);
  assert.equal(log(c, 'play').length, 0);
  // A fresh line avoids carrying the intentionally failed draft into the busy case.
  const b = await setup(); await onLine(b, EMPTY); b.actions.editText('Translation');
  b.dev.dev.fail('play', 'not_ready'); b.dev.dev.log.length = 0;
  assert.equal(await b.actions.playMine(), false);
  await b.clk.advance(120000);
  assert.equal(log(b, 'play').length, 1);
  assert.equal(b.toasts.at(-1)[0], 'e_not_ready');
  assert.equal(S(b).playing, false);
});

test('subtitle-only hero button offers original voice and keeps read-only, pending and offline gates', async () => {
  const c = await setup(); await onLine(c, EMPTY); c.actions.editText('Translation');
  const T = makeT('en');
  const button = (state) => renderHero(state, {T}).match(/<button[^>]*data-act="play-mine"[^>]*>/)[0];
  assert.match(button(S(c)), /original voice/);
  assert.doesNotMatch(button(S(c)), / disabled/);
  for (const patch of [
    {packs: S(c).packs.map(p => ({...p, editable:false}))},
    {packs: S(c).packs.map(p => ({...p, restartRequired:true}))},
    {conn: {connected:false}}
  ]) assert.match(button({...S(c), ...patch}), / disabled/);
});
