import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
let server, chrome, page;
before(async () => {
  server = await serveWeb(root);
  chrome = await launchChrome({ extraArgs: ['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1'] });
  page = await openPage(chrome.port);
});
after(async () => { page?.close(); await chrome?.close(); await server?.close(); });
const load = (lang = 'en') => loadStudio(page, `${server.url}index.html?ui=2&backend=dev&fixture=normal&lang=${lang}`);
const q = (expr) => page.eval(expr);
const wait = async (expr) => {
  for (let i = 0; i < 100; i++) {
    if (await q(expr)) return;
    await new Promise((r) => setTimeout(r, 50));
  }
  throw new Error(`Timed out: ${expr}`);
};

test('Voice lines keeps every matching row reachable with a bounded DOM window', async () => {
  await load();
  await q(`(() => { const s = __studio.store.get(), base = s.lines[0];
    const lines = Array.from({length: 2168}, (_, i) => ({...base, id: 'synthetic-' + i, status: ['todo','draft','needs-retake','done'][i % 4]}));
    __studio.store.set({lines, page: 'lines', view: {filter:'all', q:'', group:null}}); })()`);
  await wait("document.querySelectorAll('.vb-row').length > 0");
  for (const mode of ['all', 'status']) {
    await q(`document.querySelector('[data-act="vb-mode"][data-id="${mode}"]').click()`);
    await wait(`__studio.store.get().view.groupByStatus === ${mode === 'status'}`);
    await new Promise((r) => setTimeout(r, 100));
    assert.ok(await q("document.querySelectorAll('.vb-row').length") < 100, mode);
    assert.match(await q("document.querySelector('#l-count').textContent"), /2,?168/);
    // Re-scroll inside the wait: a page rebuild may reset the offset between the scroll and the repaint.
    await wait("(() => { const n = document.querySelector('#vb-list'); if (!n) return false; n.scrollTop = n.scrollHeight; n.dispatchEvent(new Event('scroll')); return !!document.querySelector('[data-id=\"synthetic-2167\"]'); })()");
    await q("(() => { const n = document.querySelector('#vb-list'); n.focus(); n.dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true,cancelable:true})); })()");
    await wait("document.activeElement?.dataset?.id === 'synthetic-2167'");
    assert.equal(await q("document.activeElement.getAttribute('aria-setsize')"), '2168');
  }
  await q(`(() => { const n = document.querySelector('[data-in="q"]'); n.value = 'synthetic-2167'; n.dispatchEvent(new Event('input',{bubbles:true})); })()`);
  await wait("document.querySelectorAll('.vb-row').length === 1");
  assert.equal(await q("document.querySelector('.vb-row').dataset.id"), 'synthetic-2167');
  await q(`(() => { const n = document.querySelector('[data-in="q"]'); n.value = ''; n.dispatchEvent(new Event('input',{bubbles:true})); })()`);
  await wait("document.querySelector('#l-count').textContent.includes('2,168') || document.querySelector('#l-count').textContent.includes('2168')");
  await q("document.querySelector('[data-act=\"filter\"][data-id=\"todo\"]').click()");
  await wait("document.querySelector('#l-count').textContent.includes('542')");
  assert.ok(await q("document.querySelectorAll('.vb-row').length") < 100);
  assert.match(await q("document.querySelector('#l-count').textContent"), /542/);
  await q("(() => { const n = document.querySelector('#vb-list'); n.focus(); n.dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true,cancelable:true})); })()");
  await wait("document.activeElement?.dataset?.id === 'synthetic-2164'");
  const opened = await q(`(() => { let id; __studio.actions.selectLine = (value) => { id = value; };
    document.querySelector('[data-act="open-line-studio"][data-id="synthetic-2164"]').click(); return id; })()`);
  assert.equal(opened, 'synthetic-2164');
});

test('Studio keyboard shortcuts stay on Studio while Escape works on other pages', async () => {
  await load();
  const result = await q(`(() => {
    const {store, actions} = __studio, calls = [];
    for (const name of ['toggleRecord','playTake','playMine','playOriginal','completeLine','selectLine','closeModal']) actions[name] = () => calls.push(name);
    store.set({page:'lines', modal:'settings'});
    for (const key of ['r','R',' ','t','T','Enter','ArrowDown','ArrowUp']) document.body.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true}));
    document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',ctrlKey:true,bubbles:true,cancelable:true}));
    document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));
    const other = calls.slice(); calls.length = 0; store.set({page:'studio', modal:null});
    document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'r',bubbles:true,cancelable:true}));
    return {other, studio:calls}; })()`);
  assert.deepEqual(result.other, ['closeModal']);
  assert.deepEqual(result.studio, ['toggleRecord']);
});

test('pending pack explains restart in banner, subtitle placeholder and deck in both languages', async () => {
  for (const [lang, message] of [['en', 'Restart the game once, then you can type and record here'], ['th', 'รีสตาร์ตเกมสักครั้ง แล้วพิมพ์กับอัดเสียงตรงนี้ได้เลย']]) {
    await load(lang);
    await q(`(() => { const s = __studio.store.get(); __studio.store.set({packs:s.packs.map(p => p.id === s.packId ? {...p, editable:false, restartRequired:true} : p), mic:'off'}); })()`);
    await wait("document.querySelector('#banner.b-first') && document.querySelector('#ed').readOnly");
    const parts = await q(`({banner:document.querySelector('#banner').textContent, placeholder:document.querySelector('#ed').placeholder, deck:document.querySelector('#deck').textContent, readOnly:document.querySelector('#ed').readOnly, recordDisabled:document.querySelector('[data-act="setup-mic"]').disabled})`);
    for (const part of ['banner', 'placeholder', 'deck']) assert.ok(parts[part].includes(message), `${lang} ${part}: ${parts[part]}`);
    assert.equal(parts.readOnly, true); assert.equal(parts.recordDisabled, true);
  }
});

test('empty custom language shows the validation toast and does not create a Thai pack', async () => {
  await load();
  await q("__studio.actions.openModal('newpack')");
  await wait("!!document.querySelector('#np-lang')");
  await q(`(() => { const sel = document.querySelector('#np-lang'); sel.value = 'custom'; sel.dispatchEvent(new Event('change',{bubbles:true})); document.querySelector('#np-name').value = 'Custom test'; document.querySelector('[data-act="np-create"]').click(); })()`);
  await wait("!!document.querySelector('#studio-toasts .toast')");
  assert.match(await q("document.querySelector('#studio-toasts').textContent"), /Language code must be/);
  assert.equal(await q("__studio.store.get().packs.some(p => p.name === 'Custom test')"), false);
  assert.equal(await q("document.querySelector('#np-lang-code').value"), '');
});
