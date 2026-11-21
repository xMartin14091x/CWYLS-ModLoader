// Gentle QA for one subtitle: pills with one-click fixes. Pure; the caller supplies the reading basis.
import { cpsCount, wrapLines, MAX_CPS } from './thai.js';

/**
 * @param {{en?:string, ja?:string}} reference the line's reference texts (placeholders and tags are taken from them)
 * @param {string} text the creator's subtitle
 * @param {{lang:string, seconds:number, wrapOptions?:object}} ctx pack language and the reading-basis seconds
 * @returns {Array<{k:'ph'|'tag'|'mai'|'double'|'lines'|'fast', x?:string, n?:number, v?:string}>}
 */
export function qaList(reference, text, ctx) {
  const out = [];
  if (!text || !text.trim()) return out;
  const { lang, seconds } = ctx;
  const src = `${reference.en || ''} ${reference.ja || ''}`;
  const placeholders = src.match(/\{\w+\}/g) || [];
  for (const p of placeholders.filter((x, i) => placeholders.indexOf(x) === i)) {
    if (!text.includes(p)) out.push({ k: 'ph', x: p });
  }
  for (const tag of reference.en?.match(/<[^>]+>/g) || []) {
    if (!text.includes(tag)) out.push({ k: 'tag', x: tag });
  }
  if (text.split('\n').some((ln) => /^\s*[ๆฯ]/.test(ln))) out.push({ k: 'mai' });
  if (/ {2,}/.test(text)) out.push({ k: 'double' });
  const wrapped = wrapLines(text, lang, ctx.wrapOptions);
  if (wrapped.length > 2) out.push({ k: 'lines', n: wrapped.length });
  if (seconds > 0) {
    const cps = cpsCount(text, lang) / seconds;
    if (cps > MAX_CPS) out.push({ k: 'fast', v: cps.toFixed(1) });
  }
  return out;
}

/**
 * Applies one pill's one-click fix and returns the new text (insert for missing placeholder/tag, clean-up otherwise).
 * @param {string} text
 * @param {{k:string, x?:string}} issue
 */
export function applyFix(text, issue) {
  if (issue.k === 'ph' || issue.k === 'tag') return text + (text && !/\s$/.test(text) ? ' ' : '') + issue.x;
  if (issue.k === 'double') return text.replace(/ {2,}/g, ' ');
  if (issue.k === 'mai') {
    // join a line that starts with ๆ/ฯ to the line before it
    return text.split('\n').reduce((acc, line, i) => {
      if (i > 0 && /^\s*[ๆฯ]/.test(line)) return acc + line.trim();
      return i === 0 ? line : `${acc}\n${line}`;
    }, '');
  }
  return text;
}
