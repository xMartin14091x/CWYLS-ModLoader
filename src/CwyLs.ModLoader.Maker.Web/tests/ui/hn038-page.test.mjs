import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';
import { renderPacksPage } from '../../ui/zones/packs-modal.js';
import { makeT } from '../../ui/i18n/i18n.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
let server, chrome, page;
before(async () => {
  server = await serveWeb(root);
  chrome = await launchChrome({ extraArgs: ['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1'] });
  page = await openPage(chrome.port);
});
after(async () => { page?.close(); await chrome?.close(); await server?.close(); });
const q = (expr) => page.eval(expr);
const wait = async (expr) => {
  for (let i = 0; i < 100; i++) {
    if (await q(expr)) return;
    await new Promise((r) => setTimeout(r, 50));
  }
  throw new Error(`Timed out: ${expr}`);
};
// Drain both the immediate keyboard repaint and its later scroll/rAF repaint.
const frames = () => q('new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(r))))');
const load = async () => {
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=en&notour=1`);
  await frames();
};
const click = (selector) => q(`document.querySelector(${JSON.stringify(selector)}).click()`);
const key = async (value, extra = {}) => {
  await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key: value, code: value, ...extra });
  await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key: value, code: value, ...extra });
};
const syntheticLines = async (groupByStatus = false) => {
  await q(`(() => {
    const {store} = __studio, s = store.get(), base = s.lines[0];
    const lines = Array.from({length: 800}, (_, i) => ({...base, id:'hn038-' + i, status:['todo','draft','needs-retake','done'][i % 4]}));
    store.set({lines, byId:new Map(lines.map(l => [l.id,l])), page:'lines', modal:null, menu:null,
      view:{group:null, filter:'all', q:'', groupByStatus:${groupByStatus}}});
  })()`);
  await wait("document.querySelector('#vb-list .vb-row')");
  await frames();
};

for (const name of ['packs', 'export', 'settings']) test(`HN038 Esc: guide, help, then ${name} page close one layer at a time`, async () => {
  await load();
  await q(`__studio.store.set({page:'${name}',modal:'${name}',tour:{page:'${name}',step:0}})`);
  await wait("!!document.querySelector('#tour-card')");
  await key('Escape');
  await wait('!__studio.store.get().tour');
  assert.deepEqual(await q('(({page,modal}) => ({page,modal}))(__studio.store.get())'), {page:name, modal:name});
  await q("__studio.store.set({menu:'keys'})");
  await wait("!!document.querySelector('.keys-menu')");
  await key('Escape');
  await wait('!__studio.store.get().menu');
  assert.equal(await q('__studio.store.get().page'), name);
  await key('Escape');
  assert.equal(await q('__studio.store.get().page'), 'studio');
});

test('HN038 Esc: dismissing a guide leaves recording running until the next Escape', async () => {
  await load();
  await q(`(() => { window.cancelCalls=0; __studio.actions.cancelRecord=()=>{cancelCalls++;__studio.store.set({rec:'idle'});};
    __studio.store.set({rec:'recording',tour:{page:'studio',step:0}}); })()`);
  await wait("!!document.querySelector('#tour-card')");
  await key('Escape');
  await wait('!__studio.store.get().tour');
  assert.equal(await q('cancelCalls'), 0);
  assert.equal(await q('__studio.store.get().rec'), 'recording');
  await key('Escape');
  assert.equal(await q('cancelCalls'), 1);
});

test('HN038 focus: Voice lines keyboard navigation survives delayed scroll repaints in both modes', async () => {
  await load();
  for (const grouped of [false, true]) {
    await syntheticLines(grouped);
    await q("document.querySelector('#vb-list').focus()");
    for (let index = 0; index < 5; index++) {
      await key('ArrowDown');
      await frames();
      assert.equal(await q('Number(document.activeElement.dataset.vbIndex)'), index, `grouped=${grouped}, row=${index}`);
    }
    await key('End'); await frames();
    assert.equal(await q('document.activeElement.dataset.id'), 'hn038-799');
    await key('ArrowUp'); await frames();
    assert.equal(await q('Number(document.activeElement.dataset.vbIndex)'), 798);
    await key('Home'); await frames();
    assert.equal(await q('Number(document.activeElement.dataset.vbIndex)'), 0);
    await key('PageDown'); await frames();
    const paged = await q('Number(document.activeElement.dataset.vbIndex)');
    assert.ok(paged > 1);
    await key('PageUp'); await frames();
    assert.equal(await q('Number(document.activeElement.dataset.vbIndex)'), 0);
  }
});

test('HN038 scroll: Voice lines keeps its offset through a full page rebuild without row focus', async () => {
  await load(); await syntheticLines();
  await q("document.querySelector('#vb-list').scrollTop=30000");
  await frames();
  const before = await q("document.querySelector('#vb-list').scrollTop");
  assert.ok(before > 20000);
  await q("__studio.store.set({packProgress:{...__studio.store.get().packProgress}})");
  await frames();
  assert.equal(await q("document.querySelector('#vb-list').scrollTop"), before);
  assert.ok(await q("document.querySelector('#vb-list .vb-row').dataset.vbIndex > 400"));
});

test('HN038 focus and scroll: status updates keep the same logical row, offset and next key', async () => {
  await load(); await syntheticLines();
  await q("document.querySelector('#vb-list').focus()");
  await key('End'); await key('ArrowUp'); await frames();
  const before = await q("({id:document.activeElement.dataset.id,top:document.querySelector('#vb-list').scrollTop})");
  // A catalogue/workflow event changes lines and triggers the whole page render.
  await q("__studio.store.set({lines:__studio.store.get().lines.map(l=>({...l,starred:!l.starred}))})");
  await frames();
  assert.equal(await q('document.activeElement.dataset.id'), before.id);
  assert.equal(await q("document.querySelector('#vb-list').scrollTop"), before.top);
  await key('ArrowUp'); await frames();
  assert.equal(await q('document.activeElement.dataset.id'), 'hn038-797');
  // Narrowing the data clamps scroll and falls back to the list if its focused row vanished.
  await q("__studio.store.set({lines:__studio.store.get().lines.slice(0,2)})");
  await frames();
  assert.equal(await q("document.querySelector('#vb-list').scrollTop"), 0);
  assert.equal(await q("document.querySelectorAll('#vb-list .vb-row').length"), 2);
  assert.equal(await q('document.activeElement.id'), 'vb-list');
});

test('HN038 Create: double submit sends one request and keeps Create disabled across repaint', async () => {
  await load(); await q("__studio.actions.openModal('newpack')");
  await wait("!!document.querySelector('#np-name')");
  await q(`(() => {
    const raw=__studio.conn.api.raw, original=raw.studioCreatePack;
    window.createCalls=0; window.createGate=new Promise(r=>window.releaseCreate=r);
    raw.studioCreatePack=async (...a)=>{createCalls++;await createGate;return original(...a);};
    document.querySelector('#np-name').value='HN038 double create';
    document.querySelector('#np-id').value='hn038-double';
    const button=document.querySelector('[data-act="np-create"]');button.click();button.click();
  })()`);
  try {
    await frames();
    assert.equal(await q('createCalls'), 1);
    assert.equal(await q("document.querySelector('[data-act=np-create]').disabled"), true);
    await q("__studio.store.set({packProgress:{...__studio.store.get().packProgress}})"); await frames();
    assert.equal(await q("document.querySelector('[data-act=np-create]').disabled"), true);
    assert.equal(await q("document.querySelector('#np-name').value"), 'HN038 double create');
  } finally { await q('releaseCreate()'); }
  await wait("__studio.store.get().page==='studio' && __studio.store.get().packs.some(p=>p.id==='hn038-double')");
  await frames();
  assert.deepEqual(await q("__studio.store.get().packs.filter(p=>p.id.startsWith('hn038-double')).map(p=>p.id)"), ['hn038-double']);
});

test('HN038 Create: a failed request releases the guard, keeps form data and allows retry', async () => {
  await load(); await q("__studio.actions.openModal('newpack')");
  await wait("!!document.querySelector('#np-name')");
  await q(`(() => { document.querySelector('#np-name').value='HN038 retry'; document.querySelector('#np-id').value='hn038-retry';
    __studio.conn.api.raw.devControls.fail('createPack','bad_request',{times:1}); })()`);
  await click('[data-act="np-create"]'); await frames();
  await wait("!!document.querySelector('#studio-toasts .toast.warn')");
  assert.equal(await q('__studio.store.get().creatingPack'), false);
  assert.equal(await q("document.querySelector('[data-act=np-create]').disabled"), false);
  assert.equal(await q("document.querySelector('#np-name').value"), 'HN038 retry');
  await click('[data-act="np-create"]');
  await wait("__studio.store.get().packId==='hn038-retry'");
});

for (const lang of ['en', 'th']) test(`HN038 Packs: ${lang} separates editing from loaded state and explains load order`, () => {
  const T = makeT(lang), packs = [
    {id:'a',name:'A',languages:['th'],loaded:true,editable:true,selectable:true},
    {id:'b',name:'B',languages:['th'],loaded:true,editable:true,selectable:true},
    {id:'pending',name:'Pending',languages:['th'],loaded:false,restartRequired:true}
  ];
  const expectedEditing = lang === 'en' ? 'Open in Pack Maker' : 'เปิดอยู่ใน Pack Maker';
  const expectedLoaded = lang === 'en' ? 'Loaded in game' : 'โหลดอยู่ในเกม';
  for (const packId of ['a','b']) {
    const html = renderPacksPage({packs,packId,overall:{}}, {T});
    const cards = [...html.matchAll(/<article[^>]*data-pack-id="([^"]+)"[^>]*>(.*?)<\/article>/gs)];
    for (const [,id,card] of cards) {
      assert.equal(card.includes(expectedEditing), id === packId);
      assert.equal(card.includes(expectedLoaded), id !== 'pending');
      assert.doesNotMatch(card, /Active in game|takes precedence|Loaded fallback|ใช้งานอยู่ในเกม|แสดงผลเป็นลำดับแรก|โหลดไว้เป็นแพ็กสำรอง/);
    }
    assert.equal(cards.length, 3);
  }
  assert.match(T('packsWinsInfo'), lang === 'en' ? /load order/ : /ลำดับ.*โหลด/);
  assert.match(T('help_packs_s1'), lang === 'en' ? /edit/i : /แก้ไข/);
  assert.match(T('tour_packs_2_w'), lang === 'en' ? /edit/i : /แก้ไข/);
});

test('HN038 dropdown: Tab and Shift+Tab leave the dropdown and input keeps typing, arrows and Enter', async () => {
  await load(); await q("__studio.actions.openModal('newpack')");
  await wait("!!document.querySelector('[data-dd-btn=dd-np-lang]')");
  await q("document.querySelector('[data-dd-btn=dd-np-lang]').focus()");
  await key('ArrowDown');
  assert.equal(await q("document.querySelector('[data-dd=dd-np-lang]').classList.contains('open')"), true);
  await key('Tab', {windowsVirtualKeyCode:9}); await frames();
  assert.equal(await q('document.activeElement.id'), 'np-lang-code');
  assert.equal(await q("document.querySelector('[data-dd=dd-np-lang]').classList.contains('open')"), false);
  await q("document.activeElement.value='';window.outsideKeys=[];document.addEventListener('keydown',e=>{if(e.target.id==='np-lang-code')outsideKeys.push([e.key,e.defaultPrevented]);});");
  await page.send('Input.dispatchKeyEvent', {type:'keyDown',key:'v',code:'KeyV',text:'v',windowsVirtualKeyCode:86});
  await page.send('Input.dispatchKeyEvent', {type:'keyUp',key:'v',code:'KeyV',windowsVirtualKeyCode:86});
  await key('ArrowLeft'); await key('Enter');
  assert.equal(await q("document.querySelector('#np-lang-code').value"), 'v');
  assert.deepEqual(await q('outsideKeys'), [['v',false],['ArrowLeft',false],['Enter',false]]);
  await q("document.querySelector('[data-dd-btn=dd-np-lang]').focus()");
  await key('ArrowDown'); await key('Tab',{modifiers:8,windowsVirtualKeyCode:9}); await frames();
  assert.equal(await q('document.activeElement.id'), 'np-id');
  assert.equal(await q("document.querySelector('[data-dd=dd-np-lang]').classList.contains('open')"), false);
  // Programmatic/assistive focus changes must also release the old open dropdown.
  await q("document.querySelector('[data-dd-btn=dd-np-lang]').focus()"); await key('ArrowDown');
  await q("document.querySelector('#np-lang-code').focus()");
  assert.equal(await q("document.querySelector('[data-dd=dd-np-lang]').classList.contains('open')"), false);
});
