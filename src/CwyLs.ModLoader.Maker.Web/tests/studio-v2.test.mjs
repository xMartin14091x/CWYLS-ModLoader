import test from 'node:test';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtemp,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {studioApi} from '../modules/loader/studio-api.js';
import {studioFeatures,studioGroupLabels} from '../modules/loader/contract.js';

test('Studio method contract maps text-work and recovery calls without losing mutation identity',async()=>{
 const calls=[],api=studioApi(async(...args)=>{calls.push(args);return {revision:'next'};});
 const mutation={expectedRevision:'before',operationId:'1'.repeat(32),text:null};
 await api.studioCapabilities();await api.studioStatus();await api.studioPacks();
 await api.studioLines({packId:'p',lang:'th'});await api.studioLine('p','th','id');
 await api.studioGroups('p','th');await api.studioUpdateWorkflow('p','th','id',mutation);
 await api.studioSaveSubtitle('p','th','id',mutation);await api.studioHistory('p','th','id');
 await api.studioRevertSubtitle('p','th','id','revision',mutation);
 await api.studioGlossary('p','th');await api.studioSaveGlossary('p','th',mutation);
 await api.studioOperation('p','op');
 await api.studioRecovery('p','blocked');await api.studioResolve('p','blocked',mutation);
 const creation={operationId:'2'.repeat(32),id:'lab.pack',name:'Lab',language:{code:'th'}};
 await api.studioCreatePack(creation);
 assert.deepEqual(calls.map(c=>c[0]),['studioCapabilities','studioStatus','studioPacks','studioLines','studioLine',
  'studioGroups','studioWorkflow','studioSubtitle','studioHistory','studioRevert','studioGlossary','studioSaveGlossary','studioOperation','studioRecovery','studioResolve','studioCreatePack']);
 assert.equal(calls[7][2],mutation);assert.equal(calls[9][1].revisionId,'revision');
 assert.deepEqual(calls[15],['studioCreatePack',{},creation]);
 assert.equal(studioFeatures.newPacks,'newPacks');assert.notEqual(studioFeatures.projects,studioFeatures.newPacks);
 assert.equal(studioGroupLabels.clickReaction,'group.clickReaction');
});

