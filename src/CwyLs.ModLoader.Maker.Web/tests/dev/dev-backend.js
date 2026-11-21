// In-memory implementation of W2's *proposed* Studio contract 2 (research/2026-11-08/w2-backend/PLAN.md §3), used for
// `?backend=dev`, the node tests and the screenshot matrix. NOT shipped: build.sh leaves tests/ out of the release.
// It returns wire-shaped DTOs and rejects with StudioError {code} exactly where W2's plan says the loader would.
// Differences from the plan are listed in DEV_NOTES at the bottom (one revision per line, no clip aliases, no disk).
// All lines are invented, except clickheroine_rest_010 (given in the design brief). No game text.
import { StudioError } from '../../ui/api/model.js';
import { parseWavHeader } from '../../ui/audio/wav.js';

const CONSENT_VERSION = 'consent-1';
const MAX_TAKES = 3, MAX_HISTORY = 20;

const NAMED_SCENES = [
  ['morning', 'Morning'],
  ['clickheroine_rest_008', 'clickheroine_rest_008'],
  ['clickheroine_rest_009', 'clickheroine_rest_009'],
  ['clickheroine_rest_010', 'clickheroine_rest_010'],
  ['clickheroine_rest_011', 'clickheroine_rest_011'],
  ['clickheroine_rest_012', 'clickheroine_rest_012'],
  ['clickheroine_rest_013', 'clickheroine_rest_013'],
  ['clickheroine_work_004', 'clickheroine_work_004'],
  ['smalltalk', 'Small talk'],
  ['pomodoro', 'Pomodoro'], ['story01', 'Story ch. 1'], ['night', 'Late night']
];

// id, scene, original duration (s), JA, EN
const NAMED_LINES = [
  ['morning_wake_001', 'morning', 2.40, '「おはよう。今日もよろしくね。」', 'Morning. Let’s do our best today, too.'],
  ['morning_wake_002', 'morning', 3.10, '「コーヒー、もう淹れた？私はまだ眠くて…」', 'Made your coffee yet? I’m still half asleep…'],
  ['morning_wake_003', 'morning', 2.20, '「窓、少し開けようか。」', 'Shall I open the window a little?'],
  ['morning_wake_004', 'morning', 2.90, '「朝の光って、ちょっと特別な感じがするよね。」', 'Morning light feels a little special, doesn’t it?'],
  ['morning_wake_005', 'morning', 1.80, '「よし、始めよっか。」', 'Okay, shall we start?'],
  ['morning_wake_006', 'morning', 3.40, '「昨日の続き、どこまで書いたっけ…」', 'Where did I leave off yesterday…'],
  ['clickheroine_rest_008', 'clickheroine_rest_008', 1.10, '「ん？」', 'Hm?'],
  ['clickheroine_rest_009', 'clickheroine_rest_009', 1.62, '「どうしたの？」', 'What’s up?'],
  ['clickheroine_rest_010', 'clickheroine_rest_010', 1.29, '「ん？何か言った？」', 'Hm? Did you say something?'],
  ['clickheroine_rest_011', 'clickheroine_rest_011', 2.05, '「ちょっと、くすぐったいってば。」', 'Hey, that tickles!'],
  ['clickheroine_rest_012', 'clickheroine_rest_012', 2.40, '「休憩中だよ。一緒に休む？」', 'I’m on a break. Want to rest with me?'],
  ['clickheroine_rest_013', 'clickheroine_rest_013', 1.70, '「ふふ、なに？」', 'Heh, what is it?'],
  ['clickheroine_work_004', 'clickheroine_work_004', 2.30, '「今、集中してるから…あとでね。」', 'I’m focusing right now… later, okay?'],
  ['smalltalk_tea_001', 'smalltalk', 3.20, '「紅茶とコーヒー、どっち派？」', 'Tea or coffee: which side are you on?'],
  ['smalltalk_tea_002', 'smalltalk', 4.10, '「私はね、夜は紅茶。朝はコーヒー。ずるいでしょ。」', 'Me? Tea at night, coffee in the morning. Sneaky, right?'],
  ['smalltalk_kou_001', 'smalltalk', 3.60, '「コウちゃん、今日もいい顔してる。」', 'Kou-chan is making a nice face again today.'],
  ['smalltalk_rain_002', 'smalltalk', 3.80, '「雨の音、好きなんだ。落ち着くから。」', 'I like the sound of rain. It calms me down.'],
  ['smalltalk_novel_003', 'smalltalk', 4.60, '「サトルがね、また勝手に動き出したの。」', 'Satoru went and did his own thing again.'],
  ['smalltalk_novel_004', 'smalltalk', 3.90, '「キャラが勝手に喋り出すの、作家あるあるだよ。」', 'Characters talking on their own? Every writer knows that one.'],
  ['smalltalk_stars_001', 'smalltalk', 3.30, '「今夜は星、見えるかな。」', 'Think we’ll see stars tonight?'],
  ['pomodoro_start_001', 'pomodoro', 2.60, '「ポモドーロ、スタート！」', 'Pomodoro, start!'],
  ['pomodoro_start_002', 'pomodoro', 3.00, '「{0}分、一緒にがんばろう。」', 'Let’s give it {0} minutes together.'],
  ['pomodoro_break_001', 'pomodoro', 2.80, '「おつかれさま。少し休もう。」', 'Good work. Let’s take a little break.'],
  ['pomodoro_break_003', 'pomodoro', 1.80, '「ポモドーロ、あと{0}分。ゆっくり伸びしよっか。」', '{0} more Pomodoro minutes. Let’s stretch, nice and slow.'],
  ['pomodoro_end_001', 'pomodoro', 3.50, '「今日はここまで。よくがんばったね。」', 'That’s it for today. You did great.'],
  ['pomodoro_long_001', 'pomodoro', 4.20, '「長めの休憩だよ。お茶、淹れてくるね。」', 'Long break! I’ll go make some tea.'],
  ['story01_010', 'story01', 4.40, '「はじめまして…じゃないよね。通話、つながってる？」', 'Nice to meet… no, wait. Is the call working?'],
  ['story01_011', 'story01', 3.70, '「私、サトネ。小説を書いてるの。」', 'I’m Satone. I write novels.'],
  ['story01_012', 'story01', 5.20, '「誰かと一緒だと、不思議と筆が進むんだ。」', 'Somehow the words come easier when someone’s with me.'],
  ['story01_013', 'story01', 3.00, '「だから、ちょっとだけ付き合ってくれる？」', 'So… will you keep me company for a bit?'],
  ['story01_014', 'story01', 2.10, '「…ありがとう。」', '…Thank you.'],
  ['night_sleepy_001', 'night', 2.70, '「ふぁ…ちょっと眠くなってきた。」', 'Yawn… I’m getting a little sleepy.'],
  ['night_sleepy_002', 'night', 3.30, '「あと一段落だけ書いたら寝るね。」', 'One more paragraph, then I’ll sleep.'],
  ['night_goodnight_001', 'night', 2.40, '「おやすみ。また明日ね。」', 'Goodnight. See you tomorrow.'],
  ['night_goodnight_002', 'night', 3.00, '「今日も一緒にいてくれて、ありがとう。」', 'Thanks for staying with me today, too.']
];

