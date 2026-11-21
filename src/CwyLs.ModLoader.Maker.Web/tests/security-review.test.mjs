// DS-06: hostile synthetic catalogue/event text through the real HTTP adapter and
// Studio renderer. This checks generated markup; it is not browser acceptance.
import test from 'node:test';
import assert from 'node:assert/strict';

test('catalogue, subtitle and speaking-event payloads stay escaped in live Studio markup',async()=>{
 const payload='</textarea><img src=x onerror="globalThis.ds06=true">&\'"';
 const token='a'.repeat(64),requests=[];
 globalThis.location={search:'?backend=http'};
 globalThis.document={
  querySelector:selector=>selector==='meta[name="cwyls-maker-token"]'?{content:token}:null,
  addEventListener(){}
 };
 const line={id:'review_line',voice:'review_clip',group:payload,durationSec:2,ja:payload,en:payload,subtitle:payload,subtitlePack:payload,dubPack:payload,dubDurationSec:1};
 globalThis.fetch=async(path,options)=>{
  requests.push({path,options});
  const data=path==='/v1/maker/status'?{gameLanguage:'th',gameLanguageIsPack:true,roomReady:true}
   :path==='/v1/maker/packs'?{packs:[{id:'lab.review',name:payload,version:payload,languages:['th'],editable:true},{id:'lab.locked',name:payload,editable:false,readOnlyReason:payload}]}
   :{total:1,offset:0,items:[line]};
  return new Response(JSON.stringify(data),{headers:{'Content-Type':'application/json'}});
 };
 const {startStudio,renderStudio}=await import('../modules/studio.js');
 const loader=(await import('../modules/loader/index.js')).default;
 try{
  await startStudio();
  loader.bus.dispatchEvent(new CustomEvent('maker-event',{detail:{type:'line.spoken',data:{text:payload,textJa:payload,localizationId:'review_line'}}}));
  const html=renderStudio();
  assert.ok(html.includes('&lt;/textarea&gt;&lt;img src=x onerror=&quot;globalThis.ds06=true&quot;&gt;&amp;&#39;&quot;'));
  assert.ok(!html.includes(payload));
  assert.ok(!html.includes('<img'));
  assert.ok(!html.includes(token));
  assert.ok(requests.length>=4);
  for(const {path,options} of requests){
   assert.ok(path.startsWith('/v1/maker/'));
   assert.ok(!path.includes(token));
   assert.equal(options.headers.Authorization,`Bearer ${token}`);
   assert.equal(options.redirect,'error');
   assert.equal(options.cache,'no-store');
  }
 }finally{loader.close();}
});
