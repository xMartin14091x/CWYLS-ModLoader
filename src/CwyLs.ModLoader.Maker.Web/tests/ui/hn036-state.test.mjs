import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../ui/store.js';
import { createApi, StudioError } from '../../ui/api.js';
import { createActions } from '../../ui/actions.js';
import { createDevBackend } from '../dev/dev-backend.js';
import { createCapture } from '../../ui/audio/capture.js';
import { canEditText } from '../../ui/selectors.js';

const P = 'satone-th-dub', B = 'cozy-en-rewrite', ID = 'clickheroine_rest_010', EMPTY = 'morning_wake_006';
const tick = () => new Promise(r => setImmediate(r));
const drain = async () => { for (let i=0;i<20;i++) await tick(); };
const deferred = () => { let resolve; const promise = new Promise(r => resolve=r); return {promise, resolve}; };
function clock() {
  let time=0, seq=0; const pending=new Map();
  return {setTimeout(fn,ms) {const id=++seq; pending.set(id,{fn,at:time+ms}); return id;}, clearTimeout(id){pending.delete(id);},
    get time(){return time;}, async advance(ms){const end=time+ms; for (;;) {const item=[...pending].sort((a,b)=>a[1].at-b[1].at).find(x=>x[1].at<=end); if(!item)break; pending.delete(item[0]);time=item[1].at;item[1].fn();await drain();}time=end;await drain();}};
}
async function setup(extra={}) {
  const dev=createDevBackend({lines:120}), clk=clock(), toasts=[];
  const store=createStore({ready:false,conn:{connected:false,unauthorized:false},packs:[],packId:null,packLang:'th',groups:[],overall:{},lines:[],byId:new Map(),view:{group:null,filter:'all',q:''},lineId:null,line:null,draft:null,rec:'idle',mic:'none',peaks:{},settings:{autoDoneOnNext:false,jumpAfterDone:false}});
  let seq=0;
  const actions=createActions({store,api:createApi(dev),mode:'dev',timers:clk,now:()=>clk.time,selectDelayMs:0,storage:null,newOp:()=>(++seq).toString(16).padStart(32,'0'),toast:(...a)=>toasts.push(a),...extra});
  await actions.start(); await drain(); return {dev,clk,store,actions,toasts};
}
const state=c=>c.store.get();
const decoded={channels:[new Float32Array(24000)],sampleRate:48000};

test('HN026-1: switching pack invalidates the previous editor until the destination loads', async()=>{
  const c=await setup(), gate=deferred(), orig=c.dev.studioLines;
  c.dev.studioLines=async q=>{if(q.packId===B)await gate.promise;return orig(q);};
  const loading=c.actions.loadPack(B,'en'); await drain();
  const pending=state(c); const editable=canEditText(pending); c.actions.editText('must not disappear');
  gate.resolve(); await loading;
  assert.equal(pending.line,null); assert.equal(pending.draft,null); assert.equal(editable,false);
  assert.equal(state(c).line.id,state(c).lineId); assert.equal(state(c).draft.key,`${B}/en/${state(c).lineId}`);
});

for(const refusal of [false,true]) test(`HN026-3: late workflow ${refusal?'rollback':'success'} cannot patch another pack`,async()=>{
  const c=await setup(),gate=deferred();
  await c.actions.setStatus('draft'); await c.clk.advance(2000);
  const orig=c.dev.studioUpdateWorkflow;
  const other=await c.dev.studioLine(B,'en',ID);
  await orig(B,'en',ID,{expectedRevision:other.revision,operationId:'b'.repeat(32),status:'done'});
  c.dev.studioUpdateWorkflow=async(...a)=>{await gate.promise;if(refusal)throw new StudioError('bad_request');return orig(...a);};
  const changing=c.actions.setStatus('needs-retake');await drain();await c.actions.loadPack(B,'en');
  const before={...state(c).byId.get(ID)};gate.resolve();await changing;await c.clk.advance(2000);
  assert.equal(state(c).byId.get(ID).status,before.status);assert.equal(state(c).byId.get(ID).revision,before.revision);
});

