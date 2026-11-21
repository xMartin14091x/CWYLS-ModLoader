// The only file that knows wire names: W2's CONTRACT-v2.md (approved, binding; main checkout research/2026-11-08/w2-backend/)
// and its JS adapter list STUDIO-JS.md. DTO -> view-model mappers and the error-code table. A rename costs one edit here.
// Shapes the contract leaves open are marked OPEN/ASSUMPTION and listed in the milestone report.

export class StudioError extends Error {
  /** @param {string} code a W2 error code (or `unavailable` / `unauthorized` for transport failures) */
  constructor(code, message, { status = 0, retryAfter = 0 } = {}) {
    super(message || code);
    this.name = 'StudioError';
    this.code = code;
    this.status = status;
    this.retryAfter = retryAfter;
  }
}

const KNOWN_CODES = new Set(['bad_request', 'unauthorized', 'read_only_pack', 'consent_required', 'not_found', 'conflict',
  'take_limit', 'take_in_use', 'render_required', 'restart_required', 'game_paused', 'not_ready', 'too_large',
  'storage_limit', 'unsupported_media_type', 'unavailable', 'metadata_unavailable', 'recovery_required', 'rate_limited', 'export_blocked', 'internal']);

/** i18n key for an error code (`e_<code>`), or `e_internal` for anything unknown. */
export const errorKey = (code) => (KNOWN_CODES.has(code) ? `e_${code}` : 'e_internal');

/** Errors that mean "the write may or may not have happened": reconcile through the operation route before retrying. */
export const isUncertain = (err) => err instanceof StudioError && (err.code === 'unavailable' || err.code === 'internal' || err.status === 0);

/**
 * Normalizes `LoaderError` from `modules/loader/http.js` (which lacks `.status` and uses `code: 'uncertain'` for lost mutation
 * replies) into `StudioError` so `actions.js` and `dub.js` can branch on `instanceof StudioError` and `isUncertain`.
 */
export function toStudioError(err, connection = 'connected') {
  if (err instanceof StudioError) return err;
  if (err && typeof err.code === 'string') {
    const raw = err.code, code = raw === 'uncertain' ? 'unavailable' : raw;
    const status = raw === 'uncertain' || (code === 'unavailable' && connection === 'offline') ? 0 : code === 'unauthorized' ? 401 : (err.status ?? 400);
    return new StudioError(code, err.message, { status, retryAfter: err.retryAfter || 0 });
  }
  return new StudioError('unavailable', err?.message || 'request failed', { status: 0 });
}

/** W2's fake loader requires `consent.statementVersion` even when `confirmed` is `false` on take creation/update. */
export function takeMutationBody(body, consentVersion = 'consent-1') {
  if (!body || !body.consent || body.consent.statementVersion) return body;
  return { ...body, consent: { ...body.consent, statementVersion: consentVersion || 'consent-1' } };
}

/** @returns {{gameVersion:string,loaderVersion:string,gameLanguage:string,roomReady:boolean,focused:boolean|null,paused:boolean|null,busy:boolean|null,stale:boolean,reason:string|null,observedAt:string|null,focusOnPlay:boolean}} */
export function statusVM(dto) {
  return {
    gameVersion: dto.gameVersion || '', loaderVersion: dto.loaderVersion || '',
    gameLanguage: dto.gameLanguage || 'en', roomReady: dto.roomReady !== false,
    focused: dto.focused ?? null, paused: dto.paused ?? null, busy: dto.busy ?? null,
    stale: dto.stale === true, reason: dto.reason ?? null, observedAt: dto.observedAt ?? null, focusOnPlay: dto.focusOnPlay !== false
  };
}

/**
 * The link indicator. `null`/missing focus facts mean unknown, never "paused" (W2 §4 honest game state).
 * @returns {'ok'|'paused'|'unfocused'|'stale'|'off'|'unauthorized'}
 */
export function linkState(status, transport) {
  if (transport?.unauthorized) return 'unauthorized';
  if (!transport?.connected || !status) return 'off';
  if (status.stale) return 'stale';
  if (status.paused === true) return 'paused';
  if (status.focused === false) return 'unfocused';
  return 'ok';
}

const RO_KEYS = { reference: 'ro_ref', code: 'ro_code', failed: 'ro_failed', pending: 'ro_pending' };

/**
 * CONTRACT-v2 `StudioPack`: `{id,name,version,languages[],editable,readOnlyReason,loaded,restartRequired,studioEditable,
 * studioReadOnlyReason,metadataState}`. OPEN: the *format* of the two reason fields is not specified (code or sentence?).
 * ASSUMPTION: a short code (`reference`, `code`, `failed`, `pending`) maps to a UI string; any other text is shown as-is.
 */
