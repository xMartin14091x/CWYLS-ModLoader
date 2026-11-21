import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi, StudioError } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { makeT } from '../../ui/i18n/i18n.js';

for (const operation of ['createPack', 'exportPack', 'savePackIcon']) {
  test(`storage error on ${operation} retains the backend folder and remedy in both languages`, async () => {
    const store = createStore({conn:{connected:true,unauthorized:false}, packs:[], lines:[], byId:new Map(), view:{}, peaks:{}});
    const api = createApi(createDevBackend({lines:40}));
    const toasts = [];
    const actions = createActions({store, api, mode:'dev', selectDelayMs:0,
      toast:(...args)=>toasts.push(args), timers:{setTimeout:()=>0, clearTimeout:()=>{}}});
    await actions.start({});
    const message = 'Storage at /synthetic/CWYLS Mods/.maker-exports/satone-th-dub: 100 files and 4 GiB per pack; move older exports elsewhere, then retry. <img src=x onerror=alert(1)>';
    let calls = 0;
    api[operation] = async () => { calls++; throw new StudioError('storage_limit', message, {status:409}); };
    if (operation === 'createPack') assert.equal(await actions.createPack({name:'Storage test', lang:'th'}), false);
    if (operation === 'exportPack') {
      const id = store.get().packId;
      store.set({exportPreview:{packId:id,ok:true,errorCount:0,version:'0.1.0'}});
      assert.equal(await actions.runExport({autoDownload:false}), null);
      assert.equal(store.get().exportBusy, false);
    }
    if (operation === 'savePackIcon') assert.equal(await actions.uploadPackIcon(store.get().packId, new Uint8Array([1])), false);
    assert.equal(calls, 1, 'storage failures are not retried');
    const [key, kind, vars] = toasts.at(-1);
    assert.equal(kind, 'warn');
    for (const lang of ['en', 'th']) assert.ok(makeT(lang)(key, vars).includes(message), lang);
  });
}
