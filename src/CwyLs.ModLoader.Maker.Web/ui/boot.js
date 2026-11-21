// Chooses the UI. Studio v2 is the default (`FLIPPED = true`, W3-10): `?ui=1` forces v1, `?ui=2` explicitly selects Studio v2.
// v1's stylesheet and v2's never load together (v1's styles.css would leak into v2).
import { chooseUi } from './choose.js';
export const FLIPPED = true; // W3-10: v2 is the default, `?ui=1` keeps v1 selectable.
const wantV2 = chooseUi(location.search, FLIPPED) === 2;

function style(href) {
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = href;
  document.head.appendChild(link);
  return new Promise((resolve) => { link.onload = link.onerror = resolve; });
}

if (wantV2) {
  await style('./ui/styles/index.css');
  const { start } = await import('./main.js');
  await start();
} else {
  await style('./styles.css');
  await import('../modules/app.js');
}
