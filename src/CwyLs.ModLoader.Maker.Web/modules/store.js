import loader from './loader/index.js';
import {emptyState} from './loader/contract.js';
// Outside the adapter, localStorage is used ONLY for UI preferences.
export function readLocal(key) { try { return localStorage.getItem(key); } catch { return null; } }
export function writeLocal(key,value) { try { localStorage.setItem(key,value); return true; } catch { return false; } }
export let state=emptyState(),bootError=null;
try {state=await loader.loadState();await loader.status();} catch(error){bootError=error;}
const past=[],future=[];
let queue=Promise.resolve(),revision=0,lastError=bootError;
export let storageOK=!bootError,saveStatus=bootError?'error':'saved';
function announce(){loader.bus.dispatchEvent(new Event('persistence'));}
export function persist() {
 const snapshot=structuredClone(state),rev=++revision;
 for(const rule of Object.values(snapshot.textures))delete rule.preview;
 saveStatus='saving';storageOK=false;announce();
 queue=queue.then(async()=>{
  try{await loader.saveState(snapshot);if(rev===revision){storageOK=true;saveStatus='saved';lastError=null;}}
  catch(error){lastError=error;if(rev===revision){storageOK=false;saveStatus='error';}loader.emit('SAVE',error.message,{level:'warn'});}
  announce();
 });
 return queue;
}
export async function flush(){await queue;if(lastError)throw lastError;}
export function edit(fn) {if(bootError)throw bootError;past.push(structuredClone(state));if(past.length>75)past.shift();future.length=0;fn(state);persist();}
export function history(dir) {const from=dir==='undo'?past:future,to=dir==='undo'?future:past;if(!from.length)return false;to.push(structuredClone(state));state=from.pop();persist();return true;}
export const canUndo=()=>past.length>0;
export const canRedo=()=>future.length>0;
export async function reset(empty=false) {if(loader.mode!=='mock')return;const fresh=await loader.resetDemo(empty);past.length=0;future.length=0;state=fresh;bootError=null;persist();}
export async function resync(){
 await queue;
 if(saveStatus!=='saved'){loader.emit('SYNC','Unsaved edits kept. Retry saving before refreshing from the loader.',{level:'warn'});return;}
 const before=revision;
 try{const fresh=await loader.loadState();await loader.status();if(before!==revision){loader.emit('SYNC','Edits made during refresh were kept.',{toast:false});return;}state=fresh;past.length=0;future.length=0;loader.bus.dispatchEvent(new Event('state'));}
 catch(error){loader.emit('SYNC',error.message,{level:'warn'});}
}
export function problems() {
 const p=[]; if(!state.pack) return ['Create a pack to get started.'];
 if(!/^[a-z0-9]+(?:[._-][a-z0-9]+)*\.[a-z0-9]+(?:[._-][a-z0-9]+)*$/.test(state.pack.id))p.push('Pack ID must use author.name, with lowercase letters and no spaces.');
 if(!/^\d+\.\d+\.\d+$/.test(state.pack.version))p.push('Version must have three numbers, such as 0.1.0.');
 if(!state.pack.name.trim())p.push('Give your pack a name.');
 for(const l of state.lines)for(const lang of state.pack.languages)if(!l.text[lang]?.trim())p.push(`${l.id}: ${lang} text is missing.`);
 for(const pool of new Set(state.lines.map(l=>l.pool))){if(state.lines.filter(l=>l.pool===pool).reduce((s,l)=>s+l.weight,0)>1.00001)p.push(`${pool}: weights add up to more than 1.`);}
 return p;
}
export const manifest=()=>({id:state.pack?.id,name:state.pack?.name,version:state.pack?.version,author:state.pack?.author,description:state.pack?.description,languages:state.pack?.languages,unofficial:true,lines:'lines.json',textures:Object.entries(state.textures).map(([material,r])=>({material,tint:r.tint,color:r.color,file:r.file||null,mode:'Shared'})),scenes:state.scenes,translations:state.translations});
export const lineFile=()=>({pack:state.pack?.id,lines:state.lines.map(l=>({...l,takes:undefined,voice:Object.fromEntries(Object.entries(l.takes||{}).map(([lang,takes])=>[lang,takes.filter(t=>t.used&&t.file).map(t=>({file:t.file,trimStart:t.start,trimEnd:t.end}))]))}))});
