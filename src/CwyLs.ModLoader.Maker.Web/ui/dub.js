// The dub half of the actions: microphone, countdown, recording, import, takes, trim, even-out, origin and consent, publishing and
// the Play buttons. It shares the per-line mutation queue, the revision map and the stale-read guards of actions.js (passed in), and
// uses injected `mic`, `player` and `decodeFile` so every rule runs in Node with fakes.
//
// "Play my version" follows W2's order exactly (STUDIO-JS.md): save the subtitle if dirty, prepare and upload the explicitly selected
// take, publish that take, check every acknowledgement, then ask the game to play. The first failure stops the sequence. Nothing is
// ever replayed automatically, least of all `studioPlay` after a timeout.
import { StudioError } from './api.js';
import { errorKey, takeVM } from './api/model.js';
import { LevelTracker } from './audio/levels.js';
import { downmixToMono, resample, encodeWavPcm16Mono, decodePcm16Wav, prepareRender, renderSamples, MAX_SECONDS, TARGET_RATE } from './audio/wav.js';
import { peaks as computePeaks } from './audio/peaks.js';
import { canAddTake, canRemoveTake, publishPreflight, CONSENT_VERSION } from './logic/takes.js';
import { currentTake, canDub, canPlay, featureOn, hasPackVoiceDefault, takeOriginDraft, isReadOnly, packOf } from './selectors.js';

const COUNTDOWN = 3;
/** The microphone is let go after this long with no recording and no count-in (GATE-M4): the browser's recording indicator goes out. */
export const MIC_IDLE_MS = 60000;
const MIN_SECONDS = 0.2;