// Thai subtitles of the sample pack (invented, from the mockup), with their workflow status.
const SEED_TH = {
  morning_wake_001: ['อรุณสวัสดิ์ วันนี้ก็ฝากตัวด้วยนะ', 'done'], morning_wake_002: ['ชงกาแฟรึยัง? ฉันยังง่วงอยู่เลย…', 'done'],
  morning_wake_003: ['เปิดหน้าต่างสักหน่อยดีไหม', 'done'], morning_wake_004: ['แสงตอนเช้านี่ดูพิเศษนิด ๆ เนอะ', 'draft'],
  morning_wake_005: ['เอาล่ะ เริ่มกันเลยไหม', 'done'], clickheroine_rest_008: ['หืม?', 'done'], clickheroine_rest_009: ['มีอะไรเหรอ?', 'done'],
  clickheroine_rest_010: ['ว่าไง? เมื่อกี้เรียกเรารึเปล่า', 'done'], clickheroine_rest_011: ['นี่ จั๊กจี้นะ!', 'draft'],
  clickheroine_rest_012: ['พักอยู่น่ะ มาพักด้วยกันไหม', 'done'], clickheroine_work_004: ['ตอนนี้กำลังโฟกัสอยู่… ไว้ทีหลังนะ', 'done'],
  smalltalk_tea_001: ['ชากับกาแฟ เธอชอบแบบไหนมากกว่า?', 'done'], smalltalk_tea_002: ['ฉันเหรอ? กลางคืนดื่มชา เช้าดื่มกาแฟ ขี้โกงเนอะ', 'draft'],
  smalltalk_kou_001: ['วันนี้โคจังก็หน้าตาดีเหมือนเดิมเลย', 'done'], smalltalk_rain_002: ['ชอบเสียงฝนนะ ฟังแล้วใจสงบดี', 'done'],
  smalltalk_novel_003: ['ซาโตรุน่ะ ทำอะไรตามใจตัวเองอีกแล้ว', 'draft'], smalltalk_stars_001: ['คืนนี้จะเห็นดาวไหมนะ', 'done'],
  pomodoro_start_001: ['เริ่มโพโมโดโร่กันเลย!', 'done'], pomodoro_start_002: ['ลุยด้วยกัน {0} นาทีนะ', 'done'],
  pomodoro_break_001: ['เก่งมาก พักกันสักหน่อยนะ', 'done'], pomodoro_break_003: ['อีกไม่กี่นาทีแล้วนะ  มายืดเส้นยืดสายกันช้า\nๆ ดีกว่า', 'draft'],
  pomodoro_end_001: ['วันนี้พอแค่นี้ก่อน เก่งมากเลยนะ', 'done'], story01_010: ['ยินดีที่ได้รู้จัก… เอ๊ะ ไม่ใช่สิ คอลติดอยู่ใช่ไหม?', 'done'],
  story01_011: ['ฉันชื่อซาโตเนะ เขียนนิยายอยู่น่ะ', 'done'], story01_012: ['เวลามีใครอยู่ด้วย ไม่รู้ทำไมถึงเขียนได้ลื่นขึ้น', 'done'],
  story01_013: ['เพราะงั้น อยู่เป็นเพื่อนกันสักพักได้ไหม?', 'draft'], night_sleepy_001: ['ฮ้าว… เริ่มง่วงแล้วแฮะ', 'done'],
  night_sleepy_002: ['ขอเขียนอีกย่อหน้าเดียว แล้วจะไปนอนนะ', 'draft'], night_goodnight_001: ['ฝันดีนะ เจอกันพรุ่งนี้', 'done']
};
// Lines where ANOTHER loaded pack contributes to what the game uses (invented), for the sample Thai pack only. `wins`: that pack
// outranks the sample pack even when the sample pack has its own text / dub; without it that pack is used only while the sample pack has none.
const ELSEWHERE = {
  night_goodnight_002: { sub: { pack: 'thai-subs-community', text: 'ขอบคุณที่อยู่ด้วยกันวันนี้นะ' }, dub: { pack: 'thai-subs-community', seconds: 2.6 } },
  story01_013: { sub: { pack: 'thai-subs-community', text: 'ดังนั้นอยู่เป็นเพื่อนกันสักพักนะ', wins: true } },
  story01_011: { dub: { pack: 'thai-subs-community', seconds: 5.1, wins: true } }
};
// Published dubs of the sample pack: id -> [durations of the takes], published index
const SEED_DUBS = {
  morning_wake_001: [[2.71], 0], morning_wake_002: [[3.32, 3.05], 1], morning_wake_003: [[2.48], 0, 'retake'],
  clickheroine_rest_008: [[1.20], 0], clickheroine_rest_009: [[1.71], 0], clickheroine_rest_010: [[1.62, 1.94, 2.31], 1],
  clickheroine_rest_012: [[2.95], 0, 'retake'], clickheroine_work_004: [[2.62], 0, 'assist'], smalltalk_tea_001: [[3.41], 0],
  smalltalk_tea_002: [[4.60], null], smalltalk_kou_001: [[3.80], 0], smalltalk_rain_002: [[4.10, 3.90], null],
  pomodoro_start_001: [[2.40], 0], pomodoro_start_002: [[3.10], 0], pomodoro_break_001: [[2.90], 0], story01_010: [[4.90], 0],
  story01_011: [[3.60], 0], story01_012: [[5.60, 5.40], null], night_sleepy_001: [[2.90], 0], night_goodnight_001: [[2.70], 0]
};
const SEED_STARS = new Set(['smalltalk_stars_001', 'pomodoro_end_001', 'night_goodnight_001']);
const SEED_HISTORY = {
  clickheroine_rest_010: ['หืม? พูดอะไรเหรอ?', 'ว่าไง? เมื่อกี้เรียกฉันรึเปล่า'],
  morning_wake_001: ['อรุณสวัสดิ์นะ วันนี้ก็ฝากตัวด้วย'],
  pomodoro_break_003: ['อีกนิดเดียวนะ ยืดเส้นยืดสายกันหน่อย']
};

