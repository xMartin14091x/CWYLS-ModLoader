import test from 'node:test';
import assert from 'node:assert/strict';
import { createDevBackend } from '../dev/dev-backend.js';
import { createApi, loadCatalog, newOperationId, StudioError } from '../../ui/api.js';
import { encodeWavPcm16Mono, TARGET_RATE } from '../../ui/audio/wav.js';
import { statusVM, linkState, packVM, lineVM, errorKey, isUncertain, contextVM, toStudioError, takeMutationBody, pendingLineVM } from '../../ui/api/model.js';

const P = 'satone-th-dub', LANG = 'th', ID = 'clickheroine_rest_010';
const fresh = (o) => createDevBackend({ lines: 120, ...o });
const wav = (sec, rate = TARGET_RATE) => encodeWavPcm16Mono(new Float32Array(Math.round(sec * rate)).fill(0.1), rate);
const reject = async (p, code) => assert.rejects(p, (e) => e instanceof StudioError && e.code === code, `expected ${code}`);
const GOOD = { confirmed: true, statementVersion: 'consent-1' };

// ---- contract shapes (W2 PLAN §3 read models) ----
test('capabilities advertises contract 2', async () => {
  const c = await fresh().studioCapabilities();
  assert.equal(c.contract, 2);
  assert.deepEqual(c.features, ['catalog', 'workflow', 'subtitles', 'dubs', 'projects', 'newPacks', 'playback']);
  assert.equal(c.limits.takesPerLine, 3);
  assert.equal(c.consentVersion, 'consent-1');
});

test('packs: editable packs, a reference pack, a code mod and a failed pack, each read-only with a reason', async () => {
  const packs = (await fresh().studioPacks()).packs;
  assert.deepEqual(packs.filter((p) => p.studioEditable).map((p) => p.id), ['satone-th-dub', 'cozy-en-rewrite']);
  assert.deepEqual(Object.fromEntries(packs.filter((p) => !p.studioEditable).map((p) => [p.id, p.studioReadOnlyReason])),
    { 'thai-subs-community': 'reference', 'cwyls-rain-fx': 'code', 'old-test-pack': 'failed' });
});

test('lines: paged, default catalogue of 2169 lines in 5 pages of 500, revision on every page', async () => {
  const api = createApi(createDevBackend());
  const { catalogRevision, lines } = await loadCatalog(api, P, LANG);
  assert.equal(lines.length, 2169);
  assert.match(catalogRevision, /^c\d+$/);
  assert.equal(new Set(lines.map((l) => l.id)).size, 2169, 'ids are unique');
  assert.equal(lines.find((l) => l.id === ID).text, 'ว่าไง? เมื่อกี้เรียกเรารึเปล่า');
});

test('lines: limit 0 and 501 are bad requests', async () => {
  const b = fresh();
  await reject(b.studioLines({ packId: P, lang: LANG, limit: 0 }), 'bad_request');
  await reject(b.studioLines({ packId: P, lang: LANG, limit: 501 }), 'bad_request');
});

test('loadCatalog restarts when the catalogue revision changes between pages, and gives up after three tries', async () => {
  const b = createDevBackend({ lines: 1200 });
  const api = createApi(b);
  let n = 0;
  const original = b.studioLines;
  b.studioLines = async (...a) => { const p = await original(...a); if (++n === 2) b.dev.packs.push({ id: 'x', name: 'x', languages: ['th'], studioEditable: false }); return n === 2 ? { ...p, catalogRevision: 'c99' } : p; };
  const { catalogRevision, lines } = await loadCatalog(api, P, LANG);
  assert.equal(lines.length, 1200);
  assert.ok(catalogRevision);
  let flip = 0;
  b.studioLines = async (...a) => { const p = await original(...a); return { ...p, catalogRevision: `c${flip++}` }; };
  await reject(loadCatalog(api, P, LANG), 'conflict');
});