test('real HTTP adapter integrates with the v2 fake loader', {timeout:20000}, async()=>{
 const data=await mkdtemp(join(tmpdir(),'cwy-studio-v2-http-'));
 const script=fileURLToPath(new URL('../tools/fake_loader.py',import.meta.url));
 const token='c'.repeat(64);
 const child=spawn('python3',['-u',script,'--port','0','--data',data,'--token',token,'--tick-sec','0','--recovery-fixture']);
 const originalFetch=globalThis.fetch,originalDocument=globalThis.document;
 let api;
 const exited=new Promise(resolve=>child.once('exit',resolve));
 try {
  const base=await new Promise((resolve,reject)=>{
   let output='';const timer=setTimeout(()=>reject(new Error('fake startup timeout')),5000);
   child.once('error',e=>{clearTimeout(timer);reject(e);});
   child.once('exit',()=>{clearTimeout(timer);reject(new Error('fake exited during startup'));});
   child.stdout.on('data',data=>{output+=data;const match=output.match(/http:\/\/127\.0\.0\.1:\d+/);if(match){clearTimeout(timer);resolve(match[0]);}});
  });
  globalThis.document={querySelector:()=>({content:token})};
  globalThis.fetch=(path,options)=>originalFetch(base+path,options);
  api=(await import('../modules/loader/http.js?studio-v2-integration')).default;
  assert.deepEqual((await api.studioCapabilities()).features,['catalog','workflow','subtitles','dubs','projects','playback','newPacks','packInfo','export','voiceDefault']);
  assert.equal((await api.studioStatus()).paused,false);
  const pack=(await api.studioPacks()).packs.find(p=>p.studioEditable).id;
  const page=await api.studioLines({packId:pack,lang:'th',limit:2});
  let line=await api.studioLine(pack,'th',page.items[0].id,{occurrenceId:null});
  const op='d'.repeat(32);
  const save={expectedRevision:line.revision,operationId:op,text:'ดาวกระดาษ\n🙂'};
  const saved=await api.studioSaveSubtitle(pack,'th',line.id,save);
  assert.equal(saved.live,false);
  assert.deepEqual(await api.studioSaveSubtitle(pack,'th',line.id,save),saved);
  assert.equal((await api.studioOperation(pack,op)).state,'committed');
  line=await api.studioLine(pack,'th',line.id);
  assert.equal(line.ownedSubtitle,save.text);
  await api.studioUpdateWorkflow(pack,'th',line.id,{expectedRevision:line.revision,operationId:'e'.repeat(32),status:'done'});
  assert.equal((await api.studioLines({packId:pack,lang:'th',filter:'done'})).total,1);
  assert.equal((await api.studioGroups(pack,'th')).overall.done,1);
  const history=await api.studioHistory(pack,'th',line.id);
  await api.studioRevertSubtitle(pack,'th',line.id,history.items[0].revisionId,
   {expectedRevision:history.revision,operationId:'f'.repeat(32)});
  const glossary=await api.studioGlossary(pack,'th');
  assert.equal((await api.studioSaveGlossary(pack,'th',{expectedRevision:glossary.revision,
   operationId:'a'.repeat(32),entries:[{id:'paper',sourceJa:null,sourceEn:'paper',target:'กระดาษ',note:null}]})).entries.length,1);
  const creation={operationId:'9'.repeat(32),id:'lab.http.pack',name:'Over HTTP',author:'Lab',language:{code:'th'}};
  const created=await api.studioCreatePack(creation);
  assert.deepEqual(Object.keys(created),['pack','restartRequired','live','note']);
  assert.equal(created.restartRequired,true);assert.equal(created.live,false);
  assert.deepEqual([created.pack.loaded,created.pack.studioEditable,created.pack.metadataState],[false,false,'pending']);
  assert.deepEqual(await api.studioCreatePack(creation),created);
  assert.equal((await api.studioPacks()).packs.find(p=>p.id==='lab.http.pack').restartRequired,true);
  await assert.rejects(()=>api.studioLines({packId:'lab.http.pack',lang:'th'}),e=>e.code==='restart_required'&&e.message.length>0);
  await assert.rejects(()=>api.studioCreatePack({...creation,name:'Another'}),e=>e.code==='conflict');
  await assert.rejects(()=>api.studioCreatePack({...creation,operationId:'8'.repeat(32),id:'ab'}),e=>e.code==='bad_request');
  const preview=await api.studioRecovery('sample-recovery','a'.repeat(32));
  const resolution={operationId:'3'.repeat(32),expectedRecoveryRevision:preview.recoveryRevision,strategy:'keep-current'};
  const resolved=await api.studioResolve('sample-recovery','a'.repeat(32),resolution);
  assert.equal(resolved.resolved,true);assert.equal(resolved.live,false);
  assert.deepEqual(await api.studioResolve('sample-recovery','a'.repeat(32),resolution),resolved);

  // M5d dubs slice through the real adapter: take, WAV upload and download, favourite, publish, unpublish, delete.
  const voiced=(await api.studioLines({packId:pack,lang:'th',limit:500})).items.find(l=>l.voice&&l.id!==line.id);
  let takes=await api.studioTakes(pack,'th',voiced.id);
  const createdTake=await api.studioCreateTake(pack,'th',voiced.id,{expectedRevision:takes.revision,operationId:'4'.repeat(32),
   origin:'own',consent:{confirmed:true,statementVersion:'consent-1'}});
  const takeId=createdTake.take.id;
  const source=await api.studioPutTakeAudio(pack,'th',voiced.id,takeId,{kind:'source',expectedRevision:createdTake.revision,operationId:'5'.repeat(32)},wavBlob(24000));
  const render=await api.studioPutTakeAudio(pack,'th',voiced.id,takeId,{kind:'render',expectedRevision:source.revision,operationId:'6'.repeat(32)},wavBlob(48000));
  assert.equal(render.take.renderReady,true);
  const downloaded=await api.studioGetTakeAudio(pack,'th',voiced.id,takeId,'render');
  assert.ok(downloaded instanceof Blob);
  assert.deepEqual(new Uint8Array(await downloaded.arrayBuffer()),new Uint8Array(await wavBlob(48000).arrayBuffer()));
  const favourite=await api.studioSetFavourite(pack,'th',voiced.id,{expectedRevision:render.revision,operationId:'7'.repeat(32),takeId});
  assert.equal(favourite.favouriteTakeId,takeId);
  takes=await api.studioTakes(pack,'th',voiced.id);
  const published=await api.studioPublish(pack,'th',voiced.id,{expectedRevision:takes.revision,expectedClipRevision:takes.clipRevision,operationId:'8'.repeat(32),takeId});
  assert.equal(published.publishedTakeId,takeId);
  const playedDub=await api.studioPlay({id:voiced.id,variant:'dub',lang:'th',packId:pack,expectedRevision:published.revision});
  assert.equal(playedDub.saved,false);assert.equal(playedDub.live,true);assert.equal(playedDub.publishedTakeId,takeId);assert.equal(playedDub.effectivePackId,pack);
  const playedOriginal=await api.studioPlay({id:voiced.id,variant:'original',lang:'th',packId:pack,expectedRevision:published.revision});
  assert.equal(playedOriginal.saved,false);assert.equal(playedOriginal.live,true);
  await assert.rejects(()=>api.studioPlay({id:voiced.id,variant:'dub',lang:'th',packId:'lab.http.pack'}),e=>e.code==='restart_required');
  takes=await api.studioTakes(pack,'th',voiced.id);
  await assert.rejects(()=>api.studioDeleteTake(pack,'th',voiced.id,takeId,{expectedRevision:takes.revision,operationId:'9'.repeat(32)}),e=>e.code==='take_in_use');
  const updated=await api.studioUpdateTake(pack,'th',voiced.id,takeId,{expectedRevision:takes.revision,operationId:'b'.repeat(32),trimStartSec:null,evenOutVolume:true});
  assert.equal(updated.take.renderReady,false);
  takes=await api.studioTakes(pack,'th',voiced.id);
  const unpublished=await api.studioUnpublish(pack,'th',voiced.id,{expectedRevision:takes.revision,expectedClipRevision:takes.clipRevision,operationId:'c'.repeat(32)});
  assert.equal(unpublished.publishedTakeId,null);
  takes=await api.studioTakes(pack,'th',voiced.id);
  assert.equal((await api.studioDeleteTake(pack,'th',voiced.id,takeId,{expectedRevision:takes.revision,operationId:'2'.repeat(32)})).removed,true);
 } finally {
  api?.close();globalThis.fetch=originalFetch;globalThis.document=originalDocument;
  child.kill('SIGINT');await exited;
  await rm(data,{recursive:true,force:true});
 }
});

