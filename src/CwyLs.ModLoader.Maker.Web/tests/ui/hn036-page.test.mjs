import test, {before,after} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {dirname,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {launchChrome,openPage,loadStudio,setViewport} from '../e2e/cdp.mjs';
import {serveWeb} from '../e2e/server.mjs';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
let server,chrome,page;
before(async()=>{server=await serveWeb(root);chrome=await launchChrome({extraArgs:['--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1']});page=await openPage(chrome.port);});
after(async()=>{page?.close();await chrome?.close();await server?.close();});
const q=expr=>page.eval(expr);
const wait=async expr=>{for(let i=0;i<120;i++){if(await q(expr))return;await new Promise(r=>setTimeout(r,50));}throw new Error('Timed out: '+expr);};
const load=async()=>{await loadStudio(page,`${server.url}index.html?ui=2&backend=dev&lang=en&notour=1`);await q("__studio.store.set({motion:{...__studio.store.get().motion,mode:'none'}})");};
const input=(id,value)=>q(`(()=>{const n=document.getElementById(${JSON.stringify(id)});n.value=${JSON.stringify(value)};n.dispatchEvent(new Event('input',{bubbles:true}));})()`);
const change=(id,value)=>q(`(()=>{const n=document.getElementById(${JSON.stringify(id)});n.value=${JSON.stringify(value)};n.dispatchEvent(new Event('change',{bubbles:true}));})()`);
const click=selector=>q(`document.querySelector(${JSON.stringify(selector)}).click()`);

test('HN026-1: repeated pack switches remove the previous editor while catalogue loading is pending',async()=>{
  await load();
  for(let i=0;i<2;i++){
    await q("__studio.actions.loadPack('satone-th-dub','th')");
    await q(`(()=>{const raw=__studio.conn.api.raw,orig=raw.studioLines;window.loadGate=new Promise(r=>window.releaseLoad=r);raw.studioLines=async v=>{if(v.packId==='cozy-en-rewrite')await loadGate;return orig(v);};window.switching=__studio.actions.loadPack('cozy-en-rewrite','en');})()`);
    await wait("!document.querySelector('#ed')");
    assert.equal(await q('__studio.store.get().line'),null);
    await q('releaseLoad();switching');await wait("!!document.querySelector('#ed') && __studio.store.get().packId==='cozy-en-rewrite'");
  }
});

test('HN011: every new-pack field and customization flag survives repaint and page changes',async()=>{
  await load();await q("__studio.actions.openModal('newpack')");await wait("!!document.querySelector('#np-name')");
  await input('np-name','My custom name');await input('np-id','my.custom.id');await input('np-author','Author');await input('np-desc','Description');
  await change('np-lang','custom');await input('np-lang-code','tl');await input('np-lang-name','Tagalog');
  await click('[data-act="ui-th"]');await wait("document.documentElement.lang==='th'");
  const form=await q("Object.fromEntries(['np-name','np-id','np-author','np-desc','np-lang-code','np-lang-name','np-lang'].map(id=>[id,document.getElementById(id).value]))");
  assert.deepEqual(form,{'np-name':'My custom name','np-id':'my.custom.id','np-author':'Author','np-desc':'Description','np-lang-code':'tl','np-lang-name':'Tagalog','np-lang':'custom'});
  assert.deepEqual(await q("(({nameCustom,idCustom,langNameCustom})=>({nameCustom,idCustom,langNameCustom}))(__studio.store.get().newPackForm)"),{nameCustom:true,idCustom:true,langNameCustom:true});
  await q("__studio.store.set({page:'lines',modal:null})");await wait("!!document.querySelector('.vb-page')");
  await q("__studio.actions.openModal('newpack')");await wait("!!document.querySelector('#np-name')");
  assert.equal(await q("document.querySelector('#np-desc').value"),'Description');
  await input('np-author','Another author');assert.equal(await q("document.querySelector('#np-name').value"),'My custom name');
  assert.equal(await q("document.querySelector('#np-id').value"),'my.custom.id');assert.equal(await q("document.querySelector('#np-lang-name').dataset.custom"),'1');
});

test('HN011: Studio Arrow and editor Enter follow the visible scene despite hidden filters',async()=>{
  await load();await q("__studio.actions.selectLine('morning_wake_001')");
  await q(`(()=>{const {store,actions}=__studio;store.set({view:{...store.get().view,q:'nothing matches',filter:'done',voice:'nodub',char:'someone'}});window.moves=[];actions.selectLine=id=>moves.push(id);})()`);
  await wait("!!document.querySelector('#ed')");
  await q(`document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}));document.querySelector('#ed').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));`);
  assert.deepEqual(await q('moves'),['morning_wake_002','morning_wake_002']);
});

test('debt11/12: untouched suggestions fit the name limit and retry an ID taken after listing',async()=>{
  await load();await q("__studio.actions.openModal('newpack')");await wait("!!document.querySelector('#np-author')");
  await input('np-author','A'.repeat(80));
  const name=await q("document.querySelector('#np-name').value"),id=await q("document.querySelector('#np-id').value");
  assert.ok(Array.from(name).length<=48);assert.match(name,/Thai$/);
  await q("__studio.conn.api.raw.devControls.fail('createPack','conflict',{times:1})");
  await click('[data-act="np-create"]');await wait("__studio.store.get().newPackForm===null");
  assert.equal(await q('__studio.store.get().packId'),id+'-2');
});

test('HN028-P1: both trim endpoints support keyboard steps, bounds, focus and persistence',async()=>{
  await load();const originalLine=await q('__studio.store.get().lineId');
  await q("document.querySelector('.handle[data-h=s]').focus()");
  assert.equal(await q("document.activeElement.getAttribute('role')"),'slider');
  assert.ok(await q("!!document.activeElement.getAttribute('aria-label')"));
  async function key(value){
    const previous=await q('__studio.store.get().line.revision');
    await page.send('Input.dispatchKeyEvent',{type:'keyDown',key:value,code:value});
    await page.send('Input.dispatchKeyEvent',{type:'keyUp',key:value,code:value});
    await wait(`__studio.store.get().line.revision !== ${JSON.stringify(previous)}`);
    await wait(`(async()=>{const el=document.activeElement,s=__studio.store.get(),t=(await import('/ui/selectors.js')).currentTake(s);return el?.classList.contains('handle') && Number(el.getAttribute('aria-valuenow'))===Number((el.dataset.h==='s'?t.trimStartSec:(t.trimEndSec??t.sourceDurationSec)).toFixed(3));})()`);
  }
  const start0=Number(await q("document.activeElement.getAttribute('aria-valuenow')"));
  await key('ArrowRight');assert.ok(Number(await q("document.activeElement.getAttribute('aria-valuenow')"))>start0);
  // Held keys keep advancing the preview while the preceding write is still awaiting acknowledgement.
  await q(`(()=>{const raw=__studio.conn.api.raw,orig=raw.studioUpdateTake;window.trimGate=new Promise(r=>window.releaseTrim=r);raw.studioUpdateTake=async(...a)=>{await trimGate;return orig(...a);};window.trimBefore=Number(document.activeElement.getAttribute('aria-valuenow'));})()`);
  for(let i=0;i<3;i++){await page.send('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowRight',code:'ArrowRight'});await page.send('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowRight',code:'ArrowRight'});}
  assert.ok(await q('__studio.store.get().trimDraft.start >= trimBefore + 0.029'));
  await q('releaseTrim()');await wait('!__studio.store.get().trimDraft');
  await key('PageUp');await key('Home');assert.equal(Number(await q("document.activeElement.getAttribute('aria-valuenow')")),0);
  await key('End');const boundary=await q("({now:Number(document.activeElement.getAttribute('aria-valuenow')),max:Number(document.activeElement.getAttribute('aria-valuemax'))})");assert.equal(boundary.now,boundary.max);
  await q("document.querySelector('.handle[data-h=e]').focus()");
  await key('Home');const minEnd=await q("({now:Number(document.activeElement.getAttribute('aria-valuenow')),min:Number(document.activeElement.getAttribute('aria-valuemin'))})");assert.equal(minEnd.now,minEnd.min);
  await key('PageUp');await key('ArrowLeft');await key('End');
  assert.equal(await q('__studio.store.get().lineId'),originalLine);
  const persisted=await q(`(async()=>{const s=__studio.store.get(),r=await __studio.conn.api.takes(s.packId,s.packLang,s.lineId),t=r.items.find(t=>t.id===__studio.store.get().selTakeId)||r.items.find(t=>t.id===s.line.favouriteTakeId);return {start:t.trimStartSec,end:t.trimEndSec,total:t.sourceDurationSec};})()`);
  assert.equal(persisted.end,null);assert.ok(persisted.total-persisted.start>=0.1-1e-9);
});

