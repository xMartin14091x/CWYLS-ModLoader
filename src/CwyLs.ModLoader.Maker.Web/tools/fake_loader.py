#!/usr/bin/env python3
"""Stand-in Dub & Subtitle Studio loader implementing CONTRACT.md (DS-00/DS-05).

Uses the Python standard library only. Serves static web assets at /maker/ with
the per-start maker token injected into index.html, and implements all /v1/maker/*
routes, security rules, limits, disk writes, and event ring behaviours.
"""
import argparse
import collections
import datetime
import hmac
import io
import json
import math
import mimetypes
import pathlib
import re
import secrets
import shutil
import struct
import tempfile
import threading
import time
import urllib.parse
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from studio_v2_fake import Created, StudioV2

DEFAULT_WEB_DIR = pathlib.Path(__file__).resolve().parent.parent
JSON_TYPE = 'application/json; charset=utf-8'

PACK_ID_RE = re.compile(r'^[a-z0-9][a-z0-9._-]{0,63}$')
LANG_RE = re.compile(r'^[a-z]{2,3}(-[a-z0-9]{2,8})?$')
ID_CLIP_RE = re.compile(r'^[A-Za-z0-9_.()-]{1,128}$')  # CONTRACT v1.1: game voice names have parentheses

MAX_JSON_BYTES = 64 * 1024
MAX_WAV_BYTES = 16 * 1024 * 1024
MAX_ICON_BYTES = 512 * 1024
MAX_WAV_DURATION_SEC = 60.0
MAX_DRAIN_BYTES = 32 * 1024 * 1024
EVENT_CAPACITY = 1024

GAME_VERSION = '1.18.0'
LOADER_VERSION = '0.1.0-fake'
GAME_LANGUAGE = 'th'
GAME_LANGUAGE_IS_PACK = True
# Languages the game itself ships; any other code is a pack language (gameLanguageIsPack true).
GAME_OWN_LANGUAGES = ('ja', 'en', 'zh-hans', 'zh-hant', 'pt-br', 'ko', 'ru')
ROOM_READY = True
# The game's own seven languages as the real loader reports them (LanguageCodes); any other code comes from a pack.
BUILT_IN_LANGUAGES = ('ja', 'en', 'zh-hans', 'zh-hant', 'pt-br', 'ko', 'ru')

EDITABLE_PACK_ID = 'sample-thai-dub'
READONLY_PACK_ID = 'sample-reference-pack'
OVERRIDE_PACK_ID = 'sample-override-pack'

# 40 invented scenario lines (sample_01 .. sample_40): 33 voiced, 7 unvoiced.
RAW_SAMPLE_LINES = [
    ('sample_01', 'vo_sample_01', 'morning', 2.8, 'おはよう。よく眠れた？', 'Good morning. Did you sleep well?'),
    ('sample_02', 'vo_sample_02', 'morning', 3.4, 'カーテンを開けて、朝の光を入れよう。', 'Let us open the curtains and let the morning light in.'),
    ('sample_03', 'vo_sample_03', 'morning', 2.5, 'まずは温かいお茶を淹れてくるね。', 'I will go brew a warm cup of tea first.'),
    ('sample_04', 'vo_sample_04', 'morning', 4.1, '今日の予定、ゆっくり一つずつ片付けていこう。', 'Let us take on today\'s plans slowly, one step at a time.'),
    ('sample_05', 'vo_sample_05', 'morning', 3.0, '朝の空気は少しひんやりして気持ちいいね。', 'The morning air is a little crisp and feels nice.'),
    ('sample_06', None, 'morning', None, '（机の上のノートをぱらぱらとめくる音）', '(The sound of flipping through a notebook on the desk.)'),
    ('sample_07', 'vo_sample_07', 'focus', 2.6, 'それじゃあ、集中の時間を始めようか。', 'Alright, shall we start our focus session?'),
    ('sample_08', 'vo_sample_08', 'focus', 3.9, 'タイマーをセットしたよ。無理しないでね。', 'I set the timer for us. Please do not push yourself too hard.'),
    ('sample_09', 'vo_sample_09', 'focus', 3.2, 'ペンの走る音って、なんだか落ち着くよね。', 'The sound of a pen moving across paper is strangely calming.'),
    ('sample_10', 'vo_sample_10', 'focus', 4.5, '難しいところに来たら、深呼吸してみて。', 'If you hit a tricky part, try taking a deep breath.'),
    ('sample_11', 'vo_sample_11', 'focus', 2.9, 'いいペースだよ。その調子、その調子。', 'That is a good pace. Keep it going just like that.'),
    ('sample_12', 'vo_sample_12', 'focus', 3.7, '同じ姿勢が続いているから、肩の力を抜いてね。', 'You have been in the same posture for a while, so relax your shoulders.'),
    ('sample_13', None, 'focus', None, '（静かにページをめくる音だけが響く）', '(Only the quiet sound of turning a page echoes in the room.)'),
    ('sample_14', 'vo_sample_14', 'focus', 3.1, 'あと少しで区切りの時間だよ。', 'Just a little longer until our break time.'),
    ('sample_15', 'vo_sample_15', 'break', 2.7, 'おつかれさま。少し休憩しようか。', 'Good work. Shall we take a short break?'),
    ('sample_16', 'vo_sample_16', 'break', 3.8, '背伸びをすると、頭まですっきりするよ。', 'Stretching your back clears your head too.'),
    ('sample_17', 'vo_sample_17', 'break', 3.3, 'お水は飲んだ？水分補給も大事だからね。', 'Have you had some water? Staying hydrated is important too.'),
    ('sample_18', 'vo_sample_18', 'break', 4.2, '遠くの景色を眺めて、目を休めてあげよう。', 'Let us look at something far away and give our eyes a rest.'),
    ('sample_19', 'vo_sample_19', 'break', 2.9, '甘いクッキーが一つ残っているよ。半分こしようか。', 'There is one sweet cookie left. Want to split it in half?'),
    ('sample_20', None, 'break', None, '（カップをソーサーに置く小さな音）', '(The soft clink of a cup resting on its saucer.)'),
    ('sample_21', 'vo_sample_21', 'rain', 3.5, 'あ、窓の外、雨が降り始めたみたい。', 'Oh, it looks like it started raining outside the window.'),
    ('sample_22', 'vo_sample_22', 'rain', 4.4, '雨音を聞いていると、部屋の中がもっと安心できるね。', 'Listening to the rain makes the room feel even cozier.'),
    ('sample_23', 'vo_sample_23', 'rain', 3.1, 'レコードの針を落として、静かな曲をかけよう。', 'Let us drop the record needle and put on a quiet tune.'),
    ('sample_24', 'vo_sample_24', 'rain', 3.6, '湿気で紙が少し柔らかくなっている気がするな。', 'I feel like the humidity made the paper a little softer.'),
    ('sample_25', 'vo_sample_25', 'rain', 2.8, '雨の日は、いつもよりゆっくり進んでもいいんだよ。', 'On rainy days, it is okay to move slower than usual.'),
    ('sample_26', None, 'rain', None, '（窓ガラスを叩く優しい雨粒のリズム）', '(The gentle rhythm of raindrops tapping against the windowpane.)'),
    ('sample_27', 'vo_sample_27', 'clickReaction', 1.9, 'ん？どうしたの？', 'Hmm? What is the matter?'),
    ('sample_28', 'vo_sample_28', 'clickReaction', 2.3, 'くすぐったいよ、もう。', 'Hey, that tickles.'),
    ('sample_29', 'vo_sample_29', 'clickReaction', 2.6, 'わたしならここにいるから、安心して。', 'I am right here, so do not worry.'),
    ('sample_30', 'vo_sample_30', 'clickReaction', 3.0, '集中が切れたときは、いつでも話しかけてね。', 'Whenever you lose focus, feel free to talk to me.'),
    ('sample_31', 'vo_sample_31', 'clickReaction', 2.2, 'メガネ、少しずれていたかな？ありがとう。', 'Were my glasses slipping a bit? Thank you.'),
    ('sample_32', None, 'clickReaction', None, '（ふっと小さく笑ってうなずく）', '(She gives a small chuckle and nods.)'),
    ('sample_33', 'vo_sample_33', 'evening', 3.4, 'いつの間にか、外が夕焼け色になっているね。', 'Before we knew it, the sky outside turned the colour of sunset.'),
    ('sample_34', 'vo_sample_34', 'evening', 4.0, 'デスクランプを点けると、夜の始まりって感じがするな。', 'Turning on the desk lamp makes it feel like evening has begun.'),
    ('sample_35', 'vo_sample_35', 'evening', 3.7, '今日できたことをノートに書き出してみようよ。', 'Let us write down what we accomplished today in the notebook.'),
    ('sample_36', 'vo_sample_36', 'evening', 3.2, '少しずつでも前に進んでいるのがわかるよ。', 'You can see that you are moving forward little by little.'),
    ('sample_37', 'vo_sample_37', 'evening', 4.3, '夜はハーブティーにして、心を落ち着けようか。', 'Let us have herbal tea tonight and wind down.'),
    ('sample_38', 'vo_sample_38', 'evening', 3.5, '今日も一緒に過ごせてうれしかったよ。', 'I was happy to spend today together with you too.'),
    ('sample_39', None, 'evening', None, '（ランプの暖かい光が机を照らしている）', '(The warm light of the lamp illuminates the desk.)'),
    ('sample_40', None, 'evening', None, '（鉛筆をペン立てに戻す静かな音）', '(The quiet sound of placing a pencil back into the holder.)'),
]

