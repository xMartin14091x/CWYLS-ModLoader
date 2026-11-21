// Autosave state machine for one subtitle (one instance per `packId/lang/lineId` key). Pure: it returns the next
// state and a list of effects the caller performs (arm a timer, send a save, ask the server, ...). Nothing here
// touches the network, the clock or the DOM, so every race can be tested.
//
// Rules (PLAN §1.5): one save in flight per key; newer text is coalesced; a stale revision is a conflict and the
// creator's draft is never overwritten; an uncertain failure (timeout, dropped connection) is resolved by asking
// the server about the operation before any retry; the same operationId is reused for a retry of the same text.

export const DEBOUNCE_MS = 800;
const PERMANENT = new Set(['read_only_pack', 'restart_required', 'storage_limit', 'recovery_required', 'bad_request',
  'consent_required', 'not_found', 'unauthorized']);

/**
 * @typedef {Object} Draft
 * @property {string} key
 * @property {string} text what the editor holds now
 * @property {string} baseText the last text the server is known to hold
 * @property {string|null} baseRevision
 * @property {'saved'|'typing'|'saving'|'conflict'|'failed'} status
 * @property {{opId:string,text:string}|null} inflight
 * @property {{text:string,revision:string}|null} theirs the server's version while in conflict
 * @property {string|null} error an error code when status is failed
 * @property {number} retries
 */

/** @returns {Draft} */
export function createDraft(key, text, revision) {
  return { key, text, baseText: text, baseRevision: revision, status: 'saved', inflight: null, theirs: null, error: null, retries: 0 };
}

const same = (a, b) => (a ?? '') === (b ?? '');
const isDirty = (d) => !same(d.text, d.baseText);

/**
 * @param {Draft} d
 * @param {object} e one of: {type:'edit',text} {type:'flush',opId} {type:'saved',opId,revision}
 *   {type:'failed',opId,code,uncertain} {type:'operation',opId,saved,revision} {type:'server',text,revision}
 *   {type:'resolve',choice:'mine'|'theirs'} {type:'retry',opId} {type:'revision',revision}
 * @returns {{state:Draft, effects:Array<object>}}
 */
