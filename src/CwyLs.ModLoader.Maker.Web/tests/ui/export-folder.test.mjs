// TD-5 (T-061, T-063): the Export page names the folder the zip is saved in, and a zip over the browser limit is explained
// (with that folder) instead of being skipped silently or refused with the generic "file too big" message.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { makeT } from '../../ui/i18n/i18n.js';
import { exportResultVM, exportPreviewVM } from '../../ui/api/model.js';
import { renderExportPage } from '../../ui/zones/packs-modal.js';

const FOLDER = '/games/CWYLS Mods/.maker-exports/p';
const pack = { id: 'p', name: 'Pack', version: '0.1.1', editable: true, restartRequired: false, languages: ['th'] };
const page = (res, lang = 'en') => renderExportPage({ packId: 'p', packs: [pack], exportPreview: null, exportResult: res }, { T: makeT(lang), lang });
const result = (o) => exportResultVM({ packId: 'p', version: '0.1.1', fileName: 'p-v0.1.1.zip', sizeBytes: 1024, sha256: 'ab', files: 3, downloadable: true, folder: FOLDER, ...o });

test('the result and the last export keep the folder from the loader', () => {
  assert.equal(result({}).folder, FOLDER);
  assert.equal(exportResultVM({ packId: 'p' }).folder, null);
  assert.equal(exportPreviewVM({ packId: 'p', lastExport: { fileName: 'p-v0.1.0.zip', version: '0.1.0', sizeBytes: 9, folder: FOLDER } }).lastExport.folder, FOLDER);
});

test('the Export page result names the folder; a zip too big for the browser says so with the folder and offers no download', () => {
  const ok = page(result({}));
  assert.ok(ok.includes(FOLDER) && ok.includes('data-act="export-download"'));
  const big = page(result({ downloadable: false, sizeBytes: 300 * 1024 * 1024 }));
  assert.ok(big.includes(FOLDER), 'the folder is named');
  assert.match(big, /too big to download in the browser/);
  assert.ok(!big.includes('data-act="export-download"'), 'no download button that can only fail');
  assert.ok(page(result({ downloadable: false }), 'th').includes(FOLDER));
});

async function setup(api) {
  const dev = createDevBackend({ lines: 20 });
  const store = createStore({
    ready: false, conn: { connected: false, unauthorized: false }, status: null, packs: [], packId: null, packLang: 'th', groups: [], overall: {}, lines: [],
    byId: new Map(), loadingLines: true, view: { group: null, filter: 'all', q: '' }, lineId: null, line: null, draft: null, history: null, last: null, peaks: {},
    exportPreview: null, exportBusy: false, exportResult: null
  });
  const toasts = [];
  const real = createApi(dev);
  const actions = createActions({ store, api: { ...real, ...api(real) }, mode: 'dev', toast: (k, kind, v) => toasts.push([k, kind, v]), timers: { setTimeout: () => 0, clearTimeout: () => {} },
    reconnectMs: 100, selectDelayMs: 0, newOp: (() => { let n = 0; return () => (++n).toString(16).padStart(32, '0'); })(), now: () => 0 });
  await actions.start({});
  for (let i = 0; i < 14; i++) await new Promise((r) => setImmediate(r));
  return { store, actions, toasts };
}

test('an export too big for the browser is not downloaded, and the toast says why and where the zip is', async () => {
  let downloads = 0;
  const c = await setup((real) => ({
    exportPack: async (...a) => ({ ...(await real.exportPack(...a)), downloadable: false, folder: FOLDER }),
    downloadExport: async () => { downloads++; throw Object.assign(new Error('too large'), { code: 'too_large' }); }
  }));
  const res = await c.actions.runExport({ bump: 'patch', notes: null });
  assert.ok(res, 'the export itself worked');
  assert.equal(downloads, 0);
  assert.deepEqual(c.toasts.find(([k]) => k === 'e_zip_too_big'), ['e_zip_too_big', 'warn', { folder: FOLDER }]);
});

test('a 413 on a download names the folder instead of the generic "file too big or too long" message', async () => {
  const c = await setup(() => ({ downloadExport: async () => { throw Object.assign(new Error('too large'), { code: 'too_large' }); } }));
  c.store.set({ exportResult: result({}) });
  await c.actions.downloadExportZip('p', 'p-v0.1.1.zip');
  assert.ok(!c.toasts.some(([k]) => k === 'e_too_large'));
  assert.deepEqual(c.toasts.at(-1), ['e_zip_too_big', 'warn', { folder: FOLDER }]);
});
