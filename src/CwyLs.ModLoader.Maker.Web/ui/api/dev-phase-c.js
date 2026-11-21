// In-memory Phase C (contract v2.3) extension for the dev adapter (`tests/dev/dev-backend.js`), plus a pure-JS
// 64x64 Dusk PNG icon generator used when the creator picks a preset icon or in headless tests.
import { StudioError } from './model.js';

const SEMVER = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/;
const ICON_MAX = 512 * 1024;

function crc32(bytes) {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) {
    c ^= bytes[i];
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (c & 1 ? 0xedb88320 : 0);
  }
  return (c ^ 0xffffffff) >>> 0;
}

function adler32(bytes) {
  let a = 1, b = 0;
  for (let i = 0; i < bytes.length; i++) {
    a = (a + bytes[i]) % 65521;
    b = (b + a) % 65521;
  }
  return ((b << 16) | a) >>> 0;
}

function pngChunk(type, data) {
  const out = new Uint8Array(12 + data.length);
  const v = new DataView(out.buffer);
  v.setUint32(0, data.length, false);
  for (let i = 0; i < 4; i++) out[4 + i] = type.charCodeAt(i);
  out.set(data, 8);
  v.setUint32(8 + data.length, crc32(out.subarray(4, 8 + data.length)), false);
  return out;
}

/** Builds a valid 64x64 RGB PNG image (Dusk sky & moon gradient) that satisfies the 64..1024 square PNG rule. */
export function makeDefaultIconPng(seed = 0) {
  const w = 64, h = 64, rowLen = 1 + w * 3, raw = new Uint8Array(h * rowLen);
  const tint = (seed & 3) * 14;
  for (let y = 0; y < h; y++) {
    const base = y * rowLen;
    raw[base] = 0; // filter 0
    for (let x = 0; x < w; x++) {
      const dx = x - 44, dy = y - 18, moon = dx * dx + dy * dy <= 72;
      const p = base + 1 + x * 3;
      raw[p] = moon ? 255 : Math.min(255, 22 + ((y * 2) | 0) + tint);
      raw[p + 1] = moon ? 215 : Math.min(255, 24 + ((y * 1.3) | 0));
      raw[p + 2] = moon ? 135 : Math.min(255, 62 + ((y * 1.6) | 0));
    }
  }
  // zlib stream with one uncompressed DEFLATE block (raw.length = 12352 <= 65535)
  const z = new Uint8Array(2 + 5 + raw.length + 4);
  z[0] = 0x78; z[1] = 0x01; z[2] = 0x01;
  z[3] = raw.length & 0xff; z[4] = (raw.length >>> 8) & 0xff;
  const nlen = (~raw.length) & 0xffff;
  z[5] = nlen & 0xff; z[6] = (nlen >>> 8) & 0xff;
  z.set(raw, 7);
  new DataView(z.buffer).setUint32(7 + raw.length, adler32(raw), false);

  const ihdr = new Uint8Array(13);
  const iv = new DataView(ihdr.buffer);
  iv.setUint32(0, w, false); iv.setUint32(4, h, false);
  ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
  const sig = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const cIhdr = pngChunk('IHDR', ihdr), cIdat = pngChunk('IDAT', z), cIend = pngChunk('IEND', new Uint8Array(0));
  const png = new Uint8Array(sig.length + cIhdr.length + cIdat.length + cIend.length);
  png.set(sig, 0);
  png.set(cIhdr, sig.length);
  png.set(cIdat, sig.length + cIhdr.length);
  png.set(cIend, sig.length + cIhdr.length + cIdat.length);
  return png;
}

function nextVersions(version) {
  const m = SEMVER.exec(version || '');
  if (!m) return { patch: null, minor: null, major: null };
  const [maj, min, pat] = [Number(m[1]), Number(m[2]), Number(m[3])];
  return { patch: `${maj}.${min}.${m[4] ? pat : pat + 1}`, minor: `${maj}.${min + 1}.0`, major: `${maj + 1}.0.0` };
}

