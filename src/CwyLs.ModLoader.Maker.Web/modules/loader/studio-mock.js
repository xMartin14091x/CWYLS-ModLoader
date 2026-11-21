// All reference text and ids here are invented, never copied from the game.
import {LoaderError} from './contract.js';
import {studioApi} from './studio-api.js';
export function addStudioMock(api){
 const packId='sample.studio',subtitles=new Map(),voices=new Map();
 const packs=[{id:packId,name:'Rainy Study · practice pack',version:'0.1.0',languages:['th','en','ja'],editable:true,readOnlyReason:null},{id:'sample.reference',name:'Reference example',version:'1.0.0',languages:['th'],editable:false,readOnlyReason:'Reference packs are read-only.'}];
 const samples=Array.from({length:40},(_,i)=>({id:`sample_click_${String(i+1).padStart(3,'0')}`,voice:i===39?null:`sample_voice_${String(i+1).padStart(3,'0')}`,group:['Sample greetings','Sample desk notes','Sample breaks','Sample evening'][Math.floor(i/10)],durationSec:i===39?null:2.5+(i%5)*.6,ja:`練習用のメモ ${i+1}。小さな紙の星を作ろう。`,en:`Practice note ${i+1}: fold a little paper star.`,subtitle:null,subtitlePack:null,dubPack:null,dubDurationSec:null}));
 for(let i=0;i<40;i++){if(i%3===0)subtitles.set(`th/${samples[i].id}`,'พับดาวกระดาษดวงเล็กด้วยกันนะ');if(i%4===0)voices.set(`th/${samples[i].voice}`,2.4);}
 const event=(type,data)=>{api.bus.dispatchEvent(new CustomEvent('maker-event',{detail:{type,data}}));api.emit(type,data.id||data.localizationId||type,{toast:false});};
 const get=(id,lang)=>{const l=samples.find(l=>l.id===id);if(!l)throw new LoaderError('not_found','Sample line not found.');return {...l,subtitle:subtitles.get(`${lang}/${id}`)??null,subtitlePack:subtitles.has(`${lang}/${id}`)?packId:null,dubPack:voices.has(`${lang}/${l.voice}`)?packId:null,dubDurationSec:voices.get(`${lang}/${l.voice}`)??null};};
 const writable=id=>{if(id!==packId)throw new LoaderError('read_only_pack','Reference packs are read-only.');};
 Object.assign(api,{
  ...studioApi(async()=>{throw new LoaderError('unavailable','This Studio feature needs the v2 fake loader or a capable game backend.');}),
  runtime:{gameVersion:'1.18.0',loaderVersion:'0.9.2',gameLanguage:'en',gameLanguageIsPack:false,roomReady:true},
  async studioCapabilities(){return {contract:2,features:[],uiText:false,limits:{jsonBytes:65536,wavBytes:16777216,wavSeconds:60,subtitleRevisions:20,takesPerLine:3,projectBytes:2147483648},storageSchema:1,consentVersion:'consent-1'};},
  async studioStatus(){return {...api.runtime,focused:true,paused:false,busy:false,reason:null,observedAt:'2026-11-08T00:00:00.0000000+07:00',stale:false,focusOnPlay:true};},
  async status(){return {...api.runtime};},
  async packs(){return {packs:structuredClone(packs)};},
  async lines({lang,q='',filter='all',offset=0,limit=100}){let items=samples.map(l=>get(l.id,lang));const query=q.toLowerCase();items=items.filter(l=>[l.id,l.voice,l.ja,l.en,l.subtitle].some(s=>s?.toLowerCase().includes(query)));items=items.filter(l=>filter==='missing-subtitle'?!l.subtitle:filter==='missing-dub'?!l.dubPack:filter==='done'?l.subtitle&&l.dubPack:true);return {total:items.length,offset,items:items.slice(offset,offset+limit)};},
  async line(id,lang){return get(id,lang);},
  async saveSubtitle(id,lang,lineId,text){writable(id);get(lineId,lang);if(text!==null&&(!text.trim()||[...text].length>2000))throw new LoaderError('bad_request','Use 1–2000 characters.');if(text===null)subtitles.delete(`${lang}/${lineId}`);else subtitles.set(`${lang}/${lineId}`,text);event('maker.subtitle.saved',{packId:id,lang,id:lineId,removed:text===null});return {live:true,path:`data/subtitles/${lang}.json`,durationSec:null,note:'Practice subtitle reloaded.'};},
  async saveVoice(id,lang,clip,wav){writable(id);if(!samples.some(l=>l.voice===clip))throw new LoaderError('not_found','Sample voice not found.');let durationSec=null;if(wav){const b=await wav.arrayBuffer(),v=new DataView(b);durationSec=(v.byteLength-44)/v.getUint32(28,true);if(durationSec>60||wav.size>16*1024*1024)throw new LoaderError('too_large','Dubs must fit within 16 MiB and 60 seconds.');voices.set(`${lang}/${clip}`,durationSec);}else voices.delete(`${lang}/${clip}`);event('maker.voice.saved',{packId:id,lang,clip,durationSec,removed:!wav});return {live:true,path:`audio/voice/${lang}/${clip}.wav`,durationSec,note:'Practice voice reloaded.'};},
  async play(id,variant,lang){const l=get(id,lang);if(!api.runtime.roomReady||api.connection!=='connected')throw new LoaderError('not_ready','The room is not ready.');if(!l.voice||variant==='dub'&&!l.dubPack)throw new LoaderError('not_found','No voice is available.');event('maker.play',{id,variant,lang,voice:l.voice});event('line.spoken',{voice:l.voice,text:variant==='dub'?l.subtitle:l.en,textJa:l.ja,language:lang,localizationId:id,source:'invented sample',scenarioGroupId:l.group,durationSec:l.durationSec});return {live:true,path:null,durationSec:l.durationSec,note:'Mock playback event; no game is connected.'};}
 });
 const watch=api.watch,close=api.close;let timer;
 api.watch=()=>{watch();timer=setTimeout(()=>event('line.spoken',{localizationId:samples[0].id,voice:samples[0].voice,text:samples[0].en,textJa:samples[0].ja,language:'en',source:'invented sample',scenarioGroupId:samples[0].group,durationSec:samples[0].durationSec}),500);};
 api.close=()=>{clearTimeout(timer);close();};
 return api;
}
