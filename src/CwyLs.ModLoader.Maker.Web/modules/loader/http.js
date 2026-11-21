import {LoaderError,friendlyErrors,emptyState} from './contract.js';
import {diagnostics} from './events.js';
import {allRoutes,routePath} from './routes.js';
import {studioApi} from './studio-api.js';

const api=diagnostics('http'), controllers=new Set();
let unauthorized=false,closed=false,watching=false,cooldown=0;
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function request(key,params={},body,attempt=0) {
  if(unauthorized)throw new LoaderError('unauthorized',friendlyErrors.unauthorized);
  if(closed)throw new LoaderError('unavailable',friendlyErrors.unavailable);
  const token=document.querySelector('meta[name="cwyls-maker-token"]')?.content;
  if(!token){unauthorized=true;api.setConnection('offline');throw new LoaderError('unauthorized',friendlyErrors.unauthorized);}
  const route=allRoutes[key],headers={Authorization:`Bearer ${token}`};
  let payload;
  if(body!==undefined) {
    if(body instanceof Blob){
      // Raw uploads: WAV (16 MiB) or, for the pack icon route (v2.3), PNG (512 KiB).
      if(route.request==='image/png raw bytes'){if(body.size>512*1024)throw new LoaderError('too_large','Pack icons must be a PNG of at most 512 KiB.');headers['Content-Type']='image/png';}
      else {if(body.size>16*1024*1024)throw new LoaderError('too_large','Dubs must fit within 16 MiB and 60 seconds.');headers['Content-Type']='audio/wav';}
      payload=body;
    }
    else {headers['Content-Type']='application/json; charset=utf-8';payload=JSON.stringify(body);if(new TextEncoder().encode(payload).length>65536)throw new LoaderError('too_large',friendlyErrors.too_large);}
  }
  while(Date.now()<cooldown){await sleep(Math.min(250,cooldown-Date.now()));if(closed||unauthorized)throw new LoaderError(unauthorized?'unauthorized':'unavailable',friendlyErrors[unauthorized?'unauthorized':'unavailable']);}
  const controller=new AbortController();controllers.add(controller);
  const timeout=setTimeout(()=>controller.abort(),key==='events'?32000:15000);
  // Diagnostics (2026-11-09): one observation per request for the page log; the page log's own posts are not observed.
  const quiet=key==='studioClientLog',started=Date.now();let seen={status:0,code:null},path=key;
  try {
    path=routePath(key,params);
    const response=await fetch(path,{method:route.method,headers,body:payload,signal:controller.signal,cache:'no-store',credentials:'same-origin',redirect:'error'});
    seen.status=response.status;
    if(!response.ok) {
      let error;try{error=(await response.json()).error;}catch{}
      const code=response.status===401?'unauthorized':error?.code||'internal';
      seen.code=code;seen.message=typeof error?.message==='string'?error.message:null;
      if(code==='unauthorized'){unauthorized=true;api.setConnection('offline');for(const c of controllers)c.abort();}
      const retry=response.headers.get('Retry-After');
      const seconds=retry?(Number.isFinite(Number(retry))?Math.max(0,Number(retry)):Math.max(0,(Date.parse(retry)-Date.now())/1000)):1;
      if(code==='rate_limited'){
        cooldown=Date.now()+Math.max(1000,Number.isFinite(seconds)?seconds*1000:1000);
        // A bounded retry is safe for reads and idempotent writes, never action POSTs.
        if(attempt<1&&route.method!=='POST')return await request(key,params,body,attempt+1);
      }
      const message=code==='recovery_required'&&typeof error?.message==='string'
        ? error.message : friendlyErrors[code]||'The loader could not finish this request.';
      throw new LoaderError(code,message+(code==='rate_limited'?` Try again in ${Math.ceil(seconds)||1}s.`:''),seconds,error?.details??null);
    }
    if(['audio/wav','image/png','application/zip'].includes(route.mediaType)) {
      // Binary reads (creator take audio; v2.3 pack icon and export zip): reject any other media type.
      if(!(response.headers.get('Content-Type')||'').toLowerCase().startsWith(route.mediaType))throw new LoaderError('internal',route.mediaType==='audio/wav'?'The loader did not return WAV audio.':`The loader did not return ${route.mediaType}.`);
      return await response.blob();
    }
    const text=await response.text();
    if(route.method!=='GET'&&!text.trim()&&!quiet) {
      api.bus.dispatchEvent(new Event('resync'));
      throw new LoaderError('uncertain','The loader returned no mutation receipt. Refetch the line and status before retrying.');
    }
    return JSON.parse(text);
  } catch(error) {
    if(error instanceof LoaderError)throw error;
    if(!seen.code)seen.code=error?.name==='AbortError'?'aborted':'network';
    if(unauthorized)throw new LoaderError('unauthorized',friendlyErrors.unauthorized);
    if(quiet)throw new LoaderError('unavailable',friendlyErrors.unavailable);
    if(route.method!=='GET'&&!closed){
      api.bus.dispatchEvent(new Event('resync'));
      throw new LoaderError('uncertain',friendlyErrors.uncertain);
    }
    api.setConnection('offline');throw new LoaderError('unavailable',friendlyErrors.unavailable);
  } finally {
    clearTimeout(timeout);controllers.delete(controller);
    if(!quiet&&typeof api.onRequest==='function'){try{api.onRequest({method:route.method,route:key,path,status:seen.status,ms:Date.now()-started,code:seen.code,message:seen.message??null,attempt});}catch{}}
  }
}
Object.assign(api,{
  async status(){const status=await request('status');api.runtime=status;api.setConnection('connected');return status;},
  ...studioApi(request),
  // Legacy workspaces remain mock-only; never call their prototype routes.
  loadState:async()=>emptyState(),
  saveState:async()=>{throw new LoaderError('unavailable','This workspace is coming later.');},
  putFile:async()=>{throw new LoaderError('unavailable','This workspace is coming later.');},
  getFile:async()=>{throw new LoaderError('unavailable','Game media is never sent to the browser.');},
  packs:()=>request('packs'),
  lines:query=>request('lines',{q:'',filter:'all',offset:0,limit:100,...query}),
  line:(id,lang)=>request('line',{id,lang}),
  saveSubtitle:(packId,lang,id,text)=>request(text===null?'deleteSubtitle':'subtitle',{packId,lang,id},text===null?undefined:{text}),
  saveVoice:(packId,lang,clip,wav)=>request(wav===null?'deleteVoice':'voice',{packId,lang,clip},wav===null?undefined:wav),
  play:(id,variant,lang)=>request('play',{}, {id,variant,lang}),
  connect(){throw new LoaderError('bad_request','Connection simulation is available only in mock mode.');},
  async resetDemo(){throw new LoaderError('bad_request','Reset is available only in mock mode.');},
  watch(){if(watching||unauthorized||closed)return;watching=true;(async()=>{
    let after=0,epoch=null,backoff=1000,lastError='';
    while(!closed&&!unauthorized){
      try {
        const page=await request('events',{after,wait:25,limit:100});
        if(page.reset||page.dropped>0||(epoch&&epoch!==page.epoch)){
          api.emit('SYNC','Event stream changed; refreshing catalogue.',{toast:false});
          api.bus.dispatchEvent(new Event('resync'));
        }
        epoch=page.epoch;after=page.next;backoff=1000;lastError='';
        for(const event of page.events){
          api.bus.dispatchEvent(new CustomEvent('maker-event',{detail:event}));
          api.emit(event.type,event.data?.note||event.data?.localizationId||event.data?.id||event.type,{toast:false});
        }
        if(api.connection==='offline')await api.status();
        // Also tolerate a fake listener that answers an empty poll immediately.
        if(!page.events.length)await sleep(100);
      }catch(error){
        if(closed)break;
        if(error.message!==lastError){api.emit('CONNECTION',error.message,{level:'warn'});lastError=error.message;}
        if(unauthorized)break;
        await sleep(backoff);backoff=Math.min(15000,backoff*2);
      }
    }
    watching=false;
  })();},
  close(){closed=true;for(const controller of controllers)controller.abort();}
});
export default api;
