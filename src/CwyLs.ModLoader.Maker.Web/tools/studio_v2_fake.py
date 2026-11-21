"""Invented-data Studio v2 stand-in: text work, recovery and (M5d) takes, creator audio and publication.
No game files, game media reads or pack creation.

Persists fake creator state in .maker/fake-v2.json. This is a protocol simulator, not the
production transaction journal or proof of native crash recovery.
"""
import copy
import datetime
import os
import hashlib
import io
import json
import pathlib
import re
import secrets
import struct
import time
import wave
import zipfile

HEX = re.compile(r'^[0-9a-f]{32}$')
DIGITS = re.compile(r'^[0-9]+$')
LANG = re.compile(r'^[a-z]{2,3}(-[a-z0-9]{2,8})?$')
SEG = re.compile(r'^[A-Za-z0-9_.()-]{1,128}$')
PACK = re.compile(r'^[a-z0-9][a-z0-9._-]{0,63}$')
LABELS = {key: 'group.' + key for key in
          ('morning', 'focus', 'break', 'rain', 'clickReaction', 'evening', 'ungrouped')}
STATES = ('todo', 'draft', 'done', 'needs-retake')
NEW_PACK = re.compile(r'^[a-z0-9][a-z0-9._-]{2,63}$')
SCRIPT = re.compile(r'^[A-Za-z]{4}$')
BUILT_IN_LANGUAGES = ('ja', 'en', 'zh-hans', 'zh-hant', 'pt', 'pt-br', 'ko', 'ru')
REFERENCE_PREFIXES = ('cwy.demo.', 'cwy.sample.')
RESTART_REASON = 'It was created after the game started: restart the game once to load it.'
RESTART_NOTE = 'Restart the game once to load this pack; until then it cannot be edited or played.'
# Contract v2.3 (Phase C): pack info, icon and export.
SEMVER = re.compile(r'^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$')
ICON_BYTES, BROWSER_ZIP_BYTES, ZIP_BYTES = 512 * 1024, 256 * 1024**2, 2 * 1024**3
STORED_SUFFIXES = ('.wav', '.ogg', '.png', '.jpg', '.jpeg')
EXPORT_NOTE = 'The loaded pack shows the new version after the next game start.'
# Contract v2.4 (Phase B1, feature `voiceDefault`): voice origin + consent once per pack.
ORIGINS = ('own', 'ai-assisted', 'ai-generated')
NO_VOICE_DEFAULT = "Set the pack's voice origin and consent first, or send origin and consent with the take."


class Created(dict):
    """A response the HTTP layer sends as 201."""


class Binary(bytes):
    """A raw 200 response with its media type and extra headers (v2.3 icon PNG and export zip)."""

    def __new__(cls, raw, content_type, headers=None):
        self = super().__new__(cls, raw)
        self.content_type, self.headers = content_type, headers or {}
        return self


def export_file_name(pack, version, author):
    """Mirrors MakerPackExporter.ExportFileName: <pack>-v<version>.zip, plus -by-<author slug> when the pack id lacks it."""
    plain = f'{pack}-v{version}.zip'
    slug = ''.join(c for c in (author or '').lower() if c in 'abcdefghijklmnopqrstuvwxyz0123456789')
    if not slug or slug in pack.lower():
        return plain
    room = 64 - len(f'v{version}-by-')
    if room < 1:
        return plain
    return f'{pack}-v{version}-by-{slug[:room]}.zip'


def digest(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True,
                                    separators=(',', ':')).encode()).hexdigest()


def stamp():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


