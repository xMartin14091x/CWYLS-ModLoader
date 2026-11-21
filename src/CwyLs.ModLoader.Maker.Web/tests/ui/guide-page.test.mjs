import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio, setViewport } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
let server, chrome, page;
before(async () => {
  server = await serveWeb(root);
  chrome = await launchChrome({ extraArgs: ['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1'] });
  page = await openPage(chrome.port);
});
after(async () => { page?.close(); await chrome?.close(); await server?.close(); });
const q = expr => page.eval(expr, 30000);
const wait = async expr => {
  const start = Date.now();
  while (Date.now() - start < 10000) { if (await q(expr)) return; await new Promise(r => setTimeout(r, 40)); }
  const state = await q("(() => {const s=__studio.store.get();return {page:s.page,modal:s.modal,packsSub:s.packsSub,menu:s.menu,tour:s.tour};})()");
  throw new Error(`Timed out: ${expr}; state=${JSON.stringify(state)}`);
};
const click = async selector => {
  // A state update can precede its rAF repaint. Measure the rendered control, then verify its hit target.
  await q('new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))');
  const pt = await q(`(() => { const el=document.querySelector(${JSON.stringify(selector)}),r=el.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2,hit=document.elementFromPoint(x,y); return {x,y,clear:el.contains(hit),hit:hit?.outerHTML.slice(0,180)}; })()`);
  assert.ok(pt.clear, `${selector} is covered by ${pt.hit}`);
  delete pt.clear; delete pt.hit;
  await page.send('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...pt });
  await page.send('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...pt });
};
const key = async (key, modifiers = 0) => {
  await page.send('Input.dispatchKeyEvent', { type: 'keyDown', key, modifiers });
  await page.send('Input.dispatchKeyEvent', { type: 'keyUp', key, modifiers });
};
const capture = async name => {
  if (!process.env.GUIDE_SHOTS) return;
  await mkdir(process.env.GUIDE_SHOTS, { recursive: true });
  const image = await page.send('Page.captureScreenshot', { format: 'png' });
  await writeFile(join(process.env.GUIDE_SHOTS, `${name}.png`), Buffer.from(image.data, 'base64'));
};
const ready = async lang => {
  await loadStudio(page, `${server.url}index.html?backend=dev&lang=${lang}`);
  await q("localStorage.removeItem('cwyls-maker-tours-seen'); localStorage.removeItem('cwyls-maker-guide-hint'); localStorage.setItem('cwyls-maker-motion',JSON.stringify({mode:'off',pageMs:700,smallMs:250}))");
  await loadStudio(page, `${server.url}index.html?backend=dev&lang=${lang}`);
};

test('D-126: first visit shows one hint; no tour starts on load, page change or Settings reset', async () => {
  await ready('en');
  assert.equal(await q('__studio.store.get().tour'), null);
  assert.equal(await q("!!document.querySelector('.guide-hint')"), true);
  await loadStudio(page, `${server.url}index.html?backend=dev&lang=en`);
  assert.equal(await q("!!document.querySelector('.guide-hint')"), false, 'the first-visit hint stays dismissed on reload');
  assert.equal(await q('__studio.store.get().tour'), null);
  await click('[data-act=page-tab][data-id=lines]');
  await wait("__studio.store.get().page==='lines'");
  assert.equal(await q('__studio.store.get().tour'), null);
  await click('[data-act=page-tab][data-id=settings]');
  await wait("!!document.querySelector('[data-act=reset-tours]')");
  await click('[data-act=reset-tours]');
  assert.equal(await q('__studio.store.get().tour'), null);
  await wait("!!document.querySelector('.help-menu')");
  assert.equal(await q("document.querySelector('[data-help-tab=guide]').getAttribute('aria-selected')"), 'true');
});

test('D-124: Guide and Glossary follow all six contexts, including New pack; keyboard tabs and Esc restore focus', async () => {
  for (const lang of ['en','th']) {
    await ready(lang);
    const guides = new Set(), glossaries = new Set();
    for (const context of ['studio','lines','packs','newpack','export','settings']) {
      if (context !== 'studio') {
        if (context === 'newpack') {
          await click('[data-act=new-pack]');
          await wait("__studio.store.get().packsSub==='newpack' && !!document.querySelector('.m-newpack')");
        }
        else await click(`[data-act=page-tab][data-id=${context}]`);
        await wait(`__studio.store.get().page==='${context === 'newpack' ? 'packs' : context}'`);
      }
      await click('[data-act=keys-menu]');
      await wait(`document.querySelector('.help-menu')?.dataset.page==='${context}'`);
      guides.add(await q("document.querySelector('#help-body').textContent"));
      await key('ArrowRight');
      await wait("document.querySelector('[data-help-tab=glossary]')?.getAttribute('aria-selected')==='true'");
      glossaries.add(await q("document.querySelector('#help-body').textContent"));
      await key('Escape');
      await wait("!document.querySelector('.help-menu')");
      assert.equal(await q('document.activeElement?.dataset.act'), 'keys-menu');
    }
    assert.equal(guides.size, 6, lang);
    assert.equal(glossaries.size, 6, lang);
    await click('[data-act=keys-menu]'); await wait("!!document.querySelector('.help-menu')");
    await key('ArrowRight');
    await click('[data-act=page-tab][data-id=packs]');
    await wait("document.querySelector('.help-menu')?.dataset.page==='packs'");
    assert.equal(await q("document.querySelector('[data-help-tab=glossary]').getAttribute('aria-selected')"), 'true');
    const before = await q("document.querySelector('#help-body').textContent");
    await click('[data-act=page-tab][data-id=studio]');
    await wait("document.querySelector('.help-menu')?.dataset.page==='studio'");
    assert.notEqual(await q("document.querySelector('#help-body').textContent"), before);
    const close = await q("(() => {const r=document.querySelector('[data-act=help-close]').getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
    await page.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{...close, radiusX: 5, radiusY: 5, force: 1, id: 0}] });
    await page.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await wait("!document.querySelector('.help-menu')");
    assert.equal(await q('document.activeElement?.dataset.act'), 'keys-menu');
  }
});

