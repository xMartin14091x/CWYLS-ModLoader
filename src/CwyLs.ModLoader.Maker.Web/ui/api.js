// THE PORT. The UI never calls fetch, loader/http.js or storage: it calls this module, which wraps one *adapter*.
//   - real:  W2's `modules/loader/index.js` (default export), unchanged: its `studio*` methods ARE the adapter (W3 writes no HTTP code)
//   - dev:   tests/dev/dev-backend.js, in memory, only with `?backend=dev` or when the page has no maker token
// The adapter has exactly W2's JS names and call shapes (research/2026-11-08/w2-backend/STUDIO-JS.md): studioCapabilities, studioStatus,
// studioPacks, studioLines({packId,lang,...}), studioLine, studioGroups, studioUpdateWorkflow, studioSaveSubtitle, studioHistory,
// studioRevertSubtitle, studioTakes, studioCreateTake, studioPutTakeAudio(…, options, wav), studioGetTakeAudio, studioUpdateTake,
// studioDeleteTake, studioSetFavourite, studioPublish, studioUnpublish, studioGlossary, studioSaveGlossary, studioCreatePack,
// studioOperation, studioPlay. Every durable mutation object carries `expectedRevision` and a client-made `operationId`.
// Events: `adapter.bus` (EventTarget) dispatches CustomEvent('maker-event', {detail}), Event('resync') and Event('connection');
// `adapter.connection` is 'connected' | 'offline' | 'reloading'; `adapter.watch()` starts the long-poll loop; `adapter.close()` stops it.
// Failures are `LoaderError`/`StudioError` with `.code`. This module maps the read models to view-models (api/model.js).
// HTTP never falls back to the dev backend.
import { StudioError, toStudioError, takeMutationBody, statusVM, packVM, groupVM, pendingGroupVM, progressVM, pendingProgressVM, lineVM, pendingLineVM, historyVM, takesVM, packInfoVM, exportPreviewVM, exportResultVM, voiceDefaultVM, omitUndefined } from './api/model.js';
import { attachDevPhaseC } from './api/dev-phase-c.js';

export { StudioError };

