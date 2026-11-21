import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { filterLines, nextUnrecordedLine, nextUnfinishedLine, isUnrecorded, loadSettings, saveSettings, SETTINGS_KEY } from '../../ui/logic/progress.js';
import { visibleLines } from '../../ui/selectors.js';
import { makeDefaultIconPng } from '../../ui/api/dev-phase-c.js';

const P = 'satone-th-dub', LANG = 'th';
const tick = () => new Promise((r) => setImmediate(r));
const settleAll = async () => { for (let i = 0; i < 14; i++) await tick(); };
const sine = (n, rate, hz, amp = 0.25) => Float32Array.from({ length: n }, (_, i) => amp * Math.sin(2 * Math.PI * hz * i / rate));

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

function fakeMic({ seconds = 1.2, rate = 44100 } = {}) {
  const calls = [];
  return {
    calls,
    supported: () => true,
    async setup(id) { calls.push(['setup', id]); return { devices: [{ id: 'a', label: 'Mic A' }], deviceId: id || 'a' }; },
    onLevel() { return () => {}; },
    start() { calls.push(['start']); },
    async stop() { calls.push(['stop']); return { channels: [sine(Math.round(seconds * rate), rate, 220)], sampleRate: rate }; },
    release() { calls.push(['release']); }
  };
}

function fakePlayer() {
  return { play() {}, stop() {}, position: () => 0 };
}

async function setup(o = {}) {
  const dev = createDevBackend({ lines: 120, ...o.dev });
  const store = createStore({
    ready: false, conn: { connected: false, unauthorized: false }, status: null, packs: [], packId: null, packLang: 'th', groups: [], overall: {}, lines: [],
    byId: new Map(), loadingLines: true, view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, history: null, saveNote: null, menu: null,
    selTakeId: null, peaks: {}, last: null, scrollTo: null, mic: 'none', micError: null, devices: [], deviceIds: [], rec: 'idle', level: 'idle', lvl: 0, count: 3,
    recElapsed: 0, livePeaks: [], features: null, consentVersion: null, trimDraft: null, playing: false, playingTake: false, speaking: null, waiting: null,
    modal: null, packDetails: {}, packIcons: {}, newPackIconBytes: null, newPackIconUrl: null, exportPreview: null, exportBusy: false, exportResult: null, exportError: null, sessionConsent: false
  });
  const toasts = [], clk = clock(), mic = o.mic || fakeMic(), player = fakePlayer();
  let seq = 0;
  const actions = createActions({
    store, api: createApi(dev), mode: 'dev', toast: (k, kind, v) => toasts.push([k, kind, v]),
    timers: clk, reconnectMs: 100, selectDelayMs: 0,
    newOp: () => (++seq).toString(16).padStart(32, 'c'), now: () => clk.time,
    mic, player, reasonText: (e) => `reason:${e.code}`
  });
  await actions.start({});
  await settleAll();
  return { dev, store, actions, toasts, clk, mic };
}
const S = (c) => c.store.get();

test('Phase B1: filterLines and visibleLines filter by voice, character, and clip/speaker search', async () => {
  const c = await setup();
  const all = S(c).lines;
  const voiced = filterLines(all, { filter: 'all', voice: 'voiced' });
  const unvoiced = filterLines(all, { filter: 'all', voice: 'unvoiced' });
  assert.ok(voiced.length > 0 && unvoiced.length > 0);
  assert.equal(voiced.length + unvoiced.length, all.length);
  assert.ok(voiced.every((l) => Boolean(l.voice)));
  assert.ok(unvoiced.every((l) => !l.voice));

  c.actions.setVoiceFilter('voiced');
  assert.equal(visibleLines(S(c)).length, voiced.length);
  c.actions.setVoiceFilter('unvoiced');
  assert.equal(visibleLines(S(c)).length, unvoiced.length);
  c.actions.setVoiceFilter('all');
  assert.equal(S(c).view.voice, undefined, 'resetting to all keeps view shape clean');

  c.actions.setCharFilter('satone');
  const satone = visibleLines(S(c));
  assert.equal(satone.length, voiced.length, 'Satone speaks the voiced lines');
  c.actions.setCharFilter('none');
  const narration = visibleLines(S(c));
  assert.equal(narration.length, unvoiced.length, 'unvoiced lines are narration/UI');
  c.actions.setCharFilter('all');

  const sampleVoice = voiced[0].voice;
  c.actions.setSearch(sampleVoice);
  const byVoice = visibleLines(S(c));
  assert.ok(byVoice.some((l) => l.id === voiced[0].id), 'search matches voice clip name');
  c.actions.setSearch('');
});

