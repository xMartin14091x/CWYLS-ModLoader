import test from 'node:test';
import assert from 'node:assert/strict';
let serial=0;
const fresh=async()=>{globalThis.document={querySelector:()=>({content:'test-maker-token'})};return (await import(`../modules/loader/http.js?case=${serial++}`)).default;};
const ok=data=>new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
test('empty mutation 2xx is uncertain and requests refetch without replaying',async()=>{
 const api=await fresh();let calls=0,resync=0;
 api.bus.addEventListener('resync',()=>resync++);
 for(const status of [200,201,204]) {
  globalThis.fetch=async()=>{calls++;return new Response(null,{status});};
  await assert.rejects(()=>api.saveSubtitle('sample.pack','th','one','text'),e=>e.code==='uncertain');
  await assert.rejects(()=>api.studioSaveSubtitle('sample.pack','th','one',{text:'text'}),e=>e.code==='uncertain');
 }
 assert.equal(calls,6);assert.equal(resync,6);api.close();
});
test('HTTP uses maker bearer, raw WAV, contract bodies and never legacy routes',async()=>{
 const api=await fresh(),calls=[];globalThis.fetch=async(path,options)=>{calls.push({path,...options});return ok({live:true,gameLanguage:'en',roomReady:true});};
 await api.status();await api.packs();await api.lines({lang:'th'});await api.line('sample_1','th');await api.saveSubtitle('sample.studio','th','sample_1','ไทย');await api.saveSubtitle('sample.studio','th','sample_1',null);const blob=new Blob(['wav'],{type:'audio/wav'});await api.saveVoice('sample.studio','th','sample_voice',blob);await api.saveVoice('sample.studio','th','sample_voice',null);await api.play('sample_1','original','th');
 assert.equal(calls.length,9);assert.ok(calls.every(c=>c.headers.Authorization==='Bearer test-maker-token'));assert.equal(calls[4].body,'{"text":"ไทย"}');assert.equal(calls[5].method,'DELETE');assert.equal(calls[6].body,blob);assert.equal(calls[6].headers['Content-Type'],'audio/wav');assert.equal(calls[8].path,'/v1/maker/play');assert.equal(calls[8].body,'{"id":"sample_1","variant":"original","lang":"th"}');await assert.rejects(()=>api.getFile('p','x'),/never sent/);api.close();
});
test('Studio capability discovery uses the separate v2 API boundary',async()=>{
 const api=await fresh(),calls=[];globalThis.fetch=async(path,options)=>{calls.push({path,...options});return ok(path.endsWith('/status')?{focused:false,paused:null,stale:true}:{contract:2,features:[]});};
 assert.deepEqual(await api.studioCapabilities(),{contract:2,features:[]});
 assert.deepEqual(await api.studioStatus(),{focused:false,paused:null,stale:true});
 assert.equal(calls.length,2);assert.equal(calls[0].path,'/v1/maker/studio/capabilities');assert.equal(calls[1].path,'/v1/maker/studio/status');assert.ok(calls.every(c=>c.method==='GET'&&c.headers.Authorization==='Bearer test-maker-token'));api.close();
});
test('401 latches terminal auth failure and prevents every future fetch',async()=>{
 const api=await fresh();let calls=0;globalThis.fetch=async()=>{calls++;return new Response('{"error":{"code":"unauthorized"}}',{status:401});};await assert.rejects(()=>api.packs(),e=>e.code==='unauthorized');await assert.rejects(()=>api.status(),e=>e.code==='unauthorized');api.watch();assert.equal(calls,1);api.close();
});
test('action POST is not automatically retried on rate limiting',async()=>{
 const api=await fresh();let calls=0;globalThis.fetch=async()=>{calls++;return new Response('{"error":{"code":"rate_limited"}}',{status:429,headers:{'Retry-After':'0'}});};await assert.rejects(()=>api.play('sample','dub','th'),e=>e.code==='rate_limited');assert.equal(calls,1);api.close();
});
test('oversize JSON and WAV are rejected before fetch',async()=>{
 const api=await fresh();let calls=0;globalThis.fetch=async()=>{calls++;return ok({});};await assert.rejects(()=>api.saveSubtitle('p','th','id','ก'.repeat(24000)),e=>e.code==='too_large');await assert.rejects(()=>api.saveVoice('p','th','clip',new Blob([new Uint8Array(16*1024*1024+1)])),e=>e.code==='too_large');assert.equal(calls,0);api.close();
});
test('poll forwards structured events, follows next and refreshes on dropped events',async()=>{
 const api=await fresh(),events=[],paths=[];let resolve;const done=new Promise(r=>resolve=r);api.bus.addEventListener('maker-event',e=>events.push(e.detail));let resync=0;api.bus.addEventListener('resync',()=>resync++);
 globalThis.fetch=async path=>{paths.push(path);if(paths.length===1)return ok({epoch:'one',next:5,latest:99,events:[{type:'line.spoken',data:{localizationId:'sample'}}]});api.close();resolve();return ok({epoch:'two',next:6,dropped:2,events:[]});};api.setConnection('connected');api.watch();await done;await new Promise(r=>setTimeout(r,0));assert.equal(events[0].data.localizationId,'sample');assert.match(paths[1],/after=5&/);assert.equal(resync,1);api.close();
});
test('missing injected token fails before making any network request',async()=>{
 const api=await fresh();globalThis.document.querySelector=()=>null;let calls=0;globalThis.fetch=async()=>{calls++;return ok({});};await assert.rejects(()=>api.packs(),e=>e.code==='unauthorized');assert.equal(calls,0);api.close();
});
test('GET rate limiting honours bounded retry and succeeds once',async()=>{
 const api=await fresh();let calls=0;globalThis.fetch=async()=>++calls===1?new Response('{"error":{"code":"rate_limited"}}',{status:429,headers:{'Retry-After':'0'}}):ok({packs:[]});assert.deepEqual(await api.packs(),{packs:[]});assert.equal(calls,2);api.close();
});
test('read-only and room-not-ready failures preserve their contract codes',async()=>{
 const api=await fresh();globalThis.fetch=async()=>new Response('{"error":{"code":"read_only_pack","message":"not editable"}}',{status:403});await assert.rejects(()=>api.saveSubtitle('ref','th','id','draft'),e=>e.code==='read_only_pack');globalThis.fetch=async()=>new Response('{"error":{"code":"not_ready"}}',{status:409});await assert.rejects(()=>api.play('id','original','th'),e=>e.code==='not_ready');api.close();
});
test('close aborts in-flight HTTP requests',async()=>{
 const api=await fresh();let signal;globalThis.fetch=async(_path,options)=>{signal=options.signal;return new Promise((_,reject)=>signal.addEventListener('abort',()=>reject(new Error('aborted'))));};const request=api.packs();api.close();assert.equal(signal.aborted,true);await assert.rejects(()=>request,e=>e.code==='unavailable');
});
test('network drop or client abort on non-GET mutation is uncertain and emits resync while GET stays unavailable',async()=>{
 const api=await fresh();let resync=0;
 api.bus.addEventListener('resync',()=>resync++);
 for(const failure of [()=>new TypeError('fetch failed'),()=>{const e=new Error('timed out');e.name='AbortError';return e;}]) {
  globalThis.fetch=async()=>{throw failure();};
  await assert.rejects(()=>api.saveSubtitle('sample.pack','th','one','text'),e=>e.code==='uncertain');
  await assert.rejects(()=>api.studioSaveSubtitle('sample.pack','th','one',{text:'text'}),e=>e.code==='uncertain');
  await assert.rejects(()=>api.play('one','original','th'),e=>e.code==='uncertain');
  await assert.rejects(()=>api.packs(),e=>e.code==='unavailable');
 }
 assert.equal(resync,6);api.close();
});
