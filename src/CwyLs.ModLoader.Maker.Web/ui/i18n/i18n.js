// UI language: follows the game language (th or en, anything else shows English), with a manual toggle.
import en from './en.js';
import th from './th.js';

export const STRINGS = { en, th };
export const UI_LANGS = ['th', 'en'];
const STORE_KEY = 'cwyls-maker-ui-lang';

/** The UI language for a game language code. */
export const followGame = (gameLanguage) => (gameLanguage === 'th' ? 'th' : 'en');

/** The creator's manual choice, if any (the only thing this app keeps in localStorage). */
export function storedChoice(storage = globalThis.localStorage) {
  try { const v = storage?.getItem(STORE_KEY); return UI_LANGS.includes(v) ? v : null; } catch { return null; }
}
export function storeChoice(lang, storage = globalThis.localStorage) {
  try { storage?.setItem(STORE_KEY, lang); } catch { /* private mode: the choice just lasts for this page */ }
}

/** Effective language: a manual choice wins, otherwise the game's. */
export const effectiveLang = (gameLanguage, manual) => manual || followGame(gameLanguage);

/**
 * Builds `T(key, vars)` for a language. Falls back to English, then to the key itself; `{name}` placeholders are replaced.
 * Non-string values (arrays, maps) are returned as they are (e.g. `firstSteps`).
 */
export function makeT(lang) {
  return function T(key, vars) {
    let s = STRINGS[lang]?.[key];
    if (s == null) s = STRINGS.en[key];
    if (s == null) return key;
    if (vars && typeof s === 'string') for (const k of Object.keys(vars)) s = s.split(`{${k}}`).join(vars[k]);
    return s;
  };
}