test('Phase B1: nextUnrecordedLine and actions.nextUnrecorded jump to the next voiced line without a published dub', async () => {
  const c = await setup();
  await c.actions.selectLine('morning_wake_001');
  await settleAll();
  const next = nextUnrecordedLine(S(c).lines, 'morning_wake_001');
  assert.equal(next?.id, 'morning_wake_004', '001..003 have dubs; 004 is the first unrecorded voiced line after 001');
  assert.equal(isUnrecorded(next), true);

  await c.actions.nextUnrecorded();
  await settleAll();
  assert.equal(S(c).lineId, 'morning_wake_004');
  assert.equal(S(c).scrollTo, 'morning_wake_004');
});

test('Phase B1: easier recording flow — quickRecord sets up mic & records; keepAndNext confirms consent once, publishes and advances', async () => {
  const c = await setup();
  await c.actions.selectLine('morning_wake_006');
  await settleAll();
  assert.equal(S(c).mic, 'none');

  // 1. quickRecord sets up the mic automatically and begins the 3-2-1 count-in
  await c.actions.quickRecord();
  await settleAll();
  assert.equal(S(c).mic, 'ready');
  assert.equal(S(c).rec, 'countdown');
  await c.clk.advance(3000);
  assert.equal(S(c).rec, 'recording');
  await c.clk.advance(1200);

  // 2. quickRecord again stops recording and saves the take
  await c.actions.quickRecord();
  await settleAll();
  assert.equal(S(c).rec, 'idle');
  assert.equal(S(c).line.takes.length, 1);

  // 3. First keepAndNext opens the consent statement because sessionConsent is not yet confirmed
  await c.actions.keepAndNext();
  await settleAll();
  assert.equal(S(c).menu, 'consent');
  await c.actions.setConsent(true);
  await settleAll();
  assert.equal((await c.dev.studioLine(P, LANG, 'morning_wake_006')).ownedDub, true, 'morning_wake_006 take published into the pack');
  const secondUnrec = S(c).lineId;
  assert.notEqual(secondUnrec, 'morning_wake_006', 'advanced automatically to the next unrecorded line');
  assert.equal(isUnrecorded(S(c).byId.get(secondUnrec)), true, 'the newly selected line is an unrecorded voiced line');

  // 4. Record on the new unrecorded line and click keepAndNext: no second consent prompt needed!
  await c.actions.quickRecord();
  await settleAll();
  await c.clk.advance(3000);
  await c.clk.advance(1200);
  await c.actions.quickRecord();
  await settleAll();
  assert.equal(S(c).line.takes.length, 1);
  await c.actions.keepAndNext();
  await settleAll();
  assert.equal((await c.dev.studioLine(P, LANG, secondUnrec)).ownedDub, true, 'published in one click without reopening consent menu');
  assert.notEqual(S(c).lineId, secondUnrec, 'advanced to the following unrecorded line');
});

