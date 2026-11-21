// Thai-aware text rules for the subtitle editor: character counting, word-safe wrapping.
// Ported from mockup A (app.js: cplCount, cpsCount, tokens, wrapLines) and hardened: pure, no globals,
// the segmenter is injectable, and a forced cut never separates a letter from its tone mark or vowel.
// The text that is saved is exactly what the creator typed: wrapping only decides how it is *shown*.

/** Combining marks that do not count towards the line width: ั (U+0E31), ิ–ฺ (U+0E34–0E3A), ็–๎ (U+0E47–0E4E). */
const COMBINING = /[\u0E31\u0E34-\u0E3A\u0E47-\u0E4E]/;
const COMBINING_G = new RegExp(COMBINING.source, 'g');

/** Characters allowed per line: 35 base letters for Thai, 42 for other languages (the mockup's values). */
export function maxCpl(lang) { return lang === 'th' ? 35 : 42; }

/** The longest comfortable reading speed, characters per second (Thai base letters or code points). */
export const MAX_CPS = 17;

/** Characters counted for one line: Thai counts base letters only, other languages every code point. */
export function cplCount(s, lang) {
  const text = String(s ?? '');
  return Array.from(lang === 'th' ? text.replace(COMBINING_G, '') : text).length;
}

/** Characters counted for reading speed: like {@link cplCount} but whitespace never counts. */
export function cpsCount(s, lang) {
  return cplCount(String(s ?? '').replace(/\s+/g, ''), lang);
}

/** The default word segmenter (Intl.Segmenter, Thai), or null when the browser has none. */
export function defaultSegmenter() {
  return typeof Intl !== 'undefined' && Intl.Segmenter ? new Intl.Segmenter('th', { granularity: 'word' }) : null;
}
const DEFAULT_SEGMENTER = defaultSegmenter();

/** Marks that must stay with the word before them: ๆ and ฯ, and closing punctuation. */
function isGlue(token) {
  return /^[ๆฯ]/.test(token) || /^[?!.,…)」』"”’:;]+$/.test(token);
}

/**
 * Splits one paragraph into wrap tokens (words and spaces). ๆ/ฯ and closing punctuation are glued to the
 * word before them, with any spaces in between, so they never start a line.
 * @param {string} paragraph
 * @param {string} lang
 * @param {{segment:(s:string)=>Iterable<{segment:string}>}|null} [segmenter] pass null to force the whitespace fallback
 */
export function tokens(paragraph, lang, segmenter = DEFAULT_SEGMENTER) {
  const raw = segmenter && lang === 'th'
    ? Array.from(segmenter.segment(paragraph), (s) => s.segment)
    : paragraph.split(/(\s+)/).filter(Boolean);
  const out = [];
  for (const t of raw) {
    if (isGlue(t) && out.length) {
      let acc = t;
      while (out.length && /^\s+$/.test(out[out.length - 1])) acc = out.pop() + acc;
      if (out.length) out[out.length - 1] += acc; else out.push(acc);
    } else out.push(t);
  }
  return out;
}

/**
 * Cuts a string after `max` counted characters, keeping combining marks with their base letter.
 * Used only when a single word is longer than a whole line.
 * @returns {[string,string]} head and rest
 */
export function cutAtCount(s, max, lang) {
  const chars = Array.from(s);
  let counted = 0, i = 0;
  while (i < chars.length) {
    const isMark = lang === 'th' && COMBINING.test(chars[i]);
    if (!isMark) {
      if (counted === max) break;
      counted += 1;
    }
    i += 1;
  }
  return [chars.slice(0, i).join(''), chars.slice(i).join('')];
}

/** Counts explicit blank lines too; a third empty line is still a third line. */
export function subtitleLineCount(text, lang) {
  const normalized = String(text ?? '').replace(/\r\n?/g, '\n');
  return Math.max(normalized.split('\n').length, wrapLines(normalized, lang).length);
}
/** Existing long subtitles can be shortened without silently truncating loaded content. */
export function acceptsSubtitleEdit(text, previous, lang) {
  const next = subtitleLineCount(text, lang), old = subtitleLineCount(previous, lang);
  return next <= 2 || (old > 2 && next <= old && text.length < previous.length);
}

/**
 * Safe wrap: breaks at word boundaries, keeps ๆ/ฯ and closing punctuation with the word before,
 * and force-cuts only words longer than a line. Never inserts zero-width spaces.
 * @param {string} text
 * @param {string} lang
 * @param {{segmenter?:object|null, max?:number}} [options]
 * @returns {string[]} the visible lines (explicit newlines are kept as breaks)
 */
export function wrapLines(text, lang, options = {}) {
  if (!text || !String(text).trim()) return [];
  const max = options.max ?? maxCpl(lang);
  const segmenter = 'segmenter' in options ? options.segmenter : DEFAULT_SEGMENTER;
  const result = [];
  for (const paragraph of String(text).split('\n')) {
    let cur = '';
    for (const tok of tokens(paragraph, lang, segmenter)) {
      const candidate = cur + tok;
      if (!cur.trim() || cplCount(candidate.trim(), lang) <= max) cur = candidate;
      else { result.push(cur.trim()); cur = tok.replace(/^\s+/, ''); }
      while (cplCount(cur, lang) > max) {
        const [head, rest] = cutAtCount(cur, max, lang);
        if (!head) break;
        result.push(head);
        cur = rest;
      }
    }
    result.push(cur.trim());
  }
  return result;
}
