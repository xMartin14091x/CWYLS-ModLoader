// The only module that calls the port. It owns request ordering and the guards that keep a late answer from landing in the
// wrong place: one mutation queue per line (a status change never overlaps a subtitle save), a request counter on line
// selection, pinned revisions per operation, and the autosave state machine of logic/draft.js (per line, kept in a Map so a
// save that is still in flight when the creator moves on is finished and reconciled, not dropped).
import { reduce, createDraft, wireText } from './logic/draft.js';
import { newOperationId, loadCatalog, StudioError } from './api.js';
import { isUncertain, errorKey, takeVM, exportFolder } from './api/model.js';
import { createDub } from './dub.js';
import { featureOn, canCreatePacks, canExport, canVoiceDefault, canLabelDubs, hasPackVoiceDefault, canEditText, isReadOnly, currentTake, visibleLines, displayGroupOf, inDisplayGroup } from './selectors.js';
import { nextUnrecordedLine, nextUnfinishedLine, loadSettings, saveSettings } from './logic/progress.js';
import { omitUndefined } from './api/model.js';
import { authorSlug, suggestPackName, suggestPackId } from './logic/pack-naming.js';

const keyOf = (packId, lang, id) => `${packId}/${lang}/${id}`;
const splitKey = (key) => { const [packId, lang, ...rest] = key.split('/'); return { packId, lang, id: rest.join('/') }; };

/**
 * @param {{store:object, api:object, mode:string, params:URLSearchParams, toast?:(key:string, kind?:string, vars?:object)=>void,
 *   newOp?:()=>string, timers?:{setTimeout:Function, clearTimeout:Function}, reconnectMs?:number, now?:()=>number}} deps
 */