test('Phase C: Packs view, New pack form (author, description, custom id, icon, new language), and Export checks + zip download', async () => {
  const c = await setup();

  // 1. Open Packs modal and load pack details + icons
  await c.actions.openModal('packs');
  await settleAll();
  assert.equal(S(c).modal, 'packs');
  assert.ok(S(c).packInfos[P], 'loaded packInfo for current pack');
  assert.equal(S(c).packInfos[P].author, 'Creator (TH)');
  assert.ok(S(c).packIcons[P], 'loaded pack icon URL');

  // 2. Upload custom icon for current pack
  const customPng = makeDefaultIconPng('custom-test');
  await c.actions.uploadPackIcon(P, customPng);
  await settleAll();
  assert.equal(S(c).packInfos[P].hasIcon, true);

  // 3. Run export checks and build + download zip
  await c.actions.openModal('export');
  await settleAll();
  assert.equal(S(c).modal, 'export');
  const prev = S(c).exportPreview;
  assert.ok(prev, 'exportPreview loaded');
  assert.equal(prev.ok, true);
  assert.ok(prev.checks.length >= 6);
  assert.equal(prev.nextVersions.minor, '0.4.0');

  await c.actions.runExport({ bump: 'minor', notes: 'Added Thai voice lines for morning scene' });
  await settleAll();
  const res = S(c).exportResult;
  assert.ok(res && res.fileName.endsWith('.zip'));
  assert.equal(res.version, '0.4.0');
  assert.ok(res.sha256.length === 64);
  assert.equal(S(c).lastDownloadedZip?.magic, 'PK', 'auto-downloaded valid ZIP archive');

  // 4. Create a new pack with custom id, author, description, new language (vi), and icon
  await c.actions.openModal('newpack');
  const iconBytes = makeDefaultIconPng('satone-vi-pack');
  await c.actions.createPack({
    name: 'Satone Vietnamese Pack',
    id: 'satone-vi-pack',
    author: 'Lan Nguyen',
    description: 'Vietnamese subtitle & dub pack',
    lang: 'vi',
    iconBytes
  });
  await settleAll();
  assert.equal(S(c).modal, null, 'modal closes after creating pack');
  assert.equal(S(c).packId, 'satone-vi-pack');
  const created = S(c).packs.find((p) => p.id === 'satone-vi-pack');
  assert.ok(created && created.restartRequired === true);
  const info = await c.dev.studioPackInfo('satone-vi-pack');
  assert.equal(info.author, 'Lan Nguyen');
  assert.equal(info.description, 'Vietnamese subtitle & dub pack');
  assert.equal(info.hasIcon, true);
});

test('Phase B2: To do -> Draft automatically when typing a subtitle or keeping a take on a To do line', async () => {
  const c = await setup();

  // 1. Typing a subtitle on a To do line automatically moves it to Draft
  await c.actions.selectLine('morning_wake_006');
  await settleAll();
  assert.equal(S(c).line.status, 'todo');
  c.actions.editText('อรุณสวัสดิ์นะ');
  assert.equal(S(c).line.status, 'draft', 'optimistic To do -> Draft on first typed subtitle');
  await c.clk.advance(900);
  await settleAll();
  const afterSub = await c.dev.studioLine(P, LANG, 'morning_wake_006');
  assert.equal(afterSub.ownedSubtitle, 'อรุณสวัสดิ์นะ');
  assert.equal(afterSub.workflow.status, 'draft', 'server workflow status is now draft');

  // 2. Recording / keeping a take on a To do line automatically moves it to Draft
  await c.actions.selectLine('night_goodnight_002');
  await settleAll();
  assert.equal(S(c).line.status, 'todo');
  await c.actions.quickRecord();
  await settleAll();
  await c.clk.advance(3000);
  await c.clk.advance(1200);
  await c.actions.quickRecord();
  await settleAll();
  assert.equal(S(c).line.status, 'draft', 'recording a take on a To do line moves it to Draft');
  assert.equal((await c.dev.studioLine(P, LANG, 'night_goodnight_002')).workflow.status, 'draft');

  // 3. Manual status change back to To do, then keeping the take moves it to Draft again
  await c.actions.setStatus('todo');
  await settleAll();
  assert.equal((await c.dev.studioLine(P, LANG, 'night_goodnight_002')).workflow.status, 'todo');
  await c.actions.setConsent(true);
  await settleAll();
  await c.actions.keepTake();
  await settleAll();
  assert.equal(S(c).line.status, 'draft', 'keeping a take on a To do line moves it to Draft');
  assert.equal((await c.dev.studioLine(P, LANG, 'night_goodnight_002')).workflow.status, 'draft');
});