test('debt15: 1180x700 subtitle tools use the freed height beside the inspector',async()=>{
  await setViewport(page,1180,700);await load();
  const box=await q(`(()=>{const card=document.querySelector('#card').getBoundingClientRect(),qa=document.querySelector('#qa').getBoundingClientRect(),inspector=document.querySelector('#studio-inspector').getBoundingClientRect(),editor=document.querySelector('#editor').getBoundingClientRect();return{cardRight:card.right,cardBottom:card.bottom,qaBottom:qa.bottom,inspectorLeft:inspector.left,editorHeight:editor.height};})()`);
  assert.ok(box.cardBottom-box.qaBottom<=16 && box.qaBottom<=box.cardBottom,JSON.stringify(box));
  assert.ok(box.inspectorLeft>=box.cardRight && box.editorHeight>=192,JSON.stringify(box));
  await setViewport(page,1440,900);
});

for(const code of ['unavailable','internal']) test(`HN026-5: startup ${code} renders a wired Retry and recovers`,async()=>{
  const source=await readFile(resolve(root,'ui/main.js'),'utf8');
  const injected=source.replace('await actions.start({ fixture: store.get().fixture });',`if (!globalThis.__failedStart) { globalThis.__failedStart=true; throw new StudioError('${code}','injected startup failure',${code==='unavailable'?0:503}); } await actions.start({ fixture: store.get().fixture });`);
  assert.notEqual(injected,source);
  const route=async ev=>{if(ev.request.url.includes('/ui/main.js'))await page.send('Fetch.fulfillRequest',{requestId:ev.requestId,responseCode:200,responseHeaders:[{name:'Content-Type',value:'text/javascript'}],body:Buffer.from(injected).toString('base64')});else await page.send('Fetch.continueRequest',{requestId:ev.requestId});};
  page.on('Fetch.requestPaused',route);await page.send('Fetch.enable',{patterns:[{urlPattern:'*/ui/main.js'}]});
  try{
    await loadStudio(page,`${server.url}index.html?ui=2&backend=dev&lang=en&notour=1`);
    await wait("!!document.querySelector('[data-act=retry-lines]')");
    await click('[data-act="ui-th"]');await wait("document.documentElement.lang==='th'");
    await click('[data-act="retry-lines"]');await wait("!!document.querySelector('#ed') && __studio.store.get().lines.length>0 && !__studio.store.get().linesError");
  }finally{await page.send('Fetch.disable');page.handlers.get('Fetch.requestPaused').splice(page.handlers.get('Fetch.requestPaused').indexOf(route),1);}
});
