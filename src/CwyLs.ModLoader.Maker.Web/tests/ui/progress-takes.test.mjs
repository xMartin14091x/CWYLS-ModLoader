import test from 'node:test';
import assert from 'node:assert/strict';
import { subDot, dubDot, filterLines, listRows, countByStatus, visibleWindow } from '../../ui/logic/progress.js';
import { canAddTake, canRemoveTake, selectedTake, consentOk, publishPreflight, playMineSteps, CONSENT_VERSION, MAX_TAKES } from '../../ui/logic/takes.js';

const L = (id, o = {}) => ({ id, group: 'g1', durationSec: 2, ja: '', en: id, text: '', status: 'todo', starred: false, takeCount: 0, publishedTakeId: null, ...o });

test('subtitle dot: no own text is to do; text is draft unless done', () => {
  assert.equal(subDot(L('a')), 'todo');
  assert.equal(subDot(L('a', { text: 'x', status: 'todo' })), 'draft');
  assert.equal(subDot(L('a', { text: 'x', status: 'draft' })), 'draft');
  assert.equal(subDot(L('a', { text: 'x', status: 'done' })), 'done');
  assert.equal(subDot(L('a', { text: 'x', status: 'needs-retake' })), 'draft', 'needs-retake never claims the subtitle is done');
  assert.equal(subDot(L('a', { text: '  ', status: 'done' })), 'todo');
});

test('dub dot: retake beats published beats takes beats none', () => {
  assert.equal(dubDot(L('a')), 'none');
  assert.equal(dubDot(L('a', { takeCount: 2 })), 'draft');
  assert.equal(dubDot(L('a', { takeCount: 2, publishedTakeId: 't' })), 'done');
  assert.equal(dubDot(L('a', { takeCount: 2, publishedTakeId: 't', status: 'needs-retake' })), 'retake');
});

const lines = [
  L('a1', { group: 'g1', status: 'done', text: 'x', starred: true, publishedTakeId: 't' }),
  L('a2', { group: 'g1', status: 'draft', text: 'hello world' }),
  L('b1', { group: 'g2', status: 'needs-retake', en: 'Hm? Did you say something?', ja: 'ん？' }),
  L('b2', { group: 'g2', status: 'todo' })
];

test('filters: status chips use the workflow status, star, scene, search over id/en/ja/text', () => {
  const ids = (v) => filterLines(lines, v).map((l) => l.id);
  assert.deepEqual(ids({}), ['a1', 'a2', 'b1', 'b2']);
  assert.deepEqual(ids({ filter: 'todo' }), ['b2']);
  assert.deepEqual(ids({ filter: 'draft' }), ['a2']);
  assert.deepEqual(ids({ filter: 'done' }), ['a1']);
  assert.deepEqual(ids({ filter: 'retake' }), ['b1']);
  assert.deepEqual(ids({ filter: 'star' }), ['a1']);
  assert.deepEqual(ids({ group: 'g2' }), ['b1', 'b2']);
  assert.deepEqual(ids({ q: ' SAY ' }), ['b1']);
  assert.deepEqual(ids({ q: 'world' }), ['a2']);
  assert.deepEqual(ids({ q: 'ん' }), ['b1']);
  assert.deepEqual(ids({ q: 'b', group: 'g2', filter: 'todo' }), ['b2']);
});

test('the Warnings filter runs over the whole catalogue with the supplied predicate', () => {
  const warn = (l) => l.id === 'a2' || l.id === 'b2';
  assert.deepEqual(filterLines(lines, { filter: 'warn', hasWarning: warn }).map((l) => l.id), ['a2', 'b2']);
  assert.deepEqual(filterLines(lines, { filter: 'warn' }), [], 'no predicate, no warnings');
});

test('list rows: scene headings only when no scene is selected', () => {
  assert.deepEqual(listRows(lines, null).map((r) => r.head ?? r.line.id), ['g1', 'a1', 'a2', 'g2', 'b1', 'b2']);
  assert.deepEqual(listRows(lines.slice(0, 2), 'g1').map((r) => r.line.id), ['a1', 'a2']);
});