test('Phase B2: completeLine marks Done and jumps to next unfinished line (same scene first); settings control auto-Done and jump', async () => {
  // 1. Settings persistence and broken storage resilience
  const mem = new Map();
  const fakeStorage = { getItem: (k) => mem.get(k) ?? null, setItem: (k, v) => mem.set(k, v) };
  const brokenStorage = { getItem: () => { throw new Error('denied'); }, setItem: () => { throw new Error('quota'); } };
  assert.deepEqual(loadSettings(fakeStorage), { autoDoneOnNext: false, jumpAfterDone: true });
  assert.deepEqual(loadSettings(brokenStorage), { autoDoneOnNext: false, jumpAfterDone: true });
  assert.doesNotThrow(() => saveSettings({ autoDoneOnNext: true, jumpAfterDone: false }, brokenStorage));
  saveSettings({ autoDoneOnNext: true, jumpAfterDone: false }, fakeStorage);
  assert.deepEqual(loadSettings(fakeStorage), { autoDoneOnNext: true, jumpAfterDone: false });
  assert.ok(mem.has(SETTINGS_KEY));

  const c = await setup();

  // 2. Same-scene-first nextUnfinishedLine and completeLine()
  // In 'morning' scene: 001..003 are 'done', 004 is 'draft', 005 is 'done', 006 is 'todo'
  await c.actions.selectLine('morning_wake_004');
  await settleAll();
  assert.equal(S(c).line.status, 'draft');
  const expectedNext = nextUnfinishedLine(S(c).lines, 'morning_wake_004', 'morning');
  assert.equal(expectedNext?.id, 'morning_wake_006', 'skips morning_wake_005 (already done) and picks morning_wake_006 in same scene');

  const jumpedTo = await c.actions.completeLine();
  await settleAll();
  assert.equal(jumpedTo, 'morning_wake_006');
  assert.equal(S(c).lineId, 'morning_wake_006');
  assert.equal((await c.dev.studioLine(P, LANG, 'morning_wake_004')).workflow.status, 'done');

  // Completing the last unfinished line of a scene jumps to the next scene's first unfinished line
  const synthUnfinished = [
    { id: 'm1', group: 'morning', status: 'done' },
    { id: 'm2', group: 'morning', status: 'draft' },
    { id: 'c1', group: 'click', status: 'done' },
    { id: 'c2', group: 'click', status: 'todo' }
  ];
  assert.equal(nextUnfinishedLine(synthUnfinished, 'm2', 'morning')?.id, 'c2', 'falls back to next scene when current scene has no other unfinished lines');

  // 3. When jumpAfterDone is off, completeLine marks Done and stays on the same line
  c.actions.setSetting('jumpAfterDone', false);
  const stayId = S(c).lineId;
  await c.actions.completeLine();
  await settleAll();
  assert.equal(S(c).lineId, stayId, 'stayed on the line when jumpAfterDone is off');
  assert.equal(S(c).line.status, 'done');

  // 4. Moving to the next line: no status change when autoDoneOnNext is off (default), marks Done when on
  await c.actions.selectLine('clickheroine_rest_011');
  await settleAll();
  assert.equal(S(c).line.status, 'draft');
  await c.actions.moveLine(1);
  await settleAll();
  assert.equal((await c.dev.studioLine(P, LANG, 'clickheroine_rest_011')).workflow.status, 'draft', 'status unchanged when autoDoneOnNext is off');

  c.actions.setSetting('autoDoneOnNext', true);
  await c.actions.selectLine('clickheroine_rest_011');
  await settleAll();
  assert.equal(S(c).line.status, 'draft');
  await c.actions.moveLine(1);
  await settleAll();
  assert.equal((await c.dev.studioLine(P, LANG, 'clickheroine_rest_011')).workflow.status, 'done', 'marked Done automatically when moving to next line with autoDoneOnNext on');
});

