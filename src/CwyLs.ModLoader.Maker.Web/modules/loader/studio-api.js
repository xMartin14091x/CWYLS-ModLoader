// Studio v2 adapter. The transport owns auth, errors, cancellation and bounded retries.
export function studioApi(request) {
 const line=(packId,lang,id)=>({packId,lang,id});
 return {
  studioCapabilities:()=>request('studioCapabilities'),
  studioStatus:()=>request('studioStatus'),
  studioPacks:()=>request('studioPacks'),
  studioLines:query=>request('studioLines',query),
  studioLine:(packId,lang,id,options={})=>request('studioLine',{...line(packId,lang,id),...options}),
  studioGroups:(packId,lang)=>request('studioGroups',{packId,lang}),
  studioUpdateWorkflow:(packId,lang,id,mutation)=>request('studioWorkflow',line(packId,lang,id),mutation),
  studioSaveSubtitle:(packId,lang,id,mutation)=>request('studioSubtitle',line(packId,lang,id),mutation),
  studioHistory:(packId,lang,id,page={})=>request('studioHistory',{...line(packId,lang,id),...page}),
  studioRevertSubtitle:(packId,lang,id,revisionId,mutation)=>request('studioRevert',{...line(packId,lang,id),revisionId},mutation),
  studioGlossary:(packId,lang)=>request('studioGlossary',{packId,lang}),
  studioSaveGlossary:(packId,lang,mutation)=>request('studioSaveGlossary',{packId,lang},mutation),
  studioOperation:(packId,operationId)=>request('studioOperation',{packId,operationId}),
  studioRecovery:(packId,blockedOperationId)=>request('studioRecovery',{packId,blockedOperationId}),
  studioResolve:(packId,blockedOperationId,body)=>request('studioResolve',{packId,blockedOperationId},body),
  // M5d dubs slice (STUDIO-JS "M5d dubs availability"): takes, creator audio, favourite, publish and unpublish.
  studioTakes:(packId,lang,id)=>request('studioTakes',line(packId,lang,id)),
  studioCreateTake:(packId,lang,id,mutation)=>request('studioCreateTake',line(packId,lang,id),mutation),
  studioPutTakeAudio:(packId,lang,id,takeId,options,wav)=>request('studioTakeAudio',{...line(packId,lang,id),takeId,kind:options.kind,expectedRevision:options.expectedRevision,operationId:options.operationId},wav),
  studioGetTakeAudio:(packId,lang,id,takeId,kind)=>request('studioReadTakeAudio',{...line(packId,lang,id),takeId,kind}),
  studioUpdateTake:(packId,lang,id,takeId,mutation)=>request('studioTake',{...line(packId,lang,id),takeId},mutation),
  studioDeleteTake:(packId,lang,id,takeId,mutation)=>request('studioDeleteTake',{...line(packId,lang,id),takeId,expectedRevision:mutation.expectedRevision,operationId:mutation.operationId}),
  studioSetFavourite:(packId,lang,id,mutation)=>request('studioFavourite',line(packId,lang,id),mutation),
  studioPublish:(packId,lang,id,mutation)=>request('studioPublish',line(packId,lang,id),mutation),
  studioUnpublish:(packId,lang,id,mutation)=>request('studioUnpublish',line(packId,lang,id),mutation),
  studioCreatePack:body=>request('studioCreatePack',{},body), // W2c M6a
  studioPlay:body=>request('studioPlay',{},body),
  // Contract v2.3 (Phase C, `export`): pack info, icon (raw PNG) and export (check, export, zip download).
  studioPackInfo:packId=>request('studioPackInfo',{packId}),
  studioPackIcon:packId=>request('studioPackIcon',{packId}),
  studioSavePackIcon:(packId,pngBytes)=>request('studioSavePackIcon',{packId},pngBytes instanceof Blob?pngBytes:new Blob([pngBytes],{type:'image/png'})),
  studioExportCheck:packId=>request('studioExportCheck',{packId}),
  studioExport:(packId,body)=>request('studioExport',{packId},body),
  studioDownloadExport:(packId,fileName)=>request('studioDownloadExport',{packId,fileName}),
  // Contract v2.4 (Phase B1, `voiceDefault`): voice origin + consent once per pack. studioCreateTake may then omit
  // origin and consent together (the take inherits the default).
  studioVoiceDefault:packId=>request('studioVoiceDefault',{packId}),
  studioSaveVoiceDefault:(packId,{expectedRevision,operationId,origin,consent})=>request('studioSaveVoiceDefault',{packId},{expectedRevision,operationId,origin,consent}),
  studioLabelDubs:(packId,{expectedRevision,operationId,origin,consent})=>request('studioLabelDubs',{packId},{expectedRevision,operationId,origin,consent}),
  // Diagnostics (2026-11-09): the page's log, written to the game log while `maker trace on`.
  studioClientLog:entries=>request('studioClientLog',{},{entries})
 };
}