test('groups: overall equals the sum of the groups; text-only lines are not dubbable', async () => {
  const g = await fresh().studioGroups(P, LANG);
  const sum = (k) => g.groups.reduce((a, x) => a + x.progress[k], 0);
  for (const k of ['totalLines', 'dubbableLines', 'subtitlePresent', 'dubPresent', 'todo', 'draft', 'done', 'needsRetake', 'starred']) assert.equal(sum(k), g.overall[k], k);
  assert.ok(g.overall.dubbableLines < g.overall.totalLines, 'some lines have no voice');
  assert.equal(g.groups.length, 49);
});

test('line detail carries context with previous/next in group order and null mood/pose', async () => {
  const l = await fresh().studioLine(P, LANG, ID);
  assert.equal(l.context.occurrences[0].previousId, 'clickheroine_rest_009');
  assert.equal(l.context.occurrences[0].nextId, 'clickheroine_rest_011');
  assert.equal(l.context.mood, null);
  assert.equal(l.context.pose, null);
});

// ---- workflow, subtitles, history ----
test('subtitle save: revision moves, history is captured only for a real change, same save is a no-op', async () => {
  const b = fresh();
  const l0 = await b.studioLine(P, LANG, ID);
  const op1 = newOperationId();
  const r1 = await b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l0.revision, operationId: op1, text: 'ใหม่' });
  assert.notEqual(r1.revision, l0.revision);
  const h1 = await b.studioHistory(P, LANG, ID);
  const r2 = await b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: r1.revision, operationId: newOperationId(), text: 'ใหม่' });
  assert.equal(r2.revision, r1.revision, 'no change, no revision');
  assert.equal((await b.studioHistory(P, LANG, ID)).total, h1.total);
});

test('subtitle save is guarded by the expected revision (conflict) and replays by operationId', async () => {
  const b = fresh();
  const l0 = await b.studioLine(P, LANG, ID), op = newOperationId();
  const a = await b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l0.revision, operationId: op, text: 'A' });
  const again = await b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l0.revision, operationId: op, text: 'A' });
  assert.deepEqual(again, a, 'same key, same request: the recorded result, not a second write');
  assert.equal((await b.studioHistory(P, LANG, ID)).items.filter((i) => i.text === 'A').length, 1);
  await reject(b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l0.revision, operationId: op, text: 'B' }), 'conflict');
  await reject(b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: l0.revision, operationId: newOperationId(), text: 'C' }), 'conflict');
});

test('subtitle text rules: 1-2000 code points or null; empty string is refused; null removes and is in the history', async () => {
  const b = fresh();
  let rev = (await b.studioLine(P, LANG, ID)).revision;
  await reject(b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: rev, operationId: newOperationId(), text: '' }), 'bad_request');
  await reject(b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: rev, operationId: newOperationId(), text: 'ก'.repeat(2001) }), 'bad_request');
  const ok = await b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: rev, operationId: newOperationId(), text: '😀'.repeat(2000) });
  const removed = await b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: ok.revision, operationId: newOperationId(), text: null });
  const h = await b.studioHistory(P, LANG, ID);
  assert.equal(h.items[0].text, null, 'a removal is the newest history entry with text:null');
  assert.equal((await b.studioLine(P, LANG, ID)).ownedSubtitle, null);
  assert.ok(removed.saved);
});

test('history keeps 20 revisions and revert creates a new revision instead of rewinding', async () => {
  const b = fresh();
  let rev = (await b.studioLine(P, LANG, ID)).revision;
  for (let i = 0; i < 25; i++) rev = (await b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: rev, operationId: newOperationId(), text: `v${i}` })).revision;
  const h = await b.studioHistory(P, LANG, ID, { limit: 20 });
  assert.equal(h.total, 20);
  assert.equal(h.items[0].text, 'v24');
  const old = h.items[5];
  const r = await b.studioRevertSubtitle(P, LANG, ID, old.revisionId, { expectedRevision: rev, operationId: newOperationId() });
  assert.equal((await b.studioLine(P, LANG, ID)).ownedSubtitle, old.text);
  assert.equal((await b.studioHistory(P, LANG, ID)).items[0].text, old.text, 'revert is a new entry at the top');
  assert.ok(r.revision);
  await reject(b.studioRevertSubtitle(P, LANG, ID, 'f'.repeat(32), { expectedRevision: r.revision, operationId: newOperationId() }), 'not_found');
});

