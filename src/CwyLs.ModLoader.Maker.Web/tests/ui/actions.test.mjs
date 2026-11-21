import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';

const P = 'satone-th-dub', LANG = 'th', ID = 'clickheroine_rest_010', ID2 = 'clickheroine_rest_011';
const tick = () => new Promise((r) => setImmediate(r));
const settleAll = async () => { for (let i = 0; i < 12; i++) await tick(); };

/** A manual clock: timers only fire when the test advances time, so debounce and backoff are exact. */
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
    get size() { return pending.size; },
    get time() { return t; }
  };
}

async function setup(options = {}) {
  const dev = createDevBackend({ lines: 120, ...options.dev });
  const store = createStore({
    ready: false, conn: { connected: false, unauthorized: false }, status: null, packs: [], packId: null, packLang: 'th', groups: [], overall: {},
    lines: [], byId: new Map(), loadingLines: true, view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, history: null,
    saveNote: null, saveLive: null, menu: null, selTakeId: null, peaks: {}, last: null, scrollTo: null
  });
  const toasts = [], clk = clock();
  const actions = createActions({ store, api: createApi(dev), mode: 'dev', toast: (key, kind) => toasts.push([key, kind]), timers: clk, reconnectMs: 100, selectDelayMs: options.selectDelayMs ?? 0,
    newOp: (() => { let n = 0; return () => (++n).toString(16).padStart(32, '0'); })(), now: () => clk.time });
  await actions.start({});
  await settleAll();
  return { dev, store, actions, toasts, clk };
}
const S = (ctx) => ctx.store.get();
const type = async (ctx, text) => { ctx.actions.editText(text); await settleAll(); };

test('start: connects, loads the catalogue, selects the brief line and creates its draft from the server revision', async () => {
  const c = await setup();
  const s = S(c);
  assert.equal(s.conn.connected, true);
  assert.equal(s.packId, P);
  assert.equal(s.lines.length, 120);
  assert.equal(s.lineId, ID);
  assert.equal(s.draft.text, 'ว่าไง? เมื่อกี้เรียกเรารึเปล่า');
  assert.equal(s.draft.status, 'saved');
  assert.equal(s.line.takes.length, 3, 'takes come from studioTakes, merged into the selected line');
  assert.equal(s.line.favouriteTakeId, s.line.takes[1].id);
  assert.ok(s.overall.totalLines === 120);
});

test('typing is debounced: nothing is sent until the timer fires, then one save with the final text', async () => {
  const c = await setup();
  await type(c, 'ก');
  await type(c, 'กข');
  await type(c, 'กขค');
  assert.equal(c.dev.dev.log.filter((l) => l.method === 'saveSubtitle').length, 0);
  assert.equal(S(c).draft.status, 'typing');
  await c.clk.advance(900);
  const saves = c.dev.dev.log.filter((l) => l.method === 'saveSubtitle');
  assert.equal(saves.length, 1);
  assert.equal(saves[0].args[3], 'กขค');
  assert.equal(S(c).draft.status, 'saved');
  assert.equal(S(c).line.text, 'กขค');
  assert.equal(S(c).byId.get(ID).text, 'กขค', 'the list shows what was saved');
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'กขค');
});

test('text typed while a save is in flight is coalesced and saved right after, never lost, never sent twice at once', async () => {
  const c = await setup();
  const original = c.dev.studioSaveSubtitle;
  let release; const gate = new Promise((r) => { release = r; });
  let inFlight = 0, maxInFlight = 0;
  c.dev.studioSaveSubtitle = async (...a) => { inFlight++; maxInFlight = Math.max(maxInFlight, inFlight); if (inFlight === 1 && !release.done) { release.done = true; await gate; } try { return await original(...a); } finally { inFlight--; } };
  await type(c, 'หนึ่ง');
  await c.clk.advance(900);
  assert.equal(S(c).draft.status, 'saving');
  await type(c, 'หนึ่งสอง');
  await type(c, 'หนึ่งสองสาม');
  release();
  await settleAll();
  await c.clk.advance(900);
  assert.equal(maxInFlight, 1, 'one save in flight per line');
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'หนึ่งสองสาม');
  assert.equal(S(c).draft.status, 'saved');
});

