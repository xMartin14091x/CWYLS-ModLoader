// The creator's microphone, in the browser. Permission is asked only when `setup()` is called (a click on "Set up mic").
// Echo cancellation, noise suppression and automatic gain are OFF: they are made for calls and would colour a dub.
// Capture runs in an AudioWorklet (capture-worklet.js); browsers without one get the "can't record here" message and can import.
// Not Node-testable (needs Web Audio): actions are tested with a fake that has this same interface, and the real one
// by the headless Chrome run with its fake microphone.
//
// Interface (also what the test fake implements):
//   supported() -> boolean
//   setup(deviceId?) -> Promise<{devices:[{id,label}], deviceId}>   rejects with an Error named NotAllowedError / NotFoundError / ...
//   onLevel(cb) -> unsubscribe        cb({peak, rms}) about 40 times a second while set up
//   start() / stop() -> Promise<{channels:[Float32Array], sampleRate}>
//   release()

export function createCapture({ nav = globalThis.navigator, AudioContextCtor = globalThis.AudioContext, workletUrl } = {}) {
  let stream = null, ctx = null, node = null, source = null, recording = false, chunks = [];
  const listeners = new Set();
  let generation = 0, pendingSetup = null;

  const supported = () => !!(nav && nav.mediaDevices && nav.mediaDevices.getUserMedia && AudioContextCtor && globalThis.AudioWorkletNode);

  function release() {
    ++generation; pendingSetup = null;
    recording = false;
    try { node?.port.close(); node?.disconnect(); } catch { /* already gone */ }
    try { source?.disconnect(); } catch { /* already gone */ }
    stream?.getTracks().forEach((t) => t.stop());
    try { ctx?.close(); } catch { /* already closed */ }
    stream = ctx = node = source = null;
  }

  function setup(deviceId) {
    if (pendingSetup) return pendingSetup;
    release();
    const version = generation;
    const task = acquire(deviceId, version);
    pendingSetup = task;
    task.finally(() => { if (pendingSetup === task) pendingSetup = null; }).catch(() => {});
    return task;
  }

  async function acquire(deviceId, version) {
    let localStream, localCtx, localNode, localSource;
    const check = () => { if (version !== generation) { const e = new Error('capture released'); e.name = 'AbortError'; throw e; } };
    try {
      const audio = { echoCancellation: false, noiseSuppression: false, autoGainControl: false, channelCount: 1 };
      if (deviceId) audio.deviceId = { exact: deviceId };
      localStream = await nav.mediaDevices.getUserMedia({ audio });
      check();
      localCtx = new AudioContextCtor();
      await localCtx.audioWorklet.addModule(workletUrl); check();
      await localCtx.resume(); check();
      localSource = localCtx.createMediaStreamSource(localStream);
      localNode = new AudioWorkletNode(localCtx, 'cwyls-capture', { numberOfInputs: 1, numberOfOutputs: 0 });
      localNode.port.onmessage = (e) => {
        if (version !== generation) return;
        const m = e.data;
        for (const cb of listeners) cb({ peak: m.peak, rms: m.rms });
        if (recording && m.frames) chunks.push(m.frames);
      };
      localSource.connect(localNode);
      const all = await nav.mediaDevices.enumerateDevices(); check();
      const devices = all.filter((d) => d.kind === 'audioinput').map((d, i) => ({ id: d.deviceId, label: d.label || `Microphone ${i + 1}` }));
      const active = localStream.getAudioTracks()[0]?.getSettings?.().deviceId || deviceId || devices[0]?.id || '';
      stream = localStream; ctx = localCtx; node = localNode; source = localSource;
      return { devices, deviceId: active };
    } catch (e) {
      try { localNode?.port.close(); localNode?.disconnect(); } catch { /* already gone */ }
      try { localSource?.disconnect(); } catch { /* already gone */ }
      localStream?.getTracks().forEach(t => t.stop());
      try { await localCtx?.close(); } catch { /* already closed */ }
      throw e;
    }
  }

  return {
    supported, setup, release,
    onLevel(cb) { listeners.add(cb); return () => listeners.delete(cb); },
    start() { chunks = []; recording = true; node?.port.postMessage({ cmd: 'record', on: true }); },
    async stop() {
      recording = false;
      node?.port.postMessage({ cmd: 'record', on: false });
      await new Promise((r) => setTimeout(r, 60)); // let the last block arrive
      const total = chunks.reduce((n, c) => n + c.length, 0), out = new Float32Array(total);
      let at = 0;
      for (const c of chunks) { out.set(c, at); at += c.length; }
      chunks = [];
      return { channels: [out], sampleRate: ctx ? ctx.sampleRate : 48000 };
    }
  };
}