# UI sentinels deliberately include a voice and a story-looking group: neither makes a scenario line.
RAW_UI_TEXT = [
    ('ui_settings', None, 'ui', None, 'UI settings ja', 'UI Settings sentinel'),
    ('ui_continue', 'vo_ui_continue', 'morning', 1.0, 'UI continue ja', 'UI Continue sentinel'),
    ('ui_scene_title', None, None, None, 'UI title ja', 'UI Scene title sentinel'),
]
RAW_SAMPLE_NOVEL_ROWS = [dict(lineId=row[0], groupId=row[2]) for row in RAW_SAMPLE_LINES]

INITIAL_EDITABLE_SUBTITLES_TH = {
    'ui_settings': 'UI translation preserved sentinel',
    'sample_01': 'อรุณสวัสดิ์ เมื่อคืนหลับสบายดีไหม?',
    'sample_02': 'เปิดม่านรับแสงแดดยามเช้ากันเถอะ',
    'sample_03': 'เดี๋ยวฉันไปชงชาอุ่นๆ มาให้ก่อนนะ',
    'sample_04': 'แผนของวันนี้ ค่อยๆ ทำไปทีละอย่างช้าๆ กันเถอะ',
    'sample_06': '(เสียงพลิกสมุดโน้ตบนโต๊ะเบาๆ)',
    'sample_07': 'เอาล่ะ มาเริ่มช่วงเวลาโฟกัสกันเลยไหม?',
    'sample_08': 'ตั้งเวลาไว้ให้แล้วนะ อย่าหักโหมเกินไปล่ะ',
    'sample_09': 'เสียงปากกาขีดเขียนเนี่ย ฟังแล้วรู้สึกสงบจังเลยนะ',
    'sample_15': 'เหนื่อยหน่อยนะ พักกันสักนิดไหม?',
    'sample_16': 'พอยืดเส้นยืดสายแล้ว สมองก็ปลอดโปร่งขึ้นด้วยนะ',
    'sample_21': 'อ๊ะ เหมือนข้างนอกหน้าต่างฝนจะเริ่มตกแล้วนะ',
    'sample_22': 'พอฟังเสียงฝนแล้ว รู้สึกว่าในห้องอบอุ่นปลอดภัยขึ้นอีกนะ',
    'sample_27': 'หืม? มีอะไรเหรอ?',
    'sample_33': 'เผลอแป๊บเดียว ข้างนอกก็กลายเป็นสีท้องฟ้ายามเย็นแล้วนะ',
}

INITIAL_READONLY_SUBTITLES_TH = {
    'sample_01': 'สวัสดีตอนเช้า (ชุดอ้างอิง)',
    'sample_05': 'อากาศตอนเช้าเย็นสบายดีจังเลยนะ',
    'sample_38': 'วันนี้ดีใจที่ได้อยู่ด้วยกันนะ',
    'sample_39': '(แสงไฟอบอุ่นจากโคมไฟส่องสว่างบนโต๊ะ)',
}

# --precedence-fixture: a read-only pack that loads after sample-thai-dub and so wins over it.
OVERRIDE_SUBTITLES_TH = {
    'sample_02': 'ข้อความจากแพ็กที่โหลดทีหลัง (ชนะแพ็กที่แก้ไขได้)',
}
OVERRIDE_DUBS_TH = [('vo_sample_03', 1.8), ('vo_sample_04', 1.7)]

# --language-fixture: sample-thai-dub also carries invented English text, so it has two languages.
LANGUAGE_FIXTURE_SUBTITLES_EN = {
    'sample_01': 'Good morning. Did you sleep well?',
    'sample_02': 'Let us open the curtains.',
}