export function packVM(dto) {
  const reason = dto.studioReadOnlyReason ?? dto.readOnlyReason ?? null;
  return {
    id: dto.id, name: dto.name || dto.id, version: dto.version || '', languages: dto.languages || [],
    loaded: dto.loaded !== false, restartRequired: dto.restartRequired === true,
    editable: dto.studioEditable === true,
    readOnlyKey: reason ? (RO_KEYS[reason] || null) : null,
    readOnlyText: reason && !RO_KEYS[reason] ? String(reason) : null,
    selectable: !(dto.languages && dto.languages.length === 0)
  };
}

export const progressVM = (p) => ({
  totalLines: p?.totalLines ?? 0, dubbableLines: p?.dubbableLines ?? 0, subtitlePresent: p?.subtitlePresent ?? 0,
  dubPresent: p?.dubPresent ?? 0, todo: p?.todo ?? 0, draft: p?.draft ?? 0, done: p?.done ?? 0,
  needsRetake: p?.needsRetake ?? 0, starred: p?.starred ?? 0
});

/** Zeroed owned progress for a pending (`restartRequired: true`, `loaded: false`) pack whose per-pack routes return 409 on W2's fake. */
export const pendingProgressVM = (p) => ({
  totalLines: p?.totalLines ?? 0, dubbableLines: p?.dubbableLines ?? 0, subtitlePresent: 0,
  dubPresent: 0, todo: p?.totalLines ?? 0, draft: 0, done: 0, needsRetake: 0, starred: 0
});

/** CONTRACT-v2 `Groups.groups[]`: `{id,labelKey,title,order,progress}`. OPEN: what `labelKey` values look like; unknown keys fall back to `title`. */
export const groupVM = (g) => ({ id: g.id, labelKey: g.labelKey ?? null, title: g.title || g.id, order: g.order ?? 0, progress: progressVM(g.progress) });
export const pendingGroupVM = (g) => ({ id: g.id, labelKey: g.labelKey ?? null, title: g.title || g.id, order: g.order ?? 0, progress: pendingProgressVM(g.progress) });

/** CONTRACT-v2 `Take`. */
export const takeVM = (t) => ({
  id: t.id, origin: t.origin || 'own',
  consent: { confirmed: t.consent?.confirmed === true, statementVersion: t.consent?.statementVersion ?? null, confirmedAt: t.consent?.confirmedAt ?? null },
  consentSource: t.consentSource === 'pack' ? 'pack' : 'take',
  sourcePresent: t.sourcePresent === true, renderReady: t.renderReady === true,
  sourceDurationSec: t.sourceDurationSec ?? null, renderDurationSec: t.renderDurationSec ?? null,
  trimStartSec: t.trimStartSec ?? 0, trimEndSec: t.trimEndSec ?? null, evenOutVolume: t.evenOutVolume === true,
  createdAt: t.createdAt ?? null
});

/** CONTRACT-v2.4 `VoiceDefault`: `{revision,origin,consent,updatedAt}`. */
export const voiceDefaultVM = (dto) => ({
  revision: dto?.revision ?? null,
  origin: dto?.origin ?? null,
  consent: dto?.consent ? {
    confirmed: dto.consent.confirmed === true,
    statementVersion: dto.consent.statementVersion ?? null,
    confirmedAt: dto.consent.confirmedAt ?? null
  } : null,
  updatedAt: dto?.updatedAt ?? null
});

/** CONTRACT-v2 `TakePage`: `{revision,clipRevision,favouriteTakeId,publishedTakeId,items:[Take]}`. */
export const takesVM = (p) => ({ revision: p.revision ?? null, clipRevision: p.clipRevision ?? null, favouriteTakeId: p.favouriteTakeId ?? null, publishedTakeId: p.publishedTakeId ?? null, items: (p.items || []).map(takeVM) });

/**
 * CONTRACT-v2 `StudioLine` (list item and detail): `{id,voice,group,durationSec,ja,en,subtitle,subtitlePack,dubPack,dubDurationSec,
 * ownedSubtitle,ownedDub,workflow,takeCount,revision,clipRevision,publishedTakeId,publishedTakeRevision,context,readOnly}`.
 * readOnly: why Studio cannot change this line in any pack (code `id_not_a_file_name`), or null.
 * OPEN: the type of `ownedSubtitle` (ASSUMED string|null) and `ownedDub` (ASSUMED boolean). The takes themselves and the favourite are
 * NOT part of a line: they come from `studioTakes`, and are merged into the selected line by actions.js.
 */
export function lineVM(dto) {
  const ctx = dto.context ? contextVM(dto.context) : null;
  const talker = dto.talker ?? ctx?.occurrences?.[0]?.talker ?? (dto.voice ? 'Satone' : null);
  return {
    id: dto.id, voice: dto.voice ?? null, group: dto.group ?? null, talker,
    durationSec: dto.durationSec ?? null, dubDurationSec: dto.dubDurationSec ?? null,
    ja: dto.ja || '', en: dto.en || '',
    text: dto.ownedSubtitle || '', effectiveText: dto.subtitle || '', effectivePack: dto.subtitlePack ?? null,
    ownedDub: !!dto.ownedDub, dubPack: dto.dubPack ?? null,
    status: dto.workflow?.status || 'todo', starred: dto.workflow?.starred === true,
    takes: [], takeCount: dto.takeCount ?? 0, publishedTakeId: dto.publishedTakeId ?? null, favouriteTakeId: null,
    revision: dto.revision ?? null, clipRevision: dto.clipRevision ?? null,
    context: ctx, readOnly: dto.readOnly ?? null
  };
}

