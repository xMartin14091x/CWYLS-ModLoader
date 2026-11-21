// Waveform peaks of the creator's own takes (never of game audio, which never reaches the page).

/** Max absolute sample per bucket, `buckets` values in [0, 1]. */
export function peaks(samples, buckets = 96) {
  const out = new Array(buckets).fill(0);
  if (!samples.length) return out;
  for (let b = 0; b < buckets; b++) {
    const from = Math.floor(b * samples.length / buckets), to = Math.max(from + 1, Math.floor((b + 1) * samples.length / buckets));
    let max = 0;
    for (let i = from; i < to && i < samples.length; i++) max = Math.max(max, Math.abs(samples[i]));
    out[b] = Math.min(1, max);
  }
  return out;
}