test('Phase B1 (contract v2.4): pack voice default (GET/PUT P/voice), inherited takes (consentSource:"pack"), and per-take override (consentSource:"take")', async () => {
  const c = await setup();
  assert.equal(S(c).voiceDefault?.origin, null, 'pack starts with no voice default');

  // 1. Set pack voice default to ai-assisted + consent-1
  c.actions.setPackVoiceDraft('ai-assisted');
  assert.equal(S(c).packVoiceDraft, 'ai-assisted');
  const savedVd = await c.actions.savePackVoice();
  await settleAll();
  assert.equal(savedVd?.origin, 'ai-assisted');
  assert.equal(savedVd?.consent?.confirmed, true);
  assert.equal(S(c).voiceDefault?.origin, 'ai-assisted');
  assert.equal((await c.dev.studioVoiceDefault(P)).origin, 'ai-assisted');

  // 2. Record a take on an empty line: sends no origin/consent, inherits pack default (consentSource: "pack"), does not open consent menu
  await c.actions.selectLine('morning_wake_006');
  await settleAll();
  await c.actions.quickRecord();
  await settleAll();
  await c.clk.advance(3000);
  await c.clk.advance(1200);
  await c.actions.quickRecord();
  await settleAll();

  assert.equal(S(c).menu, null, 'no per-take consent prompt when pack default is set');
  assert.equal(S(c).line.takes.length, 1);
  const t1 = S(c).line.takes[0];
  assert.equal(t1.origin, 'ai-assisted', 'inherited pack origin');
  assert.equal(t1.consent.confirmed, true, 'inherited confirmed consent');
  assert.equal(t1.consentSource, 'pack', 'marked as inherited from pack default');

  // 2.5: Changing pack voice default updates inherited takes (consentSource: "pack") and reloads open line takes
  c.actions.setPackVoiceDraft('own');
  await c.actions.savePackVoice();
  await settleAll();
  assert.equal(S(c).line.takes[0].origin, 'own', 'v2.5: open line inherited take reloaded after own PUT P/voice');
  assert.equal(S(c).line.takes[0].consentSource, 'pack');

  // 3. Override per take: reveals per-take controls and PATCHes take to consentSource: "take" without changing pack default
  c.actions.overrideTake(t1.id);
  assert.equal(S(c).takeOverrides[t1.id], true);
  await c.actions.setOrigin('ai-generated');
  await settleAll();
  await c.actions.setConsent(true);
  await settleAll();
  const t1After = S(c).line.takes[0];
  assert.equal(t1After.origin, 'ai-generated');
  assert.equal(t1After.consent.confirmed, true);
  assert.equal(t1After.consentSource, 'take', 'PATCH with origin/consent sets consentSource to take');
  assert.equal((await c.dev.studioVoiceDefault(P)).origin, 'own', 'pack default unchanged by per-take override');

  // 3b. v2.5.1: Re-link overridden take to pack default with followPackDefault: true
  await c.actions.followPackDefaultTake(t1.id);
  await settleAll();
  assert.equal(S(c).line.takes[0].origin, 'own');
  assert.equal(S(c).line.takes[0].consentSource, 'pack', 'followPackDefault: true restores consentSource: pack');

  // 4. Clear pack voice default
  await c.actions.clearPackVoice();
  await settleAll();
  assert.equal(S(c).voiceDefault?.origin, null);
  assert.equal((await c.dev.studioVoiceDefault(P)).origin, null);
  assert.equal(S(c).line.takes[0].origin, 'own', 'v2.5: clearing pack default leaves existing takes at last default');
});