test('moving to another line flushes the old one at once; each line keeps its own draft', async () => {
  const c = await setup();
  await type(c, 'ยังพิมพ์ไม่เสร็จ');
  await c.actions.selectLine(ID2);
  await settleAll();
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'ยังพิมพ์ไม่เสร็จ', 'saved when the creator moved on, without waiting for the debounce');
  assert.equal(S(c).lineId, ID2);
  assert.notEqual(S(c).draft.key, `${P}/${LANG}/${ID}`);
  assert.equal(S(c).draft.text, 'นี่ จั๊กจี้นะ!');
});

test('a late answer for a line the creator already left does not land in the new line', async () => {
  const c = await setup();
  const original = c.dev.studioLine;
  let releaseA; const gateA = new Promise((r) => { releaseA = r; });
  c.dev.studioLine = async (p, l, id, o) => { if (id === 'morning_wake_001') await gateA; return original(p, l, id, o); };
  const a = c.actions.selectLine('morning_wake_001');
  await tick();
  await c.actions.selectLine('morning_wake_002');
  releaseA();
  await a;
  await settleAll();
  assert.equal(S(c).lineId, 'morning_wake_002');
  assert.equal(S(c).line.id, 'morning_wake_002');
});

test('conflict: another writer saved; the creator\'s draft survives, theirs is offered, and "keep mine" saves on top of their revision', async () => {
  const c = await setup();
  const l = await c.dev.studioLine(P, LANG, ID);
  await c.dev.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l.revision, operationId: 'a'.repeat(32), text: 'จากแท็บอื่น' });
  await type(c, 'ของฉัน');
  await c.clk.advance(900);
  const d = S(c).draft;
  assert.equal(d.status, 'conflict');
  assert.equal(d.text, 'ของฉัน');
  assert.equal(d.theirs.text, 'จากแท็บอื่น');
  await type(c, 'ของฉันแก้ต่อ');
  await c.clk.advance(2000);
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'จากแท็บอื่น', 'nothing was written while in conflict');
  c.actions.resolveConflict('mine');
  await settleAll(); await c.clk.advance(900);
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'ของฉันแก้ต่อ');
  assert.equal(S(c).draft.status, 'saved');
});

test('conflict resolved with "use theirs" adopts the pack\'s text everywhere', async () => {
  const c = await setup();
  const l = await c.dev.studioLine(P, LANG, ID);
  await c.dev.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l.revision, operationId: 'b'.repeat(32), text: 'จากแท็บอื่น' });
  await type(c, 'ของฉัน');
  await c.clk.advance(900);
  c.actions.resolveConflict('theirs');
  await settleAll();
  assert.equal(S(c).draft.text, 'จากแท็บอื่น');
  assert.equal(S(c).draft.status, 'saved');
  assert.equal(S(c).byId.get(ID).text, 'จากแท็บอื่น');
});

test('an uncertain failure that DID commit is reconciled through the operation receipt, not replayed', async () => {
  const c = await setup();
  c.dev.dev.fail('saveSubtitle', 'unavailable', { times: 1, uncertain: true, afterCommit: true });
  const before = (await c.dev.studioHistory(P, LANG, ID)).total;
  await type(c, 'บันทึกแล้วแต่คำตอบหาย');
  await c.clk.advance(900);
  assert.equal(S(c).draft.status, 'saved');
  assert.equal((await c.dev.studioHistory(P, LANG, ID)).total, before + 1, 'exactly one revision: the POST was not replayed');
  assert.equal(c.dev.dev.log.filter((l) => l.method === 'saveSubtitle').length, 1);
  assert.ok(c.dev.dev.log.some((l) => l.method === 'operation'), 'it asked the server about the operation');
});

