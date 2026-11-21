import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { launchChrome, openPage, loadStudio, setViewport } from '../e2e/cdp.mjs';
import { serveWeb } from '../e2e/server.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
let server, chrome, page;
before(async () => {
  server = await serveWeb(root);
  chrome = await launchChrome({ extraArgs: ['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1'] });
  page = await openPage(chrome.port);
});
after(async () => { page?.close(); await chrome?.close(); await server?.close(); });
const q = expr => page.eval(expr);
const wait = async expr => {
  for (let i = 0; i < 120; i++) {
    if (await q(expr)) return;
    await new Promise(r => setTimeout(r, 50));
  }
  throw new Error(`Timed out: ${expr}`);
};
const frames = () => q('new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))');
const click = selector => q(`document.querySelector(${JSON.stringify(selector)}).click()`);
const load = async (lang = 'en') => {
  await loadStudio(page, `${server.url}index.html?ui=2&backend=dev&lang=${lang}&notour=1`);
  await q("__studio.store.set({motion:{...__studio.store.get().motion,mode:'none'}})");
  await frames();
};
const input = async (value, type = 'insertText', composing = false) => {
  await q(`(() => {const e=document.querySelector('#ed');e.focus();e.value=${JSON.stringify(value)};e.setSelectionRange(e.value.length,e.value.length);e.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:${JSON.stringify(type)},isComposing:${composing}}));})()`);
  await frames();
};
const take = () => q("(async()=>{const s=__studio.store.get();return (await __studio.conn.api.takes(s.packId,s.packLang,s.lineId)).items.find(t=>t.id===s.line.takes.find(x=>x.id===s.selTakeId)?.id) || (await __studio.conn.api.takes(s.packId,s.packLang,s.lineId)).items.find(t=>t.id===s.line.favouriteTakeId)})()");
const inherit = async () => {
  await q("__studio.actions.savePackVoice('ai-assisted',true)");
  await q('__studio.actions.followPackDefaultTake()');
  await frames();
};
const tickDraft = async () => {
  await wait("!!document.querySelector('[data-ch=take-origin-consent]')");
  await q("(()=>{const e=document.querySelector('[data-ch=take-origin-consent]');e.checked=true;e.dispatchEvent(new Event('change',{bubbles:true}));})()");
  await wait("!!document.querySelector('.consent-menu .cm-t')");
  await click('[data-act=consent-confirm]');
  await frames();
};

for (const lang of ['en', 'th']) test(`HN039 ${lang}: inherited origin stays selected; only explicit Save persists the consented override`, async () => {
  await load(lang); await inherit();
  assert.equal(await q("document.querySelector('.d-meta [data-act=origin][data-id=\"ai-assisted\"]').getAttribute('aria-checked')"), 'true');
  assert.match(await q("document.querySelector('.pv-inherited').textContent"), lang === 'en' ? /From pack default/ : /ตามค่าเริ่มต้นของแพ็ก/);
  const before = await take();
  await click('.d-meta [data-act=origin][data-id="ai-generated"]'); await frames();
  assert.equal((await take()).origin, before.origin, 'choosing is a draft, not a write');
  assert.match(await q("document.querySelector('.take-origin-state').textContent"), lang === 'en' ? /Not saved/ : /ยังไม่ได้บันทึก/);
  assert.match(await q("document.querySelector('[data-act=take-origin-save]').textContent"), lang === 'en' ? /Save for this take/ : /บันทึกให้เทคนี้/);
  assert.equal(await q("document.querySelector('[data-act=take-origin-save]').disabled"), true);
  await tickDraft(); await click('[data-act=take-origin-save]');
  await wait("!document.querySelector('[data-act=take-origin-save]')");
  assert.deepEqual(((t) => [t.origin,t.consent.confirmed,t.consentSource])(await take()), ['ai-generated',true,'take']);
  assert.match(await q("document.querySelector('.take-origin-state').textContent"), lang === 'en' ? /Saved for this take: AI-generated/ : /บันทึกให้เทคนี้แล้ว:.*AI/);
  assert.equal(await q('__studio.store.get().voiceDefault.origin'), 'ai-assisted');
  await q('__studio.actions.selectLine(__studio.store.get().lineId)'); await frames();
  assert.match(await q("document.querySelector('.take-origin-state').textContent"), lang === 'en' ? /Saved for this take/ : /บันทึกให้เทคนี้แล้ว/);
  await click('[data-act=take-follow-pack]'); await wait("!!document.querySelector('.pv-inherited')");
  assert.deepEqual(((t) => [t.origin,t.consent.confirmed,t.consentSource])(await take()), ['ai-assisted',true,'pack']);
});

