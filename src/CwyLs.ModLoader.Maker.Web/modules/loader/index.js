import {config} from '../../config.js';
const backend=new URLSearchParams(location.search).get('backend')||config.backend;
if(!['mock','http'].includes(backend))throw new Error('Unknown Pack Maker backend. Choose mock or http.');
/** @type {import('./contract.js').Loader} */
const loader=(await import(backend==='http'?'./http.js':'./mock.js')).default;
// Pack-id edits and Undo must carry the original media into the selected folder.
// Paths are immutable (UUID takes/textures), so ownership can be tracked safely.
const owners=new Map();
const files=state=>[...state.lines.flatMap(l=>Object.values(l.takes||{}).flat().map(t=>t.file)),...Object.values(state.textures).map(t=>t.file)].filter(Boolean);
const load=loader.loadState.bind(loader),save=loader.saveState.bind(loader),put=loader.putFile.bind(loader);
loader.loadState=async()=>{const state=await load();if(state.pack)for(const path of files(state))owners.set(path,state.pack.id);return state;};
loader.putFile=async(id,path,blob)=>{const result=await put(id,path,blob);owners.set(path,id);return result;};
loader.saveState=async state=>{
  if(state.pack)for(const path of new Set(files(state))){const owner=owners.get(path);if(owner&&owner!==state.pack.id)await loader.putFile(state.pack.id,path,await loader.getFile(owner,path));}
  await save(state);
};
export default loader;