test('an uncertain failure that did NOT commit is retried with the same operationId and the same revision', async () => {
  const c = await setup();
  c.dev.dev.fail('saveSubtitle', 'unavailable', { times: 1, uncertain: true });
  await type(c, 'ไม่ถึงเซิร์ฟเวอร์');
  await c.clk.advance(900);
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'ไม่ถึงเซิร์ฟเวอร์');
  const saves = c.dev.dev.log.filter((l) => l.method === 'saveSubtitle');
  assert.equal(saves.length, 2);
  assert.equal(S(c).draft.status, 'saved');
});

test('a permanent refusal (read-only pack) stops without retrying and keeps the text', async () => {
  const c = await setup();
  c.dev.dev.fail('saveSubtitle', 'read_only_pack', { times: 5 });
  await type(c, 'ห้ามเขียน');
  await c.clk.advance(900);
  assert.equal(S(c).draft.status, 'failed');
  assert.equal(S(c).draft.error, 'read_only_pack');
  await c.clk.advance(60000);
  assert.equal(c.dev.dev.log.filter((l) => l.method === 'saveSubtitle').length, 1, 'no retry loop');
  assert.equal(S(c).draft.text, 'ห้ามเขียน');
});

test('a transient failure retries with backoff and then saves', async () => {
  const c = await setup();
  c.dev.dev.fail('saveSubtitle', 'not_ready', { times: 2 });
  await type(c, 'รอแป๊บ');
  await c.clk.advance(900);
  assert.equal(S(c).draft.status, 'failed');
  await c.clk.advance(30000);
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'รอแป๊บ');
  assert.equal(S(c).draft.status, 'saved');
});

test('clearing the box removes the subtitle (null on the wire) and is a history entry', async () => {
  const c = await setup();
  await type(c, '');
  await c.clk.advance(900);
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, null);
  assert.equal(c.dev.dev.log.filter((l) => l.method === 'saveSubtitle')[0].args[3], null, 'the adapter saw null, not an empty string');
  assert.equal((await c.dev.studioHistory(P, LANG, ID)).items[0].text, null);
});

test('saved-but-not-live shows the loader\'s note instead of claiming "live in game"', async () => {
  const c = await setup();
  const original = c.dev.studioSaveSubtitle;
  c.dev.studioSaveSubtitle = async (...a) => ({ ...(await original(...a)), live: false, note: 'Reloads when the game is ready.' });
  await type(c, 'ยังไม่ขึ้นในเกม');
  await c.clk.advance(900);
  assert.equal(S(c).saveNote, 'Reloads when the game is ready.');
  assert.equal(S(c).draft.status, 'saved');
});

test('"live in game" is claimed only after a save the loader called live; an empty note is not a live save', async () => {
  const c = await setup();
  assert.equal(S(c).saveLive, null, 'nothing saved yet this session: no live claim');
  await type(c, 'หนึ่ง');
  await c.clk.advance(900);
  assert.equal(S(c).saveLive, true, 'the dev loader answers live:true');
  const original = c.dev.studioSaveSubtitle;
  c.dev.studioSaveSubtitle = async (...a) => ({ ...(await original(...a)), live: false, note: undefined }); // no note at all: the old code treated this as live
  await type(c, 'สอง');
  await c.clk.advance(900);
  assert.equal(S(c).saveLive, false);
  assert.equal(S(c).saveNote, '');
  await c.actions.selectLine('clickheroine_rest_008');
  assert.equal(S(c).saveLive, null, 'another line starts without a claim');
});

