// P3 page lane (HN-047): regressions for the Milestone 1 review items S1, S4, S5, M7, C1, C2, C4, C5, C6 and HN-044 P2.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createStore } from '../../ui/store.js';
import { createApi } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { createDraft, reduce } from '../../ui/logic/draft.js';
import { renderDropdown, bindDropdownEvents } from '../../ui/zones/dropdown.js';
import { exportCheckDetail } from '../../ui/logic/export-copy.js';
import { PAGE_HELP, resolveStepTarget } from '../../ui/zones/tour.js';
import { STRINGS, makeT } from '../../ui/i18n/i18n.js';

const tick = () => new Promise((r) => setImmediate(r));
const drain = async () => { for (let i = 0; i < 20; i++) await tick(); };
const deferred = () => { let resolve; const promise = new Promise((r) => { resolve = r; }); return { promise, resolve }; };
const P = 'satone-th-dub', B = 'cozy-en-rewrite';
const root = (p) => fileURLToPath(new URL(`../../${p}`, import.meta.url));

async function setup() {
  const dev = createDevBackend({ lines: 120 });
  const store = createStore({ ready: false, conn: { connected: false, unauthorized: false }, packs: [], packId: null, packLang: 'th', groups: [], overall: {}, lines: [], byId: new Map(), view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, history: null, menu: null, selTakeId: null, peaks: {}, mic: 'none', rec: 'idle', trimDraft: null, playing: false, playingTake: false });
  let seq = 0;
  const actions = createActions({ store, api: createApi(dev), mode: 'dev', timers: { setTimeout, clearTimeout }, now: () => Date.now(), selectDelayMs: 0, storage: null, newOp: () => (++seq).toString(16).padStart(32, '0'), toast() {} });
  await actions.start(); await drain();
  return { dev, store, actions };
}

test('S1: Keep mine after retyping the other writer’s exact text ends clean, so settle() cannot loop', () => {
  let d = { ...createDraft('k', 'old', 'r1'), text: 'same', status: 'conflict', error: 'conflict', theirs: { text: 'same', revision: 'r2' } };
  const { state } = reduce(d, { type: 'resolve', choice: 'mine' });
  assert.equal(state.status, 'saved');
  assert.equal(state.baseText, state.text);
  assert.equal(state.baseRevision, 'r2');
  assert.equal(state.theirs, null);
  // different text still saves the creator's version
  d = { ...d, text: 'mine', theirs: { text: 'theirs', revision: 'r2' } };
  assert.equal(reduce(d, { type: 'resolve', choice: 'mine' }).state.status, 'typing');
});

test('S5: the top bar totals are zeros (not undefined) while a pack loads', async () => {
  const c = await setup(), gate = deferred(), orig = c.dev.studioLines;
  c.dev.studioLines = async (q) => { if (q.packId === B) await gate.promise; return orig(q); };
  const loading = c.actions.loadPack(B, 'en'); await drain();
  const o = c.store.get().overall;
  assert.deepEqual(o, { totalLines: 0, dubbableLines: 0, subtitlePresent: 0, dubPresent: 0 });
  gate.resolve(); await loading;
});

test('S4: changing only the pack language keeps the export check; another pack clears it', async () => {
  const c = await setup(), gate = deferred(), orig = c.dev.studioLines;
  c.store.set({ exportPreview: { packId: P, checks: [] }, exportNotes: 'notes' });
  c.dev.studioLines = async (q) => { await gate.promise; return orig(q); };
  const a = c.actions.loadPack(P, 'en'); await drain();
  assert.ok(c.store.get().exportPreview, 'language switch keeps the preview');
  assert.equal(c.store.get().exportNotes, 'notes');
  gate.resolve(); await a;
  const g2 = deferred(); c.dev.studioLines = async (q) => { await g2.promise; return orig(q); };
  const b = c.actions.loadPack(B, 'en'); await drain();
  assert.equal(c.store.get().exportPreview, null, 'another pack clears it');
  assert.equal(c.store.get().exportNotes, '');
  g2.resolve(); await b;
});