test('counts are derived from the same lines the list shows', () => {
  assert.deepEqual(countByStatus(lines), { total: 4, todo: 1, draft: 1, done: 1, retake: 1, star: 1, subtitles: 2, dubs: 1 });
});

test('virtual window: overscan, clamping, empty list', () => {
  assert.deepEqual(visibleWindow(2169, 0, 400, 40), { from: 0, to: 14 });
  assert.deepEqual(visibleWindow(2169, 4000, 400, 40), { from: 96, to: 114 });
  assert.deepEqual(visibleWindow(10, 4000, 400, 40), { from: 10, to: 10 });
  assert.deepEqual(visibleWindow(0, 0, 400, 40), { from: 0, to: 0 });
});

const take = (id, o = {}) => ({ id, origin: 'own', consent: { confirmed: true, statementVersion: CONSENT_VERSION }, sourcePresent: true, renderReady: true, ...o });

test('takes: three at most, the fourth is refused (never replaced)', () => {
  assert.equal(MAX_TAKES, 3);
  assert.deepEqual(canAddTake([take('1'), take('2')]), { ok: true });
  assert.deepEqual(canAddTake([take('1'), take('2'), take('3')]), { ok: false, reason: 'take_limit' });
});

test('takes: a published take cannot be removed', () => {
  assert.deepEqual(canRemoveTake(take('1'), 'zz'), { ok: true });
  assert.deepEqual(canRemoveTake(take('1'), '1'), { ok: false, reason: 'take_in_use' });
});

test('selected take: explicit, then favourite, then published, then first; unknown ids are ignored', () => {
  const t = [take('1'), take('2'), take('3')];
  assert.equal(selectedTake(t, { selectedId: '3', favouriteId: '2' }).id, '3');
  assert.equal(selectedTake(t, { favouriteId: '2' }).id, '2');
  assert.equal(selectedTake(t, { publishedId: '2' }).id, '2');
  assert.equal(selectedTake(t, { selectedId: 'gone' }).id, '1');
  assert.equal(selectedTake([], {}), null);
});

test('consent counts only when confirmed for the current statement version', () => {
  assert.equal(consentOk(take('1')), true);
  assert.equal(consentOk(take('1', { consent: { confirmed: false, statementVersion: CONSENT_VERSION } })), false);
  assert.equal(consentOk(take('1', { consent: { confirmed: true, statementVersion: 'consent-0' } })), false);
  assert.equal(consentOk(take('1', { consent: null })), false);
  assert.equal(consentOk(null), false);
});

test('publish preflight: read-only, no take, no audio, no consent, render needed', () => {
  assert.equal(publishPreflight(take('1'), { packEditable: false }).reason, 'dis_ro');
  assert.equal(publishPreflight(null).reason, 'mine_empty');
  assert.equal(publishPreflight(take('1', { sourcePresent: false })).reason, 'noTakes');
  assert.equal(publishPreflight(take('1', { consent: null })).reason, 'need_consent');
  assert.deepEqual(publishPreflight(take('1')), { ok: true, needRender: false });
  assert.deepEqual(publishPreflight(take('1', { renderReady: false })), { ok: true, needRender: true });
});

test('Play my version runs its steps in the order save, render, publish, play', () => {
  assert.deepEqual(playMineSteps(take('1'), { textDirty: true, needRender: true }), ['save-subtitle', 'upload-render', 'publish', 'play']);
  assert.deepEqual(playMineSteps(take('1'), { textDirty: false, needRender: false }), ['publish', 'play']);
  assert.deepEqual(playMineSteps(take('1', { publishedHere: true }), { textDirty: false, needRender: false }), ['play']);
});

test('subtitle-only preflight and steps skip render, publish and voice consent', () => {
  assert.deepEqual(publishPreflight(null, { subtitle: 'Translation' }), {ok: true, needRender: false, variant: 'sub'});
  assert.equal(publishPreflight(null, {subtitle: '  '}).reason, 'mine_empty');
  assert.equal(publishPreflight(null, {subtitle: 'Translation', packEditable: false}).reason, 'dis_ro');
  assert.deepEqual(playMineSteps(null, {textDirty: true, needRender: true}), ['save-subtitle', 'play']);
  assert.deepEqual(playMineSteps(null, {textDirty: false, needRender: false}), ['play']);
});