test('HN026-3: delayed subtitle acknowledgement stays with its pack and draft',async()=>{
  const c=await setup(),gate=deferred(),orig=c.dev.studioSaveSubtitle;
  c.dev.studioSaveSubtitle=async(...a)=>{const res=await orig(...a);await gate.promise;return {...res,live:false,note:'pack A only'};};
  c.actions.editText('A edit');c.actions.flushCurrent();await drain();await c.actions.loadPack(B,'en');
  const before={...state(c).byId.get(ID)};gate.resolve();await drain();await c.clk.advance(2000);
  assert.equal(state(c).byId.get(ID).text,before.text);assert.equal(state(c).byId.get(ID).revision,before.revision);
  assert.equal(state(c).saveNote,null);assert.equal(c.actions._drafts.get(`${P}/th/${ID}`).status,'saved');
});

test('HN026-7: committed receipt advances the next expected revision without replaying the write',async()=>{
  const c=await setup();c.dev.dev.fail('saveSubtitle','internal',{times:1,uncertain:true,afterCommit:true});
  c.actions.editText('committed once');await c.clk.advance(900);
  assert.equal(state(c).draft.status,'saved');assert.equal(state(c).line.text,'committed once');
  c.actions.editText('next own edit');await c.actions.settle();await drain();
  assert.equal(state(c).draft.status,'saved');assert.equal((await c.dev.studioLine(P,'th',ID)).ownedSubtitle,'next own edit');
  assert.equal(c.dev.dev.log.filter(x=>x.method==='saveSubtitle').length,2);
});

test('HN026-4: Play my version stops on failed, conflicting and unresolved subtitle saves',async()=>{
  for(const code of ['bad_request','conflict','internal']) {
    const c=await setup();c.dev.dev.fail('saveSubtitle',code,{times:3,uncertain:code==='internal'});
    if(code==='internal') c.dev.studioOperation=async()=>{throw new StudioError('internal');};
    c.actions.editText('unsaved replacement');const ok=await c.actions.playMine();await drain();
    assert.equal(ok,false,code);assert.equal(c.dev.dev.log.some(x=>['publish','play','putTakeAudio'].includes(x.method)),false,code);
  }
});

test('HN026-4: settle saves text typed during an in-flight save before allowing playback',async()=>{
  const c=await setup(),gate=deferred(),orig=c.dev.studioSaveSubtitle;let first=true;
  c.dev.studioSaveSubtitle=async(...a)=>{if(first){first=false;await gate.promise;}return orig(...a);};
  c.actions.editText('first');c.actions.flushCurrent();await drain();
  c.actions.editText('latest');const settled=c.actions.settle();gate.resolve();
  assert.equal(await settled,true);assert.equal((await c.dev.studioLine(P,'th',ID)).ownedSubtitle,'latest');
});

test('HN026-6: switching packs drops old export receipts and ignores late previews and exports',async()=>{
  const c=await setup();await c.actions.runExport({autoDownload:false});assert.ok(state(c).exportResult);
  const gate=deferred(),orig=c.dev.studioExportCheck;
  c.dev.studioExportCheck=async(...a)=>{const res=await orig(...a);if(a[0]===P)await gate.promise;return res;};
  const check=c.actions.runExportCheck();await drain();await c.actions.loadPack(B,'en');
  gate.resolve();await check;assert.equal(state(c).exportResult,null);assert.equal(state(c).exportPreview,null);
  await c.actions.runExportCheck();assert.equal(state(c).exportPreview.packId,B);
});

test('T3: an export that finishes after switching packs is ignored',async()=>{
  const c=await setup(),gate=deferred(),orig=c.dev.studioExport;
  assert.equal(typeof orig,'function','the dev backend exposes studioExport');
  c.dev.studioExport=async(...a)=>{const res=await orig(...a);await gate.promise;return res;};
  const exporting=c.actions.runExport({autoDownload:false});await drain();
  await c.actions.loadPack(B,'en');
  gate.resolve();await exporting;await drain();
  assert.equal(state(c).packId,B);assert.equal(state(c).exportResult,null,'the late result of the old pack is not shown on the new pack');
  assert.equal(state(c).exportBusy,false);
});

test('HN026-8: take changes on a nonselected line refresh recorded and published catalogue fields',async()=>{
  const c=await setup();const before=state(c).lineId;
  const full=await c.dev.studioLine(P,'th',EMPTY);
  const made=await c.dev.studioCreateTake(P,'th',EMPTY,{expectedRevision:full.revision,operationId:'a'.repeat(32),origin:'own',consent:{confirmed:false}});
  await c.clk.advance(400);assert.equal(state(c).lineId,before);assert.equal(state(c).byId.get(EMPTY).takeCount,1);
  // A publication hint must refresh the nonselected row even when no take detail is open.
  const orig=c.dev.studioLine;c.dev.studioLine=async(...a)=>{const l=await orig(...a);return a[2]===EMPTY?{...l,ownedDub:true,publishedTakeId:made.take.id}:l;};
  c.actions.onEvent({type:'maker.take.published',data:{packId:P,lang:'th',id:EMPTY}});await c.clk.advance(400);
  assert.equal(state(c).byId.get(EMPTY).ownedDub,true);assert.equal(state(c).byId.get(EMPTY).publishedTakeId,made.take.id);
});