export function createDub(int) {
  const micIdleMs = int.micIdleMs ?? MIC_IDLE_MS;
  const { store, api, keyOf, queue, revs, refetchLine, patchLine, settle, newOp, toast, timers, now, handleError, mic, player, decodeFile } = int;
  const S = () => store.get();
  const buffers = new Map();          // takeId -> {samples (mono), rate}: the creator's own audio, kept in memory for waveform, trim and playback
  const tracker = new LevelTracker();
  let unsubLevel = null, recTimer = 0, countTimer = 0, speakTimer = 0, idleTimer = 0, lastDeviceId;

  const consentVersion = () => S().consentVersion || CONSENT_VERSION;
  const lineCtx = () => {
    const s = S(), line = s.line;
    return line ? { packId: s.packId, lang: s.packLang, id: line.id, key: keyOf(s.packId, s.packLang, line.id), line } : null;
  };
  const isCurrent = (c) => !!c && c.key === lineCtx()?.key;
  const fail = (e) => { handleError(e); toast(e instanceof StudioError ? errorKey(e.code) : 'e_internal', 'warn'); };
  const takeNo = (take) => (S().line?.takes || []).findIndex((t) => t.id === take.id) + 1;

  // ---------------- local audio of takes ----------------
  async function ensureBuffer(take) {
    const have = buffers.get(take.id);
    if (have) return have;
    const c = lineCtx();
    const blob = await api.takeAudio(c.packId, c.lang, c.id, take.id, 'source');
    const dec = decodePcm16Wav(new Uint8Array(await blob.arrayBuffer()));
    if (!dec) throw new StudioError('unsupported_media_type', 'the stored take is not a WAV this page can read');
    const b = { samples: dec.samples, rate: dec.sampleRate };
    buffers.set(take.id, b);
    return b;
  }
  const setPeaks = (takeId, samples) => store.set({ peaks: { ...S().peaks, [takeId]: computePeaks(samples, 96) } });

  /** Reads back the waveforms of the line's takes (the creator's own uploads only) after a line was opened. */
  async function hydrate(line, isCurrent) {
    for (const t of (line.takes || []).slice(0, 3)) {
      if (S().peaks[t.id] || !t.sourcePresent) continue;
      try { const b = await ensureBuffer(t); if (isCurrent()) setPeaks(t.id, b.samples); } catch { /* a take without readable audio just shows no waveform */ }
    }
  }

  // ---------------- microphone ----------------
  const stopIdleClock = () => { timers.clearTimeout(idleTimer); idleTimer = 0; };
  /** (Re)starts the 60 s countdown to letting the microphone go. Only an idle, set-up mic has one. */
  function startIdleClock() {
    stopIdleClock();
    if (S().mic !== 'ready') return;
    idleTimer = timers.setTimeout(() => { idleTimer = 0; releaseMic(); }, micIdleMs);
  }
  /**
   * Lets the microphone go (idle for 60 s, or the page was hidden) unless a count-in or a recording is running.
   * The screen shows "mic is off"; R or "Set up mic" takes it again (permission was already given, so no prompt).
   */
  function releaseMic() {
    const s = S();
    if (s.mic !== 'ready' || s.rec !== 'idle') return false;
    stopIdleClock();
    unsubLevel?.(); unsubLevel = null;
    mic.release?.();
    tracker.reset();
    store.set({ mic: 'off', level: 'idle', lvl: 0 });
    return true;
  }

  async function setupMic(deviceId = lastDeviceId) {
    if (!canDub(S()) || S().mic === 'asking') return false;
    if (!mic.supported()) { store.set({ mic: 'none', micError: 'mic_unsupported' }); return false; }
    store.set({ mic: 'asking', micError: null });
    try {
      const r = await mic.setup(deviceId);
      unsubLevel?.();
      tracker.reset();
      unsubLevel = mic.onLevel(({ peak, rms }) => {
        const l = tracker.push(now(), peak, rms);
        const s = S();
        if (s.level !== l.cls || Math.abs((s.lvl || 0) - l.lvl) > 0.04) store.set({ level: l.cls, lvl: l.lvl });
      });
      lastDeviceId = r.deviceId;
      store.set({ mic: 'ready', devices: r.devices.map((d) => d.label), deviceIds: r.devices.map((d) => d.id), device: r.devices.find((d) => d.id === r.deviceId)?.label || r.devices[0]?.label || '', level: 'idle', lvl: 0 });
      startIdleClock();
      return true;
    } catch (e) {
      const key = e && e.name === 'NotFoundError' ? 'mic_none' : e && e.name === 'NotSupportedError' ? 'mic_unsupported' : 'mic_denied';
      store.set({ mic: 'none', micError: key });
      return false;
    }
  }
  const selectDevice = (label) => { const s = S(), i = (s.devices || []).indexOf(label); if (i >= 0 && s.deviceIds?.[i]) return setupMic(s.deviceIds[i]); return undefined; };

  // ---------------- recording ----------------
  function clearRecTimers() { timers.clearTimeout(recTimer); timers.clearTimeout(countTimer); recTimer = countTimer = 0; }

  async function toggleRecord() {
    let s = S();
    if (s.rec === 'recording') return stopRecord();
    if (s.rec === 'countdown') return cancelRecord();
    if (s.mic === 'off') { await setupMic(); s = S(); } // R wakes a released microphone; the 3-2-1 that follows covers its start-up
    if (s.mic !== 'ready') { toast('t_needMic', 'warn'); return undefined; }
    if (!canDub(s) || !s.line) { toast(canDub(s) ? 't_ro' : 't_ro', 'warn'); return undefined; }
    const gate = canAddTake(s.line.takes);
    if (!gate.ok) { toast(gate.reason, 'warn'); return undefined; }
    // a silent 3-2-1: nothing reaches the recorder until it ends, so the creator's "ready?" never ends up in the take
    stopIdleClock();
    store.set({ rec: 'countdown', count: COUNTDOWN });
    const tick = () => {
      const n = S().count - 1;
      if (n > 0) { store.set({ count: n }); countTimer = timers.setTimeout(tick, 1000); return; }
      beginRecording();
    };
    countTimer = timers.setTimeout(tick, 1000);
    return undefined;
  }

  function beginRecording() {
    clearRecTimers();
    mic.start();
    const t0 = now(), live = [];
    store.set({ rec: 'recording', recElapsed: 0, livePeaks: [] });
    const loop = () => {
      const elapsed = (now() - t0) / 1000, l = S();
      live.push(Math.min(1, (l.lvl || 0)));
      store.set({ recElapsed: elapsed, livePeaks: live.slice(-96) });
      if (elapsed >= MAX_SECONDS - 0.5) { stopRecord(); return; }
      recTimer = timers.setTimeout(loop, 100);
    };
    recTimer = timers.setTimeout(loop, 100);
  }

  async function stopRecord() {
    if (S().rec !== 'recording') return;
    const c = lineCtx();
    clearRecTimers();
    store.set({ rec: 'idle' });
    let decoded;
    try {
      decoded = await mic.stop();
    } catch (e) { releaseMic(); fail(e); return; }
    releaseMic();
    try {
      await addTake(decoded, c);
    } catch (e) { fail(e); }
  }

  function cancelRecord() {
    const s = S();
    if (s.rec === 'idle') return;
    clearRecTimers();
    if (s.rec === 'recording') mic.stop().catch(() => {});
    store.set({ rec: 'idle', count: COUNTDOWN });
    releaseMic();
  }

  async function importFile(file) {
    if (!file) return false;
    if (!canDub(S())) { toast('t_ro', 'warn'); return false; }
    const c = lineCtx();
    try {
      const decoded = await decodeFile(file);
      const ok = await addTake(decoded, c);
      if (ok) toast('t_import', 'ok', { f: file.name || '' });
      return ok;
    } catch { toast('t_importBad', 'warn'); return false; }
  }

  // ---------------- takes ----------------
  /** Makes a new take from audio: create it (unconfirmed, origin "own"), upload the source. A half-made take is removed again. */
  async function addTake(decoded, c = lineCtx()) {
    // Selection changes during decode/stop cancel the import rather than attaching audio to another dialogue.
    if (!isCurrent(c) || !canDub(S())) return false;
    const gate = canAddTake(c.line.takes);
    if (!gate.ok) { toast(gate.reason, 'warn'); return false; }
    let mono = downmixToMono(decoded.channels), rate = decoded.sampleRate;
    if (rate > TARGET_RATE) { mono = resample(mono, rate, TARGET_RATE); rate = TARGET_RATE; }
    const secs = mono.length / rate;
    if (secs > MAX_SECONDS) { toast('e_too_large', 'warn'); return false; }
    if (secs < MIN_SECONDS) { toast('t_tooShort', 'warn'); return false; }
    const wav = encodeWavPcm16Mono(mono, rate);
    const usePackDefault = featureOn(S(), 'voiceDefault') && hasPackVoiceDefault(S());
    let takeId = null, error = null;
    await queue(c.key, async () => {
      try {
        const createBody = usePackDefault
          ? { expectedRevision: revs.get(c.key), operationId: newOp() }
          : { expectedRevision: revs.get(c.key), operationId: newOp(), origin: 'own', consent: { confirmed: false } };
        const created = await api.createTake(c.packId, c.lang, c.id, createBody);
        takeId = created.take.id; revs.set(c.key, created.revision);
        const up = await api.putTakeAudio(c.packId, c.lang, c.id, takeId, { kind: 'source', expectedRevision: revs.get(c.key), operationId: newOp() }, new Blob([wav], { type: 'audio/wav' }));
        revs.set(c.key, up.revision);
        int.dispatchDraft?.(c.key, { type: 'revision', revision: up.revision });
        if ((S().line?.status || c.line.status) === 'todo' && featureOn(S(), 'workflow')) {
          try {
            const wf = await api.updateWorkflow(c.packId, c.lang, c.id, { expectedRevision: revs.get(c.key), operationId: newOp(), status: 'draft' });
            revs.set(c.key, wf.revision);
            int.dispatchDraft?.(c.key, { type: 'revision', revision: wf.revision });
            int.refreshGroupsSoon?.();
          } catch { /* workflow transition is best-effort inside addTake */ }
        }
      } catch (e) {
        error = e;
        if (takeId) { // do not leave an empty take behind
          try { const del = await api.deleteTake(c.packId, c.lang, c.id, takeId, { expectedRevision: revs.get(c.key), operationId: newOp() }); revs.set(c.key, del.revision); } catch { /* the refetch below shows what is left */ }
        }
      }
    });
    if (error) { fail(error); await refetchLine(c.id, c); return false; }
    buffers.set(takeId, { samples: mono, rate });
    await refetchLine(c.id, c);
    if (!isCurrent(c)) return true;
    setPeaks(takeId, mono);
    const sAfter = S();
    const shouldPromptOnce = !usePackDefault && featureOn(sAfter, 'voiceDefault') && !hasPackVoiceDefault(sAfter) && !sAfter.promptedPackVoice?.[c.packId];
    store.set({
      selTakeId: takeId,
      ...(shouldPromptOnce ? {
        menu: 'consent',
        promptedPackVoice: { ...(sAfter.promptedPackVoice || {}), [c.packId]: true },
        firstPackTakeId: takeId
      } : {})
    });
    toast('t_take', 'ok', { n: (S().line?.takes || []).findIndex((t) => t.id === takeId) + 1 });
    return true;
  }

  /** Applies a take answer to the screen at once (no read needed): the take in the line's list and the line's revision. */
  function applyTake(c, res) {
    revs.set(c.key, res.revision);
    const s = S();
    if (isCurrent(c) && s.line) {
      const nextTakes = s.line.takes.map((t) => (t.id === res.take.id ? takeVM(res.take) : t));
      store.set({ line: { ...s.line, takes: nextTakes, revision: res.revision } });
      int.onTakesChanged?.(c.key, nextTakes);
    }
    patchLine(c.id, { revision: res.revision }, c.key);
  }

  async function updateTake(take, patch) {
    const c = lineCtx();
    if (!c || !canDub(S())) return false;
    let ok = false;
    await queue(c.key, async () => {
      try { applyTake(c, await api.updateTake(c.packId, c.lang, c.id, take.id, { expectedRevision: revs.get(c.key), operationId: newOp(), ...patch })); ok = true; }
      catch (e) { fail(e); if (e.code === 'conflict') refetchLine(c.id); }
    });
    return ok;
  }
  const selected = () => currentTake(S());
  const setOrigin = (origin) => { const t = selected(); return t && t.origin !== origin ? updateTake(t, { origin }) : undefined; };
  // Choosing an origin never changes saved metadata. Save sends origin + fresh consent together.
  function chooseTakeOrigin(origin) {
    const t = selected(), s = S();
    if (!t || !canDub(s) || !['own', 'ai-assisted', 'ai-generated'].includes(origin) || takeOriginDraft(s)?.saving) return;
    if (origin === t.origin) { store.set({ takeOriginDraft: null }); return; }
    store.set({ takeOriginDraft: { packId: s.packId, lang: s.packLang, lineId: s.line.id, takeId: t.id, origin, confirmed: false, saving: false } });
  }
  function confirmTakeOrigin(confirmed) {
    const d = takeOriginDraft(S());
    if (d && !d.saving) store.set({ takeOriginDraft: { ...d, confirmed: !!confirmed } });
  }
  async function saveTakeOrigin() {
    const d = takeOriginDraft(S()), t = selected();
    if (!d || !d.confirmed || d.saving || !t || !canDub(S())) return false;
    const pending = { ...d, saving: true };
    store.set({ takeOriginDraft: pending });
    const ok = await updateTake(t, { origin: d.origin, consent: { confirmed: true, statementVersion: consentVersion() } });
    // A late answer must not clear another take's newer choice.
    if (S().takeOriginDraft === pending) store.set({ takeOriginDraft: ok ? null : { ...d, saving: false } });
    return ok;
  }
  const setConsent = async (confirmed) => {
    const t = selected();
    if (!t) return undefined;
    const s0 = S();
    const resumeKeep = confirmed && s0.pendingKeepNext;
    const shouldSetPackDefault = confirmed && featureOn(s0, 'voiceDefault') && (!hasPackVoiceDefault(s0) || s0.firstPackTakeId === t.id);
    store.set({ menu: null, pendingKeepNext: false, ...(confirmed ? { sessionConsent: true, sessionOrigin: t.origin || 'own' } : {}) });
    const ok = await updateTake(t, { consent: confirmed ? { confirmed: true, statementVersion: consentVersion() } : { confirmed: false } });
    if (ok && shouldSetPackDefault) {
      await int.savePackVoice?.(t.origin || 'own', true, { silent: true });
    }
    if (ok && resumeKeep) await int.onConsentForKeepNext?.();
    return ok;
  };
  const setEven = (even) => { const t = selected(); return t ? updateTake(t, { evenOutVolume: !!even }) : undefined; };

  /** While a trim handle is dragged only the screen moves; the loader hears about it when the handle is let go. */
  function dragTrim(start, end) { const t = selected(); if (t) store.set({ trimDraft: { takeId: t.id, start, end } }); }
  async function commitTrim(start, end) {
    const t = selected(), draft = S().trimDraft;
    if (!t) return false;
    const total = t.sourceDurationSec || 0, s0 = start ?? draft?.start ?? t.trimStartSec, e0 = end ?? draft?.end ?? t.trimEndSec ?? total;
    // an end at (or beyond) the rounded end of the source clears the trim: null means "play to the end of the source".
    // Only the real end counts: a keyboard step 10 ms before it must stick (it used to snap back to the end).
    const preview = { takeId: t.id, start: s0, end: e0 };
    store.set({ trimDraft: preview });
    const ok = await updateTake(t, { trimStartSec: Math.max(0, +s0.toFixed(3)), trimEndSec: +e0.toFixed(3) >= +total.toFixed(3) ? null : +e0.toFixed(3) });
    // Key repeat can queue another trim before this acknowledgement. Keep its newer preview visible.
    if (S().trimDraft === preview) store.set({ trimDraft: null });
    return ok;
  }

  async function removeTake(id) {
    const c = lineCtx();
    if (!c || !canDub(S())) return false;
    const take = c.line.takes.find((t) => t.id === id);
    if (!take) return false;
    const gate = canRemoveTake(take, c.line.publishedTakeId);
    if (!gate.ok) { toast(gate.reason, 'warn'); return false; }
    const n = takeNo(take);
    let ok = false;
    await queue(c.key, async () => {
      try { const r = await api.deleteTake(c.packId, c.lang, c.id, id, { expectedRevision: revs.get(c.key), operationId: newOp() }); revs.set(c.key, r.revision); ok = true; }
      catch (e) { fail(e); }
    });
    if (ok) {
      buffers.delete(id);
      if (S().selTakeId === id) store.set({ selTakeId: null });
      await refetchLine(c.id, c);
      toast('t_take_removed', 'ok', { n });
    }
    return ok;
  }

  async function toggleFavourite(id) {
    const c = lineCtx();
    if (!c || !canDub(S())) return false;
    const next = c.line.favouriteTakeId === id ? null : id;
    let ok = false;
    await queue(c.key, async () => {
      try { const r = await api.setFavourite(c.packId, c.lang, c.id, { expectedRevision: revs.get(c.key), operationId: newOp(), takeId: next }); revs.set(c.key, r.revision); ok = true; }
      catch (e) { fail(e); }
    });
    if (ok && isCurrent(c)) { store.set({ line: { ...S().line, favouriteTakeId: next } }); patchLine(c.id, { revision: revs.get(c.key) }, c.key); }
    return ok;
  }

  // ---------------- local playback ----------------
  async function playTake() {
    const s = S(), take = selected();
    if (s.playingTake) { stopTake(); return; }
    if (!take) return;
    try {
      const b = await ensureBuffer(take);
      const t = selected() || take;
      const samples = renderSamples({ channels: [b.samples], sampleRate: b.rate }, { trimStartSec: t.trimStartSec, trimEndSec: t.trimEndSec, evenOutVolume: t.evenOutVolume });
      const dur = samples.length / TARGET_RATE;
      store.set({ playingTake: { start: now(), dur, from: (t.trimStartSec || 0) / (t.sourceDurationSec || dur), to: (t.trimEndSec ?? t.sourceDurationSec ?? dur) / (t.sourceDurationSec || dur) } });
      player.play(samples, TARGET_RATE, () => store.set({ playingTake: false }));
    } catch (e) { fail(e); }
  }
  function stopTake() { player.stop(); store.set({ playingTake: false }); }

  // ---------------- publish and play ----------------
  /** Prepares and uploads the render when the loader does not hold a current one. Returns true when it uploaded. */
  async function ensureRender(take) {
    if (take.renderReady) return false;
    const c = lineCtx(), b = await ensureBuffer(take);
    const { wav, seconds } = prepareRender({ channels: [b.samples], sampleRate: b.rate }, { trimStartSec: take.trimStartSec, trimEndSec: take.trimEndSec, evenOutVolume: take.evenOutVolume });
    if (seconds < 0.05) throw new StudioError('bad_request', 'the trimmed take is empty');
    let error = null;
    await queue(c.key, async () => {
      try { applyTake(c, await api.putTakeAudio(c.packId, c.lang, c.id, take.id, { kind: 'render', expectedRevision: revs.get(c.key), operationId: newOp() }, new Blob([wav], { type: 'audio/wav' }))); }
      catch (e) { error = e; }
    });
    if (error) throw error;
    return true;
  }

  async function publishTake(c, takeId) {
    let res = null, error = null;
    await queue(c.key, async () => {
      if (!isCurrent(c)) return; // the creator moved to another line: S().line is no longer this one
      try {
        res = await api.publish(c.packId, c.lang, c.id, { expectedRevision: revs.get(c.key), expectedClipRevision: S().line.clipRevision, operationId: newOp(), takeId });
        revs.set(c.key, res.revision);
        int.dispatchDraft?.(c.key, { type: 'revision', revision: res.revision });
        if ((S().line?.status || c.line.status) === 'todo' && featureOn(S(), 'workflow')) {
          try {
            const wf = await api.updateWorkflow(c.packId, c.lang, c.id, { expectedRevision: revs.get(c.key), operationId: newOp(), status: 'draft' });
            revs.set(c.key, wf.revision);
            int.dispatchDraft?.(c.key, { type: 'revision', revision: wf.revision });
            int.refreshGroupsSoon?.();
          } catch { /* best-effort workflow transition */ }
        }
      } catch (e) { error = e; }
    });
    if (error) throw error;
    return res;
  }

  async function unpublish() {
    const c = lineCtx();
    if (!c || !canDub(S()) || !c.line.publishedTakeId) return false;
    await refetchLine(c.id, c);
    let ok = false;
    await queue(c.key, async () => {
      try { const r = await api.unpublish(c.packId, c.lang, c.id, { expectedRevision: revs.get(c.key), expectedClipRevision: S().line.clipRevision, operationId: newOp() }); revs.set(c.key, r.revision); ok = true; }
      catch (e) { fail(e); }
    });
    if (ok) { await refetchLine(c.id, c); toast('t_unpublished'); }
    return ok;
  }

  function showSpeaking(kind, seconds) {
    timers.clearTimeout(speakTimer);
    store.set({ speaking: kind, speakDur: Math.max(0.5, seconds || 2) });
    speakTimer = timers.setTimeout(() => store.set({ speaking: null }), (Math.max(0.5, seconds || 2) + 0.6) * 1000);
  }

  async function playOriginal() {
    const c = lineCtx(), s = S();
    if (!c || s.playing) return false;
    if (!canPlay(s)) { toast('feat_playback', 'warn'); return false; }
    store.set({ playing: true, waiting: 'orig' });
    try {
      await api.play({ id: c.id, variant: 'original', lang: c.lang, packId: c.packId });
      showSpeaking('orig', c.line.durationSec);
      toast('t_orig');
      return true;
    } catch (e) { fail(e); return false; } finally { store.set({ playing: false, waiting: null }); }
  }

  async function playMine() {
    const c = lineCtx(), s = S();
    if (!c || s.playing) return false;
    if (!s.conn?.connected) { toast('dis_off', 'warn'); return false; }
    if (packOf(s)?.restartRequired) { toast('dis_first', 'warn'); return false; }
    if (isReadOnly(s)) { toast('dis_ro', 'warn'); return false; }
    if (!canPlay(s) && !canDub(s)) { toast('feat_playback', 'warn'); return false; }
    const take0 = selected();
    // A line whose dub is already in the pack but has no Studio take (a dub made outside Studio, e.g. with v1 or by
    // hand): play that dub as it is. Nothing to save or publish; the game comes to the front like Play original.
    if ((!take0 || !take0.sourcePresent) && c.line.ownedDub && canPlay(s)) {
      store.set({ playing: true, waiting: 'mine' });
      try {
        if (!await settle(c.key) || !isCurrent(c)) return false;
        const r = await api.play({ id: c.id, variant: 'dub', lang: c.lang, packId: c.packId });
        showSpeaking('mine', (r && r.durationSec) || c.line.dubDurationSec || c.line.durationSec);
        toast('t_mine_existing');
        return true;
      } catch (e) { fail(e); return false; } finally { store.set({ playing: false, waiting: null }); }
    }
    let inPack = false, saved = null; // set once the take is known to be in the pack: a later failure is only about playing
    store.set({ playing: true, waiting: 'mine' });
    try {
      // 1. the text: flush and wait, so the game plays the subtitle that is on screen
      if (!await settle(c.key) || !isCurrent(c)) return false;
      // 2. the explicitly selected take, with its consent
      const pre = publishPreflight(take0, { packEditable: !isReadOnly(S()) && (!take0 || canDub(S())), subtitle: S().line.text });
      if (!pre.ok) { toast(pre.reason, 'warn'); if (pre.reason === 'need_consent') store.set({ menu: 'consent' }); return false; }
      if (pre.variant === 'sub') {
        if (!canPlay(S())) { toast('feat_playback', 'warn'); return false; }
        const r = await api.play({ id: c.id, variant: 'sub', lang: c.lang, packId: c.packId, expectedRevision: revs.get(c.key) });
        showSpeaking('mine', r?.durationSec || c.line.durationSec);
        toast('t_mine_sub');
        return true;
      }
      // 3. its prepared audio
      const uploaded = await ensureRender(take0);
      // 4. publish it (fresh revisions first: our own writes moved them)
      await refetchLine(c.id, c);
      if (!isCurrent(c)) return false;
      const fresh = S().line.takes.find((t) => t.id === take0.id) || take0;
      if (uploaded || S().line.publishedTakeId !== fresh.id) {
        saved = await publishTake(c, fresh.id);
        if (!saved) return false;
        inPack = true;
        if (saved.live === false) toast('t_live_note', 'warn', { note: saved.note || '' });
        await refetchLine(c.id, c);
      } else inPack = true;
      // 5. only now the game is asked; a failure above never gets here (and a loader without `playback` stops after publishing)
      if (!canPlay(S())) { if (saved && saved.live !== false) toast('t_mine'); else if (!saved) toast('feat_playback', 'warn'); return true; }
      await api.play({ id: c.id, variant: 'dub', lang: c.lang, packId: c.packId, expectedRevision: revs.get(c.key) });
      showSpeaking('mine', (fresh.renderDurationSec ?? fresh.sourceDurationSec) || c.line.durationSec);
      toast('t_mine');
      return true;
    } catch (e) {
      if (inPack) {
        // the take is safe in the pack; only the game step failed. Say exactly that (GATE-M4).
        handleError(e);
        if (e instanceof StudioError && e.code === 'game_paused') toast('t_in_pack_hint', 'warn');
        else toast('t_in_pack_play_failed', 'warn', { reason: int.reasonText ? int.reasonText(e) : '' });
      } else fail(e);
      return false;
    } finally { store.set({ playing: false, waiting: null }); }
  }

  async function quickRecord() {
    if (S().rec === 'idle' && (S().mic === 'none' || !lastDeviceId)) await setupMic();
    return toggleRecord();
  }

  /** Publishes the selected take into the pack (confirming consent automatically when already confirmed in this session) without switching to the game. */
  async function keepTake() {
    const c = lineCtx(), s = S();
    if (!c || !canDub(s)) { toast('t_ro', 'warn'); return false; }
    const take0 = selected();
    if (!take0 || !take0.sourcePresent) { toast('noTakes', 'warn'); return false; }
    try {
      if (!await settle(c.key) || !isCurrent(c)) return false;
      if (!(take0.consent?.confirmed && take0.consent?.statementVersion === consentVersion())) {
        if (S().sessionConsent && take0.origin === (S().sessionOrigin || 'own')) {
          const ok = await updateTake(take0, { consent: { confirmed: true, statementVersion: consentVersion() } });
          if (!ok) return false;
        } else {
          toast('need_consent', 'warn');
          store.set({ menu: 'consent', pendingKeepNext: true });
          return 'consent';
        }
      }
      const curTake = selected() || take0;
      const uploaded = await ensureRender(curTake);
      await refetchLine(c.id, c);
      if (!isCurrent(c)) return false;
      const fresh = S().line.takes.find((t) => t.id === take0.id) || curTake;
      if (uploaded || S().line.publishedTakeId !== fresh.id) {
        if (!await publishTake(c, fresh.id)) return false;
        await refetchLine(c.id, c);
      } else if ((S().line?.status || c.line.status) === 'todo' && featureOn(S(), 'workflow')) {
        await queue(c.key, async () => {
          try {
            const wf = await api.updateWorkflow(c.packId, c.lang, c.id, { expectedRevision: revs.get(c.key), operationId: newOp(), status: 'draft' });
            revs.set(c.key, wf.revision);
            int.dispatchDraft?.(c.key, { type: 'revision', revision: wf.revision });
            int.refreshGroupsSoon?.();
          } catch { /* best-effort workflow transition */ }
        });
        await refetchLine(c.id, c);
      }
      return true;
    } catch (e) {
      fail(e);
      return false;
    }
  }

  function release() { clearRecTimers(); stopIdleClock(); timers.clearTimeout(speakTimer); unsubLevel?.(); mic.release?.(); player.stop(); }

  return {
    hydrate, setupMic, releaseMic, selectDevice, toggleRecord, quickRecord, cancelRecord, importFile, addTake, updateTake, removeTake, toggleFavourite, setOrigin, chooseTakeOrigin, confirmTakeOrigin, saveTakeOrigin, setConsent, setEven,
    dragTrim, commitTrim, playTake, stopTake, unpublish, playOriginal, playMine, keepTake, release, ensureRender, _buffers: buffers
  };
}
