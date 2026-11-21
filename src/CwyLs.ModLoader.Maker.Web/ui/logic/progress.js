// Filters, status dots and counts over the whole catalogue (loaded once; the Warnings chip needs every line).
// A "line" here is the view-model produced by api/model.js:
//   {id, group, durationSec, ja, en, text, status, starred, takeCount, publishedTakeId}
// `text` is the creator's own subtitle in the selected pack ('' when none), `status` is W2's one workflow status.

/** Subtitle dot: no own subtitle means to do; with text, `done` stays done and everything else is a draft. */
export function subDot(line) {
  if (!line.text || !line.text.trim()) return 'todo';
  return line.status === 'done' ? 'done' : 'draft';
}

/** Dub dot: `retake` when flagged, `done` when this pack has a dub (a published take, or a clip made outside Studio), `draft` when takes exist, otherwise `none`. */
export function dubDot(line) {
  if (line.status === 'needs-retake') return 'retake';
  if (line.publishedTakeId || line.ownedDub) return 'done';
  return line.takeCount > 0 ? 'draft' : 'none';
}

export const FILTERS = Object.freeze(['all', 'todo', 'draft', 'done', 'retake', 'nosub', 'nodub', 'warn', 'star']);
export const VOICE_FILTERS = Object.freeze(['all', 'voiced', 'unvoiced']);
export const CHAR_FILTERS = Object.freeze(['all', 'satone', 'none']);

/** CONTRACT-v2 `missing-subtitle`: THIS pack contributes no subtitle (another pack may still provide one). */
export const missingSubtitle = (l) => !(l.text && l.text.trim());
/** CONTRACT-v2 `missing-dub`: the line can be dubbed (it has a voice) and THIS pack has no dub for it. */
export const missingDub = (l) => !!l.voice && !l.ownedDub;
/** Voiced line with no dub in the pack and no recorded take yet. */
export const isUnrecorded = (l) => !!l.voice && !l.ownedDub && !l.publishedTakeId && !(l.takeCount > 0);
/** Voiced line with no published dub in the pack yet. */
export const isUnpublishedDub = (l) => !!l.voice && !l.ownedDub && !l.publishedTakeId;

/**
 * Finds the next voiced line after `currentId` (wrapping around) that has no recorded take yet,
 * falling back to the next voiced line without a published dub in the pack.
 * @param {Array<object>} lines
 * @param {string|null} currentId
 */
export function nextUnrecordedLine(lines, currentId) {
  if (!lines || !lines.length) return null;
  const start = currentId ? lines.findIndex((l) => l.id === currentId) : -1;
  for (const pred of [isUnrecorded, isUnpublishedDub]) {
    for (let step = 1; step <= lines.length; step++) {
      const cand = lines[(start + step + lines.length) % lines.length];
      if (cand && pred(cand)) return cand;
    }
  }
  return null;
}

/**
 * Next line whose workflow status is not 'done': checks the current scene first (wrapping within the scene),
 * then falls back to the rest of the catalogue after currentId.
 */
export function nextUnfinishedLine(lines, currentId, currentGroup) {
  if (!lines || !lines.length) return null;
  const cur = lines.find((l) => l.id === currentId);
  const grp = currentGroup || cur?.group || null;
  const unfinished = (l) => l.id !== currentId && l.status !== 'done';
  if (grp) {
    const stemOf = (id) => String(id || '').replace(/[_.-]\d+$/, '') || String(id || '');
    const targetStem = stemOf(grp);
    const sameScene = lines.filter((l) => l.group === grp || (targetStem && stemOf(l.group) === targetStem));
    const idx = sameScene.findIndex((l) => l.id === currentId);
    for (let i = 1; i <= sameScene.length; i++) {
      const cand = sameScene[(idx + i + sameScene.length) % sameScene.length];
      if (cand && unfinished(cand)) return cand;
    }
  }
  const allIdx = lines.findIndex((l) => l.id === currentId);
  for (let i = 1; i <= lines.length; i++) {
    const cand = lines[(allIdx + i + lines.length) % lines.length];
    if (cand && unfinished(cand)) return cand;
  }
  return null;
}

export const SETTINGS_KEY = 'cwyls-maker-settings';
export const DEFAULT_SETTINGS = Object.freeze({ autoDoneOnNext: false, jumpAfterDone: true });