test('HN039 failed override stays visibly unsaved and retry saves the same draft', async () => {
  await load(); await inherit();
  await click('.d-meta [data-act=origin][data-id="ai-generated"]'); await tickDraft();
  await q("__studio.conn.api.raw.devControls.fail('patchTake','bad_request',{times:1})");
  await click('[data-act=take-origin-save]');
  await wait("!!document.querySelector('[data-act=take-origin-save]:not(:disabled)')");
  assert.equal((await take()).consentSource, 'pack');
  assert.match(await q("document.querySelector('.take-origin-state').textContent"), /Not saved/);
  await click('[data-act=take-origin-save]'); await wait("!document.querySelector('[data-act=take-origin-save]')");
  assert.equal((await take()).origin, 'ai-generated');
});

for (const [width,height] of [[1180,700],[1440,900]]) test(`HN039 ${width}x${height}: one/two-line subtitle stays centered in a fixed comfortable box`, async () => {
  await setViewport(page,width,height); await load();
  const heights=[];
  for (const value of ['Hello', 'Hello\nGood night']) {
    await input(value);
    const b=await q(`(()=>{const e=document.querySelector('#ed'),c=getComputedStyle(e),r=e.getBoundingClientRect(),p=document.querySelector('#editor').getBoundingClientRect(),n=e.value.split('\\n').length,lh=parseFloat(c.lineHeight),top=r.top+parseFloat(c.paddingTop);return {height:p.height,align:c.textAlign,center:top+n*lh/2,boxCenter:p.top+p.height/2,lh,room:e.clientHeight-parseFloat(c.paddingTop)-parseFloat(c.paddingBottom),scroll:e.scrollHeight>e.clientHeight+1,bottom:document.querySelector('#studio-card').getBoundingClientRect().bottom};})()`);
    heights.push(b.height);
    assert.equal(b.align,'center'); assert.ok(Math.abs(b.center-b.boxCenter)<2,JSON.stringify(b));
    assert.ok(b.height>=2*b.lh+24,JSON.stringify(b)); assert.equal(b.scroll,false);
    assert.ok(b.bottom<=height,JSON.stringify(b));
  }
  assert.equal(heights[0],heights[1],'input must not resize the box');
  await inherit();
  await click('.d-meta [data-act=origin][data-id="ai-generated"]'); await frames();
  const controls=await q(`['#editor','.d-meta','[data-act=take-origin-save]','[data-act=take-follow-pack]','#cfoot'].map(sel=>{const r=document.querySelector(sel).getBoundingClientRect();return {sel,top:r.top,bottom:r.bottom,left:r.left,right:r.right};})`);
  for (const c of controls) assert.ok(c.bottom<=height && c.left>=0 && c.right<=width,JSON.stringify(c));
});

test('HN039 blocks third hard/estimated lines from native typing, paste and noncancelable IME input without saving overflow', async () => {
  await load(); await input('One\nTwo');
  await q("document.querySelector('#ed').focus()");
  await page.send('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',modifiers:8});
  await page.send('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',modifiers:8});
  await frames();
  assert.equal(await q("document.querySelector('#ed').value"),'One\nTwo');
  for (const [value,type,composing] of [['One\nTwo\nThree','insertFromPaste',false],['ก'.repeat(71),'insertText',false],['ก'.repeat(71),'insertCompositionText',true]]) {
    await input(value,type,composing);
    assert.equal(await q("document.querySelector('#ed').value"),'One\nTwo',type);
    assert.equal(await q('__studio.store.get().draft.text'),'One\nTwo');
    assert.match(await q("document.querySelector('#ed-limit').textContent"), /two lines/);
  }
  await q('__studio.actions.settle()');
  assert.equal(await q("(async()=>{const s=__studio.store.get();return (await __studio.conn.api.line(s.packId,s.packLang,s.lineId)).text})()"),'One\nTwo');
  await q("__studio.actions.loadPack('cozy-en-rewrite','en')"); await frames();
  await input('A'.repeat(84)); await page.send('Input.insertText',{text:'B'}); await frames();
  assert.equal(await q("document.querySelector('#ed').value"),'A'.repeat(84));
  await input('A'.repeat(85),'insertFromPaste');
  assert.equal(await q("document.querySelector('#ed').value"),'A'.repeat(84));
  await input('Shorter');
  assert.equal(await q("document.querySelector('#ed').value"),'Shorter','editing and replacement still work');
});