test('which pack the game uses follows the loader: an "only" note goes away after our first save, and costs no extra read when our text already wins', async () => {
  const c = await setup();
  await c.actions.selectLine('night_goodnight_002');
  assert.equal(S(c).line.effectivePack, 'thai-subs-community', 'the list and the detail both carry the winner');
  assert.equal(S(c).line.dubPack, 'thai-subs-community');
  await type(c, 'ของฉันเอง');
  await c.clk.advance(900);
  await c.clk.advance(400); // the refetch after the save
  assert.equal(S(c).line.effectivePack, P, 'our own text now wins (the dev loader says so)');
  assert.equal(S(c).line.effectiveText, 'ของฉันเอง');
  assert.equal(S(c).line.dubPack, 'thai-subs-community', 'the dub is still another pack\'s');
  // a line where our text already wins: saving again asks for nothing more
  await c.actions.selectLine('clickheroine_rest_011');
  c.dev.dev.log.length = 0;
  await type(c, 'ข้อความใหม่');
  await c.clk.advance(900);
  await c.clk.advance(400);
  assert.equal(c.dev.dev.log.filter((l) => l.method === 'line').length, 0, 'no extra line read after a save when our own text already wins');
  // a line where another pack outranks us keeps winning after our save, and we ask the loader again
  await c.actions.selectLine('story01_013');
  c.dev.dev.log.length = 0;
  await type(c, 'ฉันแก้ข้อความ');
  await c.clk.advance(900);
  await c.clk.advance(400);
  assert.equal(S(c).line.effectivePack, 'thai-subs-community', 'the other pack still wins');
  assert.ok(c.dev.dev.log.filter((l) => l.method === 'line').length >= 1, 'the loader was asked who wins');
});

test('status and star: optimistic, serialised with subtitle saves, and a later subtitle save does not conflict with them', async () => {
  const c = await setup();
  const star = c.actions.toggleStar();
  assert.equal(S(c).line.starred, true, 'optimistic');
  await star; await settleAll();
  await c.actions.setStatus('done');
  await type(c, 'หลังเปลี่ยนสถานะ');
  await c.clk.advance(900);
  const line = await c.dev.studioLine(P, LANG, ID);
  assert.deepEqual([line.workflow.status, line.workflow.starred, line.ownedSubtitle], ['done', true, 'หลังเปลี่ยนสถานะ']);
  assert.equal(S(c).draft.status, 'saved', 'no conflict with our own workflow change');
});

test('a refused status change is rolled back and explained', async () => {
  const c = await setup();
  const before = S(c).line.status;
  c.dev.dev.fail('setWorkflow', 'read_only_pack', { times: 1 });
  await c.actions.setStatus(before === 'done' ? 'draft' : 'done');
  await settleAll();
  assert.equal(S(c).line.status, before);
  assert.deepEqual(c.toasts.at(-1), ['t_status_failed', 'warn']);
});

test('needs-retake toggles between needs-retake and draft', async () => {
  const c = await setup();
  await c.actions.toggleRetake(); await settleAll();
  assert.equal(S(c).line.status, 'needs-retake');
  await c.actions.toggleRetake(); await settleAll();
  assert.equal(S(c).line.status, 'draft');
});

test('history revert flushes pending text first, applies the old text and keeps the box in sync', async () => {
  const c = await setup();
  await c.actions.openHistory();
  const items = S(c).history.items;
  assert.equal(items.length, 3);
  const old = items[2];
  await type(c, 'เพิ่งพิมพ์');
  await c.actions.revert(old.id);
  await settleAll();
  assert.equal(S(c).draft.text, old.text);
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, old.text);
  assert.ok(c.toasts.some((t) => t[0] === 't_revert'));
  assert.equal(S(c).draft.status, 'saved');
});

test('events: another tab\'s save refreshes a clean draft but never overwrites a dirty one; our own save is not refetched', async () => {
  const c = await setup();
  const l = await c.dev.studioLine(P, LANG, ID);
  await c.dev.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l.revision, operationId: 'c'.repeat(32), text: 'จากที่อื่น' });
  await c.clk.advance(400);
  assert.equal(S(c).draft.text, 'จากที่อื่น', 'a clean draft follows the pack');
  const reads = () => c.dev.dev.log.filter((x) => x.method === 'line').length;
  await type(c, 'พิมพ์เอง');
  await c.clk.advance(900);
  const afterOwn = reads();
  await c.clk.advance(2000);
  assert.equal(reads(), afterOwn, 'no refetch for our own save');
  await type(c, 'พิมพ์ต่อ');
  const l2 = await c.dev.studioLine(P, LANG, ID);
  await c.dev.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l2.revision, operationId: 'd'.repeat(32), text: 'จากแท็บอื่นอีกที' });
  await c.clk.advance(400);
  assert.equal(S(c).draft.text, 'พิมพ์ต่อ', 'a dirty draft is never overwritten by an event');
});