class StudioV2:
    def __init__(self, state, error):
        self.state, self.error = state, error
        self.pending = {}
        self.activity = dict(focused=True, paused=False, busy=False, reason=None,
                             observedAt=stamp(), stale=False, focusOnPlay=True)

    def fail(self, code, message, status=400, details=None):
        raise self.error(status, code, message, details=details)

    def path(self, pack):
        self.state.get_pack_meta(pack)
        root = self.state.data_dir
        for part in (pack, '.maker', 'fake-v2.json'):
            root = root / part
            if root.is_symlink():
                self.fail('metadata_unavailable', 'Creator metadata contains a link.', 503)
        return root

    def load(self, pack):
        path = self.path(pack)
        if not path.exists():
            return dict(schema=1, lines={}, glossaries={}, operations={})
        try:
            data = json.loads(path.read_text(encoding='utf-8'))
            if data['schema'] != 1 or any(not isinstance(data[k], dict) for k in
                                          ('lines', 'glossaries', 'operations')):
                raise ValueError('schema')
            return data
        except (ValueError, KeyError, TypeError):
            self.fail('metadata_unavailable', 'Unreadable fake creator metadata for pack=' + pack, 503)

    def save(self, pack, data):
        if len(json.dumps(data, ensure_ascii=False).encode()) > 2 * 1024**3:
            self.fail('storage_limit', 'Project quota exceeded.', 413)
        self.state._write_json_atomic(self.path(pack), data)

    def line_state(self, data, lang, line_id):
        return copy.deepcopy(data['lines'].get(lang + '/' + line_id, dict(
            revision='initial-' + digest([lang, line_id]), workflow=dict(status='todo', starred=False),
            history=[], historyGap=False)))

    def line(self, pack, lang, line_id, data):
        base = self.state.get_line(line_id, lang)
        meta = self.line_state(data, lang, line_id)
        table = self.state._read_json(self.state.subtitle_file(pack, lang))
        owned = table.get(line_id)
        voice = base['voice']
        owned_dub = bool(voice and self.state.voice_file(pack, lang, voice).is_file())
        clip = self.clip_state(pack, lang, voice, data) if voice else None
        record = clip and clip['record']
        return dict(base, ownedSubtitle=owned, ownedDub=owned_dub, workflow=meta['workflow'],
                    takeCount=len(meta.get('takes', [])), revision=meta['revision'],
                    clipRevision=clip['revision'] if clip else 'initial-' + digest([pack, lang, voice]),
                    publishedTakeId=record['takeId'] if record else None,
                    publishedTakeRevision=record['takeRevision'] if record else None,
                    context=dict(occurrences=[], selectedOccurrenceId=None, mood=None, pose=None, source=None))

    def all_lines(self, pack, lang, data):
        return [self.line(pack, lang, x['id'], data) for x in self.state.lines_list]

    def progress(self, lines):
        return dict(totalLines=len(lines), dubbableLines=sum(x['voice'] is not None for x in lines),
                    subtitlePresent=sum(x['ownedSubtitle'] is not None for x in lines),
                    dubPresent=sum(bool(x['voice']) and x['ownedDub'] for x in lines),
                    todo=sum(x['workflow']['status'] == 'todo' for x in lines),
                    draft=sum(x['workflow']['status'] == 'draft' for x in lines),
                    done=sum(x['workflow']['status'] == 'done' for x in lines),
                    needsRetake=sum(x['workflow']['status'] == 'needs-retake' for x in lines),
                    starred=sum(x['workflow']['starred'] for x in lines))

    def page(self, query, maximum=500):
        # Same wording as the real loader's router (MakerRouter.TryPage).
        offset, limit = query.get('offset', '0'), query.get('limit', str(min(100, maximum)))
        if not DIGITS.fullmatch(offset):
            self.fail('bad_request', 'offset must be a whole number of 0 or more')
        if not DIGITS.fullmatch(limit) or not 1 <= int(limit) <= maximum:
            self.fail('bad_request', 'limit must be 1-' + str(maximum))
        return int(offset), int(limit)

    def fields(self, obj, allowed, required=()):
        if not isinstance(obj, dict) or set(obj) - set(allowed) or set(required) - set(obj):
            self.fail('bad_request', 'Missing or unknown fields.')

    def handle(self, h, path, query):
        with self.state.lock:
            return self._handle(h, path, query)

    def _handle(self, h, path, query):
        parts = path.removeprefix('/v1/maker/studio/').split('/')
        method = h.command
        if parts == ['client-log']:
            # Diagnostics only (2026-11-09): the real loader writes these to the game log while `maker trace on`; the fake
            # validates the shape and drops them.
            if method != 'POST':
                self.fail('not_found', 'No such route.', 404)
            self.fields(query, ())
            body = h.read_json_body()
            self.fields(body, ('entries',), ('entries',))
            entries = body['entries']
            if not isinstance(entries, list):
                self.fail('bad_request', 'entries must be an array.')
            if len(entries) > 100:
                self.fail('too_large', 'At most 100 entries per post.', 413)
            return dict(accepted=0, dropped=len(entries), trace=False)
        if parts in (['capabilities'], ['status'], ['packs'], ['play']):
            if parts == ['packs'] and method == 'POST':
                return self.create_pack(h, query)
            if parts == ['play']:
                if method != 'POST':
                    self.fail('not_found', 'No such route.', 404)
                return self.play(h, query)
            if method != 'GET':
                self.fail('not_found', 'No such route.', 404)
            self.fields(query, ())
            if parts == ['capabilities']:
                return dict(contract=2, features=['catalog', 'workflow', 'subtitles', 'dubs', 'projects', 'playback', 'newPacks', 'packInfo', 'export', 'voiceDefault'], uiText=False,
                            limits=dict(jsonBytes=65536, wavBytes=16777216, wavSeconds=60,
                                        subtitleRevisions=20, takesPerLine=3, projectBytes=2147483648),
                            storageSchema=1, consentVersion='consent-1')
            if parts == ['status']:
                return dict(gameVersion='1.18.0', loaderVersion='0.1.0-fake', gameLanguage=self.state.game_language,
                            gameLanguageIsPack=self.state.game_language_is_pack, roomReady=True, **self.activity)
            return dict(packs=[dict(p, loaded=True, restartRequired=False, studioEditable=p['editable'],
                                   studioReadOnlyReason=p['readOnlyReason'], metadataState='ready')
                               for p in self.state.list_packs()] + [self.pending_entry(p) for p in self.pending.values()])

        if len(parts) >= 3 and parts[0] == 'packs' and PACK.fullmatch(parts[1]) and parts[2] in ('info', 'icon', 'export', 'exports'):
            return self.pack_files(h, parts[1], parts[2:], query)
        if len(parts) == 3 and parts[0] == 'packs' and PACK.fullmatch(parts[1]) and parts[2] == 'voice':
            return self.voice_default(h, parts[1], query)
        if len(parts) < 4 or parts[0] != 'packs' or not PACK.fullmatch(parts[1]):
            self.fail('not_found', 'No Studio route.', 404)
        pack = parts[1]
        if pack.lower() in self.pending:
            self.fail('restart_required', RESTART_REASON, 409)
        data = self.load(pack)
        if parts[2] == 'operations' and len(parts) == 5 and parts[4] in ('recovery', 'resolve'):
            self.fields(query, ())
            blocked = parts[3]
            if not HEX.fullmatch(blocked):
                self.fail('bad_request', 'Invalid blockedOperationId.')
            return self.recovery(h, pack, blocked, parts[4], data)
        if parts[2] == 'operations' and len(parts) == 4 and method == 'GET':
            self.fields(query, ())
            op = parts[3]
            if not HEX.fullmatch(op):
                self.fail('bad_request', 'Invalid operationId.')
            receipt = data['operations'].get(op)
            if not receipt or time.time() - receipt['time'] > 86400:
                self.fail('not_found', 'No retained operation.', 404)
            return dict(state='committed', revision=receipt['response']['revision'],
                        saved=receipt.get('saved', True), recoveryRequired=False)

        glossary = parts[2] == 'glossary' and len(parts) == 4
        if glossary:
            lang, line_id, action = parts[3], None, 'glossary'
        elif len(parts) >= 5 and parts[2] == 'languages' and parts[4] in ('lines', 'groups'):
            lang = parts[3]
            line_id = parts[5] if len(parts) >= 6 else None
            action = parts[6] if len(parts) >= 7 else parts[4]
            if line_id is not None and (not SEG.fullmatch(line_id) or line_id in ('.', '..')):
                self.fail('bad_request', 'Invalid line id.')
        else:
            self.fail('not_found', 'No Studio route.', 404)
        if not LANG.fullmatch(lang):
            self.fail('bad_request', 'Invalid language.')
        if line_id is not None:
            self.state.get_line(line_id, lang)
        if line_id is not None and action in ('takes', 'favourite', 'publish', 'unpublish'):
            return self.dubs(h, pack, lang, line_id, parts, query, data)

        if method == 'GET':
            if glossary:
                self.fields(query, ())
                return copy.deepcopy(data['glossaries'].get(lang, dict(revision='initial-' + digest(lang), entries=[])))
            if action == 'history' and len(parts) == 7:
                self.fields(query, ('offset', 'limit'))
                offset, limit = self.page(query, 20)
                meta = self.line_state(data, lang, line_id)
                return dict(revision=meta['revision'], total=len(meta['history']), offset=offset,
                            items=meta['history'][offset:offset + limit], historyGap=meta['historyGap'])
            if action == 'lines' and len(parts) == 6:
                self.fields(query, ('occurrenceId',))
                if query.get('occurrenceId'):
                    self.fail('not_found', 'No verified occurrence for this fake line.', 404)
                return self.line(pack, lang, line_id, data)
            if len(parts) != 5:
                self.fail('not_found', 'No Studio route.', 404)
            lines = self.all_lines(pack, lang, data)
            revision = digest(lines)
            if action == 'groups':
                self.fields(query, ())
                groups = list(dict.fromkeys(x['group'] or 'ungrouped' for x in lines))
                return dict(catalogRevision=revision, overall=self.progress(lines), groups=[
                    dict(id=g, labelKey=LABELS.get(g), title=g, order=i,
                         progress=self.progress([x for x in lines if (x['group'] or 'ungrouped') == g]))
                    for i, g in enumerate(groups)])
            self.fields(query, ('q', 'group', 'filter', 'offset', 'limit', 'catalogRevision'))
            if query.get('catalogRevision') and query['catalogRevision'] != revision:
                self.fail('conflict', 'Catalogue changed; restart paging.', 409)
            mode = query.get('filter', 'all')
            if mode not in (*STATES, 'all', 'starred', 'missing-subtitle', 'missing-dub'):
                self.fail('bad_request', 'Invalid workflow filter.')
            search = query.get('q', '').casefold()
            if len(search) > 200:
                self.fail('bad_request', 'Search exceeds 200 characters.')
            def matches(x):
                state = x['workflow']
                flag = (mode == 'all' or (mode in STATES and state['status'] == mode)
                        or (mode == 'starred' and state['starred'])
                        or (mode == 'missing-subtitle' and x['ownedSubtitle'] is None)
                        or (mode == 'missing-dub' and x['voice'] and not x['ownedDub']))
                return flag and (not query.get('group') or (x['group'] or 'ungrouped') == query['group']) and (
                    not search or any(search in (x.get(k) or '').casefold()
                                      for k in ('id', 'voice', 'ja', 'en', 'ownedSubtitle')))
            selected = [x for x in lines if matches(x)]
            offset, limit = self.page(query)
            return dict(total=len(selected), offset=offset, catalogRevision=revision,
                        items=selected[offset:offset + limit])

        self.fields(query, ())
        revert = action == 'history' and len(parts) == 9 and parts[8] == 'revert'
        expected_method = 'POST' if revert else 'PATCH' if action == 'workflow' else 'PUT'
        if method != expected_method or action not in ('glossary', 'workflow', 'subtitle', 'history'):
            self.fail('not_found', 'No Studio mutation.', 404)
        if not glossary and not revert and len(parts) != 7:
            self.fail('not_found', 'No Studio mutation.', 404)
        if action == 'history' and not revert:  # history is read-only; only .../history/{revisionId}/revert mutates
            self.fail('not_found', 'No Studio mutation.', 404)
        body = h.read_json_body()
        extra = ('status', 'starred') if action == 'workflow' else ('entries',) if glossary else () if revert else ('text',)
        required = ('expectedRevision', 'operationId') + (() if action == 'workflow' or revert else extra)
        self.fields(body, ('expectedRevision', 'operationId', *extra), required)
        op, expected = body['operationId'], body['expectedRevision']
        if not isinstance(op, str) or not HEX.fullmatch(op) or not isinstance(expected, str) or not 1 <= len(expected) <= 256:
            self.fail('bad_request', 'Invalid operationId or expectedRevision.')
        if action == 'workflow':
            if not any(k in body for k in extra) or ('status' in body and body['status'] not in STATES) or (
                    'starred' in body and type(body['starred']) is not bool):
                self.fail('bad_request', 'Invalid workflow patch.')
        if glossary:
            self.glossary_entries(body['entries'])
        if revert and not HEX.fullmatch(parts[7]):
            self.fail('bad_request', 'Invalid revisionId.')
        if not revert and action != 'workflow' and not glossary:
            self.subtitle_text(body['text'])
        # Like the real loader: the body is validated (400) before the pack's editability is checked (403).
        if not self.state.get_pack_meta(pack)['editable']:
            self.fail('read_only_pack', 'Reference pack is read-only.', 403)
        fingerprint = digest([path, method, body])
        data['operations'] = {k: v for k, v in data['operations'].items() if time.time() - v['time'] <= 86400}
        old = data['operations'].get(op)
        if old:
            if old['fingerprint'] != fingerprint:
                self.fail('conflict', 'operationId already used.', 409)
            return copy.deepcopy(old['response'])
        current = (data['glossaries'].get(lang, dict(revision='initial-' + digest(lang), entries=[]))
                   if glossary else self.line_state(data, lang, line_id))
        blocked = data.get('blocked')
        if blocked:
            self.fail('recovery_required', 'pack=' + pack + '; resource=' + blocked['resource']
                      + '; operationId=' + blocked['operationId'] + '; external edit blocks recovery', 503,
                      dict(packId=pack, resource=blocked['resource'], blockedOperationId=blocked['operationId']))
        if current['revision'] != expected:
            self.fail('conflict', 'Resource changed; refetch it.', 409)
        changed, event = False, None
        if glossary:
            entries = body['entries']
            for e in entries:
                for key in ('sourceJa', 'sourceEn', 'note'):
                    e.setdefault(key, None)
            changed = current['entries'] != entries
            if changed: current = dict(revision=secrets.token_hex(16), entries=entries)
            response = copy.deepcopy(current)
            data['glossaries'][lang] = current
            event = 'maker.glossary.changed'
        elif action == 'workflow':
            workflow = dict(current['workflow'], **{k: body[k] for k in extra if k in body})
            changed = workflow != current['workflow']
            if changed: current.update(revision=secrets.token_hex(16), workflow=workflow)
            response = dict(revision=current['revision'], workflow=copy.deepcopy(workflow))
            event = 'maker.workflow.changed'
        else:
            text = body.get('text')
            if revert:
                item = next((v for v in current['history'] if v['revisionId'] == parts[7]), None)
                if not item: self.fail('not_found', 'History revision not retained.', 404)
                text = item['text']
                self.subtitle_text(text)
            previous = self.line(pack, lang, line_id, data)['ownedSubtitle']
            changed = previous != text
            if changed:
                def entry(value, source):
                    return dict(revisionId=secrets.token_hex(16), createdAt=stamp(), text=value, source=source)
                if not current['history']: current['history'].append(entry(previous, 'baseline'))
                current['history'].append(entry(text, 'revert' if revert else 'studio'))
                current['history'] = current['history'][-20:]
                current['revision'] = secrets.token_hex(16)
                self.state.save_subtitle(pack, lang, line_id, text)
            response = dict(revision=current['revision'], operationId=op, saved=changed,
                            live=False, path='data/subtitles/' + lang + '.json', durationSec=None,
                            note='Fake pack saved; no game is connected.', publishedTakeId=None,
                            effectivePackId=self.state.resolve_line(self.state.lines_by_id[line_id], lang)['subtitlePack'],
                            restartRequired=False)
            event = 'maker.history.changed'
        if not glossary: data['lines'][lang + '/' + line_id] = current
        data['operations'][op] = dict(fingerprint=fingerprint, response=copy.deepcopy(response),
                                      time=time.time(), saved=changed)
        while len(data['operations']) > 100:
            del data['operations'][next(iter(data['operations']))]
        self.save(pack, data)
        if changed:
            fields = dict(packId=pack, lang=lang, revision=current['revision'])
            if line_id: fields['id'] = line_id
            if action == 'workflow': fields.update(current['workflow'])
            if event == 'maker.history.changed': fields['revisionId'] = current['history'][-1]['revisionId']
            self.state.emit(event, fields)
        return response

    # ---- M5d dubs slice: takes, creator audio, favourite, publish and unpublish (CONTRACT-v2 §4-§5).

    def maker_file(self, pack, *parts):
        path = self.state.data_dir / pack
        for part in ('.maker',) + parts:
            path = path / part
            if path.is_symlink():
                self.fail('metadata_unavailable', 'Creator metadata contains a link.', 503)
        return path

    @staticmethod
    def sha(raw):
        return hashlib.sha256(raw).hexdigest()

    def clip_state(self, pack, lang, clip, data):
        """Per-(lang, clip) publication: intact while the runtime WAV is the published one, else derived from the file."""
        record = data.setdefault('clips', {}).get(lang + '/' + clip)
        voice = self.state.voice_file(pack, lang, clip)
        current = self.sha(voice.read_bytes()) if voice.is_file() else None
        intact = record is not None and current == record['sha256']
        revision = record['revision'] if intact else digest([pack, lang, clip, current, record and record['revision']])[:32]
        return dict(record=record if intact else None, stale=record is not None and not intact, current=current, revision=revision)

    def public_take(self, take):
        # v2.4: consentSource is always present; a take stored before v2.4 (no member) is a 'take' consent.
        return dict({k: v for k, v in take.items() if not k.startswith('_')}, consentSource=take.get('consentSource', 'take'))

    def take_page(self, pack, lang, line_id, meta, data):
        voice = self.state.get_line(line_id, lang)['voice']
        return dict(revision=meta['revision'], favouriteTakeId=meta.get('favouriteTakeId'),
                    publishedTakeId=meta.get('publishedTakeId'),
                    clipRevision=self.clip_state(pack, lang, voice, data)['revision'] if voice else None,
                    items=[self.public_take(t) for t in meta.get('takes', [])])

    def inspect(self, raw, kind):
        from fake_loader import inspect_wav_bytes
        duration = inspect_wav_bytes(raw)
        if kind == 'render':
            with wave.open(io.BytesIO(raw), 'rb') as wav:
                if wav.getnchannels() != 1 or wav.getframerate() != 48000:
                    self.fail('unsupported_media_type', 'A render must be PCM16 48 kHz mono.', 415)
        return round(duration, 3)

    def consent(self, value, origin):
        if origin not in ORIGINS:
            self.fail('bad_request', 'origin must be own, ai-assisted or ai-generated.')
        self.fields(value, ('confirmed', 'statementVersion'), ('confirmed', 'statementVersion'))
        if type(value['confirmed']) is not bool or value['statementVersion'] != 'consent-1':
            self.fail('bad_request', 'consent needs confirmed (bool) and statementVersion consent-1.')

    def dubs(self, h, pack, lang, line_id, parts, query, data):
        method, action = h.command, parts[6]
        take_id = parts[7] if len(parts) >= 8 else None
        audio = len(parts) == 9 and parts[8] == 'audio'
        if take_id is not None and not HEX.fullmatch(take_id) or len(parts) > 9 or (len(parts) == 9 and not audio) \
                or (action != 'takes' and len(parts) != 7):
            self.fail('not_found', 'No Studio dubs route.', 404)
        line_path = '/'.join(parts)
        if method == 'GET' and action == 'takes':
            meta = self.line_state(data, lang, line_id)
            if take_id is None:
                self.fields(query, ())
                return self.take_page(pack, lang, line_id, meta, data)
            if not audio:
                self.fail('not_found', 'No Studio dubs route.', 404)
            self.fields(query, ('kind',), ('kind',))
            if query['kind'] not in ('source', 'render'):
                self.fail('bad_request', 'kind must be source or render.')
            take = next((t for t in meta.get('takes', []) if t['id'] == take_id), None)
            if take is None:
                self.fail('not_found', 'No such take.', 404)
            path = self.maker_file(pack, 'takes', lang, line_id, take_id, query['kind'] + '.wav')
            if not (take['sourcePresent'] if query['kind'] == 'source' else take['renderReady']) or not path.is_file():
                self.fail('not_found', 'The take has no ' + query['kind'] + ' audio.', 404)
            return path.read_bytes()

        # Mutations: validate the request completely, then refuse a read-only pack (as the real loader does).
        wav = None
        if audio and method == 'PUT':
            self.fields(query, ('kind', 'expectedRevision', 'operationId'), ('kind', 'expectedRevision', 'operationId'))
            if query['kind'] not in ('source', 'render'):
                self.fail('bad_request', 'kind must be source or render.')
            body = dict(expectedRevision=query['expectedRevision'], operationId=query['operationId'])
            wav = h.read_wav_body()
            duration = self.inspect(wav, query['kind'])
        elif method == 'DELETE' and take_id is not None and not audio:
            self.fields(query, ('expectedRevision', 'operationId'), ('expectedRevision', 'operationId'))
            body = dict(query)
        else:
            self.fields(query, ())
            allowed = {('POST', 'takes', False): ('origin', 'consent'), ('PATCH', 'takes', True): ('origin', 'consent', 'trimStartSec', 'trimEndSec', 'evenOutVolume', 'followPackDefault'),
                       ('PUT', 'favourite', False): ('takeId',), ('POST', 'publish', False): ('expectedClipRevision', 'takeId'),
                       ('POST', 'unpublish', False): ('expectedClipRevision',)}.get((method, action, take_id is not None))
            if allowed is None or audio:
                self.fail('not_found', 'No Studio dubs mutation.', 404)
            body = h.read_json_body()
            # v2.4: a new take may omit origin and consent together (it inherits the pack voice default).
            required = {'takes': (), 'favourite': ('takeId',),
                        'publish': ('expectedClipRevision', 'takeId'), 'unpublish': ('expectedClipRevision',)}[action]
            self.fields(body, ('expectedRevision', 'operationId') + allowed, ('expectedRevision', 'operationId') + required)
            if take_id is None and action == 'takes' and ('origin' in body) != ('consent' in body):
                self.fail('bad_request', 'Send origin and consent together, or neither to use the pack voice default.')
            if 'origin' in body or 'consent' in body:
                if take_id is None or 'consent' in body:
                    self.consent(body.get('consent'), body.get('origin', 'own'))
                elif body['origin'] not in ('own', 'ai-assisted', 'ai-generated'):
                    self.fail('bad_request', 'origin must be own, ai-assisted or ai-generated.')
            for key in ('trimStartSec', 'trimEndSec'):
                if body.get(key) is not None and (type(body[key]) not in (int, float) or not 0 <= body[key] <= 60):
                    self.fail('bad_request', key + ' must be 0-60 seconds or null.')
            if 'evenOutVolume' in body and type(body['evenOutVolume']) is not bool:
                self.fail('bad_request', 'evenOutVolume must be true or false.')
            if 'followPackDefault' in body:  # v2.5.1: re-link a take to the pack voice default
                if body['followPackDefault'] is not True:
                    self.fail('bad_request', 'followPackDefault must be true.')
                if 'origin' in body or 'consent' in body:
                    self.fail('bad_request', 'Send followPackDefault without origin and consent.')
            if take_id is not None and len(body) == 2:
                self.fail('bad_request', 'At least one take field is required.')
            if 'takeId' in body and body['takeId'] is not None and (not isinstance(body['takeId'], str) or not HEX.fullmatch(body['takeId'])):
                self.fail('bad_request', 'takeId must be 32 lowercase hex or null.')
            if action == 'publish' and body['takeId'] is None:
                self.fail('bad_request', 'takeId is required.')
            if 'expectedClipRevision' in body and (not isinstance(body['expectedClipRevision'], str) or not body['expectedClipRevision']):
                self.fail('bad_request', 'expectedClipRevision is required.')
        op, expected = body['operationId'], body['expectedRevision']
        if not isinstance(op, str) or not HEX.fullmatch(op) or not isinstance(expected, str) or not 1 <= len(expected) <= 256:
            self.fail('bad_request', 'Invalid operationId or expectedRevision.')
        if not self.state.get_pack_meta(pack)['editable']:
            self.fail('read_only_pack', 'Reference pack is read-only.', 403)

        fingerprint = digest([line_path, method, body, self.sha(wav) if wav else None])
        data['operations'] = {k: v for k, v in data['operations'].items() if time.time() - v['time'] <= 86400}
        old = data['operations'].get(op)
        if old:
            if old['fingerprint'] != fingerprint:
                self.fail('conflict', 'operationId already used.', 409)
            response = copy.deepcopy(old['response'])
            return (201, response) if method == 'POST' and action == 'takes' else response
        blocked = data.get('blocked')
        if blocked:
            self.fail('recovery_required', 'pack=' + pack + '; resource=' + blocked['resource'] + '; operationId='
                      + blocked['operationId'] + '; external edit blocks recovery', 503,
                      dict(packId=pack, resource=blocked['resource'], blockedOperationId=blocked['operationId']))
        meta = self.line_state(data, lang, line_id)
        takes = meta.setdefault('takes', [])
        if meta['revision'] != expected:
            self.fail('conflict', 'Line changed; refetch it.', 409)
        voice = self.state.get_line(line_id, lang)['voice']
        if not voice and (action in ('publish', 'unpublish') or (action == 'takes' and method in ('POST', 'PUT'))):
            self.fail('bad_request', 'This line is text-only and has no voice clip.')
        take = next((t for t in takes if t['id'] == take_id), None) if take_id else None
        if take_id and action == 'takes' and take is None:
            self.fail('not_found', 'No such take.', 404)
        now, status, change, published = stamp(), 200, None, None
        if action == 'takes' and method == 'POST':
            if len(takes) >= 3:
                self.fail('take_limit', 'A line may have at most 3 takes; delete one first.', 409)
            if 'origin' in body:
                origin, source = body['origin'], 'take'
                consent = dict(confirmed=body['consent']['confirmed'], statementVersion='consent-1',
                               confirmedAt=now if body['consent']['confirmed'] else None)
            else:  # v2.4/v2.5: inherits the pack default (consentSource pack); follows every later set (follow_voice_default), a clear leaves it at the last default
                default = self.voice_record(pack)
                if default['origin'] is None:
                    self.fail('consent_required', NO_VOICE_DEFAULT, 403)
                origin, source, consent = default['origin'], 'pack', copy.deepcopy(default['consent'])
            take = dict(id=secrets.token_hex(16), origin=origin, consent=consent, consentSource=source,
                        sourceRevision=None, sourceDurationSec=None, renderDurationSec=None, trimStartSec=None,
                        trimEndSec=None, evenOutVolume=False, sourcePresent=False, renderReady=False,
                        createdAt=now, updatedAt=now)
            takes.append(take)
            status, change = 201, 'created'
        elif audio:
            kind = query['kind']
            if kind == 'render' and not take['sourcePresent']:
                self.fail('not_found', 'The take has no source audio; upload the source first.', 404)
            folder = self.maker_file(pack, 'takes', lang, line_id, take_id)
            folder.mkdir(parents=True, exist_ok=True)
            (folder / (kind + '.wav')).write_bytes(wav)
            if kind == 'source':
                (folder / 'render.wav').unlink(missing_ok=True)
                take.update(sourcePresent=True, sourceDurationSec=duration, sourceRevision=secrets.token_hex(16),
                            renderReady=False, renderDurationSec=None, _source=self.sha(wav), _renderSource=None)
            else:
                take.update(renderReady=True, renderDurationSec=duration, _renderSource=take.get('_source'))
            change = kind
        elif action == 'takes' and method == 'PATCH':
            if 'origin' in body and body['origin'] != take['origin'] and 'consent' not in body:
                take['consent'] = dict(confirmed=False, statementVersion='consent-1', confirmedAt=None)
            if 'consent' in body:
                take['consent'] = dict(confirmed=body['consent']['confirmed'], statementVersion='consent-1',
                                       confirmedAt=now if body['consent']['confirmed'] else None)
            take['origin'] = body.get('origin', take['origin'])
            if 'origin' in body or 'consent' in body:  # v2.4: a per-take override of an inherited default
                take['consentSource'] = 'take'
            if body.get('followPackDefault') is True:  # v2.5.1: back on the pack default; a published disclosure follows
                default = self.voice_record(pack)
                if default['origin'] is None:
                    self.fail('consent_required', "Set the pack's voice origin and consent before a take can follow it.", 403)
                take.update(consentSource='pack', origin=default['origin'], consent=copy.deepcopy(default['consent']))
                follows = dict(origin=default['origin'], statementVersion='consent-1', confirmedAt=default['consent']['confirmedAt'])
                for key, record in data.get('clips', {}).items():
                    if key.split('/', 1)[0] == lang and record['lineId'] == line_id and record['takeId'] == take_id:
                        path = self.state.data_dir / pack / 'voice-provenance.json'
                        provenance = self.state._read_json(path)
                        for entry in provenance.get('clips', []):
                            if isinstance(entry, dict) and (entry.get('lang'), entry.get('clip')) == tuple(key.split('/', 1)):
                                entry.update(follows)
                        self.state._write_json_atomic(path, provenance)
            if any(k in body and body[k] != take[k] for k in ('trimStartSec', 'trimEndSec', 'evenOutVolume')):
                take.update(renderReady=False, renderDurationSec=None, _renderSource=None)
                self.maker_file(pack, 'takes', lang, line_id, take_id, 'render.wav').unlink(missing_ok=True)
            take.update({k: body[k] for k in ('trimStartSec', 'trimEndSec', 'evenOutVolume') if k in body})
            change = 'updated'
        elif action == 'takes' and method == 'DELETE':
            if meta.get('publishedTakeId') == take_id:
                self.fail('take_in_use', 'Unpublish or replace this take before deleting it.', 409)
            takes.remove(take)
            if meta.get('favouriteTakeId') == take_id:
                meta['favouriteTakeId'] = None
            folder = self.maker_file(pack, 'takes', lang, line_id, take_id)
            for name in ('source.wav', 'render.wav'):
                (folder / name).unlink(missing_ok=True)
            change = 'deleted'
        elif action == 'favourite':
            if body['takeId'] is not None and not any(t['id'] == body['takeId'] for t in takes):
                self.fail('not_found', 'No such take.', 404)
            meta['favouriteTakeId'] = body['takeId']
            take_id, change = body['takeId'], 'favourite'
        else:
            clip = self.clip_state(pack, lang, voice, data)
            if body['expectedClipRevision'] != clip['revision']:
                self.fail('conflict', 'The clip changed (another line sharing it, or an edit outside Studio).', 409)
            published = self.publish(pack, lang, line_id, voice, body.get('takeId'), takes, meta, clip, data, now)
        if take is not None:
            take['updatedAt'] = now
        meta['revision'] = secrets.token_hex(16)
        data['lines'][lang + '/' + line_id] = meta
        if action == 'favourite':
            response = dict(revision=meta['revision'], favouriteTakeId=meta.get('favouriteTakeId'))
        elif action == 'takes' and method == 'DELETE':
            response = dict(revision=meta['revision'], removed=True)
        elif action == 'takes':
            response = dict(revision=meta['revision'], take=self.public_take(take))
        else:
            response = dict(revision=meta['revision'], operationId=op, saved=True, live=False, path=published['path'],
                            durationSec=published['durationSec'], note='Fake pack saved; no game is connected.',
                            publishedTakeId=published['takeId'], effectivePackId=self.state.get_line(line_id, lang)['dubPack'],
                            restartRequired=False)
        data['operations'][op] = dict(fingerprint=fingerprint, response=copy.deepcopy(response), time=time.time(), saved=True)
        while len(data['operations']) > 100:
            del data['operations'][next(iter(data['operations']))]
        self.save(pack, data)
        if change:
            self.state.emit('maker.take.changed', dict(packId=pack, lang=lang, id=line_id, takeId=take_id,
                                                       revision=meta['revision'], change=change))
        else:
            self.state.emit('maker.take.published', dict(packId=pack, lang=lang, id=line_id, clip=voice,
                                                         takeId=published['takeId'], revision=meta['revision'], live=False))
        return (status, response) if status != 200 else response

    def publish(self, pack, lang, line_id, clip, take_id, takes, meta, state, data, now):
        """Publish take_id (or unpublish when None): runtime WAV, backup of a non-Studio file, provenance, clip record."""
        voice = self.state.voice_file(pack, lang, clip)
        backup = self.state.data_dir / pack / '_maker_backup' / 'audio' / 'voice' / lang / (clip + '.wav')
        provenance_path = self.state.data_dir / pack / 'voice-provenance.json'
        provenance = self.state._read_json(provenance_path) or dict(schema=1, clips=[])
        provenance['clips'] = [c for c in provenance.get('clips', []) if (c['lang'], c['clip']) != (lang, clip)]
        record = state['record']
        if take_id is not None:
            take = next((t for t in takes if t['id'] == take_id), None)
            if take is None:
                self.fail('not_found', 'No such take.', 404)
            if not take['consent']['confirmed']:
                self.fail('consent_required', 'Confirm the consent-1 statement for this take before publishing it.', 403)
            render = self.maker_file(pack, 'takes', lang, line_id, take_id, 'render.wav')
            if not take['renderReady'] or take.get('_renderSource') != take.get('_source') or not render.is_file():
                self.fail('render_required', 'Prepare a render of the current source before publishing.', 409)
            raw = render.read_bytes()
            backup_of = record['backup'] if record else None
            if voice.is_file() and record is None:  # a dub made outside Studio: moved aside, never deleted
                backup.parent.mkdir(parents=True, exist_ok=True)
                voice.replace(backup)
                backup_of = str(backup.relative_to(self.state.data_dir / pack))
            voice.parent.mkdir(parents=True, exist_ok=True)
            voice.write_bytes(raw)
            provenance['clips'].append(dict(lang=lang, clip=clip, origin=take['origin'], statementVersion='consent-1',
                                            confirmedAt=take['consent']['confirmedAt'], sha256=self.sha(raw), publishedAt=now))
            data['clips'][lang + '/' + clip] = dict(revision=secrets.token_hex(16), lineId=line_id, takeId=take_id,
                                                    takeRevision=digest([take_id, self.sha(raw)])[:32],
                                                    sha256=self.sha(raw), backup=backup_of)
            meta.update(publishedTakeId=take_id, publishedTakeRevision=data['clips'][lang + '/' + clip]['takeRevision'])
            result = dict(path='audio/voice/' + lang + '/' + clip + '.wav', takeId=take_id, durationSec=take['renderDurationSec'])
        else:
            if record:
                voice.unlink(missing_ok=True)
                if record['backup'] and (self.state.data_dir / pack / record['backup']).is_file():
                    (self.state.data_dir / pack / record['backup']).replace(voice)
            elif voice.is_file():  # W3 parity gap b: a dub made outside Studio is moved aside
                backup.parent.mkdir(parents=True, exist_ok=True)
                voice.replace(backup)
            data['clips'].pop(lang + '/' + clip, None)
            meta.update(publishedTakeId=None, publishedTakeRevision=None)
            result = dict(path='audio/voice/' + lang + '/' + clip + '.wav', takeId=None, durationSec=None)
        for other in data['lines'].values():  # an alias line loses its pin when another line publishes the clip
            if other is not meta and other.get('publishedTakeId') and not any(
                    c.get('takeId') == other['publishedTakeId'] for c in data['clips'].values()):
                other.update(publishedTakeId=None, publishedTakeRevision=None)
        self.state._write_json_atomic(provenance_path, provenance)
        return result

    def glossary_entries(self, entries):
        if not isinstance(entries, list) or len(entries) > 200:
            self.fail('bad_request', 'Glossary must contain at most 200 entries.')
        ids = set()
        for e in entries:
            self.fields(e, ('id', 'sourceJa', 'sourceEn', 'target', 'note'), ('id', 'target'))
            if not isinstance(e['id'], str) or not re.fullmatch(r'[A-Za-z0-9_.-]{1,64}', e['id']) or e['id'] in ids:
                self.fail('bad_request', 'Invalid or repeated glossary id.')
            ids.add(e['id'])
            if not isinstance(e['target'], str) or not e['target'].strip() or any(
                    v is not None and (not isinstance(v, str) or len(v) > 2000) for v in e.values()):
                self.fail('bad_request', 'Invalid glossary text.')

    def subtitle_text(self, text):
        if text is not None and (not isinstance(text, str) or not text.strip() or not 1 <= len(text) <= 2000):
            self.fail('bad_request', 'Subtitle must have 1-2000 characters, or null.')

    def recovery(self, h, pack, blocked, action, data):
        if h.command != ('GET' if action == 'recovery' else 'POST'):
            self.fail('not_found', 'No recovery route.', 404)
        body, fingerprint, op = None, None, None
        if action == 'resolve':
            body = h.read_json_body()
            self.fields(body, ('operationId', 'expectedRecoveryRevision', 'strategy'),
                        ('operationId', 'expectedRecoveryRevision', 'strategy'))
            op = body['operationId']
            if not isinstance(op, str) or not HEX.fullmatch(op) or op == blocked or body['strategy'] != 'keep-current':
                self.fail('bad_request', 'Expected a new operationId and keep-current.')
            if not isinstance(body['expectedRecoveryRevision'], str) or not HEX.fullmatch(body['expectedRecoveryRevision']):
                self.fail('bad_request', 'Invalid recovery revision.')
            if not self.state.get_pack_meta(pack)['editable']:
                self.fail('read_only_pack', 'This pack is read-only.', 403)
            fingerprint = digest([pack, blocked, body])
            previous = data['operations'].get(op)
            if previous:
                if time.time() - previous['time'] >= 86400:
                    self.fail('not_found', 'Resolution receipt expired.', 404)
                if previous['fingerprint'] != fingerprint:
                    self.fail('conflict', 'operationId belongs to another request.', 409)
                return copy.deepcopy(previous['response'])
        problem = data.get('blocked')
        if not problem or problem['operationId'] != blocked:
            self.fail('not_found', 'No pending operation.', 404)
        resource = problem['resource']
        # Fake preservation is one metadata snapshot, not a native crash-safety implementation.
        revision = digest([problem, data['lines'], data['glossaries'],
                           self.state._read_json(self.state.subtitle_file(pack, 'th'))])[:32]
        if action == 'recovery':
            return dict(recoveryRevision=revision, blockedOperationId=blocked,
                        resources=[dict(resource=resource, currentFingerprint=digest(data['lines']),
                                        intendedFingerprint=digest(problem.get('intended', {})),
                                        currentExists=True, intendedExists=True)])
        if body['expectedRecoveryRevision'] != revision:
            self.fail('conflict', 'Recovery preview changed; refetch it.', 409)
        archive = '.maker/recovery/' + blocked + '/' + op
        path = self.state.data_dir / pack
        for part in (archive + '/snapshot.json').split('/'):
            path /= part
            if path.is_symlink():
                self.fail('recovery_required', 'Cannot archive through a link.', 503,
                          dict(packId=pack, resource=resource, blockedOperationId=blocked))
        snapshot = dict(schema=1, blockedOperationId=blocked, operationId=op,
                        current=copy.deepcopy(data), intended=problem.get('intended', {}))
        if path.exists():
            self.fail('conflict', 'This resolution already has an archive.', 409)
        encoded = json.dumps(snapshot, ensure_ascii=False).encode()
        maker = self.state.data_dir / pack / '.maker'
        used = sum(p.stat().st_size for p in maker.rglob('*') if p.is_file() and not p.is_symlink())
        if used + 2 * len(encoded) > 2 * 1024**3:
            self.fail('storage_limit', 'Recovery archive exceeds project quota.', 413)
        self.state._write_json_atomic(path, snapshot)
        data.pop('blocked')
        new_revision = secrets.token_hex(16)
        for line in data['lines'].values():
            line.update(revision=new_revision, historyGap=True)
        for glossary in data['glossaries'].values():
            glossary['revision'] = new_revision
        response = dict(resolved=True, operationId=op, blockedOperationId=blocked,
                        revision=new_revision, archivePath=archive, live=False)
        data['operations'][op] = dict(fingerprint=fingerprint, response=copy.deepcopy(response),
                                      time=time.time(), saved=True)
        self.save(pack, data)
        return response

    # ----- pack creation (newPacks): invented data, a protocol simulator -----------------------------------------

    def pending_entry(self, pack):
        return dict(id=pack['id'], name=pack['name'], version='0.1.0', languages=list(pack['languages']),
                    editable=False, readOnlyReason=RESTART_REASON, loaded=False, restartRequired=True,
                    studioEditable=False, studioReadOnlyReason=RESTART_REASON, metadataState='pending')

    def adopt_created(self):
        """A fake 'restart': packs an earlier session created are loaded and editable from now on."""
        root = self.state.data_dir
        for folder in sorted(p for p in root.iterdir() if p.is_dir() and not p.is_symlink() and PACK.fullmatch(p.name)):
            meta = folder / '.maker' / 'fake-v2.json'
            if not meta.is_file() or any(m['id'] == folder.name for m in self.state.packs_meta):
                continue
            try:
                created = json.loads(meta.read_text(encoding='utf-8')).get('created')
            except ValueError:
                continue
            if isinstance(created, dict):
                self.state.packs_meta.append(dict(id=folder.name, name=created.get('name', folder.name), version='0.1.0',
                                                  baseLanguages=list(created.get('languages', [])), editable=True,
                                                  readOnlyReason=None))

    def registered_languages(self):
        """Language code to the pack that registered it (any loaded pack's non-built-in language)."""
        found = {}
        for meta in self.state.packs_meta:
            for code in self.state.pack_languages(meta):
                if code not in BUILT_IN_LANGUAGES:
                    found.setdefault(code, meta['id'])
        return found

    def find_receipt(self, operation):
        for folder in sorted(self.state.data_dir.iterdir()):
            meta = folder / '.maker' / 'fake-v2.json'
            if folder.name.startswith('.') or not folder.is_dir() or folder.is_symlink() or not meta.is_file():
                continue
            try:
                found = json.loads(meta.read_text(encoding='utf-8'))['operations'].get(operation)
            except (ValueError, KeyError, TypeError):
                continue
            if found and time.time() - found['time'] <= 86400:
                return found
        return None

    def check_text(self, value, name, required=True):
        if value is None and not required:
            return None
        if not isinstance(value, str) or (required and not value.strip()):
            self.fail('bad_request', name + ' must be a non-blank string.')
        if len(value) > 100 or any(ord(c) < 32 or ord(c) == 127 for c in value):
            self.fail('bad_request', name + ' must be at most 100 characters without control characters.')
        return value

    def create_pack(self, h, query):
        self.fields(query, ())
        body = h.read_json_body()
        self.fields(body, ('operationId', 'id', 'name', 'author', 'description', 'language'), ('operationId', 'id', 'name', 'language'))
        op, pack_id = body['operationId'], body['id']
        if not isinstance(op, str) or not HEX.fullmatch(op):
            self.fail('bad_request', 'operationId must be 32 lowercase hexadecimal characters.')
        stem = pack_id.split('.')[0].lower() if isinstance(pack_id, str) else ''
        if (not isinstance(pack_id, str) or not NEW_PACK.fullmatch(pack_id) or pack_id.endswith('.')
                or stem in ('con', 'prn', 'aux', 'nul') or re.fullmatch(r'(com|lpt)[1-9]', stem)
                or pack_id == 'cwy.example' or pack_id.startswith(REFERENCE_PREFIXES)):
            self.fail('bad_request', 'id must be 3-64 lowercase letters, digits, dots, underscores or hyphens, and not a reserved name.')
        name = self.check_text(body['name'], 'name')
        author = self.check_text(body.get('author'), 'author', False)
        description = body.get('description')  # v2.3, optional: at most 500 code points, newline allowed
        if description is not None and (not isinstance(description, str) or len(description) > 500 or any(
                (ord(c) < 32 and c != '\n') or ord(c) == 127 for c in description)):
            self.fail('bad_request', 'description must be at most 500 characters without control characters.')
        language = body['language']
        self.fields(language, ('code', 'caption', 'englishName', 'script', 'fallback'), ('code',))
        code = language['code']
        if not isinstance(code, str) or not LANG.fullmatch(code):
            self.fail('bad_request', 'language.code must be a language code such as th.')
        if code in BUILT_IN_LANGUAGES:
            self.fail('bad_request', code + " is one of the game's own languages; subtitle tables are for a language a pack adds.")
        declared = any(language.get(k) is not None for k in ('caption', 'englishName', 'script', 'fallback'))
        owner = self.registered_languages().get(code)
        if owner and declared:
            self.fail('bad_request', 'language ' + code + ' is already registered by pack ' + owner + '; send only its code.')
        declaration = None
        if not owner:
            caption = self.check_text(language.get('caption'), 'language.caption')
            english = self.check_text(language.get('englishName'), 'language.englishName')
            script, fallback = language.get('script'), language.get('fallback')
            if script is not None and (not isinstance(script, str) or not SCRIPT.fullmatch(script)):
                self.fail('bad_request', 'language.script must be a 4-letter script name such as Thai.')
            if fallback is not None and (not isinstance(fallback, list) or len(fallback) > 8 or len(set(fallback)) != len(fallback)
                                         or code in fallback or any(not isinstance(f, str) or not LANG.fullmatch(f) for f in fallback)):
                self.fail('bad_request', 'language.fallback must list up to 8 other language codes once each.')
            declaration = dict(schema=1, code=code, caption=caption, englishName=english)
            if script is not None: declaration['script'] = script.lower()
            if fallback is not None: declaration['fallback'] = fallback
        fingerprint = digest(['pack-create', pack_id, name, author, code, language] + ([description] if description is not None else []))
        known = self.find_receipt(op)
        if known:
            if known['fingerprint'] != fingerprint:
                self.fail('conflict', 'operationId was already used for another request.', 409)
            return Created(copy.deepcopy(known['response']))
        taken = {p.name.lower() for p in self.state.data_dir.iterdir()} | {m['id'].lower() for m in self.state.packs_meta}
        if pack_id.lower() in taken or pack_id.lower() in self.pending:
            self.fail('conflict', 'A pack named ' + pack_id + ' already exists (names are compared ignoring case); nothing was changed.', 409)
        if declaration and any(code in p['languages'] and p.get('declared') for p in self.pending.values()):
            self.fail('conflict', 'language ' + code + ' is already declared by a pending pack.', 409)
        pack = dict(id=pack_id, name=name.strip(), languages=[code], declared=bool(declaration))
        response = dict(pack=self.pending_entry(pack), restartRequired=True, live=False, note=RESTART_NOTE)
        staging = self.state.data_dir / ('.maker-create-' + secrets.token_hex(8))
        try:
            manifest = dict(id=pack_id, name=pack['name'], version='0.1.0',
                            description=description.strip() if description and description.strip() else 'Created with Pack Maker Studio.',
                            gameVersion='1.18.0', loaderVersion='0.1.0')
            if author:
                manifest['author'] = author.strip()
            self.state._write_json_atomic(staging / 'manifest.json', manifest)
            if declaration:
                self.state._write_json_atomic(staging / 'language.json', declaration)
            self.state._write_json_atomic(staging / 'data' / 'subtitles' / (code + '.json'), {})
            self.state._write_json_atomic(staging / '.maker' / 'project.json', dict(schema=1, projectId=secrets.token_hex(16)))
            self.state._write_json_atomic(staging / '.maker' / 'fake-v2.json', dict(
                schema=1, lines={}, glossaries={}, created=dict(name=pack['name'], languages=[code]),
                operations={op: dict(fingerprint=fingerprint, response=response, time=time.time(), saved=True)}))
            staging.rename(self.state.data_dir / pack_id)
        finally:
            if staging.exists():
                import shutil
                shutil.rmtree(staging, ignore_errors=True)
        self.pending[pack_id.lower()] = pack
        self.state.emit('maker.pack.created', dict(packId=pack_id, restartRequired=True))
        self.state.emit('maker.catalog.changed', dict(catalogRevision=secrets.token_hex(16), reason='pack.created'))
        return Created(copy.deepcopy(response))

    def play(self, h, query):
        self.fields(query, ())
        body = h.read_json_body()
        self.fields(body, ('id', 'variant', 'lang', 'packId', 'expectedRevision'), ('id', 'variant', 'lang'))
        line_id, variant, lang = body['id'], body['variant'], body['lang']
        pack_id, expected = body.get('packId'), body.get('expectedRevision')
        if not isinstance(line_id, str) or not SEG.fullmatch(line_id) or line_id in ('.', '..'):
            self.fail('bad_request', 'Invalid line id.')
        if variant not in ('original', 'dub', 'sub'):
            self.fail('bad_request', 'variant must be original, dub or sub.')
        if variant == 'sub' and pack_id is None:
            self.fail('bad_request', 'sub requires packId.')
        if not isinstance(lang, str) or not LANG.fullmatch(lang):
            self.fail('bad_request', 'Invalid language.')
        if pack_id is not None and (not isinstance(pack_id, str) or not PACK.fullmatch(pack_id)):
            self.fail('bad_request', 'Invalid packId.')
        if expected is not None and (pack_id is None or not isinstance(expected, str) or not 1 <= len(expected) <= 256):
            self.fail('bad_request', 'Invalid expectedRevision.')

        revision, published_take_id, data = '', None, None
        if pack_id is not None:
            if pack_id.lower() in self.pending:
                self.fail('restart_required', RESTART_REASON, 409)
            if variant == 'sub' and not self.state.get_pack_meta(pack_id)['editable']:
                self.fail('read_only_pack', 'Reference pack is read-only.', 403)
            data = self.load(pack_id)
            blocked = data.get('blocked')
            if blocked:
                self.fail('recovery_required', 'pack=' + pack_id + '; resource=' + blocked['resource']
                          + '; operationId=' + blocked['operationId'] + '; external edit blocks recovery', 503,
                          dict(packId=pack_id, resource=blocked['resource'], blockedOperationId=blocked['operationId']))
            meta = self.line_state(data, lang, line_id)
            if expected is not None and meta['revision'] != expected:
                self.fail('conflict', 'Line changed; refetch it.', 409)
            revision = meta['revision']

        if self.activity.get('paused') is True:
            self.fail('game_paused', 'The game is paused in the background. Click the game window to continue.', 409)

        base = self.state.lines_by_id.get(line_id)
        if base is None:
            self.fail('not_found', f'Line {line_id} was not found.', 404)
        voice = base['voice']
        if not voice:
            self.fail('bad_request', f'Line {line_id} is text-only and has no voice clip.')

        if pack_id is not None:
            clip = self.clip_state(pack_id, lang, voice, data)
            published_take_id = clip['record']['takeId'] if clip['record'] else None

        resolved = self.state.resolve_line(base, lang)
        effective_pack = resolved['dubPack']
        if variant == 'dub':
            if pack_id is not None:
                wav_path = self.state.voice_file(pack_id, lang, voice)
                if not wav_path.is_file():
                    self.fail('not_found', f'Pack {pack_id} has no {lang} dub for {voice}.', 404)
                duration = self.inspect(wav_path.read_bytes(), 'source')
            else:
                if not effective_pack:
                    self.fail('not_found', f'No dub clip found for {line_id} in {lang}.', 404)
                duration = resolved['dubDurationSec']
        else:
            duration = resolved['durationSec']

        if not self.activity.get('stale') and self.activity.get('busy') is True:
            self.fail('not_ready', 'Satone is already speaking.', 409)

        subtitle = resolved['subtitle']
        if variant == 'sub':
            if pack_id is not None:
                subtitle = self.state._read_json(self.state.subtitle_file(pack_id, lang)).get(line_id)
            if subtitle is None:
                subtitle = resolved['subtitle']
        self.state.emit('maker.play', dict(id=line_id, variant=variant, lang=lang, voice=voice))
        say_group, say_row = self.state.next_say_row()
        self.state.emit('line.spoken', dict(
            voice=voice, text=subtitle or resolved['en'], textJa=(subtitle or resolved['ja']) if variant == 'sub' else resolved['ja'],
            language=lang, localizationId=say_row, source='maker.play',
            scenarioGroupId=say_group, durationSec=duration, lineId=line_id))
        if variant == 'original':
            note = "Satone is saying the game's own line."
        elif variant == 'sub':
            note = 'Satone is saying the line with the original voice and the pack subtitle.'
        elif pack_id is not None and effective_pack and effective_pack != pack_id:
            note = f"Satone is previewing {pack_id}'s {lang} dub in the game; {effective_pack} loads later and wins in normal play."
        else:
            note = f'Satone is saying the {lang} dub in the game.'
        return dict(revision=revision, operationId='', saved=False, live=True, path=None,
                    durationSec=duration, note=note, publishedTakeId=published_take_id,
                    effectivePackId=effective_pack, restartRequired=False)

    # ----- contract v2.4 (Phase B1, feature `voiceDefault`): voice origin + consent once per pack -----------------

    def voice_record(self, pack):
        """The pack default from .maker/voice.json; an absent or unreadable file is 'no default' at a stable revision."""
        raw = self.state._read_json(self.maker_file(pack, 'voice.json'))
        if raw.get('schema') != 1 or not isinstance(raw.get('revision'), str):
            return dict(revision='initial-' + digest(['voice', pack]), origin=None, consent=None, updatedAt=None)
        return dict(revision=raw['revision'], origin=raw.get('origin'), consent=raw.get('consent'),
                    updatedAt=raw.get('updatedAt'))

    def voice_default(self, h, pack, query):
        """GET/PUT P/voice. PUT follows the glossary write pattern: validate, read-only, ledger, recovery, revision."""
        if h.command not in ('GET', 'PUT'):
            self.fail('not_found', 'No such route.', 404)
        self.fields(query, ())
        if pack in self.pending:
            self.fail('restart_required', RESTART_REASON, 409)
        meta = self.state.get_pack_meta(pack)  # 404 for an unknown pack
        current = self.voice_record(pack)
        if h.command == 'GET':
            return current
        body = h.read_json_body()
        self.fields(body, ('expectedRevision', 'operationId', 'origin', 'consent'),
                    ('expectedRevision', 'operationId', 'origin', 'consent'))
        op, expected, origin, consent = body['operationId'], body['expectedRevision'], body['origin'], body['consent']
        if not isinstance(op, str) or not HEX.fullmatch(op) or not isinstance(expected, str) or not 1 <= len(expected) <= 256:
            self.fail('bad_request', 'Invalid operationId or expectedRevision.')
        if (origin is None) != (consent is None):
            self.fail('bad_request', 'Send origin and consent together, or both null to clear the default.')
        if origin is not None:
            if origin not in ORIGINS:
                self.fail('bad_request', 'origin must be own, ai-assisted or ai-generated.')
            self.fields(consent, ('confirmed', 'statementVersion'), ('confirmed', 'statementVersion'))
            if consent['confirmed'] is not True or consent['statementVersion'] != 'consent-1':
                self.fail('bad_request', 'consent needs confirmed true and statementVersion consent-1.')
        if not meta['editable']:
            self.fail('read_only_pack', 'Reference pack is read-only.', 403)
        data = self.load(pack)
        fingerprint = digest(['voice.default', pack, expected, origin, consent and consent['confirmed'],
                              consent and consent['statementVersion']])
        data['operations'] = {k: v for k, v in data['operations'].items() if time.time() - v['time'] <= 86400}
        old = data['operations'].get(op)
        if old:
            if old['fingerprint'] != fingerprint:
                self.fail('conflict', 'operationId already used.', 409)
            return copy.deepcopy(old['response'])
        blocked = data.get('blocked')
        if blocked:
            self.fail('recovery_required', 'pack=' + pack + '; resource=' + blocked['resource'] + '; operationId='
                      + blocked['operationId'] + '; external edit blocks recovery', 503,
                      dict(packId=pack, resource=blocked['resource'], blockedOperationId=blocked['operationId']))
        if current['revision'] != expected:
            self.fail('conflict', 'The pack voice default changed; refetch it.', 409)
        # An identical PUT (same origin while set, or a clear while unset) is a no-op: same revision, confirmedAt kept.
        changed = origin != current['origin'] or (origin is not None and current['consent'] is None)
        if changed:
            now = stamp()
            record = dict(schema=1, revision=secrets.token_hex(16), origin=origin,
                          consent=None if origin is None else dict(confirmed=True, statementVersion='consent-1', confirmedAt=now),
                          updatedAt=now)
            self.state._write_json_atomic(self.maker_file(pack, 'voice.json'), record)  # a clear keeps the file
            current = self.voice_record(pack)
            if origin is not None:  # v2.5: a clear leaves inheriting takes (and their disclosures) at the last default
                self.follow_voice_default(pack, data, current)
        data['operations'][op] = dict(fingerprint=fingerprint, response=copy.deepcopy(current), time=time.time(), saved=changed)
        while len(data['operations']) > 100:
            del data['operations'][next(iter(data['operations']))]
        self.save(pack, data)
        if changed:  # v2.5: one event per changing PUT (set or clear); an identical PUT or a replay emits nothing
            self.state.emit('maker.pack.changed', dict(packId=pack, reason='voice-default'))
        return current

    def follow_voice_default(self, pack, data, default):
        """v2.5: every take with consentSource pack switches to the new default, and the voice-provenance.json entry of a
        published one follows (origin, statementVersion, confirmedAt; sha256 and publishedAt stay). Takes with their own
        origin/consent (consentSource take) and their entries never change. The fake rewrites the stored takes in place,
        which is wire-equivalent to the loader resolving them at read time: line and take-page revisions, updatedAt and
        clip records stay, and no maker.take.changed is emitted."""
        consent = default['consent']
        follows = dict(origin=default['origin'], statementVersion=consent['statementVersion'], confirmedAt=consent['confirmedAt'])
        inheriting = set()
        for key, meta in data['lines'].items():
            for take in meta.get('takes', []):
                if take.get('consentSource') == 'pack':
                    take.update(origin=default['origin'], consent=dict(confirmed=True, statementVersion=consent['statementVersion'],
                                                                       confirmedAt=consent['confirmedAt']))
                    inheriting.add((key, take['id']))
        published = {tuple(key.split('/', 1)) for key, record in data.get('clips', {}).items()
                     if (key.split('/', 1)[0] + '/' + record['lineId'], record['takeId']) in inheriting}
        if not published:
            return
        path = self.state.data_dir / pack / 'voice-provenance.json'
        provenance = self.state._read_json(path)
        before = copy.deepcopy(provenance)
        for entry in provenance.get('clips', []):
            if isinstance(entry, dict) and (entry.get('lang'), entry.get('clip')) in published:
                entry.update(follows)  # existing keys keep their order
        if provenance != before:
            self.state._write_json_atomic(path, provenance)

    # ----- contract v2.3 (Phase C, feature `export`): pack info, icon, export check/export/download -------------

    def pack_files(self, h, pack, rest, query):
        """GET info, GET/PUT icon, GET/POST export, GET exports/{fileName}. Info and icon also serve a pending pack."""
        method, route = h.command, tuple(rest)
        known = {('info',): ('GET',), ('icon',): ('GET', 'PUT'), ('export',): ('GET', 'POST')}
        if route not in known and not (len(route) == 2 and route[0] == 'exports'):
            self.fail('not_found', 'No Studio route.', 404)
        if method not in known.get(route, ('GET',)):
            self.fail('not_found', 'No such route.', 404)
        self.fields(query, ())
        pending = self.pending.get(pack)
        meta = None if pending else self.state.get_pack_meta(pack)  # 404 for an unknown pack
        root = self.state.data_dir / pack
        if route == ('info',):
            return self.pack_info(pack, root, meta, pending)
        if route == ('icon',):
            return self.icon(h, pack, root, meta, pending)
        if pending:
            self.fail('restart_required', RESTART_REASON, 409)
        if route[0] == 'exports':
            return self.download_export(pack, route[1])
        if method == 'POST':
            return self.export(h, pack, root, meta)
        if not meta['editable']:
            self.fail('read_only_pack', 'Read-only packs cannot be exported from Studio.', 403)
        return self.export_preview(pack, root, meta)

    @staticmethod
    def is_reference(meta):
        from fake_loader import READONLY_PACK_ID
        return meta is not None and (meta['id'] == READONLY_PACK_ID or meta['id'].startswith(REFERENCE_PREFIXES))

    def manifest(self, root, meta, pending):
        """(raw text or None, parsed dict or None). A seeded fake pack without a file gets one in memory."""
        path = root / 'manifest.json'
        if path.is_file() and not path.is_symlink():
            raw = path.read_text(encoding='utf-8')
            try:
                parsed = json.loads(raw)
            except ValueError:
                return raw, None
            return raw, parsed if isinstance(parsed, dict) else None
        source = pending or meta
        parsed = dict(id=source['id'], name=source['name'], version=source.get('version', '0.1.0'))
        return None, parsed

    def walk(self, root):
        """Pack files split into included, excluded (dot segments, _maker_backup, Thumbs.db/desktop.ini) and links."""
        included, excluded, links = [], [], []
        for folder, dirs, names in os.walk(root, followlinks=False):
            here = pathlib.Path(folder)
            for d in list(dirs):
                rel = (here / d).relative_to(root).as_posix()
                if (here / d).is_symlink():
                    links.append(rel)
                    dirs.remove(d)
                elif d.startswith('.') or d == '_maker_backup':
                    dirs.remove(d)
                    for sub, _, files in os.walk(here / d, followlinks=False):
                        excluded += [(pathlib.Path(sub, f).relative_to(root).as_posix(), os.lstat(os.path.join(sub, f)).st_size)
                                     for f in files]
            for name in sorted(names):
                path = here / name
                rel = path.relative_to(root).as_posix()
                if path.is_symlink():
                    links.append(rel)
                elif name.startswith('.') or name.lower() in ('thumbs.db', 'desktop.ini'):
                    excluded.append((rel, path.stat().st_size))
                else:
                    included.append((rel, path.stat().st_size))
        return sorted(included), excluded, sorted(links)

    @staticmethod
    def file_counts(included, excluded):
        return dict(included=len(included), includedBytes=sum(n for _, n in included),
                    excluded=len(excluded), excludedBytes=sum(n for _, n in excluded))

    @staticmethod
    def changelog_top(root):
        path = root / 'CHANGELOG.md'
        if not path.is_file() or path.is_symlink():
            return None
        match = re.search(r'^## (\S+)[^\n]*\n(.*?)(?=^## |\Z)', path.read_text(encoding='utf-8'), re.M | re.S)
        return dict(version=match.group(1), notes=match.group(2).strip()) if match else None

    def pack_info(self, pack, root, meta, pending):
        _, manifest = self.manifest(root, meta, pending)
        manifest = manifest or {}
        included, _, _ = self.walk(root) if root.is_dir() else ([], [], [])
        source = pending or meta
        return dict(id=pack, name=manifest.get('name') or source['name'],
                    version=manifest.get('version') or source.get('version', '0.1.0'),
                    author=manifest.get('author') or None, description=manifest.get('description') or None,
                    languages=list(pending['languages']) if pending else self.state.pack_languages(meta),
                    hasIcon=(root / 'icon.png').is_file(), sizeBytes=sum(n for _, n in included),
                    pending=bool(pending), changelog=self.changelog_top(root))

    def icon(self, h, pack, root, meta, pending):
        path = root / 'icon.png'
        if h.command == 'GET':
            if self.is_reference(meta):
                self.fail('read_only_pack', 'Reference pack icons are not served.', 403)
            if not path.is_file() or path.is_symlink():
                self.fail('not_found', 'This pack has no icon.', 404)
            return Binary(path.read_bytes(), 'image/png')
        raw = h.read_png_body()
        if len(raw) > ICON_BYTES:
            self.fail('too_large', 'A pack icon must be at most 512 KiB.', 413)
        if raw[:8] != b'\x89PNG\r\n\x1a\n' or len(raw) < 24 or raw[12:16] != b'IHDR':
            self.fail('unsupported_media_type', 'The icon must be a PNG image.', 415)
        width, height = struct.unpack('>II', raw[16:24])
        if width != height or not 64 <= width <= 1024:
            self.fail('unsupported_media_type', 'The icon must be square, 64-1024 pixels.', 415)
        if not pending and not meta['editable']:
            self.fail('read_only_pack', 'This pack is read-only.', 403)
        if path.is_file():
            backup = root / '_maker_backup' / ('icon-' + datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f') + '.png')
            backup.parent.mkdir(parents=True, exist_ok=True)
            path.replace(backup)
        tmp = root / ('.icon-' + secrets.token_hex(8) + '.tmp')
        tmp.write_bytes(raw)
        tmp.replace(path)
        self.state.emit('maker.pack.changed', dict(packId=pack, reason='icon'))
        return dict(hasIcon=True, sizeBytes=len(raw), width=width, height=height)

    @staticmethod
    def next_versions(version):
        match = SEMVER.fullmatch(version or '')
        if not match:
            return dict(patch=None, minor=None, major=None)
        major, minor, patch = (int(x) for x in match.groups()[:3])
        return dict(patch=f'{major}.{minor}.{patch if match.group(4) else patch + 1}',
                    minor=f'{major}.{minor + 1}.0', major=f'{major + 1}.0.0')

    def checks(self, pack, root, raw_manifest, manifest, included, links):
        found = []

        def check(cid, errors, warnings, ok, problem, codes=None):
            paths = errors + warnings
            severity = 'error' if errors else 'warning' if warnings else 'ok'
            found.append(dict(id=cid, severity=severity, message=ok if severity == 'ok' else problem(len(errors), len(warnings)),
                              count=len(paths), paths=paths[:20]))
            # Like the real loader (TD-13): code + params the page words; only where this fake's finding matches a real code.
            coded = (codes or {}).get(severity)
            if coded:
                found[-1].update(code=coded[0], params=coded[1])

        bad = []
        if manifest is None or manifest.get('id') != pack or not SEMVER.fullmatch(str(manifest.get('version', ''))) \
                or not isinstance(manifest.get('name'), str) or not manifest['name'].strip():
            bad.append('manifest.json')
        check('manifest', bad, [], 'manifest.json is valid.', lambda e, w: 'manifest.json does not parse, or its id or version is wrong.',
              {'ok': ('manifestOk', dict(name=(manifest or {}).get('name'), version=str((manifest or {}).get('version'))))})

        errors, warnings = [], []
        for rel, _ in included:
            if re.fullmatch(r'data/subtitles/[^/]+\.json', rel):
                try:
                    table = json.loads((root / rel).read_text(encoding='utf-8'))
                    if not isinstance(table, dict) or any(not isinstance(v, str) for v in table.values()):
                        raise ValueError(rel)
                    if not table:
                        warnings.append(rel)
                except (ValueError, UnicodeDecodeError):
                    errors.append(rel)
        check('subtitles', errors, warnings, 'Every subtitle table parses.',
              lambda e, w: f'{e} subtitle table(s) do not parse; {w} are empty.')

        errors, warnings, dubs = [], [], {}
        for rel, size in included:
            if not rel.startswith('audio/voice/'):
                continue
            suffix = pathlib.PurePosixPath(rel).suffix.lower()
            raw = (root / rel).read_bytes()
            if size == 0:
                errors.append(rel)
            elif suffix == '.wav':
                try:
                    with wave.open(io.BytesIO(raw), 'rb') as wav:
                        if wav.getsampwidth() != 2 or not 8000 <= wav.getframerate() <= 48000 or wav.getnframes() / wav.getframerate() > 60:
                            warnings.append(rel)
                    dubs[rel] = raw
                except (wave.Error, EOFError, ZeroDivisionError):
                    errors.append(rel)
            elif suffix == '.ogg' and raw[:4] != b'OggS':
                errors.append(rel)
            elif suffix == '.ogg':
                dubs[rel] = raw
            else:
                warnings.append(rel)
        check('audio', errors, warnings, 'Every voice file reads.',
              lambda e, w: f'{e} voice file(s) are empty or malformed; {w} have another format or are out of bounds.')

        errors, warnings, entries = [], [], {}
        path = root / 'voice-provenance.json'
        if path.is_file():
            try:
                data = json.loads(path.read_text(encoding='utf-8'))
                for c in data.get('clips', []):
                    entries['audio/voice/' + c['lang'] + '/' + c['clip'] + '.wav'] = c['sha256']
            except (ValueError, KeyError, TypeError, AttributeError):
                errors.append('voice-provenance.json')
        missing = [rel for rel in entries if rel not in dubs]
        changed = [rel for rel, sha in entries.items() if rel in dubs and self.sha(dubs[rel]) != sha]
        errors += missing + changed
        warnings += [rel for rel in dubs if rel not in entries]
        check('provenance', errors, warnings, 'Every published dub has a matching disclosure.',
              lambda e, w: f'{len(missing)} disclosure(s) are missing audio, {len(changed)} changed after publishing; '
                           f'{w} dub(s) have no voice-provenance.json entry.')

        total = sum(n for _, n in included)
        check('size', ['(pack)'] if total > ZIP_BYTES else [], ['(pack)'] if BROWSER_ZIP_BYTES < total <= ZIP_BYTES else [],
              'The pack fits in a browser download.',
              lambda e, w: 'The pack is over 2 GiB.' if e else 'The pack is over 256 MiB: too big for the browser download.')

        gaps, summary, langs = [], [], []
        data = self.load(pack)
        for lang in self.state.pack_languages(self.state.get_pack_meta(pack)):
            lines = self.all_lines(pack, lang, data)
            no_dub = [lang + '/' + x['id'] for x in lines if x['voice'] and not x['ownedDub']]
            no_sub = [lang + '/' + x['id'] for x in lines if x['ownedSubtitle'] is None]
            gaps += no_dub + [x for x in no_sub if x not in no_dub]
            summary.append(f'{lang}: {len(no_dub)} voiced line(s) without a dub, {len(no_sub)} without a subtitle')
            langs.append(dict(lang=lang, dub=len(no_dub), voiced=sum(1 for x in lines if x['voice']), sub=len(no_sub), total=len(lines)))
        check('progress', [], gaps, 'Every line has its subtitle and dub.', lambda e, w: '; '.join(summary) + '.',
              {'ok': ('progressOk', {}), 'warning': ('progress', dict(langs=langs))})
        check('links', [], links, 'No links in the pack.', lambda e, w: f'{w} symbolic link(s) are skipped.',
              {'ok': ('linksOk', {}), 'warning': ('linksSkipped', dict(n=len(links)))})
        return found

    def export_preview(self, pack, root, meta):
        raw, manifest = self.manifest(root, meta, None)
        included, excluded, links = self.walk(root)
        if raw is None:
            included.append(('manifest.json', len(json.dumps(manifest, indent=2).encode())))
        checks = self.checks(pack, root, raw, manifest, included, links)
        errors = sum(c['severity'] == 'error' for c in checks)
        last = self.state._read_json(root / '.maker' / 'exports' / 'last.json') or None
        if last:
            last = dict(last, folder=str(self.state.data_dir / '.maker-exports'))
        version = (manifest or {}).get('version')
        return dict(packId=pack, version=version, nextVersions=self.next_versions(version), ok=errors == 0,
                    errorCount=errors, warningCount=sum(c['severity'] == 'warning' for c in checks), checks=checks,
                    files=self.file_counts(included, excluded), changelog=self.changelog_top(root), lastExport=last)

    def export(self, h, pack, root, meta):
        body = h.read_json_body()
        self.fields(body, ('operationId', 'expectedVersion', 'bump', 'notes'), ('operationId', 'expectedVersion', 'bump', 'notes'))
        op, expected, bump, notes = body['operationId'], body['expectedVersion'], body['bump'], body['notes']
        if not isinstance(op, str) or not HEX.fullmatch(op):
            self.fail('bad_request', 'operationId must be 32 lowercase hexadecimal characters.')
        if not isinstance(expected, str) or not 1 <= len(expected) <= 64:
            self.fail('bad_request', 'expectedVersion must be the version the export check returned.')
        if bump not in ('none', 'patch', 'minor', 'major'):
            self.fail('bad_request', 'bump must be none, patch, minor or major.')
        if notes is not None and (not isinstance(notes, str) or len(notes) > 4000
                                  or any(ord(c) < 32 and c not in '\n\t' or ord(c) == 127 for c in notes)):
            self.fail('bad_request', 'notes must be at most 4000 characters without control characters.')
        if not meta['editable']:
            self.fail('read_only_pack', 'Read-only packs cannot be exported from Studio.', 403)
        receipt_path = root / '.maker' / 'exports' / (op + '.json')
        fingerprint = digest(['export', pack, body])
        receipt = self.state._read_json(receipt_path)
        if receipt:
            if receipt.get('fingerprint') != fingerprint:
                self.fail('conflict', 'operationId was already used for another request.', 409)
            return Created(dict(copy.deepcopy(receipt['response']), folder=str(self.state.data_dir / '.maker-exports')))
        raw, manifest = self.manifest(root, meta, None)
        previous = (manifest or {}).get('version')
        if expected != previous:
            self.fail('conflict', 'The pack version changed; run the export check again.', 409)
        preview = self.export_preview(pack, root, meta)
        if preview['errorCount']:
            self.fail('export_blocked', f"{preview['errorCount']} export check(s) failed; fix them and check again.", 409)
        version = previous if bump == 'none' else self.next_versions(previous)[bump]
        if raw is None:
            new_manifest = json.dumps(dict(manifest, version=version), ensure_ascii=False, indent=2) + '\n'
        else:  # format-preserving: only the top-level version value changes
            new_manifest = re.sub(r'("version"\s*:\s*")[^"]*(")', lambda m: m.group(1) + version + m.group(2), raw, count=1)
        changelog_path = root / 'CHANGELOG.md'
        old_log = changelog_path.read_text(encoding='utf-8') if changelog_path.is_file() else ''
        new_log = old_log
        if notes is not None and notes.strip():
            first = re.search(r'^## ', old_log, re.M)  # text before the first section (a title) stays on top
            head, rest = (old_log[:first.start()], old_log[first.start():]) if first else (old_log, '')
            top = re.match(r'## (\S+)', rest)
            if top and top.group(1) == version:  # a top section for the same version is replaced
                following = re.search(r'^## ', rest[3:], re.M)
                rest = rest[3 + following.start():] if following else ''
            section = f'## {version} ({datetime.date.today().isoformat()})\n\n{notes.strip()}\n\n'
            new_log = (head.rstrip('\n') + '\n\n' if head.strip() else '') + section + rest
        included, excluded, _ = self.walk(root)
        entries = {rel: None for rel, _ in included}
        entries['manifest.json'] = new_manifest.encode('utf-8')
        if new_log:
            entries['CHANGELOG.md'] = new_log.encode('utf-8')
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, 'w') as archive:
            for rel in sorted(entries):
                data = entries[rel] if entries[rel] is not None else (root / rel).read_bytes()
                kind = zipfile.ZIP_STORED if rel.lower().endswith(STORED_SUFFIXES) else zipfile.ZIP_DEFLATED
                archive.writestr(zipfile.ZipInfo(pack + '/' + rel, time.localtime()[:6]), data, compress_type=kind)
        zipped = buffer.getvalue()
        if len(zipped) > ZIP_BYTES:
            self.fail('too_large', 'The export is over 2 GiB.', 413)
        exports = self.state.data_dir / '.maker-exports'
        exports.mkdir(parents=True, exist_ok=True)
        file_name = export_file_name(pack, version, (manifest or {}).get('author'))
        staged = exports / ('.' + file_name + '.' + secrets.token_hex(8) + '.tmp')
        staged.write_bytes(zipped)
        manifest_updated, changelog_updated = version != previous, new_log != old_log
        if changelog_updated:
            changelog_path.write_text(new_log, encoding='utf-8')
        if manifest_updated:
            (root / 'manifest.json').write_text(new_manifest, encoding='utf-8')
        staged.replace(exports / file_name)
        response = dict(packId=pack, version=version, previousVersion=previous, fileName=file_name, sizeBytes=len(zipped),
                        sha256=self.sha(zipped), files=len(entries), downloadable=len(zipped) <= BROWSER_ZIP_BYTES,
                        manifestUpdated=manifest_updated, changelogUpdated=changelog_updated,
                        warningCount=preview['warningCount'], note=EXPORT_NOTE)
        self.state._write_json_atomic(receipt_path, dict(fingerprint=fingerprint, response=response, time=time.time()))
        self.state._write_json_atomic(root / '.maker' / 'exports' / 'last.json',
                                      dict(fileName=file_name, version=version, sizeBytes=len(zipped), createdAt=stamp()))
        self.state.emit('maker.pack.changed', dict(packId=pack, reason='export'))
        # Like the real loader: the folder holding the zip on this computer, not stored in the receipt.
        return Created(dict(copy.deepcopy(response), folder=str(exports)))

    def download_export(self, pack, file_name):
        match = re.fullmatch(re.escape(pack) + r'-v(.+?)(?:-by-[a-z0-9]+)?\.zip', file_name)
        if not match or not SEMVER.fullmatch(match.group(1)) or '+' in match.group(1):
            self.fail('not_found', 'No such export.', 404)
        path = self.state.data_dir / '.maker-exports' / file_name
        if not path.is_file() or path.is_symlink():
            self.fail('not_found', 'No such export.', 404)
        if path.stat().st_size > BROWSER_ZIP_BYTES:
            self.fail('too_large', 'This export is over 256 MiB; open it from the .maker-exports folder.', 413)
        return Binary(path.read_bytes(), 'application/zip',
                      {'Content-Disposition': 'attachment; filename="' + file_name + '"'})
