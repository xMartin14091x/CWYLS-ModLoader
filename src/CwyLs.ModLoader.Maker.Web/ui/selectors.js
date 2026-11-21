// Derived data for the zones. Pure functions of the store state; small memoisation where the catalogue is large.
import { filterLines, listRows } from './logic/progress.js';
import { qaList } from './logic/qa.js';
import { readingBasis } from './logic/reading.js';
import { selectedTake } from './logic/takes.js';

export const packOf = (s) => s.packs.find((p) => p.id === s.packId) || null;
export const isReadOnly = (s) => !packOf(s)?.editable;
export const groupOf = (s, id) => s.groups.find((g) => g.id === id) || null;

/** Prefix of a group id with a trailing number removed (e.g. `clickheroine_rest_010` -> `clickheroine_rest`). */
export function groupStem(id) {
  const s = String(id || '');
  if (!s) return '';
  const sep = s.match(/^(.+?)[_.-]\d+$/);
  if (sep) return sep[1];
  const bare = s.match(/^([A-Za-z_]{3,})\d+$/);
  return bare ? bare[1] : s;
}

/** Humanises an unknown group prefix (`custom_weather_rain` -> `Custom weather rain`, `clickheroine_pat` -> `Click reactions (pat)`). */
export function humaniseGroupPrefix(prefix, strings = null) {
  const raw = String(prefix || '').trim();
  if (!raw) return '';
  const names = strings?.groups || {};
  if (names[raw]) return names[raw];
  const clickMatch = raw.match(/^clickheroine[_.-](.+)$/i);
  if (clickMatch) {
    const base = names.click || names.clickheroine || 'Click reactions';
    const sub = clickMatch[1].replace(/[_.-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase().trim();
    return `${base} (${sub})`;
  }
  const words = raw
    .replace(/^group\./, '')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[_.-]+/g, ' ')
    .trim()
    .toLowerCase();
  return words ? words[0].toUpperCase() + words.slice(1) : raw;
}

const PROG_KEYS = ['totalLines', 'dubbableLines', 'subtitlePresent', 'dubPresent', 'todo', 'draft', 'done', 'needsRetake', 'starred'];

function emptyProg() {
  return { totalLines: 0, dubbableLines: 0, subtitlePresent: 0, dubPresent: 0, todo: 0, draft: 0, done: 0, needsRetake: 0, starred: 0 };
}

function addProg(acc, p) {
  if (!p) return acc;
  for (const k of PROG_KEYS) if (typeof p[k] === 'number') acc[k] += p[k];
  return acc;
}

/**
 * Merges raw groups whose ids differ only by a trailing number (or per-line numbered ids with no distinct title)
 * into one display group with summed progress and a readable name.
 */
export function mergeDisplayGroups(rawGroups = []) {
  if (!rawGroups || !rawGroups.length) return [];
  const stemCounts = new Map();
  const candidates = rawGroups.map((g) => {
    const id = String(g?.id || '');
    const hasSepNum = /^.+?[_.-]\d+$/.test(id);
    const hasBareNum = /^[A-Za-z_]{3,}\d+$/.test(id) && (!g.title || g.title === id) && !g.labelKey;
    const stem = (hasSepNum || hasBareNum) ? groupStem(id) : id;
    if (stem !== id) stemCounts.set(stem, (stemCounts.get(stem) || 0) + 1);
    return { g, id, stem, hasSepNum };
  });
  const byDisplayId = new Map();
  const out = [];
  for (const { g, id, stem, hasSepNum } of candidates) {
    const noHumanTitle = !g.title || g.title === id;
    const shouldMerge = stem !== id && ((stemCounts.get(stem) || 0) > 1 || (hasSepNum && noHumanTitle && !g.labelKey));
    const displayId = shouldMerge ? stem : id;
    let dg = byDisplayId.get(displayId);
    if (!dg) {
      dg = {
        id: displayId,
        labelKey: shouldMerge ? (g.labelKey || displayId) : g.labelKey,
        title: shouldMerge ? (noHumanTitle ? '' : String(g.title).replace(/\s*#?\d+$/, '').trim()) : g.title,
        order: g.order ?? out.length,
        merged: shouldMerge,
        rawIds: [id],
        rawSet: new Set([id]),
        progress: addProg(emptyProg(), g.progress)
      };
      byDisplayId.set(displayId, dg);
      out.push(dg);
    } else {
      dg.merged = true;
      dg.rawIds.push(id);
      dg.rawSet.add(id);
      addProg(dg.progress, g.progress);
    }
  }
  return out;
}

let displayGroupsMemo = { raw: null, value: [] };
export function displayGroups(s) {
  const raw = s?.groups || [];
  if (displayGroupsMemo.raw === raw) return displayGroupsMemo.value;
  const value = mergeDisplayGroups(raw);
  displayGroupsMemo = { raw, value };
  return value;
}

export function displayGroupOf(s, id) {
  if (!id) return null;
  const list = displayGroups(s);
  return list.find((g) => g.id === id || g.rawSet?.has(id)) || null;
}

export function inDisplayGroup(s, lineGroupId, selectedGroupId) {
  if (!selectedGroupId) return true;
  if (lineGroupId === selectedGroupId) return true;
  const dg = displayGroupOf(s, selectedGroupId);
  return dg ? dg.rawSet.has(lineGroupId) : groupStem(lineGroupId) === selectedGroupId;
}

/** Display name of a group: the UI's own name for a known labelKey/prefix, else the loader's title, else a humanised prefix / id. */
export function groupName(s, group, T, strings) {
  if (!group) return T('scene_unknown');
  const names = strings?.groups || {};
  if (group.labelKey && names[group.labelKey]) return names[group.labelKey];
  if (group.id && names[group.id]) return names[group.id];
  const stem = groupStem(group.id);
  if (group.merged) {
    if (names[stem]) return names[stem];
    if (group.title && group.title !== group.id) return group.title;
    return humaniseGroupPrefix(stem, strings);
  }
  if (group.title && group.title !== group.id) return group.title;
  if (stem !== group.id && names[stem]) return names[stem];
  return group.title || group.id;
}

/** The text the editor holds for the current line (the draft while typing, else the pack's text). */
export const editorText = (s) => (s.draft ? s.draft.text : s.line?.text || '');

/** Reading basis for the current line: the selected take's trimmed length when there is one, else the original. */
export function basisFor(s, line) {
  const take = line ? currentTake(s) : null;
  const dubSec = take ? ((take.trimEndSec ?? take.sourceDurationSec ?? 0) - (take.trimStartSec ?? 0)) || null : null;
  return readingBasis({ origSec: line?.durationSec || 0, dubSec, against: s.cpsAgainst });
}

export function currentTake(s) {
  const line = s.line;
  if (!line) return null;
  return selectedTake(line.takes, { selectedId: s.selTakeId, favouriteId: line.favouriteTakeId ?? null, publishedId: line.publishedTakeId });
}

// ---- warnings are QA over the creator's own text; cached per (id, text, language, seconds) ----
const warnCache = new Map();
export function warningsOf(line, lang) {
  if (!line.text || !line.text.trim()) return [];
  const secs = line.durationSec || 0, key = `${line.id}|${lang}|${secs}|${line.text}`;
  let hit = warnCache.get(key);
  if (!hit) {
    hit = qaList(line, line.text, { lang, seconds: secs });
    warnCache.set(key, hit);
    if (warnCache.size > 6000) warnCache.delete(warnCache.keys().next().value);
  }
  return hit;
}

let visibleMemo = { key: null, value: null };
/** The lines the list shows, after scene, filter and search. */
export function visibleLines(s) {
  const key = [s.lines, s.view.group, s.view.filter, s.view.voice || 'all', s.view.char || 'all', s.view.q, s.packLang, s.warnVersion, s.groups];
  if (visibleMemo.key && key.every((v, i) => v === visibleMemo.key[i])) return visibleMemo.value;
  const dg = s.view.group ? displayGroupOf(s, s.view.group) : null;
  const value = filterLines(s.lines, { ...s.view, groupSet: dg?.rawSet || null, hasWarning: (l) => warningsOf(l, s.packLang).length > 0 });
  visibleMemo = { key, value };
  return value;
}
export const rowsOf = (s) => listRows(visibleLines(s), s.view.group);

export function neighbours(s) {
  const occ = s.line?.context?.occurrences?.find((o) => o.id === s.line.context.selectedOccurrenceId) || s.line?.context?.occurrences?.[0];
  return { prev: occ?.previousId ? s.byId.get(occ.previousId) || null : null, next: occ?.nextId ? s.byId.get(occ.nextId) || null : null };
}

/** Position of the line inside its display group, "3/7". */
export function positionInGroup(s, line) {
  const dg = displayGroupOf(s, line.group);
  const same = s.lines.filter((l) => (dg ? dg.rawSet.has(l.group) : l.group === line.group));
  return { pos: same.findIndex((l) => l.id === line.id) + 1, of: same.length };
}

// ---- what the loader offers (CONTRACT-v2 capabilities.features lists only usable features) ----
export const FEATURES = Object.freeze(['catalog', 'workflow', 'subtitles', 'dubs', 'projects', 'newPacks', 'packInfo', 'export', 'voiceDefault', 'voiceLabel', 'playback']);
/** Unknown capabilities (not loaded yet) count as available; a loaded list is the truth. */
export const featureOn = (s, f) => !s.features || s.features.includes(f);
/** Can the subtitle box be typed in? The pack must be writable and the loader must offer subtitle editing. */
export const canEditText = (s) => !!s.line && !s.loadingLines && !isReadOnly(s) && !s.line.readOnly && featureOn(s, 'subtitles');
/** Can takes be made and changed? */
export const canDub = (s) => !isReadOnly(s) && !s.line?.readOnly && featureOn(s, 'dubs');
export const canPlay = (s) => featureOn(s, 'playback');
/** Creating a pack is offered only when the loader says `newPacks` (W2 v2.1: `projects` does not enable it). Not a banner: the button explains itself. */
export const canCreatePacks = (s) => featureOn(s, 'newPacks');
export const canPackInfo = (s) => featureOn(s, 'packInfo');
export const canExport = (s) => !isReadOnly(s) && !packOf(s)?.restartRequired && featureOn(s, 'export');
export const canVoiceDefault = (s) => !isReadOnly(s) && !packOf(s)?.restartRequired && featureOn(s, 'voiceDefault');
/** A take-origin choice belongs only to the pack/language/line/take where it was made. */
export function takeOriginDraft(s) {
  const d = s.takeOriginDraft;
  return d && d.packId === s.packId && d.lang === s.packLang && d.lineId === s.line?.id && d.takeId === currentTake(s)?.id ? d : null;
}
/** D-148: the export page may offer "Label all unlabelled dubs with the pack default". */
export const canLabelDubs = (s) => canVoiceDefault(s) && featureOn(s, 'voiceLabel');
export const hasPackVoiceDefault = (s) => Boolean(s.voiceDefault?.origin && s.voiceDefault?.consent?.confirmed);
/**
 * The game runs in another language than the selected pack's. Then what the creator edits is not what the game will show (subtitles and
 * dubs are per language), so the page says so. `switchTo` is the pack's own language code equal to the game's, when the pack has one.
 * Nothing while the loader is unreachable (the last known language may be stale) or before the status is known.
 * @returns {null|{game:string, pack:string, switchTo:string|null}}
 */
export function langMismatch(s) {
  const game = s.status?.gameLanguage, pack = s.packLang;
  if (!game || !pack || s.conn?.connected === false) return null;
  if (String(game).toLowerCase() === String(pack).toLowerCase()) return null;
  const langs = packOf(s)?.languages || [];
  return { game, pack, switchTo: langs.find((l) => String(l).toLowerCase() === String(game).toLowerCase()) || null };
}
/** The first missing feature the screen has to explain (null when everything is there). Order = what the creator meets first. */
export const missingFeature = (s) => ['subtitles', 'dubs', 'playback', 'workflow', 'projects'].find((f) => !featureOn(s, f)) || null;
