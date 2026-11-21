// Reading speed: how fast a subtitle must be read against the original line or against the creator's dub.
import { cpsCount, MAX_CPS } from './thai.js';

/**
 * Picks the duration the reading speed is measured against.
 * @param {{origSec:number, dubSec:number|null, against?:'auto'|'orig'}} p
 * @returns {{seconds:number, source:'orig'|'dub', canSwitch:boolean}}
 */
export function readingBasis({ origSec, dubSec, against = 'auto' }) {
  const hasDub = Number.isFinite(dubSec) && dubSec > 0;
  if (against === 'orig' || !hasDub) return { seconds: origSec, source: 'orig', canSwitch: hasDub };
  return { seconds: dubSec, source: 'dub', canSwitch: true };
}

/** Characters per second, or 0 for an empty text or a missing duration. */
export function readingSpeed(text, lang, seconds) {
  if (!text || !String(text).trim() || !(seconds > 0)) return 0;
  return cpsCount(text, lang) / seconds;
}

/** `good` up to 15 cps, `mid` up to 17, `bad` above, `none` for an empty text (the mockup's thresholds). */
export function readingClass(cps, text) {
  if (!text || !String(text).trim()) return 'none';
  if (cps > MAX_CPS) return 'bad';
  if (cps > 15) return 'mid';
  return 'good';
}