test('HN039 Thai hint and IME Enter preserve selection instead of navigating', async () => {
  await load('th'); await input('หนึ่ง\nสอง');
  const id=await q('__studio.store.get().lineId');
  await q("document.querySelector('#ed').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',isComposing:true,bubbles:true,cancelable:true}))");
  await frames(); assert.equal(await q('__studio.store.get().lineId'),id);
  await input('หนึ่ง\nสอง\nสาม','insertCompositionText',true);
  assert.equal(await q("document.querySelector('#ed').value"),'หนึ่ง\nสอง');
  assert.match(await q("document.querySelector('#ed-limit').textContent"), /สองบรรทัด/);
});


test('HN039 pending saves cannot change another take or clear a newer take-origin draft', async () => {
  await load(); await inherit();
  const first = (await take()).id;
  await click('.d-meta [data-act=origin][data-id="ai-generated"]'); await tickDraft();
  await q(`(() => { const raw=__studio.conn.api.raw, original=raw.studioUpdateTake;window.originGate=new Promise(r=>window.releaseOrigin=r);raw.studioUpdateTake=async(...a)=>{await originGate;return original(...a);}; })()`);
  await click('[data-act=take-origin-save]');
  await wait('__studio.store.get().takeOriginDraft?.saving');
  await q(`(() => {const s=__studio.store.get(),other=s.line.takes.find(t=>t.id!==${JSON.stringify(first)});__studio.actions.selectTake(other.id);})()`);
  await frames();
  assert.equal(await q("!!document.querySelector('[data-act=take-origin-save]')"),false);
  const other = await take();
  await click(`.d-meta [data-act=origin][data-id="${other.origin === 'own' ? 'ai-assisted' : 'own'}"]`); await frames();
  const draft = await q('__studio.store.get().takeOriginDraft');
  await q('releaseOrigin()');
  await wait(`__studio.store.get().line.takes.find(t=>t.id===${JSON.stringify(first)}).origin==='ai-generated'`);
  assert.deepEqual(await q('__studio.store.get().takeOriginDraft'),draft);
  assert.equal((await take()).origin,other.origin,'saved answer belongs to the original take');
});

test('HN039 native IME composition cannot introduce an estimated third line', async () => {
  await load(); await input('ก'.repeat(69));
  await page.send('Input.imeSetComposition',{text:'ก',selectionStart:1,selectionEnd:1});
  await frames();
  assert.equal(await q("document.querySelector('#ed').value"),'ก'.repeat(69));
  assert.equal(await q('__studio.store.get().draft.text'),'ก'.repeat(69));
  await page.send('Input.imeSetComposition',{text:'',selectionStart:0,selectionEnd:0});
  await frames();
  assert.equal(await q("document.querySelector('#ed').value"),'ก'.repeat(69));
  assert.equal(await q('__studio.store.get().draft.text'),'ก'.repeat(69));
});

test('HN039 loading existing long content preserves it and allows shortening; blank third lines are blocked', async () => {
  await load();
  await q("(()=>{const s=__studio.store.get();__studio.store.set({draft:{...s.draft,text:'ก'.repeat(110)}});})()");
  await frames();
  assert.equal(await q("document.querySelector('#ed').value"),'ก'.repeat(110));
  await input('ก'.repeat(109));
  assert.equal(await q('__studio.store.get().draft.text'),'ก'.repeat(109));
  await input('Short');
  await input('Short\n\n');
  assert.equal(await q("document.querySelector('#ed').value"),'Short');
});
