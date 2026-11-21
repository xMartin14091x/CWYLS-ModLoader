// 2026-11-09 live bug: the game answered the catalogue with 503 (3 s timeout) twice and the page stayed blank. The page must keep
// retrying with backoff while it shows "Loading lines from the game…", then show the error with Retry, and log what happened.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi, StudioError } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createClientLog, safePath } from '../../ui/clientlog.js';
import { renderRows } from '../../ui/zones/scenes.js';
import { makeT, STRINGS } from '../../ui/i18n/i18n.js';
import { createDevBackend } from '../dev/dev-backend.js';

const tick = () => new Promise((r) => setImmediate(r));
const settleAll = async () => { for (let i = 0; i < 12; i++) await tick(); };

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

/** The dev backend, but its catalogue reads answer 503 `unavailable` (as the game did) while `failing` is true. */
function flaky() {
  const dev = createDevBackend({ lines: 30 });
  const state = { failing: true, calls: 0 };
  const busy = () => { state.calls++; return Promise.reject(Object.assign(new Error('the game did not answer within 3 s'), { code: 'unavailable', status: 503 })); };
  const adapter = new Proxy(dev, { get(target, name) { return (name === 'studioLines' && state.failing) ? busy : Reflect.get(target, name); } });
  return { adapter, state };
}

async function setup() {
  const { adapter, state } = flaky();
  const store = createStore({
    ready: false, conn: { connected: false, unauthorized: false }, status: null, packs: [], packId: null, packLang: 'th', groups: [], overall: {},
    lines: [], byId: new Map(), loadingLines: true, linesError: null, linesAttempt: 0, view: { group: null, filter: 'all', q: '' }, lineId: null, line: null,
    draft: null, history: null, saveNote: null, saveLive: null, menu: null, selTakeId: null, peaks: {}, last: null, scrollTo: null
  });
  const clk = clock(), logged = [];
  const clog = { log: (kind, data) => logged.push({ kind, ...data }) };
  const actions = createActions({ store, api: createApi(adapter), mode: 'dev', timers: clk, now: () => clk.time, selectDelayMs: 0, clog,
    retryMs: 1000, retryMaxMs: 8000, longFailMs: 20000, slowRetryMs: 30000 });
  const started = actions.start({});
  await settleAll();
  return { store, actions, clk, state, logged, started };
}

const T = makeT('en');
const rowsHtml = (s) => renderRows(s, { T, strings: STRINGS.en, lang: 'en' }, null).html;

test('a 503 catalogue keeps "Loading lines from the game…" and retries with backoff until the game answers', async () => {
  const c = await setup();
  assert.equal(c.store.get().loadingLines, true);
  assert.equal(c.store.get().linesError, null);
  assert.equal(c.store.get().linesAttempt, 1);
  assert.match(rowsHtml(c.store.get()), /Loading lines from the game/);
  assert.match(rowsHtml(c.store.get()), /attempt 2/);
  const first = c.state.calls;
  await c.clk.advance(1000); // backoff 1 s, then 2 s, 4 s
  assert.ok(c.state.calls > first, 'retried after the first backoff');
  await c.clk.advance(2000);
  c.state.failing = false;
  await c.clk.advance(4000);
  await c.started;
  const s = c.store.get();
  assert.equal(s.loadingLines, false);
  assert.equal(s.linesError, null);
  assert.ok(s.lines.length >= 30);
  assert.ok(c.logged.some((e) => e.kind === 'catalog.fail' && e.code === 'unavailable'));
  assert.ok(c.logged.some((e) => e.kind === 'catalog.retry' && e.nextMs === 2000));
  assert.ok(c.logged.some((e) => e.kind === 'catalog.done' && e.lines === s.lines.length));
});