test('workflow: the four states and the star; a subtitle save never changes the status', async () => {
  const b = fresh();
  const l0 = await b.studioLine(P, 'th', 'morning_wake_004');
  assert.equal(l0.workflow.status, 'draft');
  const s = await b.studioSaveSubtitle(P, 'th', 'morning_wake_004', { expectedRevision: l0.revision, operationId: newOperationId(), text: 'เปลี่ยนแล้ว' });
  assert.equal((await b.studioLine(P, 'th', 'morning_wake_004')).workflow.status, 'draft');
  const w = await b.studioUpdateWorkflow(P, 'th', 'morning_wake_004', { expectedRevision: s.revision, operationId: newOperationId(), status: 'needs-retake', starred: true });
  assert.deepEqual(w.workflow, { status: 'needs-retake', starred: true });
  await reject(b.studioUpdateWorkflow(P, 'th', 'morning_wake_004', { expectedRevision: w.revision, operationId: newOperationId(), status: 'weird' }), 'bad_request');
  await reject(b.studioUpdateWorkflow(P, 'th', 'morning_wake_004', { expectedRevision: w.revision, operationId: newOperationId() }), 'bad_request');
});

test('read-only, code and pending packs refuse writes with the right code', async () => {
  const b = fresh({ fixture: 'firstrun' });
  const rev = 'r1';
  await reject(b.studioSaveSubtitle('thai-subs-community', 'th', ID, { expectedRevision: rev, operationId: newOperationId(), text: 'x' }), 'read_only_pack');
  await reject(b.studioSaveSubtitle('cwyls-rain-fx', 'th', ID, { expectedRevision: rev, operationId: newOperationId(), text: 'x' }), 'read_only_pack');
  await reject(b.studioSaveSubtitle('my-new-pack', 'th', ID, { expectedRevision: rev, operationId: newOperationId(), text: 'x' }), 'restart_required');
  await reject(b.studioSaveSubtitle('nope', 'th', ID, { expectedRevision: rev, operationId: newOperationId(), text: 'x' }), 'not_found');
  await reject(b.studioSaveSubtitle(P, 'en', ID, { expectedRevision: rev, operationId: newOperationId(), text: 'x' }), 'not_found');
});

test('operationId must be 32 hex characters', async () => {
  await reject(fresh().studioSaveSubtitle(P, LANG, ID, { expectedRevision: 'r1', operationId: 'short', text: 'x' }), 'bad_request');
});

// ---- takes ----
async function takeWithAudio(b, id = 'morning_wake_006', consent = GOOD) {
  let rev = (await b.studioLine(P, LANG, id)).revision;
  const created = await b.studioCreateTake(P, LANG, id, { expectedRevision: rev, operationId: newOperationId(), origin: 'own', consent });
  rev = created.revision;
  const src = await b.studioPutTakeAudio(P, LANG, id, created.take.id, { kind: 'source', expectedRevision: rev, operationId: newOperationId() }, wav(1.5, 44100));
  const ren = await b.studioPutTakeAudio(P, LANG, id, created.take.id, { kind: 'render', expectedRevision: src.revision, operationId: newOperationId() }, wav(1.4));
  return { id, takeId: created.take.id, rev: ren.revision, take: ren.take };
}

test('three takes at most: the fourth is take_limit and nothing is evicted', async () => {
  const b = fresh();
  const id = 'morning_wake_006';
  let rev = (await b.studioLine(P, LANG, id)).revision;
  const ids = [];
  for (let i = 0; i < 3; i++) { const r = await b.studioCreateTake(P, LANG, id, { expectedRevision: rev, operationId: newOperationId(), origin: 'own', consent: { confirmed: false } }); rev = r.revision; ids.push(r.take.id); }
  await reject(b.studioCreateTake(P, LANG, id, { expectedRevision: rev, operationId: newOperationId(), origin: 'own', consent: { confirmed: false } }), 'take_limit');
  assert.deepEqual((await b.studioTakes(P, LANG, id)).items.map((t) => t.id), ids);
});