test('events: focus, room and language change the status; line.spoken becomes "Satone just said"; unknown events are ignored', async () => {
  const c = await setup();
  c.dev.dev.setStatus({ paused: true, focused: false });
  assert.deepEqual([S(c).status.paused, S(c).status.focused], [true, false]);
  c.dev.dev.setStatus({ roomReady: false });
  assert.equal(S(c).status.roomReady, false);
  c.dev.dev.setStatus({ gameLanguage: 'en' });
  assert.equal(S(c).status.gameLanguage, 'en');
  c.dev.dev.emit('maker.play', { id: ID, variant: 'dub', lang: LANG, voice: 'v' });
  c.dev.dev.emit('line.spoken', { localizationId: ID, source: 'maker' });
  assert.equal(S(c).last.id, ID);
  assert.equal(S(c).last.variant, 'dub');
  c.dev.dev.emit('line.spoken', { localizationId: 'morning_wake_001', source: 'game' });
  assert.equal(S(c).last.variant, 'orig');
  assert.doesNotThrow(() => c.dev.dev.emit('something.new', { x: 1 }));
});

test('events: a changed catalogue or a new pack reloads packs and groups', async () => {
  const c = await setup();
  await c.dev.studioCreatePack({ operationId: 'e'.repeat(32), id: 'from-elsewhere', name: 'x', language: { code: 'th' } });
  await settleAll();
  assert.ok(S(c).packs.some((p) => p.id === 'from-elsewhere'));
});

test('offline: the page says so, keeps the text, and reconnects with backoff, reloading when the game is back', async () => {
  const c = await setup();
  await type(c, 'พิมพ์ตอนออฟไลน์');
  c.dev.dev.setOffline(true);
  await c.clk.advance(900);
  assert.equal(S(c).conn.connected, false);
  assert.equal(S(c).draft.text, 'พิมพ์ตอนออฟไลน์');
  await c.clk.advance(5000);
  assert.equal(S(c).conn.connected, false);
  c.dev.dev.setOffline(false);
  await c.clk.advance(30000);
  assert.equal(S(c).conn.connected, true);
  assert.ok(c.toasts.some((t) => t[0] === 't_reconnected'));
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'พิมพ์ตอนออฟไลน์', 'the unsent text went out once the game was back');
  assert.equal(S(c).draft.status, 'saved');
});

test('a 401 latches: no more requests (not even from a timer already running), the page says the session ended', async () => {
  const c = await setup();
  await type(c, 'ก่อนหมดเวลา');
  await c.clk.advance(900);                          // saved; a progress refresh is now waiting on its own timer
  c.dev.dev.fail('status', 'unauthorized', { times: 1 });
  await c.actions.refreshStatus();
  assert.deepEqual(S(c).conn, { connected: false, unauthorized: true });
  const calls = c.dev.dev.log.length;
  await c.clk.advance(60000);
  assert.equal(c.dev.dev.log.length, calls, 'nothing is retried after a 401');
});