export function createActions({ store, api: rawApi, mode, params = new URLSearchParams(), toast = () => {}, newOp = newOperationId,
  timers = { setTimeout: (...a) => setTimeout(...a), clearTimeout: (...a) => clearTimeout(...a) }, reconnectMs = 2000, now = () => Date.now(),
  selectDelayMs = 120, micIdleMs, mic = null, player = null, decodeFile = null, reasonText = null, clog = null, storage = globalThis.localStorage,
  retryMs = 1000, retryMaxMs = 15000, longFailMs = 45000, slowRetryMs = 30000 }) {
  // The page log (ui/clientlog.js): never allowed to break an action.
  const note = (kind, data) => { try { clog?.log(kind, data); } catch { /* diagnostics only */ } };
  const wait = (ms) => new Promise((resolve) => timers.setTimeout(resolve, ms));
  // Codes a later attempt cannot fix: the page shows them with Retry instead of retrying on its own.
  const FINAL = new Set(['unauthorized', 'not_found', 'bad_request', 'read_only_pack', 'restart_required', 'forbidden_origin', 'recovery_required', 'metadata_unavailable']);
  const retryable = (e) => !(e instanceof StudioError) || !FINAL.has(e.code);
  const noMic = { supported: () => false, setup: async () => { throw new Error('none'); }, onLevel: () => () => {}, start() {}, stop: async () => ({ channels: [new Float32Array(0)], sampleRate: 48000 }), release() {} };
  const noPlayer = { play() {}, stop() {}, position: () => 0 };
  // After a 401 nothing more is sent, not even by a timer that was already running (W2: a 401 latches the page offline).
  const api = new Proxy(rawApi, {
    get(target, name) {
      const value = target[name];
      if (typeof value !== 'function' || name === 'close' || name === 'watch') return value;
      return (...args) => (store.get().conn.unauthorized ? Promise.reject(new StudioError('unauthorized', 'session ended')) : value.apply(target, args));
    }
  });
  const drafts = new Map();          // key -> Draft (logic/draft.js)
  const draftTimers = new Map();     // key -> timer id (debounce, retry)
  const revs = new Map();            // key -> latest revision the server told us
  const pinned = new Map();          // operationId -> the expectedRevision first used with it (a retry must send the same request)
  const queues = new Map();          // key -> promise tail (one mutation at a time per line)
  const recentOwn = new Map();       // key -> time of our last save (so our own events are not refetched)
  const refetchTimers = new Map();
  const knownTakes = new Map();      // key -> Take[] known for unconfirmed take counting
  let exportCheckVersion = 0;
  let idx = new Map();               // line id -> index in store.lines
  let lineVersion = 0, loadVersion = 0, reloadVersion = 0, reconnecting = false, wasOffline = false, groupsTimer = 0, lastPlay = null;

  const S = () => store.get();
  const currentKey = () => { const s = S(); return s.lineId ? keyOf(s.packId, s.packLang, s.lineId) : null; };

  function recomputeUnconfirmed() {
    const s = S(), { packId, packLang: lang, consentVersion } = s;
    if (!packId) { if (s.unconfirmedCount !== 0) store.set({ unconfirmedCount: 0 }); return 0; }
    const ver = consentVersion || 'consent-1';
    const prefix = `${packId}/${lang}/`;
    let count = 0;
    for (const [k, items] of knownTakes.entries()) {
      if (!k.startsWith(prefix)) continue;
      for (const t of items || []) {
        if (!(t.consent?.confirmed === true && t.consent?.statementVersion === ver)) count++;
      }
    }
    if (S().unconfirmedCount !== count) store.set({ unconfirmedCount: count });
    return count;
  }

  // ---------------- errors and connection ----------------
  function handleError(e) {
    if (!(e instanceof StudioError)) return;
    note('error', { code: e.code, status: e.status, message: e.message });
    if (e.code === 'unauthorized') { latch(); return; }
    if (e.code === 'unavailable' && e.status === 0) setDisconnected();
  }
  function warnStorageOrError(e, fallback = errorKey(e.code)) {
    if (e.code === 'storage_limit' && typeof e.message === 'string' && e.message.trim())
      toast('e_storage_detail', 'warn', { message: e.message });
    else toast(fallback, 'warn');
  }
  function latch() { store.set({ conn: { connected: false, unauthorized: true } }); api.close?.(); }
  function setDisconnected() {
    if (S().conn.unauthorized) return;
    wasOffline = true;
    if (S().conn.connected) store.set({ conn: { connected: false, unauthorized: false } });
    startReconnect();
  }
  function startReconnect() {
    if (reconnecting) return;
    reconnecting = true;
    let delay = reconnectMs;
    const tick = async () => {
      if (S().conn.unauthorized) { reconnecting = false; return; }
      try {
        const status = await api.status();
        reconnecting = false;
        store.set({ status, conn: { connected: true, unauthorized: false } });
        if (wasOffline) { wasOffline = false; toast('t_reconnected'); await reload({ keepLine: true }); }
      } catch (e) {
        if (e instanceof StudioError && e.code === 'unauthorized') { reconnecting = false; latch(); return; }
        delay = Math.min(15000, delay * 2);
        timers.setTimeout(tick, delay);
      }
    };
    timers.setTimeout(tick, delay);
  }
  async function refreshStatus() {
    try { const status = await api.status(); store.set({ status, conn: { connected: true, unauthorized: false } }); }
    catch (e) { handleError(e); }
  }

  // ---------------- loading ----------------
  let startedOk = false, startOptions = {};
  async function start({ fixture = 'normal' } = {}) {
    startOptions = { fixture };
    if (!S().settings) store.set({ settings: loadSettings(storage) });
    const caps = await api.capabilities();
    note('capabilities', { contract: caps?.contract ?? null, features: (caps?.features || []).join(',') });
    if (!(caps.contract >= 2) || !(caps.features || []).includes('catalog')) throw new StudioError('not_found', 'contract or catalogue missing');
    store.set({ conn: { connected: true, unauthorized: false }, features: caps.features, consentVersion: caps.consentVersion || null });
    await refreshStatus();
    const packs = await packsWithRetry();
    const wanted = fixture === 'readonly' ? 'thai-subs-community' : fixture === 'firstrun' ? 'my-new-pack' : packs.find((p) => p.editable)?.id || packs[0]?.id;
    const pack = packs.find((p) => p.id === wanted) || packs[0];
    store.set({ packs, gameLang: S().status?.gameLanguage || null, ready: true });
    if (pack) await loadPack(pack.id, pack.languages[0] || 'th');
    api.bus?.addEventListener('maker-event', (ev) => onEvent(ev.detail));
    api.bus?.addEventListener('resync', () => reload({ keepLine: true }));
    api.bus?.addEventListener('connection', () => { if (api.connection === 'offline') setDisconnected(); });
    api.watch();
    startedOk = true;
  }

  async function reload({ keepLine = false } = {}) {
    const seq = ++reloadVersion;
    try {
      const packs = await api.packs();
      if (seq !== reloadVersion) return;
      const s = S();
      store.set({ packs });
      const pack = packs.find((p) => p.id === s.packId) || packs.find((p) => p.editable) || packs[0];
      if (pack) await loadPack(pack.id, pack.languages.includes(s.packLang) ? s.packLang : pack.languages[0] || 'th', { keepLine, keepView: true });
      if (seq !== reloadVersion) return;
      await refreshStatus();
    } catch (e) { handleError(e); }
  }

  /**
   * The pack list at start: a game that is slow in the background answers 503 for a while, which is not "too old".
   * Retries with backoff for about `longFailMs`, showing "Loading lines from the game…", then gives up to the caller.
   */
  async function packsWithRetry() {
    const since = now();
    for (let attempt = 0; ; attempt++) {
      try { return await api.packs(); } catch (e) {
        if (!retryable(e) || (e instanceof StudioError && e.status === 0) || now() - since >= longFailMs) throw e;
        const delay = Math.min(retryMaxMs, retryMs * 2 ** attempt);
        note('packs.retry', { code: e?.code ?? null, attempt: attempt + 1, nextMs: delay });
        store.set({ linesAttempt: attempt + 1 });
        await wait(delay);
      }
    }
  }

  /**
   * Loads a pack's groups and whole catalogue. Never leaves the list blank: while the game does not answer it keeps
   * retrying with backoff and shows "Loading lines from the game…" (with the attempt); after `longFailMs` of failures it
   * shows the error with Retry and keeps trying every `slowRetryMs`. A newer loadPack (Retry, another pack) ends this one.
   */
  async function loadPack(packId, lang, { keepLine = false, keepView = false } = {}) {
    const previousLineId = S().lineId;
    const switching = S().packId !== packId || S().packLang !== lang;
    if (switching) {
      const packChanged = S().packId !== packId;
      flushCurrent();
      ++lineVersion;
      dub.cancelRecord(); dub.stopTake();
      store.set({ line: null, lineId: null, draft: null, byId: new Map(), groups: [], overall: { totalLines: 0, dubbableLines: 0, subtitlePresent: 0, dubPresent: 0 }, history: null,
        selTakeId: null, trimDraft: null, saveNote: null, saveLive: null });
      // The export check, result and notes belong to the pack, not to the language being edited: only another pack clears them.
      if (packChanged) {
        ++exportCheckVersion;
        store.set({ exportPreview: null, exportResult: null, exportLoading: false, exportBusy: false, exportNotes: '' });
      }
    }
    ++reloadVersion;
    const version = ++loadVersion;
    store.set({ packId, packLang: lang, loadingLines: keepView ? S().lines.length === 0 : true, linesError: null, linesAttempt: 0, menu: null, ...(keepView ? {} : { lines: [], view: { group: null, filter: 'all', q: '' } }) });
    let since = null;
    for (let attempt = 0; ; attempt++) {
      if (await loadPackOnce(packId, lang, { keepLine, previousLineId, switching, version, attempt })) return;
      if (version !== loadVersion) return;
      const e = lastLoadError;
      if (!retryable(e)) {
        store.set({ loadingLines: false, linesError: { code: e?.code || 'internal' }, linesAttempt: attempt + 1 });
        note('catalog.giveup', { packId, lang, code: e?.code ?? null, attempt });
        return;
      }
      since ??= now();
      const long = now() - since >= longFailMs;
      const delay = long ? slowRetryMs : Math.min(retryMaxMs, retryMs * 2 ** attempt);
      // Lines already on screen stay; an empty list shows the loading text, then the error with Retry.
      const empty = S().lines.length === 0;
      store.set({ loadingLines: empty && !long, linesError: empty && long ? { code: e?.code || 'unavailable' } : null, linesAttempt: attempt + 1 });
      note('catalog.retry', { packId, lang, code: e?.code ?? null, attempt: attempt + 1, nextMs: delay, shownError: empty && long });
      await wait(delay);
      if (version !== loadVersion) return;
    }
  }

  let lastLoadError = null;
  /** One attempt; true when it finished (loaded, or superseded by a newer loadPack). */
  async function loadPackOnce(packId, lang, { keepLine, previousLineId, switching, version, attempt }) {
    const started = now();
    const mutSeq0 = new Map(mutSeq);
    note('catalog.start', { packId, lang, attempt });
    try {
      const [groups, cat] = await Promise.all([api.groups(packId, lang), loadCatalog(api, packId, lang, {
        onPage: (p) => note('catalog.page', { offset: p.offset, items: p.items, total: p.total, ms: p.ms, restart: p.restart })
      })]);
      if (version !== loadVersion) return true;
      note('catalog.done', { packId, lang, lines: cat.lines.length, groups: groups.groups.length, ms: now() - started, attempt });
      idx = new Map(cat.lines.map((l, i) => [l.id, i]));
      const byId = new Map(cat.lines.map((l) => [l.id, l]));
      for (const l of cat.lines) {
        const k = keyOf(packId, lang, l.id);
        if ((busy.get(k) || 0) === 0 && (mutSeq.get(k) || 0) === (mutSeq0.get(k) || 0) && !drafts.get(k)?.inflight) {
          revs.set(k, l.revision);
        }
      }
      store.set({ groups: groups.groups, overall: groups.overall, catalogRevision: cat.catalogRevision, lines: cat.lines, byId, loadingLines: false, linesError: null, linesAttempt: 0 });
      await loadVoiceDefault(packId);
      if (version !== loadVersion) return true;
      recomputeUnconfirmed();
      const liveLineId = S().lineId;
      const preferredId = keepLine && liveLineId && byId.has(liveLineId) ? liveLineId : previousLineId;
      const wanted = keepLine && byId.has(preferredId) ? preferredId
        : params.get('line') && byId.has(params.get('line')) ? params.get('line')
          : byId.has('clickheroine_rest_010') ? 'clickheroine_rest_010' : cat.lines[0]?.id;
      if (!wanted) store.set({ line: null, lineId: null, draft: null });
      else if (keepLine && !switching && liveLineId === wanted && S().line?.id === wanted) await refetchLine(wanted);
      else await selectLine(wanted, { scroll: true, force: true });
      return true;
    } catch (e) {
      if (version !== loadVersion) return true;
      lastLoadError = e;
      note('catalog.fail', { packId, lang, code: e?.code ?? null, status: e?.status ?? null, message: e?.message ?? null, ms: now() - started, attempt });
      handleError(e);
      return false;
    }
  }

  /** The Retry button of the line list: a fresh load of the current pack. */
  async function retryLines() {
    const s = S();
    note('catalog.retryClicked', { packId: s.packId, lang: s.packLang, started: startedOk });
    if (!startedOk) {
      // the first start never got the packs: run it again (it also starts the event stream)
      store.set({ loadingLines: true, linesError: null, linesAttempt: 0 });
      try { await start(startOptions); } catch (e) { store.set({ loadingLines: false, linesError: { code: e?.code || 'unavailable' } }); handleError(e); }
      return;
    }
    if (s.packId) return loadPack(s.packId, s.packLang, { keepLine: true, keepView: true });
    return reload({ keepLine: true });
  }

  function refreshGroupsSoon() {
    timers.clearTimeout(groupsTimer);
    groupsTimer = timers.setTimeout(async () => {
      const s = S();
      try { const g = await api.groups(s.packId, s.packLang); if (S().packId === s.packId && S().packLang === s.packLang) store.set({ groups: g.groups, overall: g.overall, catalogRevision: g.catalogRevision }); } catch (e) { handleError(e); }
    }, 1200);
  }

  // ---------------- lines ----------------
  const visibleKey = (key, id) => key === keyOf(S().packId, S().packLang, id);
  function patchLine(id, patch, key = keyOf(S().packId, S().packLang, id)) {
    if (!visibleKey(key, id)) return;
    const s = S(), entry = s.byId.get(id);
    if (!entry) return;
    const next = { ...entry, ...patch };
    s.byId.set(id, next);
    const lines = s.lines.slice();
    lines[idx.get(id)] = next;
    store.set(s.lineId === id ? { lines, line: { ...s.line, ...patch } } : { lines });
  }

  async function selectLine(id, { scroll = false, force = false, skipAutoDone = false } = {}) {
    const s0 = S();
    if (!s0.byId.has(id) || (!force && id === s0.lineId)) return;
    const prev = currentKey();
    if (prev) flush(prev);
    const st = s0.settings || loadSettings(storage);
    if (!force && !skipAutoDone && st.autoDoneOnNext && s0.line && s0.line.id !== id && s0.line.status !== 'done' && featureOn(s0, 'workflow') && !isReadOnly(s0)) {
      setWorkflow({ status: 'done' }, s0.line.id);
    }
    if (S().rec !== 'idle') dub.cancelRecord();
    if (S().playingTake) dub.stopTake();
    if (S().trimDraft) store.set({ trimDraft: null });
    const version = ++lineVersion, { packId, packLang: lang } = s0, key = keyOf(packId, lang, id), entry = s0.byId.get(id);
    if (!drafts.has(key)) drafts.set(key, createDraft(key, entry.text, revs.get(key) ?? entry.revision));
    const seq0 = mutSeq.get(key) || 0;
    const nextView = s0.view?.group && entry?.group && !inDisplayGroup(s0, entry.group, s0.view.group)
      ? { ...s0.view, group: displayGroupOf(s0, entry.group)?.id || entry.group }
      : s0.view;
    store.set({ lineId: id, line: entry, view: nextView, draft: drafts.get(key), menu: s0.menu === 'hist' ? null : s0.menu, selTakeId: null, history: null, saveNote: null, saveLive: null, scrollTo: scroll ? id : null });
    // holding ↑/↓ moves through lines faster than the loader allows reads (20/s): the detail is read only once the selection rests
    if (selectDelayMs > 0 && !force) { await new Promise((r) => timers.setTimeout(r, selectDelayMs)); if (version !== lineVersion) return; }
    try {
      // A read-only line (TD-3: its id cannot name a file) has no takes, and the loader answers 400 for them.
      const [full, takes] = await Promise.all([api.line(packId, lang, id), entry?.readOnly ? null : api.takes(packId, lang, id).catch((e) => { if (e instanceof StudioError && e.code === 'not_found') return null; throw e; })]);
      if (version !== lineVersion) return;
      if (stale(key, seq0)) { refetchLineSoon(id); return; }
      mergeLine(key, id, full, takes);
      dub.hydrate(S().line, () => version === lineVersion);
    } catch (e) { if (version === lineVersion) handleError(e); }
  }

  /** Puts a fetched line (and its takes) into the catalogue, the current view and the draft. */
  function mergeLine(key, id, full, takes) {
    revs.set(key, full.revision);
    if (takes) {
      knownTakes.set(key, takes.items || []);
      recomputeUnconfirmed();
    }
    const patch = { text: full.text, status: full.status, starred: full.starred, revision: full.revision, clipRevision: full.clipRevision, publishedTakeId: full.publishedTakeId,
      ownedDub: full.ownedDub, takeCount: takes ? takes.items.length : full.takeCount, context: full.context, dubDurationSec: full.dubDurationSec,
      effectivePack: full.effectivePack, effectiveText: full.effectiveText, dubPack: full.dubPack, readOnly: full.readOnly };
    const s = S();
    if (!visibleKey(key, id)) return;
    if (s.byId.has(id)) patchLine(id, patch, key);
    if (S().lineId === id && takes) store.set({ line: { ...S().line, ...patch, takes: takes.items, favouriteTakeId: takes.favouriteTakeId, publishedTakeId: takes.publishedTakeId } });
    else if (S().lineId === id) store.set({ line: { ...S().line, ...patch } });
    dispatchDraft(key, { type: 'server', text: full.text, revision: full.revision });
  }

  async function refetchLine(id, context = S()) {
    const s = { packId: context.packId, packLang: context.lang ?? context.packLang, lineId: S().lineId }, key = keyOf(s.packId, s.packLang, id), seq0 = mutSeq.get(key) || 0;
    try {
      const [full, takes] = await Promise.all([api.line(s.packId, s.packLang, id), api.takes(s.packId, s.packLang, id).catch(() => null)]);
      if (S().packId !== s.packId || S().packLang !== s.packLang) return;
      if (stale(key, seq0)) { refetchLineSoon(id); return; }
      mergeLine(key, id, full, takes);
      if (S().lineId === id) dub.hydrate(S().line, () => S().lineId === id);
    } catch (e) { handleError(e); }
  }
  function refetchLineSoon(id) {
    const context = S(), key = keyOf(context.packId, context.packLang, id);
    timers.clearTimeout(refetchTimers.get(key));
    refetchTimers.set(key, timers.setTimeout(() => { refetchTimers.delete(key); if (visibleKey(key, id)) refetchLine(id, context); }, 250));
  }

  /** "Open line": the line the game just spoke. Clears a scene/filter/search that would hide it, then selects it and scrolls to it. */
  async function openSpoken(id) {
    const s = S();
    if (!id || !s.byId.has(id)) return false;
    if (!visibleLines(s).some((l) => l.id === id)) { store.set({ view: { group: null, filter: 'all', q: '' } }); toast('t_open_cleared'); }
    await selectLine(id, { scroll: true });
    return true;
  }

  /** Jumps to the next voiced line that has no recorded take yet (or no published dub), clearing filters if needed. */
  async function nextUnrecorded({ silent = false } = {}) {
    const s = S();
    let cand = nextUnrecordedLine(visibleLines(s), s.lineId);
    if (!cand) {
      cand = nextUnrecordedLine(s.lines, s.lineId);
      if (cand && !visibleLines(s).some((l) => l.id === cand.id)) {
        store.set({ view: { group: null, filter: 'all', q: '' } });
      }
    }
    if (!cand) {
      if (!silent) toast('t_all_recorded');
      return null;
    }
    await selectLine(cand.id, { scroll: true });
    return cand.id;
  }

  function selectTake(id) { store.set({ selTakeId: id }); }
  function setVoiceFilter(v) {
    const nx = { ...S().view };
    if (!v || v === 'all') delete nx.voice; else nx.voice = v;
    store.set({ view: nx });
  }
  function setCharFilter(ch) {
    const nx = { ...S().view };
    if (!ch || ch === 'all') delete nx.char; else nx.char = ch;
    store.set({ view: nx });
  }
  function setSearch(q) {
    store.set({ view: { ...S().view, q: q || '' } });
  }

  // ---------------- autosave ----------------
  const busy = new Map();            // key -> mutations queued or running
  const mutSeq = new Map();          // key -> counter that moves when a mutation is queued or finishes
  const bumpSeq = (key) => mutSeq.set(key, (mutSeq.get(key) || 0) + 1);
  /** A read that began before one of our own writes finished may carry the old text: it must not overwrite the newer state. */
  const stale = (key, seq0) => (mutSeq.get(key) || 0) !== seq0 || (busy.get(key) || 0) > 0;
  /** Is a change on this line ours (running now, or finished a moment ago)? Our own events must not trigger refetches. */
  const own = (key) => (busy.get(key) || 0) > 0 || (recentOwn.has(key) && now() - recentOwn.get(key) < 1500);
  function queue(key, fn) {
    busy.set(key, (busy.get(key) || 0) + 1);
    bumpSeq(key);
    const run = async () => { try { return await fn(); } finally { busy.set(key, busy.get(key) - 1); recentOwn.set(key, now()); bumpSeq(key); } };
    const tail = (queues.get(key) || Promise.resolve()).catch(() => {}).then(run);
    queues.set(key, tail);
    return tail;
  }
  const clearDraftTimer = (key) => { timers.clearTimeout(draftTimers.get(key)); draftTimers.delete(key); };

  function dispatchDraft(key, event) {
    const d = drafts.get(key);
    if (!d) return;
    const { state, effects } = reduce(d, event);
    drafts.set(key, state);
    if (key === currentKey()) store.set({ draft: state });
    for (const eff of effects) runEffect(key, eff);
  }
  function runEffect(key, eff) {
    switch (eff.type) {
      case 'arm-timer': clearDraftTimer(key); draftTimers.set(key, timers.setTimeout(() => { draftTimers.delete(key); dispatchDraft(key, { type: 'flush', opId: newOp() }); }, eff.ms)); break;
      case 'cancel-timer': clearDraftTimer(key); break;
      case 'arm-retry': clearDraftTimer(key); draftTimers.set(key, timers.setTimeout(() => { draftTimers.delete(key); dispatchDraft(key, { type: 'retry', opId: newOp() }); }, eff.ms)); break;
      case 'save': performSave(key, eff); break;
      case 'check-operation': performCheck(key, eff.opId); break;
      case 'fetch-line': performFetchLine(key); break;
      default: break;
    }
  }
  function performSave(key, eff) {
    const { packId, lang, id } = splitKey(key);
    return queue(key, async () => {
      if (!pinned.has(eff.opId)) pinned.set(eff.opId, revs.get(key) ?? eff.expectedRevision);
      const expectedRevision = pinned.get(eff.opId);
      try {
        const res = await api.saveSubtitle(packId, lang, id, { expectedRevision, operationId: eff.opId, text: wireText(eff.text) });
        acknowledgeSave(key, eff, res);
      } catch (e) {
        handleError(e);
        dispatchDraft(key, { type: 'failed', opId: eff.opId, code: e.code || 'internal', uncertain: isUncertain(e) });
      }
    });
  }
  // Normal acknowledgements and committed-operation receipts reconcile exactly the same identity and revision.
  function acknowledgeSave(key, eff, res) {
    const { packId, id } = splitKey(key);
    pinned.delete(eff.opId);
    revs.set(key, res.revision);
    recentOwn.set(key, now());
    bumpSeq(key);
    patchLine(id, { text: eff.text || '', revision: res.revision }, key);
    if (key === currentKey()) store.set({ saveLive: res.live === true, saveNote: res.live === true ? null : (res.note || '') });
    dispatchDraft(key, { type: 'saved', opId: eff.opId, revision: res.revision });
    if (!visibleKey(key, id)) return;
    const cur = S().byId.get(id);
    if (cur && !(cur.effectivePack === packId && eff.text && eff.text.trim())) refetchLineSoon(id);
    refreshGroupsSoon();
    if (S().menu === 'hist' && S().lineId === id) openHistory(true);
  }
  async function performCheck(key, opId) {
    const { packId } = splitKey(key);
    try {
      const op = await api.operation(packId, opId);
      const inflight = drafts.get(key)?.inflight;
      if (inflight?.opId !== opId) return;
      if (op.saved === true) acknowledgeSave(key, { opId, text: inflight.text }, op);
      else dispatchDraft(key, { type: 'operation', opId, saved: false });
    } catch (e) {
      if (e instanceof StudioError && e.code === 'not_found') { dispatchDraft(key, { type: 'operation', opId, saved: false }); return; }
      handleError(e);
      // still unreachable: ask again once the connection is back (the reconnect loop reloads; this retries the question)
      timers.setTimeout(() => performCheck(key, opId), Math.max(500, reconnectMs));
    }
  }
  async function performFetchLine(key) {
    const { packId, lang, id } = splitKey(key);
    try {
      const full = await api.line(packId, lang, id);
      revs.set(key, full.revision);
      dispatchDraft(key, { type: 'server', text: full.text, revision: full.revision });
      patchLine(id, { status: full.status, starred: full.starred, revision: full.revision }, key);
    } catch (e) { handleError(e); }
  }

  function editText(text) {
    const s = S();
    if (!canEditText(s) || !s.line) return;
    const key = currentKey();
    if (!key) return;
    dispatchDraft(key, { type: 'edit', text });
    if (s.line.status === 'todo' && text && text.trim() && featureOn(s, 'workflow') && !isReadOnly(s)) {
      setWorkflow({ status: 'draft' });
    }
  }
  function flush(key) {
    const d = drafts.get(key);
    if (!d || d.status === 'conflict') return;
    clearDraftTimer(key);
    dispatchDraft(key, { type: 'flush', opId: newOp() });
  }
  const flushCurrent = () => { const k = currentKey(); if (k) flush(k); };
  /** Everything typed and not yet sent goes out now (page hide, tab switch). Best effort: a closing page cannot wait for the answer. */
  function flushAll() { for (const key of drafts.keys()) flush(key); }
  /** Resolves when the current line's queued mutations have finished. */
  async function settle(key = currentKey()) {
    if (!key) return false;
    for (;;) {
      const before = drafts.get(key);
      if (!before || before.status === 'failed' || before.status === 'conflict') return false;
      flush(key);
      const tail = queues.get(key);
      await (tail || Promise.resolve()).catch(() => {});
      if (tail !== queues.get(key)) continue;
      const d = drafts.get(key);
      if (d?.status === 'typing' && !d.inflight) continue; // text typed while the previous save was in flight
      return !!d && d.status === 'saved' && !d.inflight && (d.text ?? '') === (d.baseText ?? '');
    }
  }
  function resolveConflict(choice) {
    const key = currentKey(), d = key && drafts.get(key);
    if (!d) return;
    if (choice === 'theirs' && d.theirs) patchLine(S().lineId, { text: d.theirs.text, revision: d.theirs.revision });
    dispatchDraft(key, { type: 'resolve', choice });
  }

  // ---------------- workflow, history ----------------
  async function setWorkflow(patch, targetId = S().line?.id) {
    const s = S(), line = targetId ? s.byId.get(targetId) : s.line;
    if (!line) return;
    if (!featureOn(s, 'workflow')) { toast('feat_workflow', 'warn'); return; }
    if (isReadOnly(s) || line.readOnly) { toast('t_ro', 'warn'); return; }
    const { packId, packLang: lang } = s, key = keyOf(packId, lang, line.id), id = line.id;
    const prev = { status: line.status, starred: line.starred };
    patchLine(id, patch.status !== undefined ? { status: patch.status, ...(patch.starred !== undefined ? { starred: patch.starred } : {}) } : { starred: patch.starred });
    return queue(key, async () => {
      const operationId = newOp();
      try {
        const res = await api.updateWorkflow(packId, lang, id, { expectedRevision: revs.get(key), operationId, ...patch });
        revs.set(key, res.revision);
        recentOwn.set(key, now());
        patchLine(id, { revision: res.revision, status: res.workflow.status, starred: res.workflow.starred }, key);
        dispatchDraft(key, { type: 'revision', revision: res.revision });
        if (visibleKey(key, id)) refreshGroupsSoon();
      } catch (e) {
        patchLine(id, prev, key);
        handleError(e);
        toast(e.code === 'conflict' ? 'e_conflict' : 't_status_failed', 'warn');
        if (e.code === 'conflict' && visibleKey(key, id)) refetchLine(id);
      }
    });
  }
  const setStatus = (status) => setWorkflow({ status });
  const toggleStar = () => setWorkflow({ starred: !S().line?.starred });
  const toggleRetake = () => setWorkflow({ status: S().line?.status === 'needs-retake' ? 'draft' : 'needs-retake' });

  function setSetting(key, value) {
    const cur = S().settings || loadSettings(storage);
    const next = saveSettings({ ...cur, [key]: !!value }, storage);
    store.set({ settings: next });
    return next;
  }

  async function moveLine(dir = 1) {
    const s = S(), vis = visibleLines(s);
    const i = vis.findIndex((l) => l.id === s.lineId);
    const target = vis[i + dir];
    if (!target) return null;
    await selectLine(target.id, { scroll: true });
    return target.id;
  }

  async function completeLine() {
    const s = S(), line = s.line;
    if (!line) return null;
    if (!featureOn(s, 'workflow')) { toast('feat_workflow', 'warn'); return null; }
    if (isReadOnly(s)) { toast('t_ro', 'warn'); return null; }
    const id = line.id, grp = line.group || s.view?.group || null;
    flushCurrent();
    const wfPromise = setWorkflow({ status: 'done' }, id);
    const jump = (S().settings || loadSettings(storage)).jumpAfterDone !== false;
    if (jump) {
      const next = nextUnfinishedLine(S().lines, id, grp);
      if (next) {
        const curS = S();
        if (!visibleLines(curS).some((l) => l.id === next.id)) {
          const nextDg = displayGroupOf(curS, next.group)?.id || next.group;
          const nextGroup = curS.view.group && !inDisplayGroup(curS, next.group, curS.view.group) ? nextDg : curS.view.group;
          const nextView = { ...curS.view, group: nextGroup };
          if (!visibleLines({ ...curS, view: nextView }).some((l) => l.id === next.id)) {
            store.set({ view: { group: null, filter: 'all', q: '' } });
          } else {
            store.set({ view: nextView });
          }
        }
        await Promise.all([wfPromise, selectLine(next.id, { scroll: true, skipAutoDone: true })]);
        return next.id;
      }
    }
    await wfPromise;
    return id;
  }

  async function openHistory(silent = false) {
    const s = S(), line = s.line;
    if (!line) return;
    try { const history = await api.history(s.packId, s.packLang, line.id, { limit: 20 }); if (S().packId === s.packId && S().packLang === s.packLang && S().lineId === line.id) store.set({ history }); }
    catch (e) { handleError(e); if (!silent) toast('t_history_failed', 'warn'); }
  }
  async function revert(revisionId) {
    const s = S(), line = s.line;
    if (!line) return;
    const { packId, packLang: lang } = s, key = keyOf(packId, lang, line.id), id = line.id;
    await settle(key);
    let reverted = false;
    await queue(key, async () => {
      try {
        const res = await api.revertSubtitle(packId, lang, id, revisionId, { expectedRevision: revs.get(key), operationId: newOp() });
        revs.set(key, res.revision);
        reverted = true;
      } catch (e) { handleError(e); toast(errorKey(e.code), 'warn'); if (e.code === 'conflict') reverted = 'conflict'; }
    });
    // read the result back only after the queue is idle (a read taken while a write is running is discarded as stale)
    if (reverted) {
      await refetchLine(id);
      if (reverted === true) { toast('t_revert'); if (S().menu === 'hist') await openHistory(true); refreshGroupsSoon(); }
    }
  }

  // ---------------- packs & export (Phase C) ----------------
  const BUILT_IN_LANGS = new Set(['ja', 'en', 'zh-hans', 'zh-hant', 'pt', 'pt-br', 'ko', 'ru']);
  const LANG_META = {
    th: { caption: 'ไทย', en: 'Thai' },
    vi: { caption: 'Tiếng Việt', en: 'Vietnamese' },
    id: { caption: 'Bahasa Indonesia', en: 'Indonesian' },
    es: { caption: 'Español', en: 'Spanish' },
    fr: { caption: 'Français', en: 'French' },
    de: { caption: 'Deutsch', en: 'German' }
  };
  const slug = (name) => {
    const base = name.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 56);
    return base.length >= 3 ? base : `pack-${newOp().slice(0, 6)}`;
  };
  function bytesToDataUrl(bytes, mime = 'image/png') {
    let bin = '';
    const u8 = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    for (let i = 0; i < u8.length; i++) bin += String.fromCharCode(u8[i]);
    const b64 = typeof btoa === 'function' ? btoa(bin) : Buffer.from(u8).toString('base64');
    return `data:${mime};base64,${b64}`;
  }
  async function blobToDataUrl(blob) {
    if (!blob) return null;
    const buf = await blob.arrayBuffer();
    return bytesToDataUrl(new Uint8Array(buf), blob.type || 'image/png');
  }

  async function createPack(options = {}) {
    if (S().creatingPack) return false;
    store.set({ creatingPack: true });
    try { return await createPackOnce(options); }
    finally { store.set({ creatingPack: false }); }
  }

  async function createPackOnce({ name, id: rawId, idCustom = true, author, description, lang = 'th', langName = '', iconBytes = null } = {}) {
    if (!canCreatePacks(S())) { toast('feat_newpacks', 'warn'); return false; }
    const clean = (name || '').trim();
    if (!clean || Array.from(clean).length > 48) { toast('np_name_bad', 'warn'); return false; }
    const suppliedId = (rawId || '').trim();
    const customId = idCustom ? suppliedId : '';
    if (customId && !/^[a-z0-9][a-z0-9._-]{2,63}$/.test(customId)) { toast('np_id_bad', 'warn'); return false; }
    const code = String(lang ?? '').trim().toLowerCase();
    if (BUILT_IN_LANGS.has(code)) { toast('np_lang_builtin', 'warn'); return false; }
    if (!/^[a-z]{2,3}(-[a-z0-9]{2,8})?$/.test(code)) { toast('np_lang_bad', 'warn'); return false; }
    const base = suppliedId || slug(clean);
    const cleanAuthor = author && String(author).trim() ? String(author).trim() : undefined;
    const cleanDesc = description && String(description).trim() ? String(description).trim() : undefined;
    const cleanLangName = String(langName || S().newPackForm?.langName || '').trim();
    const registered = mode === 'dev' || S().packs.some((p) => p.loaded && !p.restartRequired && (p.languages || []).includes(code));
    const language = registered
      ? { code }
      : { code, caption: cleanLangName || LANG_META[code]?.caption || code.toUpperCase(), englishName: LANG_META[code]?.en || cleanLangName || code.toUpperCase() };
    const icon = iconBytes || S().newPackForm?.iconBytes || null;
    const maxAttempts = customId ? 1 : 3;
    for (let attempt = 0; attempt < maxAttempts; attempt++) {
      const id = attempt ? `${base}-${attempt + 1}`.slice(0, 64) : base, operationId = newOp();
      for (let tryNo = 0; tryNo < 2; tryNo++) {
        try {
          busy.set('__pack__', (busy.get('__pack__') || 0) + 1);
          const res = await api.createPack(omitUndefined({ operationId, id, name: clean, author: cleanAuthor, description: cleanDesc, language }));
          if (icon && typeof api.savePackIcon === 'function') {
            try { await uploadPackIcon(res.pack.id, icon, { silent: true }); } catch (ie) { handleError(ie); }
          }
          store.set({ packId: res.pack.id, packLang: code, page: 'studio', packsSub: 'list', menu: null, modal: null, newPackForm: null });
          const packs = await api.packs();
          store.set({ packs, page: 'studio', packsSub: 'list', menu: null, modal: null });
          await loadPack(res.pack.id, code);
          toast('t_created', 'ok', { p: clean });
          return true;
        } catch (e) {
          if (e.code === 'conflict' && tryNo === 0 && !customId) break; // id taken: next suffix
          if (isUncertain(e) && tryNo === 0) continue;                  // lost reply: the same request is safe to repeat (same operationId)
          handleError(e);
          warnStorageOrError(e, e.code === 'conflict' ? 't_pack_failed' : errorKey(e.code));
          return false;
        } finally {
          busy.set('__pack__', Math.max(0, (busy.get('__pack__') || 1) - 1));
          recentOwn.set('__pack__', now());
        }
      }
    }
    toast('t_pack_failed', 'warn');
    return false;
  }

  async function loadPacksDetails() {
    const s = S();
    const infos = { ...(s.packInfos || {}) };
    const icons = { ...(s.packIcons || {}) };
    const progs = { ...(s.packProgress || {}) };
    if (s.packId && s.overall?.totalLines) progs[s.packId] = s.overall;
    await Promise.all(s.packs.map(async (p) => {
      if (featureOn(S(), 'packInfo') && typeof api.packInfo === 'function') {
        try {
          const info = await api.packInfo(p.id);
          infos[p.id] = info;
          if (info.hasIcon && !icons[p.id] && typeof api.packIcon === 'function') {
            try {
              const url = await blobToDataUrl(await api.packIcon(p.id));
              if (url) icons[p.id] = url;
            } catch { /* ignore icon load errors */ }
          }
        } catch { /* ignore packInfo errors on unsupported packs */ }
      }
      if (!progs[p.id] && p.loaded && !p.restartRequired && p.languages?.length) {
        try {
          const g = await api.groups(p.id, p.languages[0]);
          progs[p.id] = g.overall;
        } catch { /* ignore group progress errors */ }
      }
      if (p.loaded && !p.restartRequired && featureOn(S(), 'voiceDefault')) {
        await loadVoiceDefault(p.id);
      }
    }));
    store.set({ packInfos: infos, packIcons: icons, packProgress: progs });
    await scanUnconfirmedTakes(S().packId, S().packLang);
  }

  // ---------------- pack voice default (Phase B1 / contract v2.4) ----------------
  async function loadVoiceDefault(packId = S().packId) {
    if (!packId || !featureOn(S(), 'voiceDefault') || typeof api.voiceDefault !== 'function') return null;
    const p = S().packs.find((x) => x.id === packId);
    if (p?.restartRequired) {
      const empty = { revision: 'pending', origin: null, consent: null, updatedAt: null };
      if (S().packId === packId) store.set({ voiceDefault: empty, packVoiceDraft: 'own' });
      return empty;
    }
    try {
      const vd = await api.voiceDefault(packId);
      const s = S();
      const packVoices = { ...(s.packVoices || {}), [packId]: vd };
      if (s.packId === packId) {
        store.set({
          voiceDefault: vd,
          packVoices,
          packVoiceDraft: vd?.origin || s.packVoiceDraft || 'own'
        });
      } else {
        store.set({ packVoices });
      }
      return vd;
    } catch {
      return null;
    }
  }

  function setPackVoiceDraft(origin) {
    if (!['own', 'ai-assisted', 'ai-generated'].includes(origin)) return;
    store.set({ packVoiceDraft: origin });
  }

  async function savePackVoice(origin = S().packVoiceDraft || S().voiceDefault?.origin || 'own', confirmed = true, { packId = S().packId, silent = false } = {}) {
    if (!packId || !canVoiceDefault(S()) || typeof api.saveVoiceDefault !== 'function') return null;
    let cur = S().packVoices?.[packId] || (S().packId === packId ? S().voiceDefault : null);
    if (!cur || !cur.revision || cur.revision === 'pending') cur = await loadVoiceDefault(packId);
    if (!cur || !cur.revision || cur.revision === 'pending') return null;
    const ver = S().consentVersion || 'consent-1';
    const body = origin === null || confirmed === false
      ? { expectedRevision: cur.revision, operationId: newOp(), origin: null, consent: null }
      : { expectedRevision: cur.revision, operationId: newOp(), origin, consent: { confirmed: true, statementVersion: ver } };
    busy.set('__pack__', (busy.get('__pack__') || 0) + 1);
    try {
      let vd;
      try {
        vd = await api.saveVoiceDefault(packId, body);
      } catch (e) {
        if (e instanceof StudioError && e.code === 'conflict') {
          const fresh = await loadVoiceDefault(packId);
          if (!fresh?.revision) throw e;
          vd = await api.saveVoiceDefault(packId, { ...body, expectedRevision: fresh.revision, operationId: newOp() });
        } else throw e;
      }
      const s = S();
      const packVoices = { ...(s.packVoices || {}), [packId]: vd };
      const patch = { packVoices };
      if (s.packId === packId) {
        patch.voiceDefault = vd;
        patch.packVoiceDraft = vd.origin || 'own';
        if (vd.origin && vd.consent?.confirmed) {
          patch.sessionConsent = true;
          patch.sessionOrigin = vd.origin;
        }
      }
      store.set(patch);
      if (S().packId === packId) {
        if (S().lineId) await refetchLine(S().lineId);
        await scanUnconfirmedTakes(packId, S().packLang, { force: true });
      }
      if (!silent) toast(vd.origin ? 't_voice_saved' : 't_voice_cleared');
      return vd;
    } catch (e) {
      handleError(e);
      if (!silent) toast(errorKey(e.code), 'warn');
      return null;
    } finally {
      busy.set('__pack__', Math.max(0, (busy.get('__pack__') || 1) - 1));
      recentOwn.set('__pack__', now());
    }
  }

  const clearPackVoice = (packId = S().packId, opts = {}) => savePackVoice(null, false, { ...opts, packId });

  function overrideTake(takeId = S().selTakeId) {
    const tId = takeId || currentTake(S())?.id;
    if (!tId) return;
    store.set({ takeOverrides: { ...(S().takeOverrides || {}), [tId]: true } });
  }

  async function followPackDefaultTake(takeId = S().selTakeId) {
    const s = S();
    const t = (s.line?.takes || []).find((x) => x.id === takeId) || currentTake(s);
    if (!t) return null;
    const draft = s.takeOriginDraft;
    const ok = await dub.updateTake(t, { followPackDefault: true });
    if (ok) {
      const overrides = { ...(S().takeOverrides || {}) };
      delete overrides[t.id];
      store.set({ takeOverrides: overrides, ...(S().takeOriginDraft === draft ? { takeOriginDraft: null } : {}) });
    }
    return ok;
  }

  async function scanUnconfirmedTakes(packId = S().packId, lang = S().packLang, { force = false } = {}) {
    const s = S();
    if (!packId || packId !== s.packId || !featureOn(s, 'dubs')) return recomputeUnconfirmed();
    const p = s.packs.find((x) => x.id === packId);
    if (!p || p.restartRequired) return 0;
    const linesWithTakes = s.lines.filter((l) => (l.takeCount || 0) > 0);
    await Promise.all(linesWithTakes.map(async (l) => {
      const k = keyOf(packId, lang, l.id);
      if (!force && knownTakes.has(k)) return;
      try {
        const t = await api.takes(packId, lang, l.id);
        if (t) {
          knownTakes.set(k, t.items || []);
          if (!revs.has(k) && t.revision) revs.set(k, t.revision);
        }
      } catch { /* ignore per-line read errors */ }
    }));
    return recomputeUnconfirmed();
  }

  async function applyPackVoiceToUnconfirmed(packId = S().packId) {
    const s = S();
    const vd = packId === s.packId ? s.voiceDefault : s.packVoices?.[packId];
    if (!vd?.origin || !vd?.consent?.confirmed) {
      toast('need_consent', 'warn');
      return 0;
    }
    const lang = s.packLang;
    const ver = s.consentVersion || 'consent-1';
    await scanUnconfirmedTakes(packId, lang, { force: true });
    const prefix = `${packId}/${lang}/`;
    let patched = 0;
    let openLineTouched = false;
    const nextOverrides = { ...(S().takeOverrides || {}) };
    for (const [k, items] of [...knownTakes.entries()]) {
      if (!k.startsWith(prefix)) continue;
      const { id } = splitKey(k);
      const unconf = (items || []).filter((t) => !(t.consent?.confirmed === true && t.consent?.statementVersion === ver));
      if (!unconf.length) continue;
      await queue(k, async () => {
        const curItems = [...(knownTakes.get(k) || items)];
        for (const t of unconf) {
          try {
            const res = await api.updateTake(packId, lang, id, t.id, {
              expectedRevision: revs.get(k),
              operationId: newOp(),
              followPackDefault: true
            });
            revs.set(k, res.revision);
            const idxT = curItems.findIndex((x) => x.id === t.id);
            if (idxT >= 0) curItems[idxT] = takeVM(res.take);
            delete nextOverrides[t.id];
            patched++;
            if (S().lineId === id) openLineTouched = true;
          } catch (e) {
            handleError(e);
          }
        }
        knownTakes.set(k, curItems);
      });
    }
    store.set({ takeOverrides: nextOverrides });
    recomputeUnconfirmed();
    if (openLineTouched && S().lineId) await refetchLine(S().lineId);
    if (patched > 0) toast('t_voice_applied', 'ok', { n: patched });
    return patched;
  }

  async function uploadPackIcon(packId, pngBytes, { silent = false } = {}) {
    try {
      const u8 = pngBytes instanceof Uint8Array ? pngBytes : new Uint8Array(await pngBytes.arrayBuffer());
      const res = await api.savePackIcon(packId, new Blob([u8], { type: 'image/png' }));
      const url = bytesToDataUrl(u8);
      const s = S();
      store.set({
        packIcons: { ...(s.packIcons || {}), [packId]: url },
        packInfos: { ...(s.packInfos || {}), [packId]: { ...(s.packInfos?.[packId] || { id: packId }), hasIcon: true, sizeBytes: res.sizeBytes } }
      });
      if (!silent) toast('t_icon_saved');
      return true;
    } catch (e) {
      handleError(e);
      if (!silent) warnStorageOrError(e);
      return false;
    }
  }

  async function runExportCheck(packId = S().packId) {
    if (!packId || packId !== S().packId || !canExport(S())) return null;
    const version = ++exportCheckVersion;
    store.set({ exportLoading: true });
    try {
      const preview = await api.exportCheck(packId);
      if (version !== exportCheckVersion || S().packId !== packId) return null;
      store.set({ exportPreview: preview, exportLoading: false, exportBump: S().exportBump || 'patch' });
      return preview;
    } catch (e) {
      if (version === exportCheckVersion && S().packId === packId) store.set({ exportLoading: false });
      handleError(e);
      warnStorageOrError(e);
      return null;
    }
  }

  async function downloadExportZip(packId = S().packId, fileName = S().exportResult?.fileName || S().exportPreview?.lastExport?.fileName) {
    if (!packId || !fileName || !fileName.startsWith(`${packId}-`)) return null;
    try {
      const blob = await api.downloadExport(packId, fileName);
      const buf = new Uint8Array(await blob.arrayBuffer());
      store.set({ lastDownloadedZip: { packId, fileName, sizeBytes: buf.length, magic: String.fromCharCode(buf[0] || 0, buf[1] || 0) } });
      if (typeof document !== 'undefined' && typeof URL !== 'undefined' && URL.createObjectURL) {
        const u = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = u; a.download = fileName; a.style.display = 'none';
        document.body.appendChild(a); a.click(); a.remove();
        timers.setTimeout(() => URL.revokeObjectURL(u), 2000);
      }
      toast('t_downloaded', 'ok', { f: fileName });
      return buf;
    } catch (e) {
      handleError(e);
      if (e.code === 'too_large') {
        const s = S(), dl = s.exportResult?.fileName === fileName ? s.exportResult : s.exportPreview?.lastExport;
        toast('e_zip_too_big', 'warn', { folder: exportFolder(dl, packId) });
      } else toast(errorKey(e.code), 'warn');
      return null;
    }
  }

  async function runExport({ bump = S().exportBump || 'patch', notes = S().exportNotes ?? null, autoDownload = true } = {}) {
    const s = S(), packId = s.packId;
    if (!packId || !canExport(s)) return null;
    let preview = s.exportPreview;
    if (!preview || preview.packId !== packId) preview = await runExportCheck(packId);
    if (!preview || S().packId !== packId) return null;
    if (!preview.ok || preview.errorCount > 0) { toast('e_export_blocked', 'warn'); return null; }
    store.set({ exportBusy: true });
    try {
      const cleanNotes = typeof notes === 'string' && notes.trim() ? notes.trim() : null;
      const res = await api.exportPack(packId, { operationId: newOp(), expectedVersion: preview.version, bump, notes: cleanNotes });
      const packs = await api.packs();
      const freshPreview = await api.exportCheck(packId).catch(() => preview);
      store.set({ packs, ...(S().packId === packId ? { exportResult: res, exportPreview: freshPreview, exportBusy: false } : {}) });
      toast('t_exported', 'ok', { f: res.fileName });
      if (!res.downloadable) toast('e_zip_too_big', 'warn', { folder: exportFolder(res, packId) });
      else if (autoDownload) await downloadExportZip(packId, res.fileName);
      return res;
    } catch (e) {
      if (S().packId === packId) store.set({ exportBusy: false });
      handleError(e);
      warnStorageOrError(e);
      if (e.code === 'conflict') await runExportCheck(packId);
      return null;
    }
  }

  // D-148: one click labels every unlabelled dub with the pack's voice default. The click is the creator's confirmation; without a
  // default nothing is labelled and the creator is taken to Settings to set one first.
  async function labelUnlabelledDubs(packId = S().packId) {
    if (!packId || packId !== S().packId || !canLabelDubs(S()) || typeof api.labelDubs !== 'function') return null;
    let vd = S().voiceDefault || S().packVoices?.[packId];
    if (!vd?.revision || vd.revision === 'pending') vd = await loadVoiceDefault(packId);
    if (!vd?.origin || !vd?.consent?.confirmed) {
      await openModal('settings');
      toast('t_label_need_default', 'warn');
      return null;
    }
    const body = (rev) => ({ expectedRevision: rev, operationId: newOp(), origin: vd.origin, consent: { confirmed: true, statementVersion: S().consentVersion || 'consent-1' } });
    store.set({ labelBusy: true });
    try {
      const res = await api.labelDubs(packId, body(vd.revision));
      toast(res.labelled > 0 ? 't_labelled' : 't_label_none', 'ok', { n: res.labelled });
      return res;
    } catch (e) {
      handleError(e);
      if (e instanceof StudioError && (e.code === 'conflict' || e.code === 'consent_required')) await loadVoiceDefault(packId);
      if (e instanceof StudioError && e.code === 'consent_required') { await openModal('settings'); toast('t_label_need_default', 'warn'); }
      else if (e instanceof StudioError && e.code === 'conflict') toast('t_label_default_changed', 'warn');
      else warnStorageOrError(e);
      return null;
    } finally {
      store.set({ labelBusy: false });
      if (S().packId === packId && S().page === 'export') await runExportCheck(packId);
    }
  }

  function openModalSync(tab = 'packs') {
    const curAuthor = S().packInfos?.[S().packId]?.author || S().packs.map((p) => S().packInfos?.[p.id]?.author).find((a) => a && a.trim()) || '';
    const initNp = tab === 'newpack' && !S().newPackForm
      ? { name: '', id: '', idCustom: false, nameCustom: false, author: curAuthor, lang: 'th', langPreset: 'th', langName: '', description: '', iconBytes: null, iconPreviewUrl: null, iconName: '' }
      : S().newPackForm;
    const page = tab === 'newpack' || tab === 'packs' ? 'packs' : tab === 'export' ? 'export' : tab === 'settings' ? 'settings' : (S().page || 'studio');
    const packsSub = tab === 'newpack' ? 'newpack' : 'list';
    store.set({ page, packsSub, modal: tab, menu: null, newPackForm: initNp });
  }

  async function loadModalData(tab = 'packs') {
    if (tab === 'packs' || tab === 'newpack') await loadPacksDetails();
    else if (tab === 'export') {
      await runExportCheck(S().packId);
      if (canLabelDubs(S())) await loadVoiceDefault(S().packId); // D-148: the label button names the default
    }
    else if (tab === 'settings') {
      await loadVoiceDefault(S().packId);
      await scanUnconfirmedTakes(S().packId, S().packLang);
    }
  }

  async function openModal(tab = 'packs', targetPackId = null) {
    if (targetPackId && targetPackId !== S().packId) {
      const p = S().packs.find((x) => x.id === targetPackId);
      if (p && p.selectable) { flushCurrent(); await loadPack(p.id, p.languages[0] || 'th'); }
    }
    openModalSync(tab);
    await loadModalData(tab);
  }

  function closeModal() {
    store.set({ modal: null, packsSub: 'list', page: 'studio', menu: S().menu === 'newpack' ? null : S().menu });
  }

  // ---------------- events ----------------
  function onEvent(ev) {
    const s = S(), d = ev.data || {};
    const mine = (e) => e.packId === s.packId && e.lang === s.packLang;
    switch (ev.type) {
      case 'maker.workflow.changed': {
        if (!mine(d) || !s.byId.has(d.id)) break;
        if (s.byId.get(d.id).revision === d.revision || own(keyOf(d.packId, d.lang, d.id))) break; // our own echo
        patchLine(d.id, { status: d.status, starred: d.starred, revision: d.revision });
        const key = keyOf(d.packId, d.lang, d.id);
        revs.set(key, d.revision);
        dispatchDraft(key, { type: 'revision', revision: d.revision });
        refreshGroupsSoon();
        break;
      }
      case 'maker.history.changed': {
        if (!mine(d) || !s.byId.has(d.id)) break;
        if (s.byId.get(d.id).revision !== d.revision && !own(keyOf(d.packId, d.lang, d.id))) refetchLineSoon(d.id);
        if (s.menu === 'hist' && s.lineId === d.id) openHistory(true);
        break;
      }
      case 'maker.subtitle.saved': {
        if (!mine(d) || !s.byId.has(d.id)) break;
        if (!own(keyOf(d.packId, d.lang, d.id))) refetchLineSoon(d.id); // a v1 client or another tab wrote it
        break;
      }
      case 'maker.take.changed':
        if (mine(d) && s.byId.has(d.id) && !own(keyOf(d.packId, d.lang, d.id))) refetchLineSoon(d.id);
        break;
      case 'maker.take.published': case 'maker.voice.saved':
        if (!mine(d)) break;
        if (s.byId.has(d.id) && !own(keyOf(d.packId, d.lang, d.id))) refetchLineSoon(d.id);
        refreshGroupsSoon();
        break;
      case 'maker.catalog.changed': case 'maker.pack.created': case 'maker.pack.changed':
        if (!own('__pack__')) reload({ keepLine: true });
        break;
      case 'game.focus': store.set({ status: { ...(s.status || {}), focused: d.focused ?? null, paused: d.paused ?? null, busy: d.busy ?? null, reason: d.reason ?? null, observedAt: d.observedAt ?? null, stale: false } }); break;
      case 'room.ready': store.set({ status: { ...(s.status || {}), roomReady: d.ready !== false } }); break;
      case 'game.language': store.set({ status: { ...(s.status || {}), gameLanguage: d.code } }); break;
      case 'maker.play': lastPlay = { id: d.id, variant: d.variant, at: now() }; break;
      case 'line.spoken': {
        const id = d.lineId || d.localizationId; // lineId: the line a Pack Maker Play spoke (the game reports a runtime cwy_say_* row)
        const variant = lastPlay && lastPlay.id === id && now() - lastPlay.at < 8000 && lastPlay.variant === 'dub' ? 'dub' : 'orig';
        store.set({ last: { id, variant, at: now() } });
        break;
      }
      default: break; // unknown event types are ignored on purpose (W2: events are hints)
    }
  }

  async function keepAndNext() {
    const ok = await dub.keepTake();
    if (ok === true) {
      refreshGroupsSoon();
      const nextId = await nextUnrecorded({ silent: true });
      if (!nextId) {
        const vis = visibleLines(S()), i = vis.findIndex((l) => l.id === S().lineId);
        if (i >= 0 && vis[i + 1]) await selectLine(vis[i + 1].id, { scroll: true });
      }
      toast('t_kept_next');
      return true;
    }
    return ok;
  }

  const dub = createDub({ micIdleMs, reasonText, store, api, keyOf, queue, revs, refetchLine, patchLine, settle, newOp, toast, timers, now, handleError, mic: mic || noMic, player: player || noPlayer, decodeFile: decodeFile || (async () => { throw new Error('no decoder'); }), onConsentForKeepNext: () => keepAndNext(), savePackVoice, onTakesChanged: (k, items) => { knownTakes.set(k, items || []); recomputeUnconfirmed(); }, dispatchDraft, refreshGroupsSoon });

  return {
    ...dub,
    start, reload, loadPack, retryLines, selectLine, moveLine, completeLine, setSetting, openSpoken, nextUnrecorded, keepAndNext, selectTake, setVoiceFilter, setCharFilter, setSearch, refreshStatus, editText, flush, flushCurrent, flushAll, settle, resolveConflict,
    setStatus, toggleStar, toggleRetake, setWorkflow, openHistory, revert, slugId: slug, authorSlug, suggestPackName, suggestPackId, createPack, openModal, openModalSync, loadModalData, closeModal, loadPacksDetails, uploadPackIcon, runExportCheck, runExport, downloadExportZip, labelUnlabelledDubs,
    loadVoiceDefault, setPackVoiceDraft, savePackVoice, clearPackVoice, overrideTake, followPackDefaultTake, scanUnconfirmedTakes, applyPackVoiceToUnconfirmed, onEvent, refetchLine,
    // test access
    _drafts: drafts, _revs: revs
  };
}