test('take audio: source accepts 8-48 kHz PCM16, render must be 48 kHz mono, junk is 415, over 60 s is 413', async () => {
  const b = fresh();
  const { id, takeId, rev } = await takeWithAudio(b);
  await reject(b.studioPutTakeAudio(P, LANG, id, takeId, { kind: 'render', expectedRevision: rev, operationId: newOperationId() }, wav(1, 44100)), 'unsupported_media_type');
  await reject(b.studioPutTakeAudio(P, LANG, id, takeId, { kind: 'source', expectedRevision: rev, operationId: newOperationId() }, new Uint8Array(100)), 'unsupported_media_type');
  await reject(b.studioPutTakeAudio(P, LANG, id, takeId, { kind: 'source', expectedRevision: rev, operationId: newOperationId() }, wav(61, 8000)), 'too_large');
  const back = await b.studioGetTakeAudio(P, LANG, id, takeId, 'render');
  assert.equal(back.size, 44 + 2 * Math.round(1.4 * 48000), 'what was uploaded is what comes back');
  await reject(b.studioGetTakeAudio(P, LANG, id, 'e'.repeat(32), 'render'), 'not_found');
});

test('publish needs a render and a confirmed statement for the current origin; success makes the take the line\'s dub', async () => {
  const b = fresh();
  const noConsent = await takeWithAudio(b, 'morning_wake_006', { confirmed: false });
  await reject(b.studioPublish(P, LANG, noConsent.id, { expectedRevision: noConsent.rev, expectedClipRevision: noConsent.rev, operationId: newOperationId(), takeId: noConsent.takeId }), 'consent_required');
  const ok = await takeWithAudio(b, 'morning_wake_005');
  const pub = await b.studioPublish(P, LANG, ok.id, { expectedRevision: ok.rev, expectedClipRevision: ok.rev, operationId: newOperationId(), takeId: ok.takeId });
  assert.equal(pub.saved, true);
  assert.equal(pub.publishedTakeId, ok.takeId);
  const line = await b.studioLine(P, LANG, ok.id);
  assert.equal(line.ownedDub, true);
  assert.equal(line.dubPack, P);
});

test('publish without a prepared render is render_required; trimming invalidates the render', async () => {
  const b = fresh();
  const { id, takeId, rev } = await takeWithAudio(b);
  const patched = await b.studioUpdateTake(P, LANG, id, takeId, { expectedRevision: rev, operationId: newOperationId(), trimStartSec: 0.2 });
  assert.equal(patched.take.renderReady, false);
  assert.equal(patched.take.sourcePresent, true);
  await reject(b.studioPublish(P, LANG, id, { expectedRevision: patched.revision, expectedClipRevision: patched.revision, operationId: newOperationId(), takeId }), 'render_required');
});

test('changing the origin withdraws the consent; a published take cannot be deleted; unpublish then delete works', async () => {
  const b = fresh();
  const t = await takeWithAudio(b);
  const o = await b.studioUpdateTake(P, LANG, t.id, t.takeId, { expectedRevision: t.rev, operationId: newOperationId(), origin: 'ai-assisted' });
  assert.deepEqual(o.take.consent, { confirmed: false, statementVersion: null, confirmedAt: null });
  const c = await b.studioUpdateTake(P, LANG, t.id, t.takeId, { expectedRevision: o.revision, operationId: newOperationId(), consent: GOOD });
  const r = await b.studioPutTakeAudio(P, LANG, t.id, t.takeId, { kind: 'render', expectedRevision: c.revision, operationId: newOperationId() }, wav(1.4));
  const pub = await b.studioPublish(P, LANG, t.id, { expectedRevision: r.revision, expectedClipRevision: r.revision, operationId: newOperationId(), takeId: t.takeId });
  await reject(b.studioDeleteTake(P, LANG, t.id, t.takeId, { expectedRevision: pub.revision, operationId: newOperationId() }), 'take_in_use');
  const un = await b.studioUnpublish(P, LANG, t.id, { expectedRevision: pub.revision, expectedClipRevision: pub.revision, operationId: newOperationId() });
  const del = await b.studioDeleteTake(P, LANG, t.id, t.takeId, { expectedRevision: un.revision, operationId: newOperationId() });
  assert.equal(del.removed, true);
});