test('Phase B1 (contract v2.4 / v2.5.1): first record with no pack default asks once and sets pack default; bulk apply sends followPackDefault: true', async () => {
  const c = await setup();
  await c.actions.selectLine('morning_wake_006');
  await settleAll();

  // 1. First record when pack has no default prompts once (opens consent menu)
  await c.actions.quickRecord();
  await settleAll();
  await c.clk.advance(3000);
  await c.clk.advance(1200);
  await c.actions.quickRecord();
  await settleAll();

  assert.equal(S(c).menu, 'consent', 'first record with no pack default opens consent prompt once');
  assert.equal(S(c).unconfirmedCount, 1);

  // Confirming sets both the take consent and the pack voice default
  await c.actions.setConsent(true);
  await settleAll();
  assert.equal(S(c).voiceDefault?.origin, 'own');
  assert.equal(S(c).voiceDefault?.consent?.confirmed, true);
  assert.equal(S(c).unconfirmedCount, 0);

  // 2. Make take 1 unconfirmed (by changing origin to ai-assisted) and create an unconfirmed take on another line, then bulk-apply pack default
  await c.actions.setOrigin('ai-assisted');
  await settleAll();
  assert.equal(S(c).line.takes[0].consent.confirmed, false);
  assert.equal(S(c).unconfirmedCount, 1);

  // Also create an unconfirmed take on night_goodnight_002 directly on the backend
  const l2 = await c.dev.studioLine(P, LANG, 'night_goodnight_002');
  await c.dev.studioCreateTake(P, LANG, 'night_goodnight_002', {
    expectedRevision: l2.revision,
    operationId: 'f'.repeat(32),
    origin: 'ai-generated',
    consent: { confirmed: false, statementVersion: 'consent-1' }
  });
  await c.actions.reload({ keepLine: true });
  await settleAll();
  const scanned = await c.actions.scanUnconfirmedTakes(P, LANG, { force: true });
  assert.equal(scanned, 2, 'found both unconfirmed takes across the pack');
  assert.equal(S(c).unconfirmedCount, 2);

  // Change pack default to ai-assisted and apply it to all unconfirmed takes
  c.actions.setPackVoiceDraft('ai-assisted');
  await c.actions.savePackVoice();
  await settleAll();
  const logStart = c.dev.dev.log.length;
  const applied = await c.actions.applyPackVoiceToUnconfirmed();
  await settleAll();
  assert.equal(applied, 2, 'PATCHed both unconfirmed takes');
  const patchCalls = c.dev.dev.log.slice(logStart).filter((entry) => entry.method === 'patchTake');
  assert.equal(patchCalls.length, 2);
  for (const call of patchCalls) {
    assert.equal(call.body?.followPackDefault, true, 'v2.5.1: applyPackVoiceToUnconfirmed sends followPackDefault: true');
    assert.equal(call.body?.origin, undefined, 'v2.5.1: followPackDefault is sent without origin');
    assert.equal(call.body?.consent, undefined, 'v2.5.1: followPackDefault is sent without consent');
  }
  assert.equal(S(c).unconfirmedCount, 0, 'unconfirmed count drops to 0');
  assert.equal(S(c).line.takes[0].origin, 'ai-assisted');
  assert.equal(S(c).line.takes[0].consent.confirmed, true);
  assert.equal(S(c).line.takes[0].consentSource, 'pack');
  const l2Takes = (await c.dev.studioTakes(P, LANG, 'night_goodnight_002')).items;
  assert.equal(l2Takes[0].origin, 'ai-assisted');
  assert.equal(l2Takes[0].consent.confirmed, true);
  assert.equal(l2Takes[0].consentSource, 'pack');
});

test('Task 4b: New pack language refuses built-in game languages and accepts custom language code + display name', async () => {
  const c = await setup();
  for (const builtIn of ['ja', 'en', 'zh-hans', 'zh-hant', 'pt-br', 'ko', 'ru']) {
    const ok = await c.actions.createPack({ name: `Built-in ${builtIn}`, lang: builtIn });
    assert.equal(ok, false, `refuses built-in game language ${builtIn}`);
  }
  assert.ok(c.toasts.some(([k]) => k === 'np_lang_builtin'), 'shows np_lang_builtin warning toast');

  const okFr = await c.actions.createPack({
    name: 'Satone French Pack',
    id: 'satone-fr-pack',
    lang: 'fr',
    langName: 'Français'
  });
  await settleAll();
  assert.equal(okFr, true, 'creates French pack with free code + display name');
  assert.equal(S(c).packId, 'satone-fr-pack');
  assert.equal(S(c).packLang, 'fr');
});