/** Projects a loaded pack's catalogue line into a newly created pending (`restartRequired: true`) pack with no owned work yet. */
export const pendingLineVM = (dto) => ({
  ...lineVM(dto), text: '', ownedDub: false, status: 'todo', starred: false,
  takes: [], takeCount: 0, publishedTakeId: null, favouriteTakeId: null
});

/** Context with mood/pose nullable (W2: null unless a mapping was reviewed); hidden chips when null. */
export function contextVM(c) {
  return {
    occurrences: (c.occurrences || []).map((o) => ({ id: o.occurrenceId, groupId: o.groupId, order: o.order, previousId: o.previousId ?? null, nextId: o.nextId ?? null, talker: o.talker ?? null })),
    selectedOccurrenceId: c.selectedOccurrenceId ?? null, mood: c.mood ?? null, pose: c.pose ?? null
  };
}

export const historyVM = (h) => ({
  revision: h.revision, total: h.total ?? (h.items || []).length, gap: h.historyGap === true,
  items: (h.items || []).map((i) => ({ id: i.revisionId, at: Date.parse(i.createdAt) || 0, text: i.text, source: i.source || 'maker' }))
});

/** CONTRACT-v2.3 `PackInfo`: `{id,name,version,author,description,languages[],hasIcon,sizeBytes,pending,changelog}`. */
export const packInfoVM = (dto) => ({
  id: dto.id, name: dto.name || dto.id, version: dto.version || '0.1.0',
  author: dto.author ?? null, description: dto.description ?? null,
  languages: dto.languages || [], hasIcon: dto.hasIcon === true,
  sizeBytes: dto.sizeBytes ?? 0, pending: dto.pending === true,
  changelog: dto.changelog ? { version: dto.changelog.version || '', notes: dto.changelog.notes || '' } : null
});

/** CONTRACT-v2.3 `ExportPreview`: `{packId,version,nextVersions,ok,errorCount,warningCount,checks[],files,changelog,lastExport}`; each check carries `code` + `params` (TD-13). */
export const exportPreviewVM = (dto) => ({
  packId: dto.packId, version: dto.version || '0.1.0',
  nextVersions: { patch: dto.nextVersions?.patch ?? null, minor: dto.nextVersions?.minor ?? null, major: dto.nextVersions?.major ?? null },
  ok: dto.ok === true, errorCount: dto.errorCount ?? 0, warningCount: dto.warningCount ?? 0,
  checks: (dto.checks || []).map((c) => ({ id: c.id, severity: c.severity || 'ok', message: c.message || '', count: c.count ?? 0, paths: c.paths || [], code: c.code || null, params: c.params || {} })),
  files: { included: dto.files?.included ?? 0, includedBytes: dto.files?.includedBytes ?? 0, excluded: dto.files?.excluded ?? 0, excludedBytes: dto.files?.excludedBytes ?? 0 },
  changelog: dto.changelog ? { version: dto.changelog.version || '', notes: dto.changelog.notes || '' } : null,
  lastExport: dto.lastExport ? { fileName: dto.lastExport.fileName, version: dto.lastExport.version, sizeBytes: dto.lastExport.sizeBytes ?? 0, createdAt: dto.lastExport.createdAt ?? null, folder: dto.lastExport.folder || null } : null
});

/** The loader serves export zips up to 256 MiB to the browser (MakerPackChecks.MaxDownloadBytes). */
export const MAX_BROWSER_ZIP = 256 * 1024 * 1024;

/** Where an export zip is saved: the loader's folder, or the default layout when an older loader does not say. */
export const exportFolder = (dl, packId) => dl?.folder || `CWYLS Mods/.maker-exports/${packId}/`;

/** CONTRACT-v2.3 `ExportResult`: `{packId,version,previousVersion,fileName,sizeBytes,sha256,files,downloadable,manifestUpdated,changelogUpdated,warningCount,note,folder?}`. */
export const exportResultVM = (dto) => ({
  packId: dto.packId, version: dto.version || '', previousVersion: dto.previousVersion || '',
  fileName: dto.fileName || '', sizeBytes: dto.sizeBytes ?? 0, sha256: dto.sha256 || '',
  files: dto.files ?? 0, downloadable: dto.downloadable !== false,
  manifestUpdated: dto.manifestUpdated === true, changelogUpdated: dto.changelogUpdated === true,
  warningCount: dto.warningCount ?? 0, note: dto.note || '', folder: dto.folder || null
});

/** Drops undefined members: CONTRACT-v2 says optional query parameters are omitted, never sent as "null"/"undefined". */
export const omitUndefined = (o) => Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined && v !== null));