test('after a long failure the list shows the error with Retry, keeps trying slowly, and Retry loads at once', async () => {
  const c = await setup();
  await c.clk.advance(25000); // past longFailMs (20 s) of failures
  let s = c.store.get();
  assert.equal(s.loadingLines, false);
  assert.equal(s.linesError.code, 'unavailable');
  const html = rowsHtml(s);
  assert.match(html, /The lines did not load/);
  assert.match(html, /data-act="retry-lines"/);
  const before = c.state.calls;
  await c.clk.advance(30000);
  assert.ok(c.state.calls > before, 'still retrying in the background');
  c.state.failing = false;
  await c.actions.retryLines();
  await settleAll();
  s = c.store.get();
  assert.equal(s.linesError, null);
  assert.ok(s.lines.length >= 30);
  assert.ok(c.logged.some((e) => e.kind === 'catalog.retryClicked'));
});

test('a code a retry cannot fix shows the error with Retry at once instead of looping', async () => {
  const { adapter } = flaky();
  const notFound = new Proxy(adapter, { get(target, name) { return name === 'studioLines' ? () => Promise.reject(new StudioError('not_found', 'no pack', { status: 404 })) : Reflect.get(target, name); } });
  const store = createStore({ ready: false, conn: { connected: false, unauthorized: false }, status: null, packs: [], packId: null, packLang: 'th', groups: [], overall: {},
    lines: [], byId: new Map(), loadingLines: true, linesError: null, linesAttempt: 0, view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, menu: null, last: null });
  const clk = clock();
  const actions = createActions({ store, api: createApi(notFound), mode: 'dev', timers: clk, now: () => clk.time, selectDelayMs: 0 });
  await actions.start({});
  await settleAll();
  assert.equal(store.get().loadingLines, false);
  assert.equal(store.get().linesError.code, 'not_found');
});

test('Thai has the loading, slow and failure copy, casual-polite', () => {
  const th = makeT('th');
  assert.equal(th('loading'), 'กำลังโหลดบรรทัดจากเกมอยู่นะ…');
  assert.match(th('loadingSlow', { n: 3 }), /ครั้งที่ 3/);
  assert.match(th('linesFail'), /ลองอีกครั้งนะ/);
  assert.equal(th('linesRetry'), 'ลองอีกครั้ง');
});

test('the client log always writes console.debug, keeps history while the game does not trace, and posts once it does', async () => {
  const clk = clock(), debug = [], posts = [];
  let trace = false;
  const log = createClientLog({ timers: clk, now: () => clk.time, flushMs: 2000, keep: 3, out: { debug: (...a) => debug.push(a) },
    send: async (entries) => { posts.push(entries); return { accepted: trace ? entries.length : 0, trace }; } });
  assert.equal(await log.probe(), false);
  assert.deepEqual(posts.map((p) => p[0].kind), ['probe']);
  for (let i = 0; i < 5; i++) log.log('api', { route: 'studioLines', status: 503, ms: 3012.25, n: i, obj: { no: 1 } });
  await clk.advance(5000);
  assert.equal(posts.length, 1, 'nothing posted while the trace is off');
  assert.equal(debug.length, 5);
  assert.equal(log.queued, 3, 'only the most recent entries are kept');
  trace = true;
  log.setRemote(true);
  await clk.advance(2000);
  assert.equal(posts.length, 2);
  assert.deepEqual(posts[1].map((e) => e.n), [2, 3, 4]);
  assert.equal(posts[1][0].ms, 3012.3);
  assert.equal(posts[1][0].obj, undefined, 'objects are not logged');
  assert.equal(safePath('/v1/maker/studio/packs/p/languages/th/lines?q=secret%20text&limit=500'), '/v1/maker/studio/packs/p/languages/th/lines?q=~&limit=500');
  const forced = createClientLog({ force: true, timers: clk, now: () => clk.time, out: null, send: async (e) => { posts.push(e); return { trace: false }; } });
  forced.log('boot', {});
  await clk.advance(2000);
  assert.equal(posts.length, 3, '?debug=1 posts even when the game says the trace is off');
});
