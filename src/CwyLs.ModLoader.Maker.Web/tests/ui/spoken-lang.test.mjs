import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { makeT } from '../../ui/i18n/i18n.js';
import { langMismatch } from '../../ui/selectors.js';
import { renderHero, langName } from '../../ui/zones/hero.js';
import { renderTopbar } from '../../ui/zones/topbar.js';

const P = 'satone-th-dub';
const tick = () => new Promise((r) => setImmediate(r));
const settleAll = async () => { for (let i = 0; i < 12; i++) await tick(); };

async function setup(options = {}) {
  const dev = createDevBackend({ lines: 120, ...options.dev });
  const store = createStore({
    ready: false, conn: { connected: false, unauthorized: false }, status: null, packs: [], packId: null, packLang: 'th', groups: [], overall: {},
    lines: [], byId: new Map(), loadingLines: true, view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, history: null, last: null, peaks: {}, selTakeId: null, saveNote: null, saveLive: null, menu: null, rec: 'idle', mic: 'none', playingTake: false, speaking: null, waiting: null
  });
  const toasts = [];
  const timers = { setTimeout: () => 0, clearTimeout: () => {} };
  const actions = createActions({ store, api: createApi(dev), mode: 'dev', toast: (k, kind) => toasts.push([k, kind]), timers, reconnectMs: 100, selectDelayMs: 0,
    newOp: (() => { let n = 0; return () => (++n).toString(16).padStart(32, '0'); })(), now: () => 0 });
  await actions.start({});
  await settleAll();
  return { dev, store, actions, toasts };
}
const S = (c) => c.store.get();
const spoke = (c, id) => c.actions.onEvent({ type: 'line.spoken', data: { localizationId: id, source: 'ambient', language: 'th' } });
const hero = (c, lang = 'en') => renderHero(S(c), { T: makeT(lang) });

// ---------------- (d) Open line ----------------
test('the line the game spoke gets an Open line button; the open line says so instead', async () => {
  const c = await setup();
  assert.ok(!hero(c).includes('data-act="open-said"'), 'nothing said yet');
  spoke(c, 'smalltalk_tea_002');
  await settleAll();
  assert.equal(S(c).last.id, 'smalltalk_tea_002');
  const html = hero(c);
  assert.ok(html.includes('data-act="open-said" data-id="smalltalk_tea_002"'), 'a button for a line that is not open');
  spoke(c, S(c).lineId);
  const here = hero(c);
  assert.ok(!here.includes('data-act="open-said"') && here.includes('This is the open line'), 'the open line needs no button');
});

test('a spoken line that is not in the catalogue is named by id and cannot be opened', async () => {
  const c = await setup();
  spoke(c, 'ambient_not_in_list_001');
  await settleAll();
  const html = hero(c, 'th');
  assert.ok(html.includes('ambient_not_in_list_001') && html.includes('เกมเพิ่งพูดบรรทัดที่ไม่มีในรายการนี้'));
  assert.ok(!html.includes('open-said'));
  assert.equal(await c.actions.openSpoken('ambient_not_in_list_001'), false);
  assert.equal(await c.actions.openSpoken(null), false);
});

test('openSpoken selects the line and scrolls to it; it clears a scene, filter or search that hides it, and says so', async () => {
  const c = await setup();
  c.store.set({ view: { group: 'morning', filter: 'done', q: 'zzz' } });
  assert.equal(await c.actions.openSpoken('smalltalk_tea_002'), true);
  assert.equal(S(c).lineId, 'smalltalk_tea_002');
  assert.deepEqual(S(c).view, { group: null, filter: 'all', q: '' });
  assert.equal(c.toasts.at(-1)[0], 't_open_cleared');
  // when the line is already visible nothing is cleared and nothing is announced
  c.store.set({ view: { group: 'pomodoro', filter: 'all', q: '' } });
  c.toasts.length = 0;
  await c.actions.openSpoken('pomodoro_start_001');
  assert.equal(S(c).lineId, 'pomodoro_start_001');
  assert.equal(S(c).view.group, 'pomodoro', 'the scene stays when it already shows the line');
  assert.deepEqual(c.toasts, []);
});

test('the line.spoken event stores the id from localizationId (the real loader\'s field)', async () => {
  const c = await setup();
  spoke(c, 'pomodoro_start_002');
  assert.equal(S(c).last.id, 'pomodoro_start_002');
  assert.equal(S(c).last.variant, 'orig', 'a game-spoken line is the original unless our own Play of the dub was just heard');
});

