// The scenery of mockup A "Dusk": pixel moon, little lit house, the window onto the same sky, stars and hills.
// Decorative only (aria-hidden); no game images. Static SVG strings, built from constants.
import { $, rem, mulberry } from '../util.js';

// pixel moon (from the project's sky art, 6-unit pixels)
export const MOON = '<svg viewBox="930 44 100 104" shape-rendering="crispEdges"><path fill="currentColor" d="M936,90h36v6h-36ZM942,72h24v6h-24ZM942,78h24v6h-24ZM942,84h24v6h-24ZM942,96h30v6h-30ZM942,102h36v6h-36ZM942,108h42v6h-42ZM948,60h24v6h-24ZM948,66h18v6h-18ZM948,114h48v6h-48ZM948,120h78v6h-78ZM954,54h18v6h-18ZM954,126h66v6h-66ZM966,48h12v6h-12ZM966,132h42v6h-42ZM984,138h6v6h-6Z"/></svg>';
// pixel house with a lit window
export const HOUSE = '<svg viewBox="742 1780 76 82" shape-rendering="crispEdges"><path fill="currentColor" d="M744,1818h72v42h-72ZM744,1812h60v6h-60ZM798,1812h18v6h-18ZM750,1806h54v6h-54ZM798,1806h12v6h-12ZM756,1800h48v6h-48ZM798,1800h6v6h-6ZM762,1794h42v6h-42ZM768,1788h24v6h-24ZM798,1788h6v6h-6ZM774,1782h12v6h-12Z"/><path class="lit" fill="#f6c177" d="M762,1824h18v18h-18Z"/></svg>';

/** Satone's window: the same Dusk sky, seen from inside the lit room (decor only). */
export const ROOM_WINDOW = (() => {
  const bands = ['#0d112b', '#111738', '#1a1d44', '#292351', '#402b5a', '#5d3564', '#945874'];
  const pane = (x) => bands.map((c, i) => `<rect x="${x}" y="${8 + i * 12}" width="98" height="12" fill="${c}"/>`).join('');
  const stars = [[22, 16], [48, 28], [70, 12], [90, 40], [130, 22], [160, 34], [196, 14], [38, 48], [176, 52], [120, 50]].map((p, i) =>
    `<rect class="tw" style="animation-duration:${4 + i % 4}s;animation-delay:-${i}s" x="${p[0]}" y="${p[1]}" width="2" height="2" fill="${i % 3 ? '#fff' : '#ffd787'}" fill-opacity=".8"/>`).join('');
  const moon = '<g transform="translate(172 10) scale(.2)" fill="#ffd787"><path d="M6,46h36v6h-36ZM12,28h24v6h-24ZM12,34h24v6h-24ZM12,40h24v6h-24ZM12,52h30v6h-30ZM12,58h36v6h-36ZM12,64h42v6h-42ZM18,16h24v6h-24ZM18,22h18v6h-18ZM18,70h48v6h-48ZM18,76h78v6h-78ZM24,10h18v6h-18ZM24,82h66v6h-66ZM36,4h12v6h-12ZM36,88h42v6h-42ZM54,94h6v6h-6Z"/></g>';
  return `<svg viewBox="0 0 220 104" shape-rendering="crispEdges"><rect x="0" y="0" width="220" height="100" rx="5" fill="#1b1234"/>${pane(8)}${pane(114)}${stars}${moon}` +
    '<rect x="0" y="96" width="220" height="8" rx="2" fill="#2a1b45"/><rect x="106" y="4" width="8" height="92" fill="#1b1234"/></svg>';
})();

/** Paints the stars and the two hill layers for the current window size. Keeps text areas of the top bar star-free. */
export function renderSky() {
  const w = innerWidth, h = innerHeight, r = mulberry(20261108), px = Math.max(2, Math.round(w / 480));
  const cols = ['#ffffff', '#ffffff', '#ffffff', '#ffd787', '#bfe9d6', '#f4b6cf', '#c9c3ff'];
  const brand = $('.brand'), right = $('.top-r'), bar = $('#top');
  const topH = bar ? bar.getBoundingClientRect().bottom : rem() * 4.4;
  const freeA = brand ? brand.getBoundingClientRect().right + 12 : w * 0.3, freeB = right ? right.getBoundingClientRect().left - 12 : w * 0.45;
  const n = Math.round(w * h / 5200);
  let out = '';
  for (let i = 0; i < n; i++) {
    const x = Math.round(r() * w / px) * px, y = Math.round(Math.pow(r(), 1.25) * h * 0.86 / px) * px;
    const c = cols[Math.floor(r() * cols.length)];
    const op = (0.35 + r() * 0.55).toFixed(2), d = (3.5 + r() * 4.5).toFixed(1), dl = (-r() * 8).toFixed(1);
    if (y < topH && (x < freeA || x > freeB)) continue;
    if (r() < 0.07) {
      out += `<path class="tw" style="animation-duration:${d}s;animation-delay:${dl}s" fill="${c}" fill-opacity="${op}" d="M${x},${y - px}h${px}v${px}h${px}v${px}h-${px}v${px}h-${px}v-${px}h-${px}v-${px}h${px}Z"/>`;
    } else {
      out += `<rect class="tw" style="animation-duration:${d}s;animation-delay:${dl}s" x="${x}" y="${y}" width="${px}" height="${px}" fill="${c}" fill-opacity="${op}"/>`;
    }
  }
  const stars = $('#stars');
  if (stars) { stars.setAttribute('viewBox', `0 0 ${w} ${h}`); stars.setAttribute('width', w); stars.setAttribute('height', h); stars.innerHTML = out; }

  // pixel hills: two stepped layers
  const hh = Math.round(rem() * 4.2), step = px * 3, r2 = mulberry(7);
  let a = '', b = '', y1 = Math.round(hh * 0.25), y2 = Math.round(hh * 0.55);
  for (let x = 0; x < w; x += step * (2 + Math.floor(r2() * 4))) {
    y1 = Math.max(0, Math.min(hh * 0.45, y1 + (Math.floor(r2() * 3) - 1) * step * 0.5));
    a += `M${x},${Math.round(y1)}h${step * 6}V${hh}H${x}Z`;
  }
  for (let x = 0; x < w; x += step * (3 + Math.floor(r2() * 4))) {
    y2 = Math.max(hh * 0.4, Math.min(hh * 0.7, y2 + (Math.floor(r2() * 3) - 1) * step * 0.5));
    b += `M${x},${Math.round(y2)}h${step * 7}V${hh}H${x}Z`;
  }
  const hills = $('#hills');
  if (hills) { hills.setAttribute('viewBox', `0 0 ${w} ${hh}`); hills.style.height = `${hh}px`; hills.innerHTML = `<path fill="#2a1b45" d="${a}"/><path fill="#140e2a" d="${b}"/>`; }
}