test('dubs and playback methods map to their routes with full request bodies',async()=>{
 const calls=[],api=studioApi(async(...args)=>{calls.push(args);return {revision:'next'};});
 const mutation={expectedRevision:'before',operationId:'1'.repeat(32)},wav=new Blob(['RIFF']);
 const play={id:'id',variant:'dub',lang:'th',packId:'p',expectedRevision:'before'};
 await api.studioTakes('p','th','id');await api.studioCreateTake('p','th','id',{...mutation,origin:'own'});
 await api.studioPutTakeAudio('p','th','id','t',{kind:'source',...mutation},wav);await api.studioGetTakeAudio('p','th','id','t','render');
 await api.studioUpdateTake('p','th','id','t',{...mutation,evenOutVolume:true});await api.studioDeleteTake('p','th','id','t',mutation);
 await api.studioSetFavourite('p','th','id',{...mutation,takeId:null});
 await api.studioPublish('p','th','id',{...mutation,expectedClipRevision:'c',takeId:'t'});await api.studioUnpublish('p','th','id',{...mutation,expectedClipRevision:'c'});
 await api.studioPlay(play);
 assert.deepEqual(calls.map(c=>c[0]),['studioTakes','studioCreateTake','studioTakeAudio','studioReadTakeAudio','studioTake',
  'studioDeleteTake','studioFavourite','studioPublish','studioUnpublish','studioPlay']);
 assert.deepEqual(calls[2][1],{packId:'p',lang:'th',id:'id',takeId:'t',kind:'source',expectedRevision:'before',operationId:'1'.repeat(32)});
 assert.equal(calls[2][2],wav);
 assert.deepEqual(calls[5][1],{packId:'p',lang:'th',id:'id',takeId:'t',expectedRevision:'before',operationId:'1'.repeat(32)});
 assert.equal(calls[5][2],undefined);
 assert.deepEqual(calls[9],['studioPlay',{},play]);
});