export function reduce(d, e) {
  switch (e.type) {
    case 'edit': {
      if (same(e.text, d.text)) return { state: d, effects: [] };
      const next = { ...d, text: e.text, error: d.status === 'failed' ? null : d.error };
      if (d.status === 'conflict') return { state: next, effects: [] };
      if (!isDirty(next) && !d.inflight) return { state: { ...next, status: 'saved', retries: 0 }, effects: [{ type: 'cancel-timer' }] };
      return { state: { ...next, status: d.inflight ? 'saving' : 'typing' }, effects: d.inflight ? [] : [{ type: 'arm-timer', ms: DEBOUNCE_MS }] };
    }
    case 'flush': {
      if (d.status === 'conflict' || d.inflight || !isDirty(d)) return { state: d, effects: [] };
      return startSave(d, e.opId);
    }
    case 'retry': {
      if (d.status === 'conflict' || d.inflight || !isDirty(d)) return { state: d, effects: [] };
      return startSave(d, e.opId);
    }
    case 'revision': {
      // Another mutation of the same line (status, star, history revert) moved the server revision: later saves must expect it.
      if (d.status === 'conflict' || !e.revision || e.revision === d.baseRevision) return { state: d, effects: [] };
      return { state: { ...d, baseRevision: e.revision }, effects: [] };
    }
    case 'saved': {
      if (!d.inflight || d.inflight.opId !== e.opId) return { state: d, effects: [] };
      const next = { ...d, baseText: d.inflight.text, baseRevision: e.revision, inflight: null, error: null, retries: 0 };
      if (isDirty(next)) return { state: { ...next, status: 'typing' }, effects: [{ type: 'arm-timer', ms: 0 }] };
      return { state: { ...next, status: 'saved' }, effects: [] };
    }
    case 'failed': {
      if (!d.inflight || d.inflight.opId !== e.opId) return { state: d, effects: [] };
      if (e.code === 'conflict') {
        return { state: { ...d, inflight: null, status: 'conflict', error: 'conflict' }, effects: [{ type: 'fetch-line' }] };
      }
      if (e.uncertain) return { state: { ...d, status: 'saving' }, effects: [{ type: 'check-operation', opId: e.opId }] };
      const next = { ...d, inflight: null, status: 'failed', error: e.code || 'internal' };
      if (PERMANENT.has(e.code)) return { state: next, effects: [] };
      const retries = d.retries + 1;
      return { state: { ...next, retries }, effects: [{ type: 'arm-retry', ms: Math.min(15000, 1000 * 2 ** Math.min(retries, 4)) }] };
    }
    case 'operation': {
      // The server's answer to "did operation X commit?" after an uncertain failure.
      if (!d.inflight || d.inflight.opId !== e.opId) return { state: d, effects: [] };
      if (e.saved) return reduce(d, { type: 'saved', opId: e.opId, revision: e.revision });
      // Not committed: the same operationId may be sent again with the same text.
      return { state: { ...d, status: 'saving' }, effects: [{ type: 'save', opId: e.opId, text: d.inflight.text, expectedRevision: d.baseRevision }] };
    }
    case 'server': {
      // A refetched version of the line (conflict, event from another tab, resync).
      if (d.status === 'conflict') {
        if (same(e.text, d.text)) return { state: { ...d, baseText: e.text, baseRevision: e.revision, status: 'saved', theirs: null, error: null }, effects: [] };
        return { state: { ...d, theirs: { text: e.text, revision: e.revision } }, effects: [] };
      }
      if (!isDirty(d) && !d.inflight) return { state: { ...d, text: e.text, baseText: e.text, baseRevision: e.revision, status: 'saved' }, effects: [] };
      if (d.inflight) return { state: d, effects: [] }; // our own save is out: its answer decides (a stale revision becomes a conflict)
      // dirty and idle: the pack's text moved on while we were typing
      if (same(e.text, d.text)) return { state: { ...d, baseText: e.text, baseRevision: e.revision, status: 'saved', error: null }, effects: [{ type: 'cancel-timer' }] };
      if (same(e.text, d.baseText)) return { state: { ...d, baseRevision: e.revision }, effects: [] }; // only the revision moved (status, star, ...)
      return { state: { ...d, status: 'conflict', theirs: { text: e.text, revision: e.revision }, error: 'conflict' }, effects: [{ type: 'cancel-timer' }] };
    }
    case 'resolve': {
      if (d.status !== 'conflict' || !d.theirs) return { state: d, effects: [] };
      if (e.choice === 'theirs') {
        return { state: { ...d, text: d.theirs.text, baseText: d.theirs.text, baseRevision: d.theirs.revision, theirs: null, status: 'saved', error: null }, effects: [] };
      }
      if (same(d.text, d.theirs.text)) {
        // Keep mine, but the creator retyped the other writer's exact text: nothing to save, so end clean at their revision.
        return { state: { ...d, baseText: d.theirs.text, baseRevision: d.theirs.revision, theirs: null, status: 'saved', error: null }, effects: [{ type: 'cancel-timer' }] };
      }
      return {
        state: { ...d, baseText: d.theirs.text, baseRevision: d.theirs.revision, theirs: null, status: 'typing', error: null },
        effects: [{ type: 'arm-timer', ms: 0 }]
      };
    }
    default:
      return { state: d, effects: [] };
  }
}

function startSave(d, opId) {
  const inflight = { opId, text: d.text };
  return {
    state: { ...d, inflight, status: 'saving', error: null },
    // empty text is a removal (null), as in W2's contract
    effects: [{ type: 'save', opId, text: d.text, expectedRevision: d.baseRevision }]
  };
}

/** The text to send for a save effect: null removes the subtitle. */
export const wireText = (text) => (text && text.length ? text : null);