/** A tiny silent-ish PCM16 mono 48 kHz WAV of `seconds` (invented audio: a quiet tone), for seeded takes. */
function toneWav(seconds, hz = 220) {
  const n = Math.round(seconds * 48000), bytes = new Uint8Array(44 + n * 2), v = new DataView(bytes.buffer);
  const s = (at, t) => { for (let i = 0; i < t.length; i++) v.setUint8(at + i, t.charCodeAt(i)); };
  s(0, 'RIFF'); v.setUint32(4, 36 + n * 2, true); s(8, 'WAVEfmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true);
  v.setUint16(22, 1, true); v.setUint32(24, 48000, true); v.setUint32(28, 96000, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  s(36, 'data'); v.setUint32(40, n * 2, true);
  for (let i = 0; i < n; i++) v.setInt16(44 + i * 2, Math.round(2400 * Math.sin(2 * Math.PI * hz * i / 48000) * Math.min(1, i / 480, (n - i) / 480)), true);
  return bytes;
}

const hex = (n) => n.toString(16).padStart(32, '0');
const cpLen = (s) => Array.from(s).length;

/**
 * @param {{fixture?:string, lines?:number, now?:()=>number}} [options]
 *  fixture: normal | paused | readonly | firstrun | offline (server-side states; the other screenshot states are UI states)
 */
export function createDevBackend(options = {}) {
  const fixture = options.fixture || 'normal';
  const now = options.now || (() => Date.now());
  const total = options.lines || 2169;
  const features = options.features || ['catalog', 'workflow', 'subtitles', 'dubs', 'projects', 'newPacks', 'playback'];
  // a route whose feature is not installed answers unavailable (503), like an M2 loader without that backend
  const FEATURE_OF = { updateWorkflow: 'workflow', setWorkflow: 'workflow', saveSubtitle: 'subtitles', revert: 'projects', history: 'projects', createPack: 'newPacks', takes: 'dubs', takeAudio: 'dubs', createTake: 'dubs', putTakeAudio: 'dubs', patchTake: 'dubs', deleteTake: 'dubs', setFavourite: 'dubs', publish: 'dubs', unpublish: 'dubs', play: 'playback' };
  const epoch = 'dev-epoch-1';

  // ---- catalogue (invented) ----
  const groups = NAMED_SCENES.map(([id, title], i) => ({ id, labelKey: id.startsWith('clickheroine_') ? null : id, title, order: i }));
  const storyLines = NAMED_LINES.map(([id, group, durationSec, ja, en]) => ({ id, voice: `Voice_${id}`, group, durationSec, ja, en }));
  const extraScenes = 37;
  for (let s = 0; s < extraScenes; s++) groups.push({ id: `scene${String(s + 8).padStart(2, '0')}`, labelKey: null, title: `Practice scene ${s + 8}`, order: NAMED_SCENES.length + s });
  const named = storyLines.length;
  for (let i = 0; i < Math.max(0, total - named); i++) {
    const g = groups[NAMED_SCENES.length + (i % extraScenes)];
    storyLines.push({
      id: `practice_${g.id}_${String(Math.floor(i / extraScenes) + 1).padStart(3, '0')}`, voice: i % 17 === 0 ? null : `Voice_practice_${i}`,
      group: g.id, durationSec: 1.2 + (i * 37 % 40) / 10, ja: `練習用の短い台詞 ${i + 1}。`, en: `An invented practice line, number ${i + 1}.`
    });
  }
  // Invented UI text exists in localization and packs, but has no scenario references.
  const novelRows = storyLines.map((l, i) => ({ rowId: `novel_${i}`, lineId: l.id, groupId: l.group }));
  const localization = [...storyLines,
    { id: 'ui_settings', voice: null, group: 'ui', durationSec: null, ja: 'UI 設定', en: 'UI Settings sentinel' },
    { id: 'ui_continue', voice: 'Voice_UI_Continue', group: 'morning', durationSec: 1, ja: 'UI 続ける', en: 'UI Continue sentinel' },
    { id: 'ui_scene_title', voice: null, group: null, durationSec: null, ja: 'UI シーン', en: 'UI Scene title sentinel' }
  ];
  const storyIds = new Set(novelRows.map((r) => r.lineId));
  const lines = localization.filter((l) => storyIds.has(l.id));
  const lineIndex = new Map(lines.map((l, i) => [l.id, i]));
  let catalogRevision = 1;

  // ---- packs ----
  const packs = [
    { id: 'satone-th-dub', name: 'Satone พากย์ไทย', version: '0.3.0', languages: options.packLanguages || ['th'], loaded: true, restartRequired: false, studioEditable: true, studioReadOnlyReason: null, metadataState: 'ok' },
    { id: 'cozy-en-rewrite', name: 'Cozy English Rewrite', version: '0.1.0', languages: ['en'], loaded: true, restartRequired: false, studioEditable: true, studioReadOnlyReason: null, metadataState: 'ok' },
    { id: 'thai-subs-community', name: 'Thai Subs (community)', version: '1.2.0', languages: ['th'], loaded: true, restartRequired: false, studioEditable: false, studioReadOnlyReason: 'reference', metadataState: 'none' },
    { id: 'cwyls-rain-fx', name: 'Rain FX', version: '0.9.1', languages: [], loaded: true, restartRequired: false, studioEditable: false, studioReadOnlyReason: 'code', metadataState: 'none' },
    { id: 'old-test-pack', name: 'old-test-pack', version: '0.0.1', languages: ['th'], loaded: false, restartRequired: false, studioEditable: false, studioReadOnlyReason: 'failed', metadataState: 'none' }
  ];
  if (fixture === 'firstrun') {
    packs.unshift({ id: 'my-new-pack', name: 'My new pack', version: '0.1.0', languages: ['th'], loaded: false, restartRequired: true, studioEditable: false, studioReadOnlyReason: 'pending', metadataState: 'ok' });
  }

  // ---- per pack/lang/line state, seeded lazily ----
  const lineStates = new Map();
  const events = [];
  let seq = 0;
  let sayRows = 0; // like the real loader, a Play is spoken as a runtime Satone.Say row (cwy_say_<n>_001)
  const bus = new EventTarget(); // like modules/loader: events arrive as CustomEvent('maker-event', {detail}); 'connection' and 'resync' too
  const emit = (type, data) => { const e = { seq: ++seq, time: new Date(now()).toISOString(), type, data }; events.push(e); if (events.length > 500) events.shift(); bus.dispatchEvent(new CustomEvent('maker-event', { detail: e })); };
  let connection = 'connected';
  const setConnection = (v) => { if (connection !== v) { connection = v; api.connection = v; bus.dispatchEvent(new Event('connection')); } };

  function seedFor(packId, lang, id) {
    const st = { text: null, status: 'todo', starred: false, history: [], takes: [], favouriteTakeId: null, publishedTakeId: null, publishedRevision: null, revision: 1, takeCounter: 0 };
    if (packId === 'satone-th-dub' && lang === 'th') {
      const seed = SEED_TH[id];
      if (seed) { st.text = seed[0]; st.status = seed[1]; }
      else if (!NAMED_LINES.some((l) => l[0] === id)) {
        const i = lineIndex.get(id) - named; // practice lines: a plausible spread for the progress bars
        if (i % 5 < 3) { st.text = `ประโยคฝึกที่ ${i + 1}`; st.status = i % 7 === 0 ? 'draft' : 'done'; }
        if (i % 11 === 0 && st.text) st.starred = true;
      }
      if (SEED_STARS.has(id)) st.starred = true;
      const dub = SEED_DUBS[id];
      if (dub) {
        dub[0].forEach((sec, k) => {
          const take = newTakeRecord(st, dub[2] === 'assist' ? 'ai-assisted' : 'own', true);
          take.source = toneWav(sec); take.render = toneWav(sec * 0.9); take.sourceDurationSec = sec; take.renderDurationSec = sec * 0.9;
          take.trimStartSec = 0.05; take.trimEndSec = sec * 0.95; take.evenOutVolume = true;
          if (dub[1] === k) { st.favouriteTakeId = take.id; st.publishedTakeId = take.id; st.publishedRevision = 1; }
        });
        if (dub[2] === 'retake') st.status = 'needs-retake';
      }
      if (SEED_HISTORY[id] && st.text) {
        SEED_HISTORY[id].forEach((t, k) => st.history.push({ revisionId: hex(1000 + k), createdAt: new Date(now() - (3 - k) * 86400000).toISOString(), text: t, source: 'maker' }));
        st.history.push({ revisionId: hex(2000), createdAt: new Date(now() - 3600000).toISOString(), text: st.text, source: 'maker' });
      }
    } else if (packId === 'cozy-en-rewrite' && lang === 'en') {
      const en = { morning_wake_001: ['Morning! Let’s make today a good one.', 'done'], clickheroine_rest_010: ['Hm? Were you saying something?', 'draft'], night_goodnight_001: ['Night night. See you tomorrow.', 'done'] }[id];
      if (en) { st.text = en[0]; st.status = en[1]; }
    }
    return st;
  }
  function newTakeRecord(st, origin, confirmed) {
    st.takeCounter += 1;
    const take = { id: hex(5000 + st.takes.length + st.takeCounter * 7), origin, consent: { confirmed, statementVersion: confirmed ? CONSENT_VERSION : null },
      source: null, render: null, sourceDurationSec: null, renderDurationSec: null, trimStartSec: 0, trimEndSec: null, evenOutVolume: false, createdAt: new Date(now()).toISOString() };
    st.takes.push(take);
    return take;
  }
  const stateOf = (packId, lang, id) => {
    lineOrThrow(id);
    const k = `${packId}/${lang}/${id}`;
    if (!lineStates.has(k)) lineStates.set(k, seedFor(packId, lang, id));
    return lineStates.get(k);
  };

  // ---- failure injection and call log, for tests ----
  const log = [];
  const failures = [];
  let offline = false; // the 'offline' fixture is applied by the page after its first load; a real offline start shows the offline banner
  const status = { gameVersion: '1.18.0', loaderVersion: '1.0.0-dev', gameLanguage: 'th', gameLanguageIsPack: false, roomReady: true, focused: fixture !== 'paused', paused: fixture === 'paused' ? true : false, busy: false, reason: fixture === 'paused' ? 'application-paused' : null, observedAt: new Date(now()).toISOString(), stale: false, focusOnPlay: true };

  async function call(method, args, fn) {
    if (method !== 'events') log.push({ method, args });
    if (offline) { setConnection('offline'); throw new StudioError('unavailable', 'offline', { status: 0 }); }
    setConnection('connected');
    if (FEATURE_OF[method] && !features.includes(FEATURE_OF[method])) throw new StudioError('unavailable', 'feature not installed', { status: 503 });
    const f = failures.findIndex((x) => x.method === method || x.method === '*');
    if (f >= 0) {
      const spec = failures[f];
      if (spec.times !== undefined && --spec.times <= 0) failures.splice(f, 1);
      if (spec.afterCommit) { const r = await fn(); log.push({ method: `${method}:committed` }); void r; }
      throw new StudioError(spec.code, spec.code, { status: spec.uncertain ? 0 : 400 });
    }
    return fn();
  }

  // ---- operation ledger (W2: same key + same request returns the recorded result; a different request conflicts) ----
  const ledger = new Map();
  function mutate(packId, operationId, fingerprint, fn) {
    if (!/^[0-9a-f]{32}$/.test(operationId || '')) throw new StudioError('bad_request', 'operationId must be 32 hex characters');
    const key = `${packId}/${operationId}`, prev = ledger.get(key);
    if (prev) {
      if (prev.fingerprint !== fingerprint) throw new StudioError('conflict', 'operationId was used for another request');
      return prev.result;
    }
    const result = fn();
    ledger.set(key, { fingerprint, result });
    if (ledger.size > 100) ledger.delete(ledger.keys().next().value);
    return result;
  }

  function editable(packId, lang) {
    const p = packs.find((x) => x.id === packId);
    if (!p) throw new StudioError('not_found', 'unknown pack');
    if (p.restartRequired) throw new StudioError('restart_required', 'restart the game');
    if (!p.studioEditable) throw new StudioError('read_only_pack', p.studioReadOnlyReason || 'read-only');
    if (!p.languages.includes(lang)) throw new StudioError('not_found', 'language is not in this pack');
    return p;
  }
  const lineOrThrow = (id) => { const i = lineIndex.get(id); if (i === undefined) throw new StudioError('not_found', 'unknown line'); return lines[i]; };
  const guard = (st, expected) => { if (expected !== undefined && expected !== `r${st.revision}`) throw new StudioError('conflict', 'stale revision'); };
  const bump = (st) => { st.revision += 1; return `r${st.revision}`; };

  const takeDto = (t) => ({ id: t.id, origin: t.origin, consent: { confirmed: t.consent.confirmed, statementVersion: t.consent.statementVersion, confirmedAt: t.consent.confirmed ? t.createdAt : null }, sourceRevision: 1, sourceDurationSec: t.sourceDurationSec, renderDurationSec: t.renderDurationSec,
    trimStartSec: t.trimStartSec, trimEndSec: t.trimEndSec, evenOutVolume: t.evenOutVolume, sourcePresent: !!t.source, renderReady: !!t.render, createdAt: t.createdAt, updatedAt: t.createdAt });

  function lineDto(packId, lang, base, withContext, occurrenceId) {
    const st = stateOf(packId, lang, base.id);
    const dto = {
      id: base.id, voice: base.voice, group: base.group, durationSec: base.durationSec, ja: base.ja, en: base.en,
      subtitle: st.text, subtitlePack: st.text ? packId : null, dubPack: st.publishedTakeId ? packId : null,
      dubDurationSec: st.publishedTakeId ? (st.takes.find((t) => t.id === st.publishedTakeId)?.renderDurationSec ?? null) : null,
      ownedSubtitle: st.text, ownedDub: !!st.publishedTakeId,
      workflow: { status: st.status, starred: st.starred }, takeCount: st.takes.length,
      publishedTakeId: st.publishedTakeId, publishedTakeRevision: st.publishedRevision, revision: `r${st.revision}`, clipRevision: `r${st.revision}`
    };
    const other = packId === 'satone-th-dub' && lang === 'th' ? ELSEWHERE[base.id] : null;
    if (other?.sub && (other.sub.wins || !st.text)) { dto.subtitle = other.sub.text; dto.subtitlePack = other.sub.pack; }
    if (other?.dub && (other.dub.wins || !st.publishedTakeId)) { dto.dubPack = other.dub.pack; dto.dubDurationSec = other.dub.seconds; }
    if (withContext) {
      const stem = (id) => String(id || '').replace(/[_.-]\d+$/, '');
      const baseStem = stem(base.group);
      const same = lines.filter((l) => l.group === base.group || stem(l.group) === baseStem), pos = same.findIndex((l) => l.id === base.id);
      dto.context = { occurrences: [{ occurrenceId: `${base.group}:${pos}`, groupId: base.group, order: pos, previousId: same[pos - 1]?.id ?? null, nextId: same[pos + 1]?.id ?? null, talker: 'Satone', bodyMotion: null, facialMotion: null }],
        selectedOccurrenceId: occurrenceId || `${base.group}:${pos}`, mood: null, pose: null, source: 'dev' };
    }
    return dto;
  }

  function progressOf(packId, lang, subset) {
    const p = { totalLines: 0, dubbableLines: 0, subtitlePresent: 0, dubPresent: 0, todo: 0, draft: 0, done: 0, needsRetake: 0, starred: 0 };
    for (const l of subset) {
      const st = stateOf(packId, lang, l.id);
      p.totalLines += 1;
      if (l.voice) p.dubbableLines += 1;
      if (st.text) p.subtitlePresent += 1;
      if (st.publishedTakeId) p.dubPresent += 1;
      if (st.status === 'todo') p.todo += 1; else if (st.status === 'draft') p.draft += 1; else if (st.status === 'done') p.done += 1; else p.needsRetake += 1;
      if (st.starred) p.starred += 1;
    }
    return p;
  }

  const api = {
    // ===== read models =====
    studioCapabilities: () => call('capabilities', [], async () => ({ contract: 2, features: [...features], uiText: false, limits: { jsonBytes: 65536, wavBytes: 16777216, wavSeconds: 60, subtitleRevisions: MAX_HISTORY, takesPerLine: MAX_TAKES, projectBytes: 2147483648 }, storageSchema: 1, consentVersion: CONSENT_VERSION })),
    studioStatus: () => call('status', [], async () => ({ ...status })),
    studioPacks: () => call('packs', [], async () => ({ packs: packs.map((p) => ({ ...p })) })),
    studioGroups: (packId, lang) => call('groups', [packId, lang], async () => ({
      catalogRevision: `c${catalogRevision}`, overall: progressOf(packId, lang, lines),
      groups: groups.map((g) => ({ ...g, progress: progressOf(packId, lang, lines.filter((l) => l.group === g.id)) }))
    })),
    // `filter` follows CONTRACT-v2 (pack-scoped): all, todo, draft, done, needs-retake, starred, missing-subtitle, missing-dub. The page filters
    // on its own copy of the catalogue; this is the independent oracle the tests compare it against (same rules as W2's fake loader).
    studioLines: ({ packId, lang, offset = 0, limit = 100, filter = 'all', q = '', group = null } = {}) => call('lines', [packId, lang, { offset, limit, filter }], async () => {
      if (limit < 1 || limit > 500 || offset < 0) throw new StudioError('bad_request', 'offset/limit out of range');
      if (!['all', 'todo', 'draft', 'done', 'needs-retake', 'starred', 'missing-subtitle', 'missing-dub'].includes(filter)) throw new StudioError('bad_request', 'Invalid workflow filter.');
      const needle = q.trim().toLowerCase();
      const keep = lines.map((l) => lineDto(packId, lang, l, false)).filter((x) => (
        (!group || (x.group || 'ungrouped') === group) && (!needle || [x.id, x.voice, x.ja, x.en, x.ownedSubtitle].some((s) => s?.toLowerCase().includes(needle))) &&
        (filter === 'all' || (filter === 'starred' ? x.workflow.starred : filter === 'missing-subtitle' ? x.ownedSubtitle === null || x.ownedSubtitle === ''
          : filter === 'missing-dub' ? !!x.voice && !x.ownedDub : x.workflow.status === filter))));
      return { total: keep.length, offset, catalogRevision: `c${catalogRevision}`, items: keep.slice(offset, offset + limit) };
    }),
    studioLine: (packId, lang, id, { occurrenceId } = {}) => call('line', [packId, lang, id], async () => lineDto(packId, lang, lineOrThrow(id), true, occurrenceId)),
    studioHistory: (packId, lang, id, { offset = 0, limit = 20 } = {}) => call('history', [packId, lang, id], async () => {
      lineOrThrow(id);
      const st = stateOf(packId, lang, id), all = st.history.slice().reverse();
      return { revision: `r${st.revision}`, total: all.length, offset, items: all.slice(offset, offset + limit), historyGap: false };
    }),
    studioTakes: (packId, lang, id) => call('takes', [packId, lang, id], async () => {
      const st = stateOf(packId, lang, id);
      return { revision: `r${st.revision}`, favouriteTakeId: st.favouriteTakeId, publishedTakeId: st.publishedTakeId, items: st.takes.map(takeDto) };
    }),
    studioGetTakeAudio: (packId, lang, id, takeId, kind) => call('takeAudio', [packId, lang, id, takeId, kind], async () => {
      const t = stateOf(packId, lang, id).takes.find((x) => x.id === takeId);
      const bytes = t && (kind === 'render' ? t.render : t.source);
      if (!bytes) throw new StudioError('not_found', 'no such audio');
      return new Blob([bytes], { type: 'audio/wav' });
    }),
    studioGlossary: (packId, lang) => call('glossary', [packId, lang], async () => ({ revision: 'g1', entries: [] })),
    studioOperation: (packId, operationId) => call('operation', [packId, operationId], async () => {
      const rec = ledger.get(`${packId}/${operationId}`);
      if (!rec) throw new StudioError('not_found', 'unknown operation');
      return { state: 'committed', revision: rec.result.revision ?? null, saved: true, recoveryRequired: false };
    }),

    // ===== mutations =====
    studioUpdateWorkflow: (packId, lang, id, { expectedRevision, operationId, status: s, starred }) => call('setWorkflow', [packId, lang, id], async () => {
      editable(packId, lang); lineOrThrow(id);
      if (s === undefined && starred === undefined) throw new StudioError('bad_request', 'nothing to change');
      if (s !== undefined && !['todo', 'draft', 'done', 'needs-retake'].includes(s)) throw new StudioError('bad_request', 'bad status');
      return mutate(packId, operationId, JSON.stringify(['wf', lang, id, expectedRevision, s, starred]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        if (s !== undefined) st.status = s;
        if (starred !== undefined) st.starred = starred === true;
        const revision = bump(st);
        emit('maker.workflow.changed', { packId, lang, id, revision, status: st.status, starred: st.starred });
        return { revision, workflow: { status: st.status, starred: st.starred } };
      });
    }),
    studioSaveSubtitle: (packId, lang, id, { expectedRevision, operationId, text }) => call('saveSubtitle', [packId, lang, id, text], async () => {
      editable(packId, lang); lineOrThrow(id);
      if (text !== null && (typeof text !== 'string' || cpLen(text) < 1 || cpLen(text) > 2000)) throw new StudioError('bad_request', 'text must be 1-2000 characters, or null to remove');
      return mutate(packId, operationId, JSON.stringify(['sub', lang, id, expectedRevision, text]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        if ((st.text ?? null) === text) return savedDto(st, operationId, 'No change.');
        st.text = text;
        st.history.push({ revisionId: hex(10000 + st.revision * 31 + st.history.length), createdAt: new Date(now()).toISOString(), text, source: 'maker' });
        while (st.history.length > MAX_HISTORY) st.history.shift();
        const revision = bump(st);
        emit('maker.subtitle.saved', { packId, lang, id, removed: text === null });
        emit('maker.history.changed', { packId, lang, id, revision, revisionId: st.history[st.history.length - 1].revisionId });
        return savedDto(st, operationId, 'Subtitle reloaded.', `data/subtitles/${lang}.json`);
      });
    }),
    studioRevertSubtitle: (packId, lang, id, revisionId, { expectedRevision, operationId }) => call('revert', [packId, lang, id, revisionId], async () => {
      editable(packId, lang);
      const st = stateOf(packId, lang, id), item = st.history.find((h) => h.revisionId === revisionId);
      if (!item) throw new StudioError('not_found', 'unknown revision');
      return api.studioSaveSubtitle(packId, lang, id, { expectedRevision, operationId, text: item.text });
    }),
    studioCreateTake: (packId, lang, id, { expectedRevision, operationId, origin, consent }) => call('createTake', [packId, lang, id], async () => {
      editable(packId, lang); lineOrThrow(id);
      if (!['own', 'ai-assisted', 'ai-generated'].includes(origin)) throw new StudioError('bad_request', 'bad origin');
      return mutate(packId, operationId, JSON.stringify(['take+', lang, id, expectedRevision, origin, consent]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        if (st.takes.length >= MAX_TAKES) throw new StudioError('take_limit', 'three takes at most');
        const ok = consent?.confirmed === true && consent.statementVersion === CONSENT_VERSION;
        const take = newTakeRecord(st, origin, ok);
        const revision = bump(st);
        emit('maker.take.changed', { packId, lang, id, takeId: take.id, revision, change: 'created' });
        return { revision, take: takeDto(take) };
      });
    }),
    studioPutTakeAudio: (packId, lang, id, takeId, { kind, expectedRevision, operationId }, blob) => call('putTakeAudio', [packId, lang, id, takeId, kind], async () => {
      editable(packId, lang);
      const bytes = blob instanceof Uint8Array ? blob : new Uint8Array(await blob.arrayBuffer());
      if (bytes.length > 16 * 1024 * 1024) throw new StudioError('too_large', 'over 16 MiB');
      const h = parseWavHeader(bytes);
      if (!h || h.sampleRate < 8000 || h.sampleRate > 48000) throw new StudioError('unsupported_media_type', 'not a supported WAV');
      if (h.seconds > 60) throw new StudioError('too_large', 'over 60 s');
      if (kind === 'render' && (h.sampleRate !== 48000 || h.channels !== 1)) throw new StudioError('unsupported_media_type', 'render must be 48 kHz mono');
      if (kind !== 'render' && kind !== 'source') throw new StudioError('bad_request', 'kind must be source or render');
      return mutate(packId, operationId, JSON.stringify(['audio', lang, id, takeId, kind, expectedRevision, bytes.length]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        const t = st.takes.find((x) => x.id === takeId); if (!t) throw new StudioError('not_found', 'unknown take');
        if (kind === 'source') { t.source = bytes; t.sourceDurationSec = h.seconds; t.render = null; t.renderDurationSec = null; if (t.trimEndSec === null) t.trimEndSec = h.seconds; }
        else { t.render = bytes; t.renderDurationSec = h.seconds; }
        const revision = bump(st);
        emit('maker.take.changed', { packId, lang, id, takeId, revision, change: `audio-${kind}` });
        return { revision, take: takeDto(t) };
      });
    }),
    studioUpdateTake: (packId, lang, id, takeId, { expectedRevision, operationId, ...fields }) => call('patchTake', [packId, lang, id, takeId], async () => {
      editable(packId, lang);
      return mutate(packId, operationId, JSON.stringify(['take~', lang, id, takeId, expectedRevision, fields]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        const t = st.takes.find((x) => x.id === takeId); if (!t) throw new StudioError('not_found', 'unknown take');
        if (fields.origin !== undefined) {
          if (!['own', 'ai-assisted', 'ai-generated'].includes(fields.origin)) throw new StudioError('bad_request', 'bad origin');
          if (fields.origin !== t.origin) { t.origin = fields.origin; t.consent = { confirmed: false, statementVersion: null }; }
        }
        if (fields.consent) t.consent = { confirmed: fields.consent.confirmed === true, statementVersion: fields.consent.statementVersion ?? null };
        let renderInvalid = false;
        for (const k of ['trimStartSec', 'trimEndSec', 'evenOutVolume']) if (fields[k] !== undefined && fields[k] !== t[k]) { t[k] = fields[k]; renderInvalid = true; }
        if (renderInvalid) { t.render = null; t.renderDurationSec = null; }
        const revision = bump(st);
        emit('maker.take.changed', { packId, lang, id, takeId, revision, change: 'patched' });
        return { revision, take: takeDto(t) };
      });
    }),
    studioDeleteTake: (packId, lang, id, takeId, { expectedRevision, operationId }) => call('deleteTake', [packId, lang, id, takeId], async () => {
      editable(packId, lang);
      return mutate(packId, operationId, JSON.stringify(['take-', lang, id, takeId, expectedRevision]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        if (st.publishedTakeId === takeId) throw new StudioError('take_in_use', 'unpublish first');
        const i = st.takes.findIndex((x) => x.id === takeId); if (i < 0) throw new StudioError('not_found', 'unknown take');
        st.takes.splice(i, 1);
        if (st.favouriteTakeId === takeId) st.favouriteTakeId = null;
        const revision = bump(st);
        emit('maker.take.changed', { packId, lang, id, takeId, revision, change: 'removed' });
        return { revision, removed: true };
      });
    }),
    studioSetFavourite: (packId, lang, id, { expectedRevision, operationId, takeId }) => call('setFavourite', [packId, lang, id, takeId], async () => {
      editable(packId, lang);
      return mutate(packId, operationId, JSON.stringify(['fav', lang, id, takeId, expectedRevision]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        if (takeId !== null && !st.takes.some((x) => x.id === takeId)) throw new StudioError('not_found', 'unknown take');
        st.favouriteTakeId = takeId;
        const revision = bump(st);
        emit('maker.take.changed', { packId, lang, id, takeId: takeId || '', revision, change: 'favourite' });
        return { revision, favouriteTakeId: takeId };
      });
    }),
    studioPublish: (packId, lang, id, { expectedRevision, expectedClipRevision, operationId, takeId }) => call('publish', [packId, lang, id, takeId], async () => {
      editable(packId, lang);
      return mutate(packId, operationId, JSON.stringify(['pub', lang, id, takeId, expectedRevision, expectedClipRevision]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        if (expectedClipRevision !== undefined && expectedClipRevision !== `r${st.revision}`) throw new StudioError('conflict', 'stale clip revision');
        const t = st.takes.find((x) => x.id === takeId); if (!t) throw new StudioError('not_found', 'unknown take');
        if (!(t.consent.confirmed && t.consent.statementVersion === CONSENT_VERSION)) throw new StudioError('consent_required', 'confirm the statement');
        if (!t.render) throw new StudioError('render_required', 'upload the prepared audio first');
        st.publishedTakeId = t.id; st.publishedRevision = st.revision;
        const revision = bump(st);
        emit('maker.voice.saved', { packId, lang, clip: lineOrThrow(id).voice || id, durationSec: t.renderDurationSec, removed: false });
        emit('maker.take.published', { packId, lang, id, clip: lineOrThrow(id).voice || id, takeId: t.id, revision, live: true });
        return { ...savedDto(st, operationId, 'Dub reloaded.', `audio/voice/${lang}/${lineOrThrow(id).voice || id}.wav`), durationSec: t.renderDurationSec, publishedTakeId: t.id };
      });
    }),
    studioUnpublish: (packId, lang, id, { expectedRevision, expectedClipRevision, operationId }) => call('unpublish', [packId, lang, id], async () => {
      editable(packId, lang);
      return mutate(packId, operationId, JSON.stringify(['unpub', lang, id, expectedRevision, expectedClipRevision]), () => {
        const st = stateOf(packId, lang, id); guard(st, expectedRevision);
        st.publishedTakeId = null; st.publishedRevision = null;
        bump(st);
        emit('maker.voice.saved', { packId, lang, clip: lineOrThrow(id).voice || id, durationSec: 0, removed: true });
        return savedDto(st, operationId, 'Dub removed.');
      });
    }),
    studioCreatePack: ({ operationId, id, name, author, language }) => call('createPack', [id], async () => {
      if (!/^[a-z0-9][a-z0-9._-]{2,63}$/.test(id || '')) throw new StudioError('bad_request', 'pack id: 3-64 characters');
      return mutate('studio', operationId, JSON.stringify(['pack+', id, name, author, language]), () => {
        if (packs.some((p) => p.id.toLowerCase() === id.toLowerCase())) throw new StudioError('conflict', 'a pack with this id exists');
        const pack = { id, name: name || id, version: '0.1.0', languages: [language?.code || 'th'], loaded: false, restartRequired: true, studioEditable: false, studioReadOnlyReason: 'pending', metadataState: 'ok' };
        packs.push(pack); catalogRevision += 1;
        emit('maker.pack.created', { packId: id, restartRequired: true });
        return { pack: { ...pack }, restartRequired: true, live: false, note: 'Restart the game once to load this pack.' };
      });
    }),
    studioPlay: ({ id, variant, lang, packId, expectedRevision }) => call('play', [id, variant, lang, packId], async () => {
      lineOrThrow(id);
      if (!['original', 'dub', 'sub'].includes(variant)) throw new StudioError('bad_request', 'variant');
      if (variant === 'sub') {
        if (!packId) throw new StudioError('bad_request', 'sub requires packId');
        editable(packId, lang); guard(stateOf(packId, lang, id), expectedRevision);
      }
      if (status.paused === true) throw new StudioError('game_paused', 'the game is paused');
      if (!status.roomReady || status.busy === true) throw new StudioError('not_ready', 'the game is not ready');
      emit('maker.play', { id, variant, lang, voice: lineOrThrow(id).voice || '' });
      emit('line.spoken', { voice: lineOrThrow(id).voice || '', text: variant === 'sub' ? (stateOf(packId, lang, id).text || lineOrThrow(id).en) : '', textJa: lineOrThrow(id).ja, language: lang, localizationId: `cwy_say_${++sayRows}_001`, lineId: id, source: 'maker', scenarioGroupId: `cwy_say_${sayRows}`, durationSec: lineOrThrow(id).durationSec });
      return { live: true, path: null, durationSec: null, note: 'Playing in game.', focused: status.focused, paused: status.paused };
    })
  };

  function savedDto(st, operationId, note, path = null) {
    return { revision: `r${st.revision}`, operationId, saved: true, live: true, path, durationSec: null, note, publishedTakeId: st.publishedTakeId, effectivePackId: null, restartRequired: false };
  }

  // ---- test controls (not part of the port) ----
  Object.defineProperties(api, {
    dev: { enumerable: false, value: {
      fixture, log, events, packs, lines, localization, novelRows, groups, status,
      emit,
      fail: (method, code, extra = {}) => failures.push({ method, code, ...extra }),
      setOffline: (v) => { offline = v; if (v) setConnection("offline"); },
      // like the loader: a change of focus/pause/room/language is announced on the event ring
      setStatus: (patch) => { Object.assign(status, patch); if ("focused" in patch || "paused" in patch || "busy" in patch) emit("game.focus", { focused: status.focused, paused: status.paused, busy: status.busy, reason: status.reason, observedAt: new Date(now()).toISOString() }); if ("roomReady" in patch) emit("room.ready", { ready: status.roomReady }); if ("gameLanguage" in patch) emit("game.language", { code: status.gameLanguage, isPack: false }); },
      state: (packId, lang, id) => stateOf(packId, lang, id),
      lineCount: lines.length,
      seq: () => seq
    } }
  });
  return Object.assign(api, { devControls: api.dev, bus, connection, watch() {}, close() {} });
}

export const DEV_NOTES = [
  'one resource revision per line (W2 has separate line, clip, take and workflow revisions)',
  'no clip aliases: one localisation id owns one clip',
  'no disk, no storage quota, no transactions or recovery; the operation ledger is unbounded by time (100 entries)',
  'glossary is empty (the UI merges its built-in project names)',
  'a stale-revision check compares the whole line revision, so any change on the line (even another take) conflicts'
];