/** 32 lowercase hex characters: the client-generated retry key W2 asks for. */
export function newOperationId() {
  const bytes = new Uint8Array(16);
  (globalThis.crypto || { getRandomValues: (a) => a.map(() => Math.floor(Math.random() * 256)) }).getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

const MUTATIONS = {
  updateWorkflow: 'studioUpdateWorkflow', saveSubtitle: 'studioSaveSubtitle', revertSubtitle: 'studioRevertSubtitle',
  putTakeAudio: 'studioPutTakeAudio', deleteTake: 'studioDeleteTake',
  setFavourite: 'studioSetFavourite', publish: 'studioPublish', unpublish: 'studioUnpublish',
  operation: 'studioOperation', play: 'studioPlay', takeAudio: 'studioGetTakeAudio'
};

/** @param {object} adapter W2's loader or the dev backend */
export function createApi(adapter) {
  if (adapter?.dev && !adapter.studioPackInfo) attachDevPhaseC(adapter);
  let consentVersion = 'consent-1', basePack = null;
  const pending = new Set(), isHttp = adapter.mode === 'http';
  const call = async (fn) => { try { return await fn(); } catch (e) { throw toStudioError(e, adapter.connection); } };
  const isPending = (packId) => isHttp && pending.has(packId) && !!basePack;
  const baseLang = (lang) => (basePack?.languages?.includes(lang) ? lang : (basePack?.languages?.[0] || lang));
  const api = {
    adapter, raw: adapter, bus: adapter.bus,
    get connection() { return adapter.connection; },
    capabilities: () => call(async () => {
      const c = await adapter.studioCapabilities();
      if (c?.consentVersion) consentVersion = c.consentVersion;
      if (adapter?.dev && Array.isArray(c?.features) && c.features.includes('newPacks')) {
        const feats = [...c.features];
        for (const f of ['packInfo', 'export', 'voiceDefault', 'voiceLabel']) if (!feats.includes(f)) feats.push(f);
        return { ...c, features: feats };
      }
      return c;
    }),
    status: () => call(async () => statusVM(await adapter.studioStatus())),
    packs: () => call(async () => {
      const list = (await adapter.studioPacks()).packs || [];
      if (isHttp) {
        pending.clear();
        for (const p of list) {
          if (p.restartRequired && p.loaded === false) pending.add(p.id);
          else if (!basePack && p.loaded !== false && !p.restartRequired) basePack = { id: p.id, languages: p.languages || [] };
        }
      }
      return list.map(packVM);
    }),
    groups: (packId, lang) => call(async () => {
      if (isPending(packId)) {
        const g = await adapter.studioGroups(basePack.id, baseLang(lang));
        return { catalogRevision: g.catalogRevision, overall: pendingProgressVM(g.overall), groups: g.groups.map(pendingGroupVM).sort((a, b) => a.order - b.order) };
      }
      const g = await adapter.studioGroups(packId, lang);
      return { catalogRevision: g.catalogRevision, overall: progressVM(g.overall), groups: g.groups.map(groupVM).sort((a, b) => a.order - b.order) };
    }),
    linesPage: (packId, lang, query = {}) => call(async () => {
      if (isPending(packId)) {
        const p = await adapter.studioLines({ packId: basePack.id, lang: baseLang(lang), ...omitUndefined(query) });
        return { total: p.total, offset: p.offset, catalogRevision: p.catalogRevision, items: p.items.map(pendingLineVM) };
      }
      const p = await adapter.studioLines({ packId, lang, ...omitUndefined(query) });
      return { total: p.total, offset: p.offset, catalogRevision: p.catalogRevision, items: p.items.map(lineVM) };
    }),
    line: (packId, lang, id, options) => call(async () => (
      isPending(packId) ? pendingLineVM(await adapter.studioLine(basePack.id, baseLang(lang), id, options)) : lineVM(await adapter.studioLine(packId, lang, id, options))
    )),
    history: (packId, lang, id, page) => call(async () => historyVM(await adapter.studioHistory(packId, lang, id, page))),
    takes: (packId, lang, id) => call(async () => (isPending(packId) ? takesVM({ revision: 'pending', items: [] }) : takesVM(await adapter.studioTakes(packId, lang, id)))),
    createTake: (packId, lang, id, body) => call(() => adapter.studioCreateTake(packId, lang, id, takeMutationBody(body, consentVersion))),
    updateTake: (packId, lang, id, takeId, body) => call(() => adapter.studioUpdateTake(packId, lang, id, takeId, takeMutationBody(body, consentVersion))),
    createPack: (body) => call(async () => {
      const res = await adapter.studioCreatePack(body);
      if (isHttp && res?.pack?.id && res.pack.restartRequired && res.pack.loaded === false) pending.add(res.pack.id);
      return res;
    }),
    packInfo: (packId) => call(async () => packInfoVM(await adapter.studioPackInfo(packId))),
    packIcon: (packId) => call(() => adapter.studioPackIcon(packId)),
    savePackIcon: (packId, png) => call(() => adapter.studioSavePackIcon(packId, png)),
    exportCheck: (packId) => call(async () => exportPreviewVM(await adapter.studioExportCheck(packId))),
    exportPack: (packId, body) => call(async () => exportResultVM(await adapter.studioExport(packId, body))),
    downloadExport: (packId, fileName) => call(() => adapter.studioDownloadExport(packId, fileName)),
    voiceDefault: (packId) => call(async () => (isPending(packId) ? voiceDefaultVM({ revision: 'pending' }) : voiceDefaultVM(await adapter.studioVoiceDefault(packId)))),
    saveVoiceDefault: (packId, body) => call(async () => voiceDefaultVM(await adapter.studioSaveVoiceDefault(packId, takeMutationBody(body, consentVersion)))),
    // D-148: labels every unlabelled dub with the pack default; {labelled, paths, origin, revision}.
    labelDubs: (packId, body) => call(async () => {
      const r = await adapter.studioLabelDubs(packId, takeMutationBody(body, consentVersion));
      return { labelled: Number(r?.labelled) || 0, paths: Array.isArray(r?.paths) ? r.paths : [], origin: r?.origin || null, revision: r?.revision || null };
    }),
    watch: () => adapter.watch?.(),
    close: () => adapter.close?.()
  };
  for (const [name, method] of Object.entries(MUTATIONS)) api[name] = (...args) => call(() => adapter[method](...args));
  return api;
}

/**
 * Loads the whole catalogue of a pack/language in pages of 500 and returns it only when every page carries the same
 * `catalogRevision` (W2: a mismatch means "restart paging"). Gives up after `attempts` restarts.
 * @returns {Promise<{catalogRevision:string, lines:object[]}>}
 */
export async function loadCatalog(api, packId, lang, { limit = 500, attempts = 3, onPage = null } = {}) {
  for (let attempt = 0; attempt < attempts; attempt++) {
    const lines = [];
    let revision = null, offset = 0, total = Infinity, changed = false;
    while (offset < total) {
      const asked = Date.now();
      const page = await api.linesPage(packId, lang, { offset, limit, catalogRevision: revision ?? undefined });
      try { onPage?.({ offset, items: page.items.length, total: page.total, ms: Date.now() - asked, restart: attempt }); } catch { /* diagnostics only */ }
      if (revision !== null && page.catalogRevision !== revision) { changed = true; break; }
      revision = page.catalogRevision;
      total = page.total;
      lines.push(...page.items);
      if (!page.items.length) break;
      offset += page.items.length;
    }
    if (!changed) return { catalogRevision: revision, lines };
  }
  throw new StudioError('conflict', 'The catalogue kept changing while it was loading.');
}

/**
 * Picks the adapter. `search` is `location.search`; `hasToken` says whether the loader injected the maker token.
 * @returns {Promise<{api:object, mode:'dev'|'http', dev?:object}>}
 */
export async function connect({ search = '', hasToken = false } = {}) {
  const params = new URLSearchParams(search);
  const wantDev = params.get('backend') === 'dev' || (!hasToken && params.get('backend') !== 'http');
  if (wantDev) {
    let mod;
    try { mod = await import('../tests/dev/dev-backend.js'); }
    catch { throw new StudioError('unavailable', 'The dev backend is not part of a release.'); }
    const dev = mod.createDevBackend({ fixture: params.get('fixture') || 'normal', lines: Number(params.get('lines')) || undefined, packLanguages: params.get('packlangs') ? params.get('packlangs').split(',') : undefined, features: params.get('features') ? params.get('features').split(',') : params.get('fixture') === 'nodubs' ? ['catalog', 'workflow', 'subtitles', 'projects'] : undefined });
    return { api: createApi(dev), mode: 'dev', dev };
  }
  // W2's own adapter, unchanged: its `studio*` methods are the port (W3 writes no HTTP code of its own). `modules/loader/index.js`
  // picks http.js for ?backend=http or config.backend === 'http'; it throws for anything else, which is shown as "too old".
  let mod;
  try { mod = await import('../modules/loader/index.js'); }
  catch { throw new StudioError('unavailable', 'The Studio adapter for this loader is not available.'); }
  const adapter = mod.default;
  if (adapter.mode !== 'http') throw new StudioError('unavailable', 'The loader is not in HTTP mode.');
  return { api: createApi(adapter), mode: 'http' };
}