export function loadSettings(storage = globalThis.localStorage) {
  try {
    const raw = storage?.getItem?.(SETTINGS_KEY);
    if (!raw) return { ...DEFAULT_SETTINGS };
    const parsed = JSON.parse(raw);
    return {
      autoDoneOnNext: typeof parsed?.autoDoneOnNext === 'boolean' ? parsed.autoDoneOnNext : DEFAULT_SETTINGS.autoDoneOnNext,
      jumpAfterDone: typeof parsed?.jumpAfterDone === 'boolean' ? parsed.jumpAfterDone : DEFAULT_SETTINGS.jumpAfterDone
    };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

export function saveSettings(settings, storage = globalThis.localStorage) {
  const next = {
    autoDoneOnNext: typeof settings?.autoDoneOnNext === 'boolean' ? settings.autoDoneOnNext : DEFAULT_SETTINGS.autoDoneOnNext,
    jumpAfterDone: typeof settings?.jumpAfterDone === 'boolean' ? settings.jumpAfterDone : DEFAULT_SETTINGS.jumpAfterDone
  };
  try {
    storage?.setItem?.(SETTINGS_KEY, JSON.stringify(next));
  } catch { /* localStorage can throw in private mode or under quota */ }
  return next;
}

/**
 * @param {Array<object>} lines all lines in catalogue order
 * @param {{group?:string|null, filter?:string, voice?:string, char?:string, q?:string, hasWarning?:(line:object)=>boolean}} view
 */
export function filterLines(lines, view) {
  const q = (view.q || '').trim().toLowerCase();
  const filter = view.filter || 'all';
  const voice = view.voice || 'all';
  const char = (view.char || 'all').toLowerCase();
  return lines.filter((l) => {
    if (view.group) {
      const ok = view.groupSet ? view.groupSet.has(l.group) : l.group === view.group;
      if (!ok) return false;
    }
    if (voice === 'voiced' && !l.voice) return false;
    if (voice === 'unvoiced' && !!l.voice) return false;
    if (char !== 'all') {
      const spk = (l.talker || (l.voice ? 'satone' : '')).toLowerCase();
      if (char === 'none' ? !!spk : spk !== char) return false;
    }
    if (filter === 'todo' && l.status !== 'todo') return false;
    if (filter === 'draft' && l.status !== 'draft') return false;
    if (filter === 'done' && l.status !== 'done') return false;
    if (filter === 'retake' && l.status !== 'needs-retake') return false;
    if (filter === 'star' && !l.starred) return false;
    if (filter === 'nosub' && !missingSubtitle(l)) return false;
    if (filter === 'nodub' && !missingDub(l)) return false;
    if (filter === 'unrec' && !isUnrecorded(l)) return false;
    if (filter === 'rec' && !(!!l.voice && !isUnrecorded(l))) return false;
    if (filter === 'warn' && !(view.hasWarning && view.hasWarning(l))) return false;
    if (q) {
      const hay = `${l.id} ${l.voice || ''} ${l.talker || ''} ${l.en || ''} ${l.ja || ''} ${l.text || ''}`.toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  });
}

/** Rows of the virtual list: a scene heading before each group when no single scene is selected. */
export function listRows(lines, groupSelected) {
  const rows = [];
  let last = null;
  for (const line of lines) {
    if (!groupSelected && line.group !== last) { rows.push({ head: line.group }); last = line.group; }
    rows.push({ line });
  }
  return rows;
}

/** Counts shown on the filter chips and in scene rows; derived locally so they match what the list shows. */
export function countByStatus(lines) {
  const c = { total: lines.length, todo: 0, draft: 0, done: 0, retake: 0, star: 0, subtitles: 0, dubs: 0 };
  for (const l of lines) {
    if (l.status === 'todo') c.todo += 1;
    else if (l.status === 'draft') c.draft += 1;
    else if (l.status === 'done') c.done += 1;
    else if (l.status === 'needs-retake') c.retake += 1;
    if (l.starred) c.star += 1;
    if (l.text && l.text.trim()) c.subtitles += 1;
    if (l.publishedTakeId) c.dubs += 1;
  }
  return c;
}

/** Window of rows for a virtual list. Returns the first and one-past-last index to render. */
export function visibleWindow(count, scrollTop, viewportHeight, rowHeight, overscan = 4) {
  if (count <= 0 || rowHeight <= 0) return { from: 0, to: 0 };
  const from = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const to = Math.min(count, Math.ceil((scrollTop + viewportHeight) / rowHeight) + overscan);
  return { from: Math.min(from, to), to };
}