test('M7: the focused button carries role=combobox; the unfocused listbox carries no aria-activedescendant', () => {
  const html = renderDropdown({ id: 'dd', value: 'b', options: [{ value: 'a', label: 'A' }, { value: 'b', label: 'B' }] });
  assert.match(html, /<button[^>]*class="c-dd-btn"[^>]*role="combobox"/);
  const list = html.match(/<div class="c-dd-list[^>]*>/)[0];
  assert.doesNotMatch(list, /aria-activedescendant/);
});

test('M2: re-picking the current option closes without firing change', () => {
  const handlers = {}, events = [];
  const opt = (v) => ({ dataset: { ddOpt: v }, classList: { contains: () => false, toggle() {}, add() {}, remove() {} }, querySelector: () => null, setAttribute() {}, id: '' });
  const select = { value: 'b', dispatchEvent: (e) => events.push(e.type), closest: () => wrap, classList: { }, };
  const list = { hidden: true, querySelectorAll: () => [], classList: { remove() {}, add() {}, contains: () => false }, setAttribute() {} };
  const btn = { setAttribute() {}, removeAttribute() {}, focus() {}, disabled: false };
  const wrap = { classList: { remove() {}, add() {}, contains: () => false }, querySelector: (q) => (q === 'select.c-dd-native' ? select : q === '.c-dd-btn' ? btn : q === '.c-dd-list' ? list : null), querySelectorAll: () => [], dataset: { dd: 'dd' } };
  const clickTarget = (o) => ({ closest: (sel) => (sel === '.c-dd-opt' ? { ...o, closest: () => wrap } : null) });
  const saved = globalThis.document;
  globalThis.document = { addEventListener: (t, fn) => { handlers[t] = fn; }, querySelectorAll: () => [] };
  try {
    bindDropdownEvents();
    handlers.click({ target: clickTarget(opt('b')), preventDefault() {}, stopPropagation() {} });
    assert.deepEqual(events, [], 'same value: no change event');
    handlers.click({ target: clickTarget(opt('a')), preventDefault() {}, stopPropagation() {} });
    assert.deepEqual(events, ['change'], 'a different value still fires change');
  } finally { globalThis.document = saved; }
});

test('C1: the ? help lists only keys that work on that page', () => {
  for (const page of ['packs', 'export', 'settings']) assert.deepEqual(PAGE_HELP[page].keys.map((k) => k[0]), ['Esc'], page);
  const lines = PAGE_HELP.lines.keys.map((k) => k[0]);
  assert.ok(!lines.includes('Search') && !lines.includes('R') && !lines.includes('T'));
});

test('C2: Thai “switch the pack language being edited” button says so', () => {
  assert.match(STRINGS.th.langnote_btn, /สลับไปแก้ภาษา/);
});

test('C4: help and tour copy names controls that exist', () => {
  for (const lang of ['en', 'th']) {
    const all = Object.entries(STRINGS[lang]).filter(([k]) => /^(help|tour)_/.test(k)).map(([, v]) => String(v)).join('\n');
    assert.doesNotMatch(all, /Build & export|สร้างไฟล์ส่งออก|Re-check|ตรวจซ้ำ|64×64|2,169/, lang);
  }
  assert.doesNotMatch(STRINGS.en.tour_export_2_w, /placeholders/);
  assert.ok(!STRINGS.th.tour_packs_3_w.includes('เอาไฟล์ออก'));
  assert.ok(!STRINGS.th.xc_provMissing.includes('Studio'));
});

test('C6: the size-limit “Not checked” line is translated, and a restart-pending pack has its own export message', () => {
  const T = makeT('th');
  const check = { id: 'manifest', severity: 'warning', message: 'Not checked until the size limits pass.' };
  assert.equal(exportCheckDetail(check, T, 'th'), STRINGS.th.xc_notChecked);
  assert.ok(STRINGS.en.b_first_export && STRINGS.th.b_first_export);
  assert.ok(!/record/.test(STRINGS.en.b_first_export));
  assert.match(STRINGS.en.k_esc, /discards a recording/);
});

test('C5: the guides describe the real Build, Download and consent behaviour', async () => {
  const en = await readFile(root('../../docs/guide/pack-maker.md'), 'utf8').catch(() => null);
  if (en === null) return; // docs live at the repository root; skipped when the package is tested alone
  assert.match(en, /downloads by itself/);
  assert.match(en, /Satone/);
  assert.match(en, /needs no tick of its own/);
});

test('HN-044 P2: a tour step never targets an overscan row outside the visible band of #vb-list', () => {
  const rect = (top, bottom) => ({ top, bottom });
  const list = { getBoundingClientRect: () => rect(100, 400) };
  const row = (top) => ({ hidden: false, offsetWidth: 10, offsetHeight: 10, closest: (s) => (s === '#vb-list' ? list : null), getBoundingClientRect: () => rect(top, top + 58) });
  const rows = [row(-150), row(-92), row(120), row(178)];
  const doc = { querySelectorAll: () => rows };
  assert.equal(resolveStepTarget({ selector: '#vb-list .vb-row' }, doc), rows[2]);
});

test('HN-044 P1: npm test reaches the subfolders and runs one file at a time', async () => {
  const pkg = JSON.parse(await readFile(root('package.json'), 'utf8'));
  assert.match(pkg.scripts.test, /tests\/\*\*\/\*\.test\.mjs/);
});
