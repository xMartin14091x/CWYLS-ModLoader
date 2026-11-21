import {addStudioMock} from './studio-mock.js';
import {seed} from '../data.js';
import {diagnostics} from './events.js';
import {emptyState,LoaderError} from './contract.js';
const api=diagnostics('mock'),KEY='cwy.pack-maker.v1';
let db,sceneToken=0;
function database(){return db||=new Promise((resolve,reject)=>{const r=indexedDB.open('cwy.maker.files',1);r.onupgradeneeded=()=>r.result.createObjectStore('files');r.onsuccess=()=>resolve(r.result);r.onerror=()=>reject(new Error('Browser file storage is unavailable.'));});}
async function fileStore(mode,callback){const db=await database();return new Promise((resolve,reject)=>{const tx=db.transaction('files',mode),r=callback(tx.objectStore('files'));tx.oncomplete=()=>resolve(r.result);tx.onerror=()=>reject(new Error('Browser file storage is unavailable.'));});}
Object.assign(api,{
  async status(){return {connection:api.connection,gameVersion:'1.18.0',loaderVersion:'0.9.2',simulated:true};},
  async resetDemo(empty=false){return empty?emptyState():seed();},
  async loadState(){try{const s=JSON.parse(localStorage.getItem(KEY));if(s?.empty&&!s.pack)return emptyState();if(s?.pack?.id&&s.pack.name&&Array.isArray(s.pack.languages)&&s.pack.languages.length&&Array.isArray(s.lines)&&s.textures&&s.translations&&Array.isArray(s.scenes)&&Array.isArray(s.rules))return s;}catch{}return seed();},
  async saveState(state){try{const copy=structuredClone(state);for(const rule of Object.values(copy.textures)){delete rule.preview;}localStorage.setItem(KEY,JSON.stringify(copy));}catch{throw new Error('Local storage unavailable');}},
  async putFile(id,path,blob){await fileStore('readwrite',s=>s.put(blob,`${id}/${path}`));return {file:path,bytes:blob.size};},
  async getFile(id,path){const blob=await fileStore('readonly',s=>s.get(`${id}/${path}`));if(!blob)throw new LoaderError('not_found','This take belongs to an earlier session. Please import or record it again.');return blob;},
  connect(value){api.setConnection(value);api.emit('CONNECTION',value==='offline'?'Game not running · recording-only mode':value==='reloading'?'Reloading pack…':'Connected · mock game 1.18.0 · loader 0.9.2');if(value==='reloading')setTimeout(()=>{if(api.connection==='reloading')api.connect('connected');},1800);},
  async say({line,language}){if(api.connection!=='connected')throw new Error('Game not running. Your work is saved; recording still works.');api.emit('SATONE',`say "${line.text[language]||Object.values(line.text).find(Boolean)}" → Spoken`);return {ok:true};},
  async applyTexture(){if(api.connection==='connected')api.emit('TEXTURE','1 rule applied  failed=0 trigger=reload mode=Shared files=1');return {ok:true};},
  async playScene({scene}){
    if(api.connection!=='connected')throw new Error('Game not running. Your work is saved; recording still works.');
    const s=structuredClone(scene),token=++sceneToken,flags={};let i=0,steps=0;
    api.emit('SCENE',`${s.name} → started (${s.rows.length} rows)`);
    const tick=()=>{if(token!==sceneToken||api.connection!=='connected')return;if(i>=s.rows.length){api.emit('SCENE',`${s.name} → Finished`);return;}if(++steps>60){api.emit('SCENE','Stopped: jump loop limit reached',{level:'warn'});return;}const row=s.rows[i];api.emit('SCENE',`${s.name} row ${i+1}: ${row.type} ${row.value}`,{toast:false});let delay=450;if(row.type==='Say')api.say({line:{text:{EN:row.value}},language:'EN'});if(row.type==='Wait')delay=Math.min(10000,Math.max(0,Number(row.value)||0)*1000);if(row.type==='Set flag'){const [k,v]=row.value.split('=');flags[k.trim()]=v?.trim()||true;}if(row.type==='Jump'){const dest=parseInt(row.value,10);if(dest>=1&&dest<=s.rows.length)i=dest-2;else api.emit('SCENE',`Invalid jump target: ${row.value}`,{toast:false,level:'warn'});}i++;setTimeout(tick,delay);};tick();return {ok:true};
  },
  watch(){setTimeout(()=>api.emit('MAKER','Local studio ready · mock event stream connected',{toast:false}),120);},
  close(){sceneToken++;}
});
export default addStudioMock(api);