INITIAL_EDITABLE_DUBS_TH = [
    ('vo_sample_01', 1.2),
    ('vo_sample_02', 1.5),
    ('vo_sample_03', 1.1),
    ('vo_sample_07', 1.3),
    ('vo_sample_15', 1.4),
    ('vo_sample_21', 1.6),
]


class ApiError(Exception):
    def __init__(self, status: int, code: str, message: str | None = None, details: dict | None = None):
        super().__init__(message or code)
        self.status = status
        self.code = code
        self.message = message or code.replace('_', ' ')
        self.details = details


def valid_segment(value: str, pattern: re.Pattern[str], forbid_dots: bool = False) -> bool:
    if not value or not pattern.fullmatch(value):
        return False
    if forbid_dots and (value in ('.', '..') or '..' in value):
        return False
    return True


def make_sine_wav(duration_sec: float = 1.0, sample_rate: int = 24000, freq_hz: float = 440.0) -> bytes:
    nframes = max(1, int(round(duration_sec * sample_rate)))
    buf = io.BytesIO()
    with wave.open(buf, 'wb') as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        samples = [
            int(1600 * math.sin(2.0 * math.pi * freq_hz * (i / sample_rate)))
            for i in range(nframes)
        ]
        wav.writeframes(struct.pack(f'<{nframes}h', *samples))
    return buf.getvalue()


def inspect_wav_bytes(raw: bytes) -> float:
    """Validate PCM 16-bit mono/stereo 8-48 kHz WAV and return duration in seconds."""
    if len(raw) < 44 or raw[:4] != b'RIFF' or raw[8:12] != b'WAVE':
        raise ApiError(415, 'unsupported_media_type', 'Body must be a valid RIFF WAVE file.')
    try:
        with wave.open(io.BytesIO(raw), 'rb') as wav:
            channels = wav.getnchannels()
            sampwidth = wav.getsampwidth()
            framerate = wav.getframerate()
            comptype = wav.getcomptype()
            nframes = wav.getnframes()
    except (wave.Error, EOFError) as exc:
        raise ApiError(415, 'unsupported_media_type', 'Invalid WAV header or stream.') from exc

    if comptype != 'NONE' or sampwidth != 2 or channels not in (1, 2) or not (8000 <= framerate <= 48000) or nframes <= 0:
        raise ApiError(
            415,
            'unsupported_media_type',
            'WAV must be uncompressed 16-bit PCM, mono or stereo, 8000-48000 Hz.',
        )
    duration = nframes / float(framerate)
    if duration > MAX_WAV_DURATION_SEC:
        raise ApiError(413, 'too_large', 'WAV duration exceeds the 60 second limit.')
    return round(duration, 3)