test('a Pack Maker Play is spoken as a runtime Say row (cwy_say_*): "Satone just said" names the line from lineId, and a dub Play stays a dub (TD-34)', async () => {
  const c = await setup();
  c.actions.onEvent({ type: 'maker.play', data: { id: 'pomodoro_start_002', variant: 'dub', lang: 'th' } });
  c.actions.onEvent({ type: 'line.spoken', data: { localizationId: 'cwy_say_12_001', lineId: 'pomodoro_start_002', scenarioGroupId: 'cwy_say_12', source: 'other', language: 'th' } });
  assert.deepEqual([S(c).last.id, S(c).last.variant], ['pomodoro_start_002', 'dub']);
  const html = hero(c);
  assert.ok(html.includes('data-act="open-said" data-id="pomodoro_start_002"'), 'the line played is offered');
  assert.ok(!html.includes('cwy_say_') && !html.includes('not in this list'), 'no runtime id, no unknown-line message');
  // Another mod's Satone.Say line: the real loader sends lineId null, so it stays an unknown line.
  c.actions.onEvent({ type: 'line.spoken', data: { localizationId: 'cwy_say_13_001', lineId: null, source: 'other', language: 'th' } });
  assert.equal(S(c).last.id, 'cwy_say_13_001');
});

// ---------------- (e) language notice ----------------
const st = (o) => ({ status: { gameLanguage: 'en' }, packLang: 'th', conn: { connected: true }, packId: 'p', packs: [{ id: 'p', languages: ['th'] }], ...o });

test('the language rule: differs, case-insensitive, unknown, offline, and whether the pack can switch', () => {
  assert.deepEqual(langMismatch(st({})), { game: 'en', pack: 'th', switchTo: null });
  assert.equal(langMismatch(st({ packs: [{ id: 'p', languages: ['th', 'EN'] }] })).switchTo, 'EN', 'the pack\'s own code is used');
  assert.equal(langMismatch(st({ status: { gameLanguage: 'TH' } })), null, 'same language, any case');
  assert.equal(langMismatch(st({ status: { gameLanguage: 'th' } })), null);
  assert.equal(langMismatch(st({ status: null })), null, 'status not known yet');
  assert.equal(langMismatch(st({ status: {} })), null);
  assert.equal(langMismatch(st({ conn: { connected: false } })), null, 'a stale language is not reported');
  assert.equal(langMismatch(st({ packLang: '' })), null);
});

test('mismatch: the mirror carries the note (with Switch only when the pack has the game\'s language) and the top bar a badge, in both languages', async () => {
  const c = await setup();
  assert.ok(!hero(c).includes('mir-lang'), 'no note while the languages agree');
  assert.ok(!renderTopbar(S(c), { T: makeT('en'), lang: 'en' }).includes('langwarn'));
  c.store.set({ status: { ...S(c).status, gameLanguage: 'en' } });
  for (const lang of ['en', 'th']) {
    const T = makeT(lang), html = renderHero(S(c), { T });
    assert.ok(html.includes('class="mir-lang"'), `${lang}: note`);
    assert.ok(html.includes(langName(T, 'en')) && html.includes(langName(T, 'th')), `${lang}: both languages are named`);
    assert.ok(!html.includes('mir-screen'), `${lang}: How it shows in game mirror panel is removed`);
    assert.ok(!html.includes('match-lang'), `${lang}: this pack has no English, so no Switch button`);
    assert.ok(renderTopbar(S(c), { T, lang }).includes('langwarn'), `${lang}: badge`);
  }
  const two = await setup({ dev: { packLanguages: ['th', 'en'] } });
  two.store.set({ status: { ...S(two).status, gameLanguage: 'en' } });
  assert.ok(hero(two).includes('data-act="match-lang" data-id="en"'), 'the pack has English: offer the switch');
  assert.ok(hero(two).includes('Switch to English'));
  assert.ok(hero(two, 'th').includes('สลับไปแก้ภาษาอังกฤษ'));
});

test('a language the page has no name for is shown as its code, escaped', () => {
  const T = makeT('en');
  assert.equal(langName(T, 'ja'), 'Japanese');
  assert.equal(langName(T, 'ko'), 'KO');
  assert.equal(langName(T, 'th'), 'Thai');
});

test('a Japanese game and a Thai pack: the notice names Japanese', async () => {
  const c = await setup();
  c.store.set({ status: { ...S(c).status, gameLanguage: 'ja' } });
  assert.ok(hero(c, 'th').includes('ภาษาญี่ปุ่น'));
  assert.ok(hero(c, 'en').includes('The game is in Japanese, this pack is in Thai.'));
});