test('create pack: a slug id from the name, the pending pack is selected, a taken id gets a suffix, a bad name is refused', async () => {
  const c = await setup();
  assert.equal(await c.actions.createPack({ name: '   ', lang: 'th' }), false);
  assert.deepEqual(c.toasts.at(-1), ['np_name_bad', 'warn']);
  assert.equal(await c.actions.createPack({ name: 'My First Pack', lang: 'th' }), true);
  assert.equal(S(c).packId, 'my-first-pack');
  const p = S(c).packs.find((x) => x.id === 'my-first-pack');
  assert.deepEqual([p.loaded, p.editable, p.restartRequired], [false, false, true]);
  assert.equal(await c.actions.createPack({ name: 'My First Pack', lang: 'th' }), true);
  assert.equal(S(c).packId, 'my-first-pack-2');
  assert.equal(await c.actions.createPack({ name: 'แพ็กภาษาไทย', lang: 'th' }), true);
  assert.match(S(c).packId, /^pack-[0-9a-f]{6}$/, 'a name without Latin letters gets a generated id');
});

test('create pack survives a lost reply: the same request (same operationId) is repeated once', async () => {
  const c = await setup();
  c.dev.dev.fail('createPack', 'unavailable', { times: 1, uncertain: true, afterCommit: true });
  const ok = await c.actions.createPack({ name: 'Lost Reply', lang: 'th' });
  assert.equal(ok, true);
  assert.equal(S(c).packs.filter((p) => p.id === 'lost-reply').length, 1, 'one pack, not two');
});

test('switching pack keeps unsent text of the old pack flowing to the old pack', async () => {
  const c = await setup();
  await type(c, 'ยังไม่ส่ง');
  c.actions.flushCurrent();
  await c.actions.loadPack('cozy-en-rewrite', 'en');
  await settleAll();
  assert.equal((await c.dev.studioLine(P, LANG, ID)).ownedSubtitle, 'ยังไม่ส่ง');
  assert.equal(S(c).packId, 'cozy-en-rewrite');
});

test('resync (dropped events or a new epoch) reloads packs, groups and the catalogue and keeps the line and its unsent draft', async () => {
  const c = await setup();
  await type(c, 'ยังพิมพ์ไม่เสร็จตอนรีซิงก์');
  const groupsBefore = c.dev.dev.log.filter((l) => l.method === 'groups').length;
  c.dev.bus.dispatchEvent(new Event('resync'));
  await settleAll();
  assert.ok(c.dev.dev.log.filter((l) => l.method === 'groups').length > groupsBefore);
  assert.equal(S(c).lineId, ID);
  assert.equal(S(c).draft.text, 'ยังพิมพ์ไม่เสร็จตอนรีซิงก์');
  assert.equal(S(c).lines.length, 120);
});

test('the catalogue is loaded with W2\'s query shape: pack, language and paging only, optional members omitted', async () => {
  const c = await setup();
  const calls = [];
  const original = c.dev.studioLines;
  c.dev.studioLines = (q) => { calls.push(q); return original(q); };
  await c.actions.reload({ keepLine: true });
  await settleAll();
  assert.ok(calls.length >= 1);
  for (const q of calls) {
    assert.deepEqual(Object.keys(q).sort().filter((k) => !['packId', 'lang', 'offset', 'limit', 'catalogRevision'].includes(k)), [], 'no stray members');
    assert.ok(!Object.values(q).some((v) => v === null || v === undefined || v === 'null' || v === 'undefined'), 'never "null" or undefined on the wire');
  }
});

test('a read that started before our own save finished is discarded as stale and re-read, never allowed to put the old text back', async () => {
  const c = await setup();
  const original = c.dev.studioLine;
  let release; const gate = new Promise((r) => { release = r; });
  let delayed = false;
  c.dev.studioLine = async (...a) => { const snapshot = await original(...a); if (!delayed) { delayed = true; await gate; } return snapshot; };
  const slowRead = c.actions.refetchLine(ID);       // takes its snapshot now (old text), answers later
  await tick();
  await type(c, 'ข้อความใหม่หลังอ่านครั้งแรก');
  await c.clk.advance(900);                          // the save completes first
  assert.equal(S(c).draft.status, 'saved');
  release();
  await slowRead;
  await c.clk.advance(400);                          // the re-read that replaces the discarded one
  assert.equal(S(c).draft.text, 'ข้อความใหม่หลังอ่านครั้งแรก');
  assert.equal(S(c).line.text, 'ข้อความใหม่หลังอ่านครั้งแรก');
  assert.equal(S(c).draft.status, 'saved');
});

