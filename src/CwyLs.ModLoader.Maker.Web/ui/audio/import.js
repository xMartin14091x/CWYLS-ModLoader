// Importing a take from a file (drop or pick): any audio the browser can decode (WAV, MP3, M4A, Ogg ...).
// Needs Web Audio, so it is not Node-testable; actions are tested with an injected `decodeFile`.

/** @returns {Promise<{channels:Float32Array[], sampleRate:number}>} rejects when the file is not audio the browser can read */
export async function decodeFile(file, AudioContextCtor = globalThis.AudioContext) {
  if (!AudioContextCtor) throw new Error('no Web Audio');
  const ctx = new AudioContextCtor();
  try {
    const buffer = await ctx.decodeAudioData(await file.arrayBuffer());
    const channels = [];
    for (let c = 0; c < buffer.numberOfChannels; c++) channels.push(buffer.getChannelData(c).slice());
    return { channels, sampleRate: buffer.sampleRate };
  } finally { try { await ctx.close(); } catch { /* closed */ } }
}