test('favourite is separate from published and does not publish', async () => {
  const b = fresh();
  const t = await takeWithAudio(b);
  const f = await b.studioSetFavourite(P, LANG, t.id, { expectedRevision: t.rev, operationId: newOperationId(), takeId: t.takeId });
  assert.equal(f.favouriteTakeId, t.takeId);
  const list = await b.studioTakes(P, LANG, t.id);
  assert.equal(list.favouriteTakeId, t.takeId);
  assert.equal(list.publishedTakeId, null);
});

// ---- packs, play, events ----
test('create pack: manifest-valid id, pending and restart-required, duplicates conflict, same operation replays', async () => {
  const b = fresh();
  const op = newOperationId();
  const created = await b.studioCreatePack({ operationId: op, id: 'my-new-pack', name: 'My new pack', author: 'me', language: { code: 'th' } });
  assert.equal(created.restartRequired, true);
  assert.equal(created.live, false);
  assert.deepEqual((await b.studioCreatePack({ operationId: op, id: 'my-new-pack', name: 'My new pack', author: 'me', language: { code: 'th' } })).pack, created.pack, 'retry returns the same pack');
  await reject(b.studioCreatePack({ operationId: newOperationId(), id: 'my-new-pack', name: 'x', author: 'x', language: { code: 'th' } }), 'conflict');
  await reject(b.studioCreatePack({ operationId: newOperationId(), id: 'MY-NEW-PACK', name: 'x', author: 'x', language: { code: 'th' } }), 'bad_request');
  await reject(b.studioCreatePack({ operationId: newOperationId(), id: 'ab', name: 'x', author: 'x', language: { code: 'th' } }), 'bad_request');
  const p = (await b.studioPacks()).packs.find((x) => x.id === 'my-new-pack');
  assert.deepEqual([p.loaded, p.studioEditable, p.restartRequired], [false, false, true]);
});

test('play: a paused game answers game_paused, a busy one not_ready; otherwise it speaks and emits events', async () => {
  const b = fresh();
  const r = await b.studioPlay({ id: ID, variant: 'original', lang: LANG, packId: P });
  assert.equal(r.live, true);
  assert.deepEqual(b.dev.events.map((e) => e.type), ['maker.play', 'line.spoken']);
  b.dev.status.paused = true;
  await reject(b.studioPlay({ id: ID, variant: 'dub', lang: LANG, packId: P }), 'game_paused');
  b.dev.status.paused = false; b.dev.status.busy = true;
  await reject(b.studioPlay({ id: ID, variant: 'dub', lang: LANG, packId: P }), 'not_ready');
});

test('events arrive on the bus as maker-event CustomEvents, like modules/loader; connection changes are announced', async () => {
  const b = fresh();
  const seen = [], conn = [];
  b.bus.addEventListener('maker-event', (e) => seen.push(e.detail));
  b.bus.addEventListener('connection', () => conn.push(b.connection));
  b.dev.emit('room.ready', { ready: true });
  assert.equal(seen[0].type, 'room.ready');
  assert.deepEqual(seen[0].data, { ready: true });
  assert.equal(seen[0].seq, 1);
  b.dev.setStatus({ paused: true, focused: false });
  assert.equal(seen.at(-1).type, 'game.focus');
  assert.equal(seen.at(-1).data.paused, true);
  b.dev.setOffline(true);
  await reject(b.studioStatus(), 'unavailable');
  b.dev.setOffline(false);
  await b.studioStatus();
  assert.deepEqual(conn, ['offline', 'connected']);
});

test('fixtures: paused and offline are server states, firstrun adds a pending pack', async () => {
  assert.deepEqual([(await createDevBackend({ fixture: 'paused', lines: 5 }).studioStatus()).paused], [true]);
  const off = createDevBackend({ fixture: 'offline', lines: 5 });
  assert.equal((await off.studioStatus()).gameLanguage, 'th', 'the offline fixture starts online; the page switches it off after loading');
  off.dev.setOffline(true);
  await reject(off.studioStatus(), 'unavailable');
  assert.ok((await createDevBackend({ fixture: 'firstrun', lines: 5 }).studioPacks()).packs.some((p) => p.restartRequired));
});