test('HN026-2: importing cancels when the line or pack changes during decode',async()=>{
  for(const packSwitch of [false,true]) {
    const gate=deferred(),c=await setup({decodeFile:()=>gate.promise});await c.actions.selectLine(EMPTY);
    const importing=c.actions.importFile({name:'synthetic.wav'});await drain();
    if(packSwitch)await c.actions.loadPack(B,'en');else await c.actions.selectLine('morning_wake_005');
    gate.resolve(decoded);assert.equal(await importing,false);assert.equal(c.dev.dev.log.some(x=>x.method==='createTake'),false);
  }
});

test('HN026-2: stopping a recording cancels if selection changes while the mic finishes',async()=>{
  const gate=deferred(),mic={supported:()=>true,setup:async()=>({devices:[],deviceId:''}),onLevel:()=>()=>{},start(){},stop:()=>gate.promise,release(){}};
  const c=await setup({mic});await c.actions.selectLine(EMPTY);c.store.set({mic:'ready'});
  await c.actions.toggleRecord();await c.clk.advance(3000);
  const stopping=c.actions.toggleRecord();await drain();await c.actions.selectLine('morning_wake_005');gate.resolve(decoded);await stopping;
  assert.equal(c.dev.dev.log.some(x=>x.method==='createTake'),false);
});

test('HN026-9: overlapping capture setup is deduplicated and release owns every resource',async()=>{
  const old=globalThis.AudioWorkletNode;const gate=deferred(),tracks=[],contexts=[];let calls=0;
  globalThis.AudioWorkletNode=class{port={close(){}};disconnect(){}};
  class AC{audioWorklet={addModule:async()=>{}};constructor(){contexts.push(this);}resume=async()=>{};close(){this.closed=true;}createMediaStreamSource(){return{connect(){},disconnect(){}};}}
  const nav={mediaDevices:{async getUserMedia(){calls++;await gate.promise;const track={stops:0,stop(){this.stops++;},getSettings:()=>({deviceId:'a'})};tracks.push(track);return{getTracks:()=>[track],getAudioTracks:()=>[track]};},enumerateDevices:async()=>[]}};
  try{const mic=createCapture({nav,AudioContextCtor:AC});const a=mic.setup(),b=mic.setup();gate.resolve();await Promise.all([a,b]);mic.release();
    assert.equal(calls,1);assert.ok(tracks.every(t=>t.stops===1));assert.ok(contexts.every(c=>c.closed));
  }finally{globalThis.AudioWorkletNode=old;}
});

test('HN026-9: release during pending permission cleans the late stream without reopening capture',async()=>{
  const old=globalThis.AudioWorkletNode;const gate=deferred();let stopped=0;
  globalThis.AudioWorkletNode=class{port={close(){}};disconnect(){}};
  class AC{audioWorklet={addModule:async()=>{}};resume=async()=>{};close(){}createMediaStreamSource(){return{connect(){},disconnect(){}};}}
  const track={stop(){stopped++;}};const nav={mediaDevices:{getUserMedia:()=>gate.promise,enumerateDevices:async()=>[]}};
  try{const mic=createCapture({nav,AudioContextCtor:AC});const pending=mic.setup().catch(e=>e.name);mic.release();gate.resolve({getTracks:()=>[track],getAudioTracks:()=>[track]});await pending;assert.equal(stopped,1);}finally{globalThis.AudioWorkletNode=old;}
});

test('debt12: generated IDs retry collisions while custom IDs keep the explicit destination',async()=>{
  const c=await setup();c.dev.dev.fail('createPack','conflict',{times:1});
  assert.equal(await c.actions.createPack({name:'Retry generated',id:'generated.th',idCustom:false,lang:'th'}),true);
  assert.equal(state(c).packId,'generated.th-2');
  const d=await setup();d.dev.dev.fail('createPack','conflict',{times:1});
  assert.equal(await d.actions.createPack({name:'Custom',id:'explicit.th',idCustom:true,lang:'th'}),false);
  assert.equal(d.dev.dev.log.filter(x=>x.method==='createPack').length,1);
});
