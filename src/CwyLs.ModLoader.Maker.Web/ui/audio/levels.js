// Input level classes for the record halo: green (good), amber (background noise), coral (clipping), grey (idle).
// The thresholds are mine, not measurements of loudness: they are listed in ui/README.md.
export const CLIP_PEAK = 0.98;       // a sample this close to full scale will clip
export const IDLE_RMS = 0.01;        // below about -40 dBFS nothing is being said
export const NOISE_FLOOR = 0.03;     // a quietest-moment level above about -30 dBFS means the room is noisy
export const CLIP_HOLD_MS = 600;     // a clip stays visible a moment so a glance sees it
export const FLOOR_WINDOW_MS = 3000; // the noise floor is the quietest level of the last 3 s

/** Pure class of one reading. `floor` is the quietest rms of the recent past. */
export function levelClass({ peak, rms, floor }) {
  if (peak >= CLIP_PEAK) return 'clip';
  if (rms < IDLE_RMS) return 'idle';
  if (floor >= NOISE_FLOOR) return 'noise';
  return 'good';
}

/** Feeds readings (time in ms) and answers with the class to show, a 0..1 halo level and the current noise floor. */
export class LevelTracker {
  constructor() { this.window = []; this.clipUntil = 0; }
  push(t, peak, rms) {
    this.window.push([t, rms]);
    while (this.window.length && this.window[0][0] < t - FLOOR_WINDOW_MS) this.window.shift();
    const floor = Math.min(...this.window.map((w) => w[1]));
    if (peak >= CLIP_PEAK) this.clipUntil = t + CLIP_HOLD_MS;
    const cls = t < this.clipUntil ? 'clip' : levelClass({ peak, rms, floor });
    return { cls, lvl: Math.min(1, rms * 4), floor };
  }
  reset() { this.window = []; this.clipUntil = 0; }
}
