/** @typedef {{id:string,name:string,file?:string,duration:number,peaks:number[],start:number,end:number,used:boolean}} VoiceTake */
/** @typedef {{id:string,pool:string,text:Record<string,string>,weight:number,expression:string,motion:string,takes:Record<string,VoiceTake[]>}} Line */
/** @typedef {{id:string,name:string,author:string,version:string,description:string,languages:string[]}} Manifest */
/** @typedef {{file?:string,color?:string,tint?:string,applied?:boolean,preview?:string}} TextureRule */
/** @typedef {{type:string,value:string}} SceneRow */
/** @typedef {{id:string,name:string,rows:SceneRow[]}} Scene */
/** @typedef {{pack:Manifest|null,lines:Line[],textures:Record<string,TextureRule>,translations:Record<string,Record<string,string>>,scenes:Scene[],rules:boolean[],empty?:boolean}} PackState */
/**
 * Pack Maker's only loader/game boundary. Studio routes follow DS-00 (API.md).
 * loadState/saveState and media helpers below are legacy mock-only workspaces.
 * Studio methods: packs(), lines(query), line(id,lang), saveSubtitle(packId,lang,id,text|null),
 * saveVoice(packId,lang,clip,Blob|null), play(id,variant,lang), status(), studioCapabilities(), studioStatus(), watch(), close().
 * No game media read exists. Events arrive as maker-event CustomEvents on bus.
 * Methods reject with LoaderError {code,message,retryAfter}; never silently use mock.
 * PackState = {pack: Manifest|null, lines: Line[], textures: Object,
 * translations: Object, scenes: Scene[], rules: boolean[], empty?: boolean}.
 * Takes contain file paths, duration, peaks, trim start/end and used; no binary data.
 * UI owns an editable working copy; saveState resolves only after disk acknowledgement.
 *
 * @typedef {Object} Loader
 * @property {'mock'|'http'} mode
 * @property {EventTarget} bus - log, connection, resync, persistence events.
 * @property {Array<Object>} logs - bounded, newest first; never includes auth headers.
 * @property {string} connection - connected, reloading, offline.
 * @property {function(): Promise<PackState>} loadState - Read active pack from backend.
 * @property {function(PackState): Promise<void>} saveState - Save all sections, then select pack.
 *   Sections are separate JSON requests, each <=64 KiB; raw files upload first.
 * @property {function(string,string,Blob): Promise<Object>} putFile - Pack id, safe relative path, WAV/PNG.
 * @property {function(string,string): Promise<Blob>} getFile - Authenticated raw file read.
 * @property {function(): Promise<Object>} status - Runtime status (no pack storage).
 * @property {function(): Promise<Object>} studioCapabilities - Studio v2 features actually installed by the loader.
 * @property {function(): Promise<Object>} studioStatus - Cached focus, pause, busy, language and room facts.
 * @property {function(Object): Promise<Object>} say - {packId,line,language}; line is an editable preview.
 * @property {function(Object): Promise<Object>} playScene - {packId,scene}.
 * @property {function(Object): Promise<Object>} applyTexture - {packId,material}.
 * @property {function(): void} watch - Start one cancellable long-poll loop; resync on reset/dropped/epoch change.
 * @property {function(): void} close - Abort polling and all requests; stop retries.
 * @property {function(string,string,Object=): Object} emit - UI diagnostic log only, no network.
 * @property {function(string,string): void} save - UI diagnostic log only, no persistence claim.
 * @property {function(string): void} connect - Mock simulation only; HTTP rejects simulation.
 * @property {function(boolean=): Promise<PackState>} resetDemo - Mock seed/welcome data; HTTP rejects simulation.
 */
export const emptyState = () => ({pack:null,lines:[],textures:{},translations:{},scenes:[],rules:[false,false,false,false],empty:true});

export class LoaderError extends Error {
  constructor(code, message, retryAfter=0, details=null) { super(message); this.name='LoaderError'; this.code=code; this.retryAfter=retryAfter; this.details=details; }
}

// v2.1: existing projects and new-pack creation are independent capabilities.
export const studioFeatures=Object.freeze({
 catalog:'catalog',workflow:'workflow',subtitles:'subtitles',dubs:'dubs',
 projects:'projects',playback:'playback',newPacks:'newPacks',packInfo:'packInfo',export:'export',voiceDefault:'voiceDefault'
});
export const studioGroupLabels=Object.freeze({
 morning:'group.morning',focus:'group.focus',break:'group.break',rain:'group.rain',
 clickReaction:'group.clickReaction',evening:'group.evening',ungrouped:'group.ungrouped'
});
export const friendlyErrors = {
  bad_request:'The loader could not save these details. Check the pack ID and fields.',
  unauthorized:'Pack Maker lost its connection; reopen it from the game',
  not_found:'That pack or file is no longer available. Reopen Pack Maker to refresh it.',
  too_large:'This item is too large. JSON details must fit within 64 KiB; try a shorter item.',
  unsupported_media_type:'Choose a WAV recording (PCM 16-bit, 8–48 kHz).',
  read_only_pack:'This pack is read-only. Choose an editable content pack.',
  not_ready:'The room is not ready for playback. Wait for the game to load.',
  conflict:'This project changed in another tab or tool. Reload it before saving again.',
  consent_required:'Confirm the current voice origin and permission statement before publishing.',
  take_limit:'This line already has three takes. Remove one before adding another.',
  take_in_use:'Unpublish or replace this take before removing it.',
  render_required:'Prepare this take before publishing it.',
  restart_required:'Restart the game once to load this new pack.',
  game_paused:'Click the game window to continue.',
  storage_limit:'This Pack Maker project has reached its storage limit.',
  metadata_unavailable:'The creator project metadata cannot be read safely.',
  recovery_required:'This change needs recovery before Pack Maker can edit it again.',
  request_timeout:'The request did not arrive in time. Refetch the line and status before retrying.',
  uncertain:'The loader returned no mutation receipt. Refetch the line and status before retrying.',
  forbidden_origin:'Open Pack Maker from the game on this computer.',
  host_refused:'Open Pack Maker on localhost or 127.0.0.1.',
  rate_limited:'The loader is busy. Please wait before trying again.',
  internal:'The loader could not finish this change. Your unsaved edits are still here.',
  unavailable:'The loader is unavailable. Your unsaved edits are still here. Try again when it returns.'
};
