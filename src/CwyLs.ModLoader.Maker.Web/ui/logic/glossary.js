// Glossary chips: which project terms appear in the line's reference text, and what the creator should write.
// Entries come from the built-in project names and, later, the creator's own entries (W2 `glossary` route).

/** Built-in terms (names used across the project's translations). Not game text dumps: names only. */
export const BUILTIN_GLOSSARY = Object.freeze([
  { id: 'satone', sourceJa: 'サトネ', sourceEn: 'Satone', target: { th: 'ซาโตเนะ', en: 'Satone' } },
  { id: 'satoru', sourceJa: 'サトル', sourceEn: 'Satoru', target: { th: 'ซาโตรุ', en: 'Satoru' } },
  { id: 'kou-chan', sourceJa: 'コウちゃん', sourceEn: 'Kou-chan', target: { th: 'โคจัง', en: 'Kou-chan' } },
  { id: 'pomodoro', sourceJa: 'ポモドーロ', sourceEn: 'Pomodoro', target: { th: 'โพโมโดโร่', en: 'Pomodoro' } },
  { id: 'call', sourceJa: '通話', sourceEn: 'call', target: { th: 'คอล', en: 'call' } },
  { id: 'focus', sourceJa: '集中', sourceEn: 'focusing', target: { th: 'โฟกัส', en: 'focus' } }
]);

const escapeRegExp = (s) => s.replace(/[.*+?^${}()|[\]\\-]/g, '\\$&');

/** Does the term occur in the reference? Latin terms match whole words, case-insensitively; Japanese matches as a substring. */
function appears(entry, line) {
  const en = entry.sourceEn || '';
  if (en && new RegExp(`\\b${escapeRegExp(en)}\\b`, 'i').test(line.en || '')) return true;
  const ja = entry.sourceJa || '';
  return !!ja && (line.ja || '').includes(ja);
}

/**
 * @param {{ja?:string,en?:string}} line reference texts
 * @param {Array<{id:string,sourceJa?:string,sourceEn?:string,target:Record<string,string>|string}>} entries
 * @param {string} packLang
 * @param {string} text the creator's current subtitle
 * @returns {Array<{id:string, source:string, ja:string, target:string, used:boolean}>}
 */
export function glossaryFor(line, entries, packLang, text) {
  const out = [];
  for (const entry of entries) {
    if (!appears(entry, line)) continue;
    const target = typeof entry.target === 'string' ? entry.target : entry.target[packLang] || entry.target.en;
    if (!target) continue;
    out.push({ id: entry.id, source: entry.sourceEn || entry.sourceJa, ja: entry.sourceJa || '', target, used: (text || '').includes(target) });
  }
  return out;
}

/** Inserts a term at a caret position (or at the end) and returns the new text and caret. */
export function insertTerm(text, term, caret = text.length) {
  const at = Math.max(0, Math.min(caret, text.length));
  return { text: text.slice(0, at) + term + text.slice(at), caret: at + term.length };
}