class FakeLoaderState:
    def __init__(self, web_dir: pathlib.Path, data_dir: pathlib.Path, token: str, recovery_fixture: bool = False,
                 precedence_fixture: bool = False, game_language: str | None = None, language_fixture: bool = False):
        self.game_language = game_language or GAME_LANGUAGE
        self.game_language_is_pack = self.game_language not in GAME_OWN_LANGUAGES
        self.web_dir = web_dir.resolve()
        self.data_dir = data_dir.resolve()
        self.token = token
        self.epoch = secrets.token_hex(12)
        self.lock = threading.RLock()
        self.cv = threading.Condition(self.lock)
        self.events: collections.deque[dict] = collections.deque(maxlen=EVENT_CAPACITY)
        self.seq = 0
        self.tick_index = 0
        self.say_index = 0

        self.lines_by_id: dict[str, dict] = {}
        self.lines_list: list[dict] = []
        self.localization: list[dict] = []
        self.novel_rows = list(RAW_SAMPLE_NOVEL_ROWS)
        story_ids = {row['lineId'] for row in self.novel_rows}
        for line_id, voice, group, dur, ja, en in RAW_SAMPLE_LINES + RAW_UI_TEXT:
            item = {
                'id': line_id,
                'voice': voice,
                'group': group,
                'durationSec': dur,
                'ja': ja,
                'en': en,
            }
            self.localization.append(item)
            if line_id not in story_ids:
                continue
            self.lines_by_id[line_id] = item
            self.lines_list.append(item)

        self.packs_meta = [
            {
                'id': EDITABLE_PACK_ID,
                'name': 'Sample Thai Dub & Subtitles',
                'version': '0.1.0',
                'baseLanguages': ['th'],
                'editable': True,
                'readOnlyReason': None,
            },
            {
                'id': READONLY_PACK_ID,
                'name': 'Sample Reference Pack',
                'version': '1.0.0',
                'baseLanguages': ['th'],
                'editable': False,
                'readOnlyReason': 'Reference pack is read-only.',
            },
        ]
        if precedence_fixture:
            self.packs_meta.append({
                'id': OVERRIDE_PACK_ID,
                'name': 'Sample Override Pack (loads last)',
                'version': '0.1.0',
                'baseLanguages': ['th'],
                'editable': False,
                'readOnlyReason': 'Invented pack that loads after sample-thai-dub and wins over it.',
                'overrides': True,
            })
        self._seed_disk()
        if precedence_fixture:
            self._seed_precedence_fixture()
        if language_fixture:
            self._seed_language_fixture()
        self.studio = StudioV2(self, ApiError)
        self.studio.adopt_created()
        if recovery_fixture:
            self.packs_meta.append(dict(id='sample-recovery', name='Recovery contract fixture', version='0.1.0',
                                        baseLanguages=['th'], editable=True, readOnlyReason=None))
            if not self.studio.path('sample-recovery').exists():
                state = self.studio.load('sample-recovery')
                state['blocked'] = dict(resource='.maker/lines/th/sample_01.json', operationId='a' * 32,
                                        intended=dict(text='Invented intended subtitle'))
                self.studio.save('sample-recovery', state)
        self.emit('room.ready', {'ready': ROOM_READY})
        self.emit('game.language', {'code': self.game_language, 'isPack': self.game_language_is_pack})

    def _seed_disk(self) -> None:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        ed_sub = self.subtitle_file(EDITABLE_PACK_ID, 'th')
        if not ed_sub.exists():
            self._write_json_atomic(ed_sub, dict(INITIAL_EDITABLE_SUBTITLES_TH))
        ro_sub = self.subtitle_file(READONLY_PACK_ID, 'th')
        if not ro_sub.exists():
            self._write_json_atomic(ro_sub, dict(INITIAL_READONLY_SUBTITLES_TH))
        for clip, dur in INITIAL_EDITABLE_DUBS_TH:
            wav_path = self.voice_file(EDITABLE_PACK_ID, 'th', clip)
            if not wav_path.exists():
                wav_path.parent.mkdir(parents=True, exist_ok=True)
                wav_path.write_bytes(make_sine_wav(duration_sec=dur))

    def _seed_precedence_fixture(self) -> None:
        sub = self.subtitle_file(OVERRIDE_PACK_ID, 'th')
        if not sub.exists():
            self._write_json_atomic(sub, dict(OVERRIDE_SUBTITLES_TH))
        for clip, dur in OVERRIDE_DUBS_TH:
            wav_path = self.voice_file(OVERRIDE_PACK_ID, 'th', clip)
            if not wav_path.exists():
                wav_path.parent.mkdir(parents=True, exist_ok=True)
                wav_path.write_bytes(make_sine_wav(duration_sec=dur))

    def _seed_language_fixture(self) -> None:
        sub = self.subtitle_file(EDITABLE_PACK_ID, 'en')
        if not sub.exists():
            self._write_json_atomic(sub, dict(LANGUAGE_FIXTURE_SUBTITLES_EN))

    def subtitle_file(self, pack_id: str, lang: str) -> pathlib.Path:
        target = (self.data_dir / pack_id / 'data' / 'subtitles' / f'{lang}.json').resolve()
        if not target.is_relative_to(self.data_dir):
            raise ApiError(400, 'bad_request', 'Invalid subtitle file path.')
        return target

    def voice_file(self, pack_id: str, lang: str, clip: str) -> pathlib.Path:
        target = (self.data_dir / pack_id / 'audio' / 'voice' / lang / f'{clip}.wav').resolve()
        if not target.is_relative_to(self.data_dir):
            raise ApiError(400, 'bad_request', 'Invalid voice file path.')
        return target

    @staticmethod
    def _write_json_atomic(path: pathlib.Path, data: dict) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(path.suffix + '.tmp')
        tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        tmp.replace(path)

    @staticmethod
    def _read_json(path: pathlib.Path) -> dict:
        if not path.is_file():
            return {}
        try:
            raw = json.loads(path.read_text(encoding='utf-8'))
            return raw if isinstance(raw, dict) else {}
        except Exception:
            return {}

    def next_say_row(self) -> tuple[str, str]:
        """Like the real loader: Pack Maker Play speaks through Satone.Say, whose runtime row is cwy_say_<n>_001."""
        with self.lock:
            self.say_index += 1
            return f'cwy_say_{self.say_index}', f'cwy_say_{self.say_index}_001'

    def emit(self, event_type: str, data: dict) -> None:
        with self.cv:
            self.seq += 1
            now = datetime.datetime.now(datetime.timezone.utc).isoformat()
            self.events.append({
                'seq': self.seq,
                'time': now,
                'type': event_type,
                'data': data,
            })
            self.cv.notify_all()

    def get_pack_meta(self, pack_id: str) -> dict:
        for meta in self.packs_meta:
            if meta['id'] == pack_id:
                return meta
        raise ApiError(404, 'not_found', f'Pack {pack_id} was not found.')

    def pack_languages(self, meta: dict) -> list[str]:
        langs = set(meta['baseLanguages'])
        pack_root = self.data_dir / meta['id']
        sub_dir = pack_root / 'data' / 'subtitles'
        if sub_dir.is_dir():
            for f in sub_dir.iterdir():
                if f.is_file() and f.suffix == '.json' and LANG_RE.fullmatch(f.stem):
                    if self._read_json(f):
                        langs.add(f.stem)
        voice_dir = pack_root / 'audio' / 'voice'
        if voice_dir.is_dir():
            for d in voice_dir.iterdir():
                if d.is_dir() and LANG_RE.fullmatch(d.name) and any(x.suffix == '.wav' for x in d.iterdir()):
                    langs.add(d.name)
        return sorted(langs)

    def list_packs(self) -> list[dict]:
        with self.lock:
            result = []
            for meta in self.packs_meta:
                result.append({
                    'id': meta['id'],
                    'name': meta['name'],
                    'version': meta['version'],
                    'languages': self.pack_languages(meta),
                    'editable': meta['editable'],
                    'readOnlyReason': meta['readOnlyReason'],
                })
            return result

    def resolve_line(self, base_line: dict, lang: str) -> dict:
        line_id = base_line['id']
        voice = base_line['voice']
        subtitle = None
        subtitle_pack = None
        dub_pack = None
        dub_duration = None

        winners_first = ([m for m in reversed(self.packs_meta) if m.get('overrides')]
                         + [m for m in self.packs_meta if not m.get('overrides')])
        for meta in winners_first:
            pack_id = meta['id']
            if subtitle is None:
                subs = self._read_json(self.subtitle_file(pack_id, lang))
                val = subs.get(line_id)
                if isinstance(val, str) and val:
                    subtitle = val
                    subtitle_pack = pack_id
            if voice and dub_pack is None:
                wav_path = self.voice_file(pack_id, lang, voice)
                if wav_path.is_file():
                    try:
                        dur = inspect_wav_bytes(wav_path.read_bytes())
                        dub_pack = pack_id
                        dub_duration = dur
                    except ApiError:
                        pass

        return {
            'id': line_id,
            'voice': voice,
            'group': base_line['group'],
            'durationSec': base_line['durationSec'],
            'ja': base_line['ja'],
            'en': base_line['en'],
            'subtitle': subtitle,
            'subtitlePack': subtitle_pack,
            'dubPack': dub_pack,
            'dubDurationSec': dub_duration,
        }

    def query_lines(self, lang: str, search: str | None, filter_mode: str, offset: int, limit: int) -> dict:
        with self.lock:
            resolved = [self.resolve_line(item, lang) for item in self.lines_list]
        if search:
            needle = search.lower()
            resolved = [
                item for item in resolved
                if needle in item['id'].lower()
                or (item['voice'] and needle in item['voice'].lower())
                or needle in item['ja'].lower()
                or needle in item['en'].lower()
                or (item['subtitle'] and needle in item['subtitle'].lower())
            ]
        if filter_mode == 'missing-subtitle':
            resolved = [item for item in resolved if not item['subtitle']]
        elif filter_mode == 'missing-dub':
            resolved = [item for item in resolved if item['voice'] is not None and item['dubPack'] is None]
        elif filter_mode == 'done':
            resolved = [
                item for item in resolved
                if item['subtitle'] and (item['voice'] is None or item['dubPack'] is not None)
            ]
        total = len(resolved)
        page = resolved[offset:offset + limit]
        return {
            'total': total,
            'offset': offset,
            'items': page,
        }

    def get_line(self, line_id: str, lang: str) -> dict:
        with self.lock:
            base = self.lines_by_id.get(line_id)
            if base is None:
                raise ApiError(404, 'not_found', f'Line {line_id} was not found.')
            return self.resolve_line(base, lang)

    def save_subtitle(self, pack_id: str, lang: str, line_id: str, text: str | None) -> dict:
        with self.lock:
            self.get_line(line_id, lang)
            meta = self.get_pack_meta(pack_id)
            if not meta['editable']:
                raise ApiError(403, 'read_only_pack', meta['readOnlyReason'] or 'Pack is read-only.')
            sub_path = self.subtitle_file(pack_id, lang)
            subs = self._read_json(sub_path)
            rel_path = f'data/subtitles/{lang}.json'
            if text is None:
                subs.pop(line_id, None)
                self._write_json_atomic(sub_path, subs)
                self.emit('maker.subtitle.saved', {
                    'packId': pack_id,
                    'lang': lang,
                    'id': line_id,
                    'removed': True,
                })
                return {
                    'live': True,
                    'path': rel_path,
                    'durationSec': None,
                    'note': f'Removed subtitle {line_id} ({lang}) from {pack_id}.',
                }
            subs[line_id] = text
            self._write_json_atomic(sub_path, subs)
            self.emit('maker.subtitle.saved', {
                'packId': pack_id,
                'lang': lang,
                'id': line_id,
                'removed': False,
            })
            return {
                'live': True,
                'path': rel_path,
                'durationSec': None,
                'note': f'Saved subtitle {line_id} ({lang}) in {pack_id}.',
            }

    def save_voice(self, pack_id: str, lang: str, clip: str, wav_bytes: bytes | None) -> dict:
        with self.lock:
            meta = self.get_pack_meta(pack_id)
            if not meta['editable']:
                raise ApiError(403, 'read_only_pack', meta['readOnlyReason'] or 'Pack is read-only.')
            wav_path = self.voice_file(pack_id, lang, clip)
            rel_path = f'audio/voice/{lang}/{clip}.wav'
            if wav_bytes is None:
                if wav_path.is_file():
                    wav_path.unlink()
                self.emit('maker.voice.saved', {
                    'packId': pack_id,
                    'lang': lang,
                    'clip': clip,
                    'durationSec': None,
                    'removed': True,
                })
                return {
                    'live': True,
                    'path': rel_path,
                    'durationSec': None,
                    'note': f'Removed voice clip {clip} ({lang}) from {pack_id}.',
                }
            duration = inspect_wav_bytes(wav_bytes)
            wav_path.parent.mkdir(parents=True, exist_ok=True)
            tmp = wav_path.with_suffix('.wav.tmp')
            tmp.write_bytes(wav_bytes)
            tmp.replace(wav_path)
            self.emit('maker.voice.saved', {
                'packId': pack_id,
                'lang': lang,
                'clip': clip,
                'durationSec': duration,
                'removed': False,
            })
            return {
                'live': True,
                'path': rel_path,
                'durationSec': duration,
                'note': f'Saved voice clip {clip} ({lang}, {duration:.2f}s) in {pack_id}.',
            }

    def play_line(self, line_id: str, variant: str, lang: str) -> dict:
        with self.lock:
            base = self.lines_by_id.get(line_id)
            if base is None:
                raise ApiError(404, 'not_found', f'Line {line_id} was not found.')
            resolved = self.resolve_line(base, lang)
            voice = resolved['voice']
            if not voice:
                raise ApiError(404, 'not_found', f'Line {line_id} has no voice clip.')
            if variant == 'dub':
                if not resolved['dubPack']:
                    raise ApiError(404, 'not_found', f'No dub clip found for {line_id} in {lang}.')
                duration = resolved['dubDurationSec']
            else:
                duration = resolved['durationSec']

            self.emit('maker.play', {
                'id': line_id,
                'variant': variant,
                'lang': lang,
                'voice': voice,
            })
            say_group, say_row = self.next_say_row()
            self.emit('line.spoken', {
                'voice': voice,
                'text': resolved['subtitle'] or resolved['en'],
                'textJa': resolved['ja'],
                'language': lang,
                'localizationId': say_row,
                'source': 'maker.play',
                'scenarioGroupId': say_group,
                'durationSec': duration,
                'lineId': line_id,
            })
            return {
                'live': True,
                'path': None,
                'durationSec': duration,
                'note': f'Playing {line_id} ({variant}, {lang}).',
            }

    def emit_background_spoken(self) -> None:
        with self.lock:
            voiced = [item for item in self.lines_list if item['voice']]
            if not voiced:
                return
            base = voiced[self.tick_index % len(voiced)]
            self.tick_index += 1
            resolved = self.resolve_line(base, self.game_language)
            self.emit('line.spoken', {
                'voice': resolved['voice'],
                'text': resolved['subtitle'] or resolved['en'],
                'textJa': resolved['ja'],
                'language': self.game_language,
                'localizationId': resolved['id'],
                'source': 'ambient',
                'scenarioGroupId': resolved['group'],
                'durationSec': resolved['dubDurationSec'] or resolved['durationSec'],
                'lineId': resolved['id'],
            })


class MakerHTTPServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, server_address, RequestHandlerClass, state: FakeLoaderState):
        super().__init__(server_address, RequestHandlerClass)
        self.state = state


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, fmt, *args):
        pass

    @property
    def state(self) -> FakeLoaderState:
        return self.server.state  # type: ignore[attr-defined]

    def send_payload(self, status: int, body, content_type: str = JSON_TYPE, extra: dict | None = None) -> None:
        if content_type == JSON_TYPE and not isinstance(body, (bytes, bytearray)):
            raw = json.dumps(body, ensure_ascii=False).encode('utf-8')
        elif isinstance(body, str):
            raw = body.encode('utf-8')
        else:
            raw = body
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(raw)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(raw)

    def drain_body_if_present(self) -> None:
        try:
            length = int(self.headers.get('Content-Length') or '0')
        except ValueError:
            return
        if 0 < length <= MAX_DRAIN_BYTES:
            remaining = length
            while remaining > 0:
                chunk = self.rfile.read(min(65536, remaining))
                if not chunk:
                    break
                remaining -= len(chunk)

    def check_host_and_origin(self, path: str) -> None:
        port = self.server.server_port
        allowed_hosts = (f'127.0.0.1:{port}', f'localhost:{port}')
        host = (self.headers.get('Host') or '').strip().lower()
        if host not in allowed_hosts:
            self.drain_body_if_present()
            raise ApiError(421, 'host_refused', f'Host must be 127.0.0.1:{port} or localhost:{port}.')

        is_maker_surface = path == '/maker' or path.startswith('/maker/') or path.startswith('/v1/maker/')
        origin = self.headers.get('Origin')
        sec_fetch_site = self.headers.get('Sec-Fetch-Site')

        if is_maker_surface:
            if origin is not None:
                allowed_origins = (f'http://127.0.0.1:{port}', f'http://localhost:{port}')
                if origin.strip().lower() not in allowed_origins:
                    self.drain_body_if_present()
                    raise ApiError(403, 'forbidden_origin', 'Origin is not permitted.')
            if sec_fetch_site is not None:
                if sec_fetch_site.strip().lower() not in ('same-origin', 'none'):
                    self.drain_body_if_present()
                    raise ApiError(403, 'forbidden_origin', 'Cross-site requests are refused.')
        elif path.startswith('/v1/'):
            has_browser_header = origin is not None or any(
                k.lower().startswith('sec-fetch-') for k in self.headers.keys()
            )
            self.drain_body_if_present()
            if has_browser_header:
                raise ApiError(403, 'forbidden_origin', 'Browser requests are refused on non-maker routes.')
            raise ApiError(404, 'not_found', 'Route not found.')

    def check_bearer(self) -> None:
        auth = self.headers.get('Authorization') or ''
        expected = 'Bearer ' + self.state.token
        if not hmac.compare_digest(auth, expected):
            self.drain_body_if_present()
            raise ApiError(401, 'unauthorized', 'Valid Bearer maker token required.')

    def read_json_body(self) -> dict:
        if self.headers.get('Transfer-Encoding'):
            raise ApiError(400, 'bad_request', 'Chunked transfer encoding is not supported.')
        cl_header = self.headers.get('Content-Length')
        if not cl_header:
            raise ApiError(400, 'bad_request', 'Content-Length header is required.')
        try:
            size = int(cl_header)
        except ValueError as exc:
            raise ApiError(400, 'bad_request', 'Invalid Content-Length.') from exc
        if size <= 0:
            raise ApiError(400, 'bad_request', 'Request body must not be empty.')
        if size > MAX_JSON_BYTES:
            self.drain_body_if_present()
            raise ApiError(413, 'too_large', 'JSON body exceeds the 64 KiB limit.')

        ctype = (self.headers.get('Content-Type') or '').split(';')[0].strip().lower()
        if ctype != 'application/json':
            self.drain_body_if_present()
            raise ApiError(415, 'unsupported_media_type', 'Content-Type must be application/json; charset=utf-8.')

        raw = self.rfile.read(size)
        if len(raw) != size:
            raise ApiError(400, 'bad_request', 'Incomplete request body.')
        try:
            def unique(pairs):
                obj = {}
                for key, value in pairs:
                    if key in obj:
                        raise ValueError('duplicate JSON key')
                    obj[key] = value
                return obj
            parsed = json.loads(raw.decode('utf-8'), object_pairs_hook=unique,
                                parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
        except (UnicodeDecodeError, ValueError) as exc:
            raise ApiError(400, 'bad_request', 'Malformed JSON body.') from exc
        if not isinstance(parsed, dict):
            raise ApiError(400, 'bad_request', 'JSON body must be an object.')
        return parsed

    def read_wav_body(self) -> bytes:
        if self.headers.get('Transfer-Encoding'):
            raise ApiError(400, 'bad_request', 'Chunked transfer encoding is not supported.')
        cl_header = self.headers.get('Content-Length')
        if not cl_header:
            raise ApiError(400, 'bad_request', 'Content-Length header is required.')
        try:
            size = int(cl_header)
        except ValueError as exc:
            raise ApiError(400, 'bad_request', 'Invalid Content-Length.') from exc
        if size <= 0:
            raise ApiError(415, 'unsupported_media_type', 'WAV body must not be empty.')
        if size > MAX_WAV_BYTES:
            self.drain_body_if_present()
            raise ApiError(413, 'too_large', 'WAV body exceeds the 16 MiB limit.')

        ctype = (self.headers.get('Content-Type') or '').split(';')[0].strip().lower()
        if ctype != 'audio/wav':
            self.drain_body_if_present()
            raise ApiError(415, 'unsupported_media_type', 'Content-Type must be audio/wav.')

        raw = self.rfile.read(size)
        if len(raw) != size:
            raise ApiError(400, 'bad_request', 'Incomplete WAV upload.')
        return raw

    def read_png_body(self) -> bytes:
        """Raw image/png body for the v2.3 pack icon route: at most 512 KiB."""
        if self.headers.get('Transfer-Encoding'):
            raise ApiError(400, 'bad_request', 'Chunked transfer encoding is not supported.')
        try:
            size = int(self.headers.get('Content-Length') or '')
        except ValueError as exc:
            raise ApiError(400, 'bad_request', 'Content-Length header is required.') from exc
        if size <= 0:
            raise ApiError(415, 'unsupported_media_type', 'PNG body must not be empty.')
        if size > MAX_ICON_BYTES:
            self.drain_body_if_present()
            raise ApiError(413, 'too_large', 'A pack icon must be at most 512 KiB.')
        if (self.headers.get('Content-Type') or '').split(';')[0].strip().lower() != 'image/png':
            self.drain_body_if_present()
            raise ApiError(415, 'unsupported_media_type', 'Content-Type must be image/png.')
        raw = self.rfile.read(size)
        if len(raw) != size:
            raise ApiError(400, 'bad_request', 'Incomplete PNG upload.')
        return raw

    def dispatch(self) -> None:
        try:
            split = urllib.parse.urlsplit(self.path)
            raw_path = split.path
            self.check_host_and_origin(raw_path)

            lower_raw = raw_path.lower()
            if (
                '%' in raw_path  # like the real ApiTarget: no percent-encoding in paths at all (CONTRACT v1.1)
                or '\\' in raw_path
                or '//' in raw_path
            ):
                if raw_path.startswith('/v1/maker/'):
                    self.check_bearer()
                self.drain_body_if_present()
                raise ApiError(400, 'bad_request', 'Malformed or traversal path.')

            path = urllib.parse.unquote(raw_path)
            if '..' in path.split('/') or '.' in path.split('/')[1:]:
                if path.startswith('/v1/maker/'):
                    self.check_bearer()
                self.drain_body_if_present()
                raise ApiError(400, 'bad_request', 'Path traversal segments are not allowed.')

            if path.startswith('/v1/maker/'):
                self.check_bearer()
                query_pairs = urllib.parse.parse_qsl(split.query, keep_blank_values=True)
                if len({k for k, _ in query_pairs}) != len(query_pairs):
                    raise ApiError(400, 'bad_request', 'Duplicate query parameters are not allowed.')
                return self.handle_maker_api(path, dict(query_pairs))

            if path == '/maker' or path.startswith('/maker/'):
                return self.handle_static(path, split.query)

            self.drain_body_if_present()
            raise ApiError(404, 'not_found', 'Not found.')
        except ApiError as err:
            self.close_connection = True
            extra = {'WWW-Authenticate': 'Bearer'} if err.status == 401 else None
            error = {'code': err.code, 'message': err.message}
            if err.details is not None:
                error['details'] = err.details
            self.send_payload(err.status, {'error': error}, extra=extra)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception:
            self.close_connection = True
            self.send_payload(500, {'error': {'code': 'internal', 'message': 'Internal fake loader error.'}})

    do_GET = dispatch
    do_PUT = dispatch
    do_POST = dispatch
    do_DELETE = dispatch
    do_PATCH = dispatch

    def handle_static(self, path: str, query: str) -> None:
        if self.command != 'GET':
            raise ApiError(404, 'not_found', 'Only GET is supported for static assets.')
        if path == '/maker':
            loc = '/maker/' + (f'?{query}' if query else '')
            return self.send_payload(302, b'', 'text/plain; charset=utf-8', {'Location': loc})

        rel = path[len('/maker/'):].lstrip('/') or 'index.html'
        if rel.startswith('.') or rel.startswith('tools/'):
            raise ApiError(404, 'not_found', 'Asset not found.')
        target = (self.state.web_dir / rel).resolve()
        if not target.is_relative_to(self.state.web_dir):
            raise ApiError(400, 'bad_request', 'Invalid asset path.')

        meta_tag = f'<meta name="cwyls-maker-token" content="{self.state.token}">'
        if rel == 'index.html':
            if target.is_file():
                html = target.read_text(encoding='utf-8')
                if 'name="cwyls-maker-token"' not in html:
                    if '<head>' in html:
                        html = html.replace('<head>', f'<head>\n  {meta_tag}', 1)
                    else:
                        html = f'<!doctype html>\n<html><head>{meta_tag}</head><body>{html}</body></html>'
            else:
                html = (
                    '<!doctype html>\n<html lang="en">\n<head>\n'
                    '  <meta charset="utf-8">\n'
                    f'  {meta_tag}\n'
                    '  <title>CWY:LS Dub &amp; Subtitle Studio (Fake Loader)</title>\n'
                    '</head>\n<body>\n  <h1>CWY:LS Dub &amp; Subtitle Studio</h1>\n</body>\n</html>\n'
                )
            return self.send_payload(200, html.encode('utf-8'), 'text/html; charset=utf-8')

        if not target.is_file():
            raise ApiError(404, 'not_found', 'Asset not found.')
        mime, _ = mimetypes.guess_type(target.name)
        return self.send_payload(200, target.read_bytes(), mime or 'application/octet-stream')

    def handle_maker_api(self, path: str, query: dict[str, str]) -> None:
        if self.command in ('GET', 'DELETE') and int(self.headers.get('Content-Length') or '0') > 0:
            self.drain_body_if_present()
            raise ApiError(400, 'bad_request', f'{self.command} requests must not include a body.')

        if path.startswith('/v1/maker/studio/'):
            payload = self.state.studio.handle(self, path, query)
            if isinstance(payload, bytes):  # creator take audio (W2b M5d); v2.3 Binary: pack icon PNG, export zip
                return self.send_payload(200, payload, content_type=getattr(payload, 'content_type', 'audio/wav'),
                                         extra=getattr(payload, 'headers', None))
            if isinstance(payload, tuple):  # (status, body), e.g. 201 for a created take (W2b M5d)
                return self.send_payload(payload[0], payload[1])
            return self.send_payload(201 if isinstance(payload, Created) else 200, payload)

        # GET /v1/maker/status
        if path == '/v1/maker/status':
            if self.command != 'GET':
                raise ApiError(404, 'not_found')
            if query:
                raise ApiError(400, 'bad_request', 'Status route takes no query parameters.')
            return self.send_payload(200, {
                'gameVersion': GAME_VERSION,
                'loaderVersion': LOADER_VERSION,
                'gameLanguage': self.state.game_language,
                'gameLanguageIsPack': self.state.game_language_is_pack,
                'roomReady': ROOM_READY,
            })

        # GET /v1/maker/packs
        if path == '/v1/maker/packs':
            if self.command != 'GET':
                raise ApiError(404, 'not_found')
            if query:
                raise ApiError(400, 'bad_request', 'Packs route takes no query parameters.')
            return self.send_payload(200, {'packs': self.state.list_packs()})

        # GET /v1/maker/lines
        if path == '/v1/maker/lines':
            if self.command != 'GET':
                raise ApiError(404, 'not_found')
            if set(query) - {'lang', 'q', 'filter', 'offset', 'limit'}:
                raise ApiError(400, 'bad_request', 'Unknown query parameter for /v1/maker/lines.')
            lang = query.get('lang') or ''
            if not valid_segment(lang, LANG_RE):
                raise ApiError(400, 'bad_request', 'Valid lang parameter is required.')
            filter_mode = query.get('filter', 'all')
            if filter_mode not in ('all', 'missing-subtitle', 'missing-dub', 'done'):
                raise ApiError(400, 'bad_request', 'Invalid filter mode.')
            try:
                offset = int(query.get('offset', '0'))
                limit = int(query.get('limit', '100'))
            except ValueError as exc:
                raise ApiError(400, 'bad_request', 'offset and limit must be integers.') from exc
            if offset < 0 or not (1 <= limit <= 500):
                raise ApiError(400, 'bad_request', 'offset must be >= 0 and limit must be 1..500.')
            search = query.get('q')
            return self.send_payload(200, self.state.query_lines(lang, search, filter_mode, offset, limit))

        # POST /v1/maker/play
        if path == '/v1/maker/play':
            if self.command != 'POST':
                raise ApiError(404, 'not_found')
            if query:
                raise ApiError(400, 'bad_request', 'Play route takes no query parameters.')
            body = self.read_json_body()
            if set(body) - {'id', 'variant', 'lang'}:
                raise ApiError(400, 'bad_request', 'Unexpected fields in play request.')
            line_id = body.get('id')
            variant = body.get('variant')
            lang = body.get('lang') or self.state.game_language
            if not isinstance(line_id, str) or not valid_segment(line_id, ID_CLIP_RE, forbid_dots=True):
                raise ApiError(400, 'bad_request', 'Invalid line id.')
            if variant not in ('original', 'dub'):
                raise ApiError(400, 'bad_request', 'variant must be original or dub.')
            if not isinstance(lang, str) or not valid_segment(lang, LANG_RE):
                raise ApiError(400, 'bad_request', 'Invalid language code.')
            return self.send_payload(200, self.state.play_line(line_id, variant, lang))

        # GET /v1/maker/events
        if path == '/v1/maker/events':
            if self.command != 'GET':
                raise ApiError(404, 'not_found')
            if set(query) - {'after', 'wait', 'limit'}:
                raise ApiError(400, 'bad_request', 'Unknown query parameter for /v1/maker/events.')
            try:
                after = int(query.get('after', '0'))
                wait = float(query.get('wait', '0'))
                limit = int(query.get('limit', '100'))
            except ValueError as exc:
                raise ApiError(400, 'bad_request', 'Invalid events query parameter types.') from exc
            if after < 0 or math.isnan(wait) or not (0.0 <= wait <= 25.0) or not (1 <= limit <= 500):
                raise ApiError(400, 'bad_request', 'Events parameters out of range.')

            deadline = time.monotonic() + wait
            with self.state.cv:
                while self.state.seq == after and time.monotonic() < deadline:
                    remaining = deadline - time.monotonic()
                    if remaining > 0:
                        self.state.cv.wait(remaining)
                reset = after > self.state.seq
                oldest = self.state.events[0]['seq'] if self.state.events else self.state.seq + 1
                cursor = oldest - 1 if reset else after
                dropped = max(0, oldest - 1 - cursor)
                events = [e for e in self.state.events if e['seq'] > cursor][:limit]
                next_seq = events[-1]['seq'] if events else (self.state.seq if reset else after)
                result = {
                    'epoch': self.state.epoch,
                    'events': events,
                    'next': next_seq,
                    'latest': self.state.seq,
                    'oldest': oldest,
                    'dropped': dropped,
                    'reset': reset,
                    'timedOut': not events and not reset,
                }
            return self.send_payload(200, result)

        parts = path.split('/')
        # GET /v1/maker/lines/{id}
        if len(parts) == 5 and parts[3] == 'lines':
            if self.command != 'GET':
                raise ApiError(404, 'not_found')
            if set(query) - {'lang'}:
                raise ApiError(400, 'bad_request', 'Unknown query parameter.')
            line_id = parts[4]
            if not valid_segment(line_id, ID_CLIP_RE, forbid_dots=True):
                raise ApiError(400, 'bad_request', 'Invalid line id segment.')
            lang = query.get('lang') or self.state.game_language
            if not valid_segment(lang, LANG_RE):
                raise ApiError(400, 'bad_request', 'Invalid lang parameter.')
            return self.send_payload(200, self.state.get_line(line_id, lang))

        # PUT | DELETE /v1/maker/packs/{packId}/subtitles/{lang}/{id}
        # PUT | DELETE /v1/maker/packs/{packId}/voice/{lang}/{clip}
        if len(parts) == 8 and parts[3] == 'packs' and parts[5] in ('subtitles', 'voice'):
            if query:
                raise ApiError(400, 'bad_request', 'Pack write routes take no query parameters.')
            pack_id, kind, lang, target_id = parts[4], parts[5], parts[6], parts[7]
            if (
                not valid_segment(pack_id, PACK_ID_RE)
                or not valid_segment(lang, LANG_RE)
                or not valid_segment(target_id, ID_CLIP_RE, forbid_dots=True)
            ):
                self.drain_body_if_present()
                raise ApiError(400, 'bad_request', 'Invalid route segment.')

            if kind == 'subtitles':
                if self.command == 'PUT':
                    body = self.read_json_body()
                    if set(body) != {'text'}:
                        raise ApiError(400, 'bad_request', 'Subtitle body must contain only {"text": "..."}.')
                    text = body.get('text')
                    if not isinstance(text, str) or not (1 <= len(text) <= 2000):
                        raise ApiError(400, 'bad_request', 'Subtitle text must be 1..2000 characters.')
                    return self.send_payload(200, self.state.save_subtitle(pack_id, lang, target_id, text))
                if self.command == 'DELETE':
                    return self.send_payload(200, self.state.save_subtitle(pack_id, lang, target_id, None))
                raise ApiError(404, 'not_found')

            if kind == 'voice':
                if self.command == 'PUT':
                    wav_bytes = self.read_wav_body()
                    return self.send_payload(200, self.state.save_voice(pack_id, lang, target_id, wav_bytes))
                if self.command == 'DELETE':
                    return self.send_payload(200, self.state.save_voice(pack_id, lang, target_id, None))
                raise ApiError(404, 'not_found')

        self.drain_body_if_present()
        raise ApiError(404, 'not_found', 'Maker route not found.')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8127, help='Loopback port to listen on (default: 8127).')
    parser.add_argument('--web', type=pathlib.Path, default=DEFAULT_WEB_DIR, help='Directory served at /maker/.')
    parser.add_argument('--data', type=pathlib.Path, default=None, help='Directory for pack files (default: temp dir).')
    parser.add_argument('--token', type=str, default=None, help='Override random maker bearer token.')
    parser.add_argument('--tick-sec', type=float, default=20.0, help='Background line.spoken interval in seconds.')
    parser.add_argument('--recovery-fixture', action='store_true', help='Seed a disposable blocked recovery contract pack.')
    parser.add_argument('--precedence-fixture', action='store_true',
                        help='Add the read-only pack sample-override-pack, which loads after sample-thai-dub and wins over it.')
    parser.add_argument('--game-language', type=str, default=None, metavar='CODE',
                        help='The language the fake game reports (default: th).')
    parser.add_argument('--language-fixture', action='store_true',
                        help='Give sample-thai-dub invented English text too, so it has two languages (en, th).')
    args = parser.parse_args()

    created_temp = False
    if args.data is None:
        data_dir = pathlib.Path(tempfile.mkdtemp(prefix='cwyls-maker-fake-'))
        created_temp = True
    else:
        data_dir = args.data

    token = args.token or secrets.token_hex(32)
    state = FakeLoaderState(web_dir=args.web, data_dir=data_dir, token=token, recovery_fixture=args.recovery_fixture,
                            precedence_fixture=args.precedence_fixture, game_language=args.game_language,
                            language_fixture=args.language_fixture)

    stop_event = threading.Event()
    if args.tick_sec > 0:
        def ticker():
            while not stop_event.wait(args.tick_sec):
                state.emit_background_spoken()
        threading.Thread(target=ticker, name='fake-loader-ticker', daemon=True).start()

    server = MakerHTTPServer(('127.0.0.1', args.port), Handler, state)
    print(f'Fake loader: http://127.0.0.1:{server.server_address[1]}/maker/ (data: {data_dir})', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        stop_event.set()
        server.server_close()
        if created_temp:
            shutil.rmtree(data_dir, ignore_errors=True)


if __name__ == '__main__':
    main()