test('take audio GET returns a Blob and refuses a response that is not WAV',async()=>{
 globalThis.document={querySelector:()=>({content:'token'})};
 const api=(await import('../modules/loader/http.js?take-audio')).default;
 const original=globalThis.fetch;let type='audio/wav';
 globalThis.fetch=async()=>new Response(new Uint8Array([82,73,70,70]),{status:200,headers:{'Content-Type':type}});
 try {
  const blob=await api.studioGetTakeAudio('p','th','id','t'.repeat(32),'source');
  assert.deepEqual(new Uint8Array(await blob.arrayBuffer()),new Uint8Array([82,73,70,70]));
  type='application/json; charset=utf-8';
  await assert.rejects(()=>api.studioGetTakeAudio('p','th','id','t'.repeat(32),'source'),e=>e.code==='internal');
 }finally{api.close();globalThis.fetch=original;}
});

function wavBlob(rate){
 const frames=rate/5,data=new Uint8Array(44+frames*2),view=new DataView(data.buffer),text=(at,s)=>[...s].forEach((c,i)=>data[at+i]=c.charCodeAt(0));
 text(0,'RIFF');view.setUint32(4,36+frames*2,true);text(8,'WAVE');text(12,'fmt ');view.setUint32(16,16,true);view.setUint16(20,1,true);
 view.setUint16(22,1,true);view.setUint32(24,rate,true);view.setUint32(28,rate*2,true);view.setUint16(32,2,true);view.setUint16(34,16,true);
 text(36,'data');view.setUint32(40,frames*2,true);for(let i=0;i<frames;i++)view.setInt16(44+i*2,Math.round(1200*Math.sin(i/7)),true);
 return new Blob([data],{type:'audio/wav'});
}

test('recovery_required preserves server resource and operation context',async()=>{
 globalThis.document={querySelector:()=>({content:'token'})};
 const api=(await import('../modules/loader/http.js?recovery-context')).default;
 const original=globalThis.fetch,message='pack=p; resource=.maker/lines/th/one.json; operationId='+'1'.repeat(32);
 const details={packId:'p',resource:'.maker/lines/th/one.json',blockedOperationId:'1'.repeat(32)};
 globalThis.fetch=async()=>new Response(JSON.stringify({error:{code:'recovery_required',message,details}}),{status:503});
 try {
  await assert.rejects(()=>api.studioOperation('p','1'.repeat(32)),e=>{
   assert.deepEqual(e.details,details);return e.code==='recovery_required'&&e.message===message;
  });
 }finally{api.close();globalThis.fetch=original;}
});