export function attachDevPhaseC(adapter) {
  const dev = adapter.dev;
  if (!dev || adapter.studioPackInfo) return;
  const metaMap = new Map([
    ['satone-th-dub', { author: 'Creator (TH)', description: 'Thai subtitle & dub pack for Satone.', icon: makeDefaultIconPng(0), changelog: { version: '0.3.0', notes: 'Added morning & click reaction dubs.' }, lastExport: null }],
    ['cozy-en-rewrite', { author: 'Cozy Team', description: 'Warm English rewrite for late-night sessions.', icon: null, changelog: null, lastExport: null }],
    ['thai-subs-community', { author: 'Community', description: 'Reference Thai subtitle pack.', icon: null, changelog: null, lastExport: null }]
  ]);
  const zips = new Map();
  const voiceMap = new Map();
  const voiceEntry = (packId) => {
    if (!voiceMap.has(packId)) voiceMap.set(packId, { revNum: 0, origin: null, consent: null, updatedAt: null });
    return voiceMap.get(packId);
  };
  const voiceDto = (v) => ({
    revision: `v${v.revNum}`,
    origin: v.origin,
    consent: v.consent ? { ...v.consent } : null,
    updatedAt: v.updatedAt
  });
  const extra = (id) => {
    if (!metaMap.has(id)) metaMap.set(id, { author: null, description: 'Created with Pack Maker Studio.', icon: null, changelog: null, lastExport: null });
    return metaMap.get(id);
  };
  const findPack = (packId) => {
    const p = dev.packs.find((x) => x.id === packId);
    if (!p) throw new StudioError('not_found', 'unknown pack', { status: 404 });
    return p;
  };

  const origCreate = adapter.studioCreatePack.bind(adapter);
  adapter.studioCreatePack = async (body) => {
    const res = await origCreate(body);
    if (res?.pack?.id) {
      const m = extra(res.pack.id);
      if (body.author && String(body.author).trim()) m.author = String(body.author).trim();
      if (body.description && String(body.description).trim()) m.description = String(body.description).trim();
    }
    return res;
  };

  adapter.studioPackInfo = async (packId) => {
    dev.log.push({ method: 'packInfo', args: [packId] });
    const p = findPack(packId), m = extra(packId);
    return {
      id: p.id, name: p.name, version: p.version || '0.1.0',
      author: m.author, description: m.description,
      languages: [...(p.languages || [])], hasIcon: !!m.icon,
      sizeBytes: 18432 + (m.icon ? m.icon.length : 0),
      pending: p.restartRequired === true, changelog: m.changelog
    };
  };

  adapter.studioPackIcon = async (packId) => {
    dev.log.push({ method: 'packIcon', args: [packId] });
    const p = findPack(packId), m = extra(packId);
    if (p.studioReadOnlyReason === 'reference') throw new StudioError('read_only_pack', 'Reference pack icons are not served.', { status: 403 });
    if (!m.icon) throw new StudioError('not_found', 'This pack has no icon.', { status: 404 });
    return new Blob([m.icon], { type: 'image/png' });
  };

  adapter.studioSavePackIcon = async (packId, png) => {
    dev.log.push({ method: 'savePackIcon', args: [packId] });
    const p = findPack(packId), m = extra(packId);
    if (!p.studioEditable && !p.restartRequired) throw new StudioError('read_only_pack', 'This pack is read-only.', { status: 403 });
    const bytes = png instanceof Uint8Array ? png : new Uint8Array(await png.arrayBuffer());
    if (bytes.length > ICON_MAX) throw new StudioError('too_large', 'A pack icon must be at most 512 KiB.', { status: 413 });
    if (bytes.length < 24 || bytes[0] !== 0x89 || bytes[1] !== 0x50 || bytes[2] !== 0x4e || bytes[3] !== 0x47) {
      throw new StudioError('unsupported_media_type', 'The icon must be a PNG image.', { status: 415 });
    }
    const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const width = v.getUint32(16, false), height = v.getUint32(20, false);
    if (width !== height || width < 64 || width > 1024) {
      throw new StudioError('unsupported_media_type', 'The icon must be square, 64-1024 pixels.', { status: 415 });
    }
    m.icon = bytes;
    dev.emit('maker.pack.changed', { packId, reason: 'icon' });
    return { hasIcon: true, sizeBytes: bytes.length, width, height };
  };

  adapter.studioExportCheck = async (packId) => {
    dev.log.push({ method: 'exportCheck', args: [packId] });
    const p = findPack(packId), m = extra(packId);
    if (p.restartRequired) throw new StudioError('restart_required', 'restart the game', { status: 409 });
    if (!p.studioEditable) throw new StudioError('read_only_pack', 'read-only', { status: 403 });
    const lang = p.languages[0] || 'th';
    let noDub = 0, noSub = 0;
    const gaps = [];
    for (const l of dev.lines) {
      const st = dev.state(packId, lang, l.id);
      if (l.voice && !st.publishedTakeId) { noDub++; if (gaps.length < 20) gaps.push(`${lang}/${l.id}`); }
      if (!st.text) { noSub++; if (gaps.length < 20 && !gaps.includes(`${lang}/${l.id}`)) gaps.push(`${lang}/${l.id}`); }
    }
    const checks = [
      { id: 'manifest', severity: 'ok', message: 'manifest.json is valid.', count: 0, paths: [] },
      { id: 'subtitles', severity: 'ok', message: 'Every subtitle table parses.', count: 0, paths: [] },
      { id: 'audio', severity: 'ok', message: 'Every voice file reads.', count: 0, paths: [] },
      (m.unlabelled || []).length > 0
        ? { id: 'provenance', severity: 'error', message: `${m.unlabelled.length} dub(s) have no valid voice label; label them with the pack default.`, count: m.unlabelled.length, paths: m.unlabelled.slice(0, 20), code: 'provUnlabelled', params: { n: m.unlabelled.length } }
        : { id: 'provenance', severity: 'ok', message: 'Every published dub has a matching disclosure.', count: 0, paths: [] },
      { id: 'size', severity: 'ok', message: 'The pack fits in a browser download.', count: 0, paths: [] },
      { id: 'progress', severity: noDub + noSub > 0 ? 'warning' : 'ok', message: noDub + noSub > 0 ? `${lang}: ${noDub} voiced line(s) without a dub, ${noSub} without a subtitle.` : 'Every line has its subtitle and dub.', count: noDub + noSub, paths: gaps },
      { id: 'links', severity: 'ok', message: 'No links in the pack.', count: 0, paths: [] }
    ];
    const errorCount = checks.filter((c) => c.severity === 'error').length;
    const warningCount = checks.filter((c) => c.severity === 'warning').length;
    return {
      packId, version: p.version || '0.1.0', nextVersions: nextVersions(p.version || '0.1.0'),
      ok: errorCount === 0, errorCount, warningCount, checks,
      files: { included: 24 + (m.icon ? 1 : 0), includedBytes: 48200 + (m.icon ? m.icon.length : 0), excluded: 3, excludedBytes: 4120 },
      changelog: m.changelog, lastExport: m.lastExport
    };
  };

  adapter.studioExport = async (packId, { operationId, expectedVersion, bump = 'patch', notes = null } = {}) => {
    dev.log.push({ method: 'export', args: [packId, { expectedVersion, bump, notes }] });
    if (!/^[0-9a-f]{32}$/.test(operationId || '')) throw new StudioError('bad_request', 'operationId must be 32 hex characters');
    if (!['none', 'patch', 'minor', 'major'].includes(bump)) throw new StudioError('bad_request', 'bad bump');
    const p = findPack(packId), m = extra(packId);
    if (p.restartRequired) throw new StudioError('restart_required', 'restart the game', { status: 409 });
    if (!p.studioEditable) throw new StudioError('read_only_pack', 'read-only', { status: 403 });
    if (expectedVersion !== p.version) throw new StudioError('conflict', 'The pack version changed; run the export check again.', { status: 409 });
    const next = bump === 'none' ? p.version : nextVersions(p.version)[bump];
    const prev = p.version;
    p.version = next;
    const hasNotes = typeof notes === 'string' && notes.trim().length > 0;
    if (hasNotes) m.changelog = { version: next, notes: notes.trim() };
    const fileName = `${packId}-v${next}.zip`; // the loader names exports <pack-id>-v<version>.zip
    const zipBytes = new TextEncoder().encode(`PK\x03\x04${packId}:${next}:${hasNotes ? notes.trim() : ''}`);
    zips.set(`${packId}/${fileName}`, zipBytes);
    const res = {
      packId, version: next, previousVersion: prev, fileName, sizeBytes: zipBytes.length,
      sha256: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
      files: 25, downloadable: true, manifestUpdated: next !== prev, changelogUpdated: hasNotes,
      warningCount: 1, note: 'The loaded pack shows the new version after the next game start.', folder: `CWYLS Mods/.maker-exports/${packId}`
    };
    m.lastExport = { fileName, version: next, sizeBytes: zipBytes.length, createdAt: new Date().toISOString() };
    dev.emit('maker.pack.changed', { packId, reason: 'export' });
    return res;
  };

  adapter.studioDownloadExport = async (packId, fileName) => {
    dev.log.push({ method: 'downloadExport', args: [packId, fileName] });
    const bytes = zips.get(`${packId}/${fileName}`);
    if (!bytes) throw new StudioError('not_found', 'No such export.', { status: 404 });
    return new Blob([bytes], { type: 'application/zip' });
  };

  adapter.studioVoiceDefault = async (packId) => {
    dev.log.push({ method: 'voiceDefault', args: [packId] });
    const p = findPack(packId);
    if (p.restartRequired) throw new StudioError('restart_required', 'restart the game', { status: 409 });
    return voiceDto(voiceEntry(packId));
  };

  adapter.studioSaveVoiceDefault = async (packId, { expectedRevision, operationId, origin = null, consent = null } = {}) => {
    dev.log.push({ method: 'saveVoiceDefault', args: [packId, { expectedRevision, origin, consent }] });
    if (!/^[0-9a-f]{32}$/.test(operationId || '')) throw new StudioError('bad_request', 'operationId must be 32 hex characters');
    const p = findPack(packId);
    if (p.restartRequired) throw new StudioError('restart_required', 'restart the game', { status: 409 });
    if (!p.studioEditable) throw new StudioError('read_only_pack', 'read-only', { status: 403 });
    const v = voiceEntry(packId);
    if (expectedRevision !== `v${v.revNum}`) throw new StudioError('conflict', 'stale revision', { status: 409 });
    if (origin === null && consent === null) {
      v.origin = null; v.consent = null; v.updatedAt = null; v.revNum += 1;
    } else {
      if (!['own', 'ai-assisted', 'ai-generated'].includes(origin)) throw new StudioError('bad_request', 'bad origin');
      if (!(consent?.confirmed === true && consent?.statementVersion === 'consent-1')) {
        throw new StudioError('consent_required', 'Pack voice consent must be confirmed against consent-1.', { status: 403 });
      }
      const ts = new Date().toISOString();
      v.origin = origin;
      v.consent = { confirmed: true, statementVersion: 'consent-1', confirmedAt: ts };
      v.updatedAt = ts;
      v.revNum += 1;
      for (const lang of p.languages || []) {
        for (const l of dev.lines || []) {
          const st = dev.state(packId, lang, l.id);
          for (const t of st.takes || []) {
            if (t.consentSource === 'pack') {
              t.origin = v.origin;
              t.consent = { ...v.consent };
            }
          }
        }
      }
    }
    dev.emit('maker.pack.changed', { packId, reason: 'voice-default' });
    return voiceDto(v);
  };

  // D-148: dubs made outside Studio (hand-copied, v1 voice route) that have no voice label; tests set them with devUnlabelDubs.
  adapter.devUnlabelDubs = (packId, paths) => { extra(packId).unlabelled = [...paths]; };
  adapter.studioLabelDubs = async (packId, { expectedRevision, operationId, origin, consent } = {}) => {
    dev.log.push({ method: 'labelDubs', args: [packId, { expectedRevision, origin, consent }] });
    if (!/^[0-9a-f]{32}$/.test(operationId || '')) throw new StudioError('bad_request', 'operationId must be 32 hex characters');
    if (!['own', 'ai-assisted', 'ai-generated'].includes(origin)) throw new StudioError('bad_request', 'origin must be own, ai-assisted or ai-generated');
    if (!(consent?.confirmed === true && consent?.statementVersion === 'consent-1')) throw new StudioError('bad_request', 'consent.confirmed must be true');
    const p = findPack(packId), m = extra(packId);
    if (p.restartRequired) throw new StudioError('restart_required', 'restart the game', { status: 409 });
    if (!p.studioEditable) throw new StudioError('read_only_pack', 'read-only', { status: 403 });
    const v = voiceEntry(packId);
    if (!v.origin || !v.consent?.confirmed) throw new StudioError('consent_required', "set the pack's voice origin and consent first; nothing was labelled", { status: 403 });
    if (expectedRevision !== `v${v.revNum}` || origin !== v.origin) throw new StudioError('conflict', "the pack's voice default changed; reload it before labelling", { status: 409 });
    const paths = m.unlabelled || [];
    m.unlabelled = [];
    if (paths.length) dev.emit('maker.pack.changed', { packId, reason: 'voice-label' });
    return { operationId, revision: `v${v.revNum}`, origin: v.origin, labelled: paths.length, paths: paths.slice(0, 20) };
  };

  const origTakes = adapter.studioTakes.bind(adapter);
  const origCreateTake = adapter.studioCreateTake.bind(adapter);
  const origUpdateTake = adapter.studioUpdateTake.bind(adapter);
  const origPutTakeAudio = adapter.studioPutTakeAudio.bind(adapter);

  const withConsentMeta = (raw, item) => ({
    ...item,
    origin: raw?.origin || item.origin,
    consent: raw?.consent ? {
      confirmed: raw.consent.confirmed === true,
      statementVersion: raw.consent.statementVersion ?? null,
      confirmedAt: raw.consent.confirmed ? (raw.consent.confirmedAt || item.consent?.confirmedAt || item.createdAt) : null
    } : item.consent,
    consentSource: raw?.consentSource || 'take'
  });

  adapter.studioTakes = async (packId, lang, id) => {
    const res = await origTakes(packId, lang, id);
    const st = dev.state(packId, lang, id);
    return {
      ...res,
      items: (res.items || []).map((item) => {
        const raw = st.takes.find((x) => x.id === item.id);
        return withConsentMeta(raw, item);
      })
    };
  };

  adapter.studioCreateTake = async (packId, lang, id, body = {}) => {
    const { origin, consent } = body;
    if (origin === undefined && consent === undefined) {
      const v = voiceEntry(packId);
      if (!v.origin || !v.consent?.confirmed) {
        dev.log.push({ method: 'createTake', args: [packId, lang, id] });
        throw new StudioError('consent_required', 'Choose a voice origin and confirm consent, or set a pack voice default first.', { status: 403 });
      }
      const res = await origCreateTake(packId, lang, id, { ...body, origin: v.origin, consent: v.consent });
      const raw = dev.state(packId, lang, id).takes.find((x) => x.id === res.take.id);
      if (raw) {
        raw.origin = v.origin;
        raw.consent = { ...v.consent };
        raw.consentSource = 'pack';
      }
      return { ...res, take: withConsentMeta(raw, res.take) };
    }
    if ((origin === undefined) !== (consent === undefined)) {
      throw new StudioError('bad_request', 'Pass both origin and consent, or omit both to use the pack voice default.');
    }
    const res = await origCreateTake(packId, lang, id, body);
    const raw = dev.state(packId, lang, id).takes.find((x) => x.id === res.take.id);
    if (raw) raw.consentSource = 'take';
    return { ...res, take: withConsentMeta(raw, res.take) };
  };

  adapter.studioUpdateTake = async (packId, lang, id, takeId, body = {}) => {
    const logIdx = dev.log.length;
    if (body.followPackDefault !== undefined) {
      if (body.followPackDefault !== true || body.origin !== undefined || body.consent !== undefined) {
        throw new StudioError('bad_request', 'followPackDefault must be true and cannot be combined with origin or consent.', { status: 400 });
      }
      const v = voiceEntry(packId);
      if (!v.origin || !v.consent?.confirmed) {
        throw new StudioError('consent_required', 'Set and confirm a pack voice default before linking takes to it.', { status: 403 });
      }
      const { followPackDefault: _, ...rest } = body;
      const res = await origUpdateTake(packId, lang, id, takeId, {
        ...rest,
        origin: v.origin,
        consent: { ...v.consent }
      });
      if (dev.log[logIdx]) dev.log[logIdx].body = { ...body };
      const raw = dev.state(packId, lang, id).takes.find((x) => x.id === takeId);
      if (raw) {
        raw.origin = v.origin;
        raw.consent = { ...v.consent };
        raw.consentSource = 'pack';
      }
      return { ...res, take: withConsentMeta(raw, res.take) };
    }
    const res = await origUpdateTake(packId, lang, id, takeId, body);
    if (dev.log[logIdx]) dev.log[logIdx].body = { ...body };
    const raw = dev.state(packId, lang, id).takes.find((x) => x.id === takeId);
    if (raw && (body.origin !== undefined || body.consent !== undefined)) raw.consentSource = 'take';
    return { ...res, take: withConsentMeta(raw, res.take) };
  };

  adapter.studioPutTakeAudio = async (packId, lang, id, takeId, opts, blob) => {
    const res = await origPutTakeAudio(packId, lang, id, takeId, opts, blob);
    const raw = dev.state(packId, lang, id).takes.find((x) => x.id === takeId);
    return { ...res, take: withConsentMeta(raw, res.take) };
  };
}
