// Take rules from W2's gate: at most 3 takes, a 4th is refused (never auto-evicted), a published take cannot be
// removed, the star is a favourite only, the selected take is what "Play my version" publishes, and publishing needs
// a confirmed consent statement for the take's current origin.
export const MAX_TAKES = 3;
/** The statement version the UI shows and sends (W2 gate: "consent-1"). */
export const CONSENT_VERSION = 'consent-1';
export const ORIGINS = Object.freeze(['own', 'ai-assisted', 'ai-generated']);

/** Can another take be created? `reason` is an i18n key when not. */
export function canAddTake(takes) {
  return takes.length < MAX_TAKES ? { ok: true } : { ok: false, reason: 'take_limit' };
}

/** Can this take be removed? A published take is not removable until it is unpublished. */
export function canRemoveTake(take, publishedTakeId) {
  return take.id === publishedTakeId ? { ok: false, reason: 'take_in_use' } : { ok: true };
}

/** The take shown as selected: the explicit choice, else the favourite, else the published one, else the first. */
export function selectedTake(takes, { selectedId = null, favouriteId = null, publishedId = null } = {}) {
  const by = (id) => (id ? takes.find((t) => t.id === id) || null : null);
  return by(selectedId) || by(favouriteId) || by(publishedId) || takes[0] || null;
}

/** Has the creator confirmed the current statement for this take's current origin? */
export function consentOk(take, version = CONSENT_VERSION) {
  return !!take && !!take.consent && take.consent.confirmed === true && take.consent.statementVersion === (version || CONSENT_VERSION);
}

/**
 * What must happen before "Play my version"? Without a take, a nonempty pack subtitle plays with the original voice.
 * @returns {{ok:boolean, needRender:boolean, reason?:string}} `needRender` means: upload the prepared WAV first
 * (not a failure). `reason` is an i18n key.
 */
export function publishPreflight(take, { packEditable = true, subtitle = '' } = {}) {
  if (!packEditable) return { ok: false, needRender: false, reason: 'dis_ro' };
  if (!take) return subtitle.trim()
    ? { ok: true, needRender: false, variant: 'sub' }
    : { ok: false, needRender: false, reason: 'mine_empty' };
  if (!take.sourcePresent) return { ok: false, needRender: false, reason: 'noTakes' };
  if (!consentOk(take)) return { ok: false, needRender: false, reason: 'need_consent' };
  return { ok: true, needRender: !take.renderReady };
}

/** The steps of "Play my version", in order. The first failure stops the sequence; there is no automatic retry of a POST. */
export function playMineSteps(take, { textDirty, needRender }) {
  const steps = [];
  if (textDirty) steps.push('save-subtitle');
  if (take && needRender) steps.push('upload-render');
  if (take && take.publishedHere !== true) steps.push('publish');
  steps.push('play');
  return steps;
}