test('D-124: spotlight follows actual target order, Back/Next/Skip/Done and Esc; fits both requested viewports', async () => {
  for (const lang of ['en','th']) for (const [w,h] of [[1440,900],[1180,820]]) {
    await setViewport(page,w,h);
    await ready(lang);
    for (const context of ['studio','lines','packs','newpack','export','settings']) {
      if (context !== 'studio') {
        if (context === 'newpack') {
          await click('[data-act=new-pack]');
          await wait("__studio.store.get().packsSub==='newpack' && !!document.querySelector('.m-newpack')");
        }
        else await click(`[data-act=page-tab][data-id=${context}]`);
      }
      await click('[data-act=keys-menu]');
      await wait("!!document.querySelector('.help-menu')");
      const helpFits = await q(`(() => {const r=document.querySelector('.help-menu').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight;})()`);
      assert.ok(helpFits, `${lang} ${context} panel ${w}x${h}`);
      const shots = lang === 'en' || ['studio','newpack'].includes(context);
      if (shots) await capture(`${lang}-${context}-${w}x${h}-guide`);
      await key('ArrowRight');
      await wait("document.querySelector('[data-help-tab=glossary]').getAttribute('aria-selected')==='true'");
      if (shots) await capture(`${lang}-${context}-${w}x${h}-glossary`);
      await click('[data-act=tour-start]');
      await wait(`__studio.store.get().tour?.page==='${context}' && !!document.querySelector('#tour-spotlight')`);
      assert.equal(await q('document.activeElement?.dataset.act'), 'tour-next');
      const targets = [];
      const total = await q("Number(document.querySelector('#tour-content').dataset.total)");
      for (let step=0;step<total;step++) {
        await wait(`Number(document.querySelector('#tour-content').dataset.step)===${step}`);
        const result = await q(`(() => {const c=document.querySelector('#tour-card'),r=c.getBoundingClientRect(),s=document.querySelector('#tour-spotlight');return {target:s.dataset.target, fits:r.left>=0&&r.top>=0&&r.right<=innerWidth&&r.bottom<=innerHeight, shade:document.querySelector('#tour-shade').getAttribute('d'), focus:c.contains(document.activeElement), shortcuts:__studio.store.get().rec};})()`);
        assert.ok(result.fits, `${lang} ${context} ${step} ${w}x${h}`);
        assert.ok(result.focus);
        assert.ok(result.shade.includes('Z M'), 'mask has a transparent target hole');
        assert.ok(result.target);
        if (shots && (step === 0 || context === 'studio')) await capture(`${lang}-${context}-${w}x${h}-tour-${step+1}`);
        targets.push(result.target);
        if (step === 1) {
          await click('[data-act=tour-prev]');
          await wait("Number(document.querySelector('#tour-content').dataset.step)===0");
          await click('[data-act=tour-next]');
          await wait("Number(document.querySelector('#tour-content').dataset.step)===1");
        }
        await key('r');
        assert.equal(await q('__studio.store.get().rec'), result.shortcuts, 'tour keyboard never starts recording');
        await key('Tab');
        assert.equal(await q("document.querySelector('#tour-card').contains(document.activeElement)"),true);
        await click('[data-act=tour-next]');
      }
      assert.equal(new Set(targets).size,total, `${context} has a distinct target at every step`);
      const order = {
        studio: ['scene','editor','deck','origin','hero'], lines: ['filters','chips','row'],
        packs: ['tabs','progress','actions'], newpack: ['name','language','author','icon','create'],
        export: ['summary','checks','build'], settings: ['voice','workflow','motion']
      };
      assert.deepEqual(targets, order[context].map(id => `${context}-${id}`));
      await wait("document.querySelector('#tour-layer').hidden");
      assert.equal(await q('document.activeElement?.dataset.act'), 'keys-menu');
      await click('[data-act=keys-menu]'); await wait("!!document.querySelector('.help-menu')");
      await click('[data-act=tour-start]'); await wait("!!document.querySelector('#tour-card')");
      await key('Escape'); await wait("document.querySelector('#tour-layer').hidden");
      assert.equal(await q('document.activeElement?.dataset.act'), 'keys-menu');
      await click('[data-act=keys-menu]'); await wait("!!document.querySelector('.help-menu')");
      await click('[data-act=tour-start]'); await wait("!!document.querySelector('#tour-card')");
      await click('[data-act=tour-skip]'); await wait("document.querySelector('#tour-layer').hidden");
      assert.equal(await q('document.activeElement?.dataset.act'), 'keys-menu');
    }
  }
});
