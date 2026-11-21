import test from 'node:test';
import assert from 'node:assert/strict';
import {progress,subtitleHint,draftKey} from '../modules/studio-model.js';
import {routePath,routes,studioRoutes} from '../modules/loader/routes.js';
import {diagnostics} from '../modules/loader/events.js';
import {addStudioMock} from '../modules/loader/studio-mock.js';
const mock=()=>addStudioMock(Object.assign(diagnostics('mock'),{watch(){},close(){}}));
test('progress separates subtitles from voiced-line dub totals and groups nulls',()=>{
 const p=progress([{group:'A',voice:'one',subtitle:'ไทย',dubPack:null},{group:'A',voice:null,subtitle:null,dubPack:null},{group:null,voice:'two',subtitle:'text',dubPack:'p'}]);
 assert.deepEqual(p.total,{total:3,subtitles:2,voiced:2,dubs:1});assert.equal(p.groups[1].name,'Ungrouped');assert.equal(p.groups[0].voiced,1);
});
test('Thai hint counts code points and only warns over 20 characters/s',()=>{
 assert.equal(subtitleHint('ก'.repeat(40),2).warn,false);assert.equal(subtitleHint('ก'.repeat(41),2).warn,true);
 assert.equal(subtitleHint('🙂',1).characters,1);assert.equal(subtitleHint('ไทย',null).cps,null);assert.equal(subtitleHint(' ',2).valid,false);assert.equal(subtitleHint('ก'.repeat(2001),2).valid,false);
});
test('draft keys isolate the selected pack, language and line',()=>{assert.notEqual(draftKey('a','th','x'),draftKey('b','th','x'));assert.notEqual(draftKey('a','th','x'),draftKey('a','en','x'));});
test('route map contains exactly DS-00 endpoints and encodes parameters',()=>{
 assert.equal(Object.keys(routes).length,10);assert.equal(routePath('voice',{packId:'sample.studio',lang:'th',clip:'sample_1'}),'/v1/maker/packs/sample.studio/voice/th/sample_1');
 assert.match(routePath('lines',{lang:'th',q:'hello & ไทย',filter:'all',offset:0,limit:10}),/q=hello%20%26%20/);assert.throws(()=>routePath('line',{lang:'th'}),/Missing route parameter/);assert.ok(!Object.values(routes).some(r=>r.path.includes('/files/')));
});
test('Studio v2 routes stay separate from v1 wiring and encode only supplied identifiers',()=>{
 assert.equal(Object.keys(studioRoutes).length,36);assert.equal(Object.keys(routes).length,10);assert.equal(routePath('studioLabelDubs',{packId:'sample.studio'}),'/v1/maker/studio/packs/sample.studio/voice/label');
 assert.equal(routePath('studioCapabilities'),'/v1/maker/studio/capabilities');
 assert.equal(routePath('studioPublish',{packId:'sample.studio',lang:'th',id:'sample_1'}),'/v1/maker/studio/packs/sample.studio/languages/th/lines/sample_1/publish');
 assert.ok(Object.values(studioRoutes).every(r=>r.path.startsWith('/v1/maker/studio/')));
});
test('routePath requires path segments and omits absent or null query values',()=>{
 assert.equal(routePath('studioLines',{packId:'sample.studio',lang:'th'}),'/v1/maker/studio/packs/sample.studio/languages/th/lines');
 assert.equal(routePath('studioLines',{packId:'sample.studio',lang:'th',q:'rain & tea',limit:20}),'/v1/maker/studio/packs/sample.studio/languages/th/lines?q=rain%20%26%20tea&limit=20');
 assert.equal(routePath('studioLine',{packId:'sample.studio',lang:'th',id:'sample_1',occurrenceId:null}),'/v1/maker/studio/packs/sample.studio/languages/th/lines/sample_1');
 assert.throws(()=>routePath('studioLine',{lang:'th',id:'sample_1'}),/Missing route parameter: packId/);
});
test('route descriptors carry non-200 statuses and binary media explicitly',()=>{
 assert.equal(studioRoutes.studioCreateTake.status,201);assert.equal(studioRoutes.studioCreatePack.status,201);
 assert.equal(studioRoutes.studioReadTakeAudio.status,200);assert.equal(studioRoutes.studioReadTakeAudio.mediaType,'audio/wav');
 assert.deepEqual(Object.keys(studioRoutes.studioDeleteTake.response),['revision','removed']);
 assert.equal(routes.status.status,200);assert.match(routes.status.mediaType,/application\/json/);
});
test('mock adapters expose Studio capability and cached status methods',async()=>{
 const api=mock();assert.deepEqual((await api.studioCapabilities()).features,[]);const status=await api.studioStatus();assert.equal(status.focused,true);assert.equal(status.paused,false);api.close();
});
test('mock catalogue has 40 invented lines with server-style filtering and paging',async()=>{
 const api=mock(),first=await api.lines({lang:'th',limit:10}),next=await api.lines({lang:'th',offset:10,limit:10});assert.equal(first.total,40);assert.equal(first.items.length,10);assert.notEqual(first.items[0].id,next.items[0].id);assert.ok(first.items.every(l=>l.id.startsWith('sample_')));
 assert.equal((await api.lines({lang:'th',q:'sample_voice_002'})).total,1);assert.ok((await api.lines({lang:'th',filter:'missing-subtitle'})).items.every(l=>!l.subtitle));assert.ok((await api.lines({lang:'th',filter:'done'})).items.every(l=>l.subtitle&&l.dubPack));
});
test('mock subtitle save/removal is language scoped and emits contract events',async()=>{
 const api=mock(),events=[];api.bus.addEventListener('maker-event',e=>events.push(e.detail));const id='sample_click_002';await api.saveSubtitle('sample.studio','th',id,'ดาวกระดาษ');assert.equal((await api.line(id,'th')).subtitle,'ดาวกระดาษ');assert.equal((await api.line(id,'en')).subtitle,null);await api.saveSubtitle('sample.studio','th',id,null);assert.equal((await api.line(id,'th')).subtitle,null);assert.equal(events.at(-1).data.removed,true);await assert.rejects(()=>api.saveSubtitle('sample.reference','th',id,'x'),e=>e.code==='read_only_pack');
});
test('mock dub upload updates duration metadata and both playback event variants',async()=>{
 const api=mock(),events=[];api.bus.addEventListener('maker-event',e=>events.push(e.detail));const bytes=new ArrayBuffer(44+96000),view=new DataView(bytes);view.setUint32(28,96000,true);await api.saveVoice('sample.studio','th','sample_voice_002',new Blob([bytes],{type:'audio/wav'}));assert.equal((await api.line('sample_click_002','th')).dubDurationSec,1);
 await api.play('sample_click_002','original','th');await api.play('sample_click_002','dub','th');assert.deepEqual(events.filter(e=>e.type==='maker.play').map(e=>e.data.variant),['original','dub']);assert.equal(events.at(-1).type,'line.spoken');await api.saveVoice('sample.studio','th','sample_voice_002',null);await assert.rejects(()=>api.play('sample_click_002','dub','th'),e=>e.code==='not_found');
});