test('failure injection: a one-shot failure, then success; an after-commit failure leaves the write done (uncertain)', async () => {
  const b = fresh();
  b.dev.fail('saveSubtitle', 'internal', { times: 1, uncertain: true, afterCommit: true });
  const rev = (await b.studioLine(P, LANG, ID)).revision, op = newOperationId();
  await assert.rejects(b.studioSaveSubtitle(P, LANG, ID, { expectedRevision: rev, operationId: op, text: 'หลังคอมมิต' }), (e) => isUncertain(e));
  assert.equal((await b.studioLine(P, LANG, ID)).ownedSubtitle, 'หลังคอมมิต', 'committed although the reply failed');
  const op2 = await b.studioOperation(P, op);
  assert.equal(op2.saved, true);
});

// ---- view-model mappers ----
test('status and link state: unknown focus is never treated as paused', () => {
  assert.equal(linkState(statusVM({}), { connected: true }), 'ok');
  assert.equal(linkState(statusVM({ focused: false }), { connected: true }), 'unfocused');
  assert.equal(linkState(statusVM({ focused: false, paused: true }), { connected: true }), 'paused');
  assert.equal(linkState(statusVM({ stale: true, paused: true }), { connected: true }), 'stale');
  assert.equal(linkState(statusVM({}), { connected: false }), 'off');
  assert.equal(linkState(statusVM({}), { connected: true, unauthorized: true }), 'unauthorized');
  assert.equal(statusVM({}).paused, null);
});

test('pack and line mappers fill defaults, map reasons to keys and keep free text', () => {
  assert.equal(packVM({ id: 'a', studioReadOnlyReason: 'reference' }).readOnlyKey, 'ro_ref');
  assert.equal(packVM({ id: 'a', studioReadOnlyReason: 'because' }).readOnlyText, 'because');
  assert.equal(packVM({ id: 'a', studioEditable: true }).editable, true);
  const l = lineVM({ id: 'x', workflow: { status: 'done', starred: true }, takeCount: 1, ownedSubtitle: 'hi' });
  assert.deepEqual([l.status, l.starred, l.takeCount, l.text, l.durationSec], ['done', true, 1, 'hi', null]);
  assert.equal(lineVM({ id: 'y' }).status, 'todo');
  assert.deepEqual(contextVM({ occurrences: [] }).occurrences, []);
});

test('error codes map to message keys; transport failures are uncertain, definite refusals are not', () => {
  assert.equal(errorKey('take_limit'), 'e_take_limit');
  assert.equal(errorKey('something-new'), 'e_internal');
  assert.equal(isUncertain(new StudioError('unavailable')), true);
  assert.equal(isUncertain(new StudioError('conflict', '', { status: 409 })), false);
  assert.equal(isUncertain(new Error('x')), false);
  // LoaderError from modules/loader/http.js (no .status, code 'uncertain' for lost mutation replies)
  const unc = toStudioError(Object.assign(new Error('lost'), { code: 'uncertain' }), 'connected');
  assert.ok(unc instanceof StudioError);
  assert.equal(isUncertain(unc), true);
  const lim = toStudioError(Object.assign(new Error('limit'), { code: 'take_limit' }), 'connected');
  assert.equal(lim.code, 'take_limit');
  assert.equal(isUncertain(lim), false);
  const off = toStudioError(Object.assign(new Error('net'), { code: 'unavailable' }), 'offline');
  assert.equal(off.status, 0);
  assert.deepEqual(takeMutationBody({ consent: { confirmed: false } }).consent, { confirmed: false, statementVersion: 'consent-1' });
  const pl = pendingLineVM({ id: 'x', ownedSubtitle: 'old', ownedDub: true, workflow: { status: 'done', starred: true }, takeCount: 2, publishedTakeId: 't1' });
  assert.deepEqual([pl.text, pl.ownedDub, pl.status, pl.starred, pl.takeCount, pl.publishedTakeId], ['', false, 'todo', false, 0, null]);
});