test('holding the arrow key does not flood the loader: only the line where the selection rests is read', async () => {
  const c = await setup({ selectDelayMs: 120 });
  const reads = () => c.dev.dev.log.filter((l) => l.method === 'line').length;
  const before = reads();
  const ids = ['morning_wake_001', 'morning_wake_002', 'morning_wake_003', 'morning_wake_004', 'morning_wake_005'];
  for (const id of ids) c.actions.selectLine(id);
  await settleAll();
  assert.equal(S(c).lineId, 'morning_wake_005', 'the screen follows at once (from the catalogue entry)');
  await c.clk.advance(200);
  assert.equal(reads() - before, 1, 'one detail read for five selections');
  assert.equal(S(c).line.context?.occurrences?.length, 1, 'and it is the last line that got its context');
});

test('a reload({ keepLine: true }) finishing after the creator switched lines and typed keeps the new line and its draft', async () => {
  const c = await setup();
  const origLines = c.dev.studioLines;
  let release; const gate = new Promise((r) => { release = r; });
  let delayNext = true;
  c.dev.studioLines = async (...a) => {
    const res = await origLines(...a);
    if (delayNext) { delayNext = false; await gate; }
    return res;
  };
  const reloading = c.actions.reload({ keepLine: true }); // starts while on ID
  await settleAll();
  await c.actions.selectLine(ID2);                       // creator moves to ID2 while catalog load is in flight
  await settleAll();
  c.dev.dev.setOffline(true);
  await type(c, 'ยังพิมพ์ไม่เสร็จตอนออฟไลน์');
  release();                                             // in-flight reload settles after line switch + offline typing
  await reloading;
  await settleAll();
  assert.equal(S(c).lineId, ID2, 'selection must stay on the line the creator moved to');
  assert.equal(S(c).draft.text, 'ยังพิมพ์ไม่เสร็จตอนออฟไลน์', 'typed text must not be overwritten');
  assert.equal(S(c).draft.status, 'typing', 'active debounce is preserved');
  c.dev.dev.setOffline(false);
  await c.clk.advance(900);
  assert.equal(S(c).draft.status, 'saved');
  assert.equal((await c.dev.studioLine(P, LANG, ID2)).ownedSubtitle, 'ยังพิมพ์ไม่เสร็จตอนออฟไลน์');
});

test('a concurrent catalog reload preserves a newer saved revision and keeps external-edit conflict detection intact', async () => {
  const c = await setup();
  const origVoice = c.dev.studioVoiceDefault;
  let release; const gate = new Promise((r) => { release = r; });
  let delayNext = true;
  c.dev.studioVoiceDefault = async (...a) => {
    const res = await origVoice(...a);
    if (delayNext) { delayNext = false; await gate; }
    return res;
  };
  const reloading = c.actions.reload({ keepLine: true });
  await settleAll();
  await type(c, 'บันทึกก่อนโหลดเสร็จ');
  await c.clk.advance(900);                              // local save finishes while loadPackOnce awaits voiceDefault
  assert.equal(S(c).draft.status, 'saved');
  const revAfterSave = S(c).draft.baseRevision;
  // External writer updates ID while reload is still wrapping up
  await c.dev.studioSaveSubtitle(P, LANG, ID, { expectedRevision: revAfterSave, operationId: 'f'.repeat(32), text: 'จากแท็บอื่น' });
  await settleAll();
  await type(c, 'ของฉันไม่ควรหาย');
  release();
  await reloading;
  await settleAll();
  assert.equal(S(c).lineId, ID);
  assert.equal(S(c).draft.status, 'conflict', 'external edit still triggers conflict');
  assert.equal(S(c).draft.text, 'ของฉันไม่ควรหาย', 'creator draft survives');
  assert.equal(S(c).draft.theirs?.text, 'จากแท็บอื่น');
});
