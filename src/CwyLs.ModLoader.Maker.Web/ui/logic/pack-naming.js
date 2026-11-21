// Pack naming and id suggestions for New pack form (CO's pack naming).
// Suggests name "<Author>'s <Language name>" (e.g. "Martin's Thai")
// and id "<authorSlug>.<langCode>" (e.g. "martin.th"), resolving collisions with "<id>.2", "<id>.3", etc.

const PRESET_EN_LANGS = {
  th: 'Thai',
  vi: 'Vietnamese',
  id: 'Indonesian',
  es: 'Spanish',
  fr: 'French',
  de: 'German',
  ja: 'Japanese',
  en: 'English'
};

/**
 * Normalises an author name into a lowercase slug conforming to mod loader pack ID rules [a-z0-9._-].
 * Strips leading/trailing punctuation so it starts and ends with [a-z0-9].
 * Falls back to 'author' when empty or when containing no ASCII alphanumeric characters.
 */
export function authorSlug(author) {
  if (!author || typeof author !== 'string') return 'author';
  const s = author
    .trim()
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[^a-z0-9._-]+/g, '-')
    .replace(/^[^a-z0-9]+|[^a-z0-9]+$/g, '');
  return s.length > 0 ? s.slice(0, 48) : 'author';
}

/**
 * Returns a language's display name for pack naming.
 * Prefers an explicit custom display name if provided, otherwise looks up T('lang_' + code),
 * then English fallback, then uppercase code.
 */
export function getLanguageLabel(langCode = 'th', langName = '', T = null) {
  if (langName && String(langName).trim()) return String(langName).trim();
  const code = String(langCode || 'th').trim().toLowerCase();
  if (T && typeof T === 'function') {
    const k = `lang_${code}`;
    const v = T(k);
    if (v && v !== k) return v;
  }
  return PRESET_EN_LANGS[code] || code.toUpperCase();
}

/**
 * Suggests a pack name formatted as "<Author>'s <Language name>" (or localized in Thai "{lang}ของ {author}").
 * If author is empty, returns empty string.
 */
export function suggestPackName({ author = '', langCode = 'th', langName = '', T = null } = {}) {
  const cleanAuthor = author ? String(author).trim() : '';
  if (!cleanAuthor) return '';
  const langLabel = getLanguageLabel(langCode, langName, T);
  const format = (author) => T && typeof T === 'function'
    ? T('newPackNameSuggest', { author, lang: langLabel }) : `${author}'s ${langLabel}`;
  // The backend measures code points, so do not split surrogate pairs or send an invalid untouched suggestion.
  const room = Math.max(1, 48 - Array.from(format('')).length);
  return Array.from(format(Array.from(cleanAuthor).slice(0, room).join(''))).slice(0, 48).join('').trimEnd();
}

/**
 * Suggests a pack id formatted as "<authorSlug>.<langCode>" (e.g. "martin.th").
 * Keeps id rules: lowercase, [a-z0-9._-], 3-64 characters.
 * If the id is taken in `existingPacks` (or existingIds Set/Array), suggests "<id>.2", "<id>.3", etc.
 * If author is empty, returns empty string.
 */
export function suggestPackId(author = '', langCode = 'th', existingPacks = []) {
  if (!author || !String(author).trim()) return '';
  const taken = existingPacks instanceof Set
    ? existingPacks
    : new Set(Array.isArray(existingPacks) ? existingPacks.map((x) => (typeof x === 'string' ? x : x?.id)).filter(Boolean) : []);
  const slug = authorSlug(author);
  const code = String(langCode || 'th').trim().toLowerCase().replace(/[^a-z0-9._-]/g, '') || 'th';
  const rawBase = `${slug}.${code}`;
  const baseId = rawBase.length > 56 ? rawBase.slice(0, 56).replace(/[-._]+$/, '') : rawBase;
  if (!taken.has(baseId)) return baseId;
  let counter = 2;
  while (taken.has(`${baseId}.${counter}`)) {
    counter++;
  }
  return `${baseId}.${counter}`;
}
