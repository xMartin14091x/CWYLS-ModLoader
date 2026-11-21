import hashlib
import json
import pathlib
import tempfile
import unittest
import threading
from fake_loader import FakeLoaderState, ApiError, EDITABLE_PACK_ID, READONLY_PACK_ID, OVERRIDE_PACK_ID, MakerHTTPServer, Handler, make_sine_wav
from contract_test import HttpClient
from studio_v2_fake import Created, Binary
import io
import struct
import zipfile
import zlib

OVERRIDE = 'sample-override-pack'


class Request:
    def __init__(self, method='GET', body=None, wav=None, png=None):
        self.command, self.body, self.wav, self.png = method, body, wav, png

    def read_png_body(self):
        return self.png

    def read_json_body(self):
        return self.body

    def read_wav_body(self):
        return self.wav


class StudioFakeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='cwy-v2-fake-test-')
        self.path = pathlib.Path(self.temp.name)
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64)
        self.root = '/v1/maker/studio/packs/' + EDITABLE_PACK_ID
        self.line = self.root + '/languages/th/lines/sample_01'

    def tearDown(self):
        self.temp.cleanup()

    def call(self, method, path, body=None, query=None):
        return self.state.studio.handle(Request(method, body), path, query or {})

    def test_creator_state_and_receipts_survive_reopening(self):
        revision = self.call('GET', self.line)['revision']
        body = dict(expectedRevision=revision, operationId='1' * 32, status='draft')
        result = self.call('PATCH', self.line + '/workflow', body)
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 'u' * 64)
        self.assertEqual('draft', self.call('GET', self.line)['workflow']['status'])
        self.assertEqual(result, self.call('PATCH', self.line + '/workflow', body))

    def test_recovery_refusal_names_blocked_resource_and_original_operation(self):
        data = self.state.studio.load(EDITABLE_PACK_ID)
        data['blocked'] = dict(resource='.maker/lines/th/sample_01.json', operationId='a' * 32)
        self.state.studio.save(EDITABLE_PACK_ID, data)
        revision = self.call('GET', self.line)['revision']
        with self.assertRaises(ApiError) as caught:
            self.call('PATCH', self.line + '/workflow', dict(
                expectedRevision=revision, operationId='b' * 32, status='done'))
        self.assertEqual('recovery_required', caught.exception.code)
        self.assertEqual(dict(packId=EDITABLE_PACK_ID, resource='.maker/lines/th/sample_01.json',
                              blockedOperationId='a' * 32), caught.exception.details)
        for value in (EDITABLE_PACK_ID, '.maker/lines/th/sample_01.json', 'a' * 32):
            self.assertIn(value, caught.exception.message)

    def test_unknown_schema_is_preserved(self):
        path = self.state.studio.path(EDITABLE_PACK_ID)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('{"schema":99}', encoding='utf-8')
        with self.assertRaises(ApiError) as caught:
            self.call('GET', self.line)
        self.assertEqual('metadata_unavailable', caught.exception.code)
        self.assertEqual('{"schema":99}', path.read_text(encoding='utf-8'))

    def test_keep_current_archives_before_unblocking_and_replays(self):
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64, recovery_fixture=True)
        path = '/v1/maker/studio/packs/sample-recovery/operations/' + 'a' * 32
        preview = self.call('GET', path + '/recovery')
        request = dict(operationId='b' * 32, expectedRecoveryRevision=preview['recoveryRevision'], strategy='keep-current')
        result = self.call('POST', path + '/resolve', request)
        self.assertFalse(result['live'])
        self.assertTrue((self.path / 'sample-recovery' / result['archivePath'] / 'snapshot.json').is_file())
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 'u' * 64, recovery_fixture=True)
        self.assertNotIn('blocked', self.state.studio.load('sample-recovery'))
        self.assertEqual(result, self.call('POST', path + '/resolve', request))

    def test_http_recovery_error_carries_structured_details(self):
        data = self.state.studio.load(EDITABLE_PACK_ID)
        data['blocked'] = dict(resource='.maker/lines/th/sample_01.json', operationId='a' * 32)
        self.state.studio.save(EDITABLE_PACK_ID, data)
        revision = self.call('GET', self.line)['revision']
        server = MakerHTTPServer(('127.0.0.1', 0), Handler, self.state)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            client = HttpClient('127.0.0.1', server.server_address[1], 't' * 64)
            status, _, _, body = client.request('PATCH', self.line + '/workflow',
                json_body=dict(expectedRevision=revision, operationId='b' * 32, status='done'))
            self.assertEqual(503, status)
            self.assertEqual(dict(packId=EDITABLE_PACK_ID, resource='.maker/lines/th/sample_01.json',
                                  blockedOperationId='a' * 32), body['error']['details'])
            self.assertTrue(body['error']['message'])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_resolve_validates_request_body_before_read_only_pack_check(self):
        path = '/v1/maker/studio/packs/' + READONLY_PACK_ID + '/operations/' + 'a' * 32 + '/resolve'
        with self.assertRaises(ApiError) as bad_body:
            self.call('POST', path, dict(operationId='not-hex', expectedRecoveryRevision='b' * 32, strategy='keep-current'))
        self.assertEqual(400, bad_body.exception.status)
        self.assertEqual('bad_request', bad_body.exception.code)
        with self.assertRaises(ApiError) as valid_body:
            self.call('POST', path, dict(operationId='b' * 32, expectedRecoveryRevision='c' * 32, strategy='keep-current'))
        self.assertEqual(403, valid_body.exception.status)
        self.assertEqual('read_only_pack', valid_body.exception.code)

    def refused(self, method, path, body=None, query=None):
        with self.assertRaises(ApiError) as caught:
            self.state.studio.handle(Request(method, body), path, query or {})
        return caught.exception

    def test_page_errors_name_the_allowed_range(self):
        lines = self.root + '/languages/th/lines'
        for query, message in ((dict(limit='900'), 'limit must be 1-500'), (dict(limit='0'), 'limit must be 1-500'),
                               (dict(limit='ten'), 'limit must be 1-500'),
                               (dict(offset='-1'), 'offset must be a whole number of 0 or more')):
            error = self.refused('GET', lines, query=query)
            self.assertEqual(('bad_request', message), (error.code, error.message), query)
        error = self.refused('GET', self.line + '/history', query=dict(limit='21'))
        self.assertEqual(('bad_request', 'limit must be 1-20'), (error.code, error.message))

    def test_mutation_bodies_are_validated_before_the_read_only_pack_check(self):
        # Same order as the real loader (MakerM5bTests.Real_loader_validates_studio_bodies_before_refusing_a_read_only_pack).
        pack = '/v1/maker/studio/packs/' + READONLY_PACK_ID
        line = pack + '/languages/th/lines/sample_01'
        mutation = dict(expectedRevision='initial', operationId='a' * 32)
        too_long = 'x' * 2001
        cases = (
            ('PUT', pack + '/glossary/th', dict(entries=[dict(id='word', target=too_long)]), dict(entries=[dict(id='word', target='ok')])),
            ('PUT', line + '/subtitle', dict(text=too_long), dict(text='ok')),
            ('PATCH', line + '/workflow', dict(status='retake'), dict(status='done')),
        )
        for method, path, bad, good in cases:
            self.assertEqual((400, 'bad_request'), (lambda e: (e.status, e.code))(self.refused(method, path, dict(mutation, **bad))), path)
            self.assertEqual((403, 'read_only_pack'), (lambda e: (e.status, e.code))(self.refused(method, path, dict(mutation, **good))), path)

    def test_saved_effective_pack_id_names_the_pack_whose_text_the_game_shows(self):
        own = self.call('GET', self.line)
        saved = self.call('PUT', self.line + '/subtitle', dict(expectedRevision=own['revision'], operationId='1' * 32, text='ของเรา'))
        self.assertEqual(EDITABLE_PACK_ID, saved['effectivePackId'])  # our own save wins
        # sample_05 has no text in the editable pack; the reference pack's text shows until ours is saved.
        other = self.root + '/languages/th/lines/sample_05'
        self.assertEqual(READONLY_PACK_ID, self.call('GET', other)['subtitlePack'])
        first = self.call('PUT', other + '/subtitle', dict(expectedRevision=self.call('GET', other)['revision'],
                                                          operationId='2' * 32, text='ของเรา'))
        self.assertEqual(EDITABLE_PACK_ID, first['effectivePackId'])
        removed = self.call('PUT', other + '/subtitle', dict(expectedRevision=first['revision'], operationId='3' * 32, text=None))
        self.assertEqual(READONLY_PACK_ID, removed['effectivePackId'])  # removing ours lets the other pack's text show again

    def test_precedence_fixture_has_lines_where_another_pack_wins(self):
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64, precedence_fixture=True)
        ids = [p['id'] for p in self.call('GET', '/v1/maker/studio/packs')['packs']]
        self.assertIn(OVERRIDE_PACK_ID, ids)
        lines = {x['id']: x for x in self.call('GET', self.root + '/languages/th/lines', None)['items']}
        wins_subtitle = [x for x in lines.values() if x['ownedSubtitle'] is not None and x['subtitlePack'] not in (None, EDITABLE_PACK_ID)]
        wins_dub = [x for x in lines.values() if x['ownedDub'] and x['dubPack'] not in (None, EDITABLE_PACK_ID)]
        only_dub = [x for x in lines.values() if x['voice'] and not x['ownedDub'] and x['dubPack'] not in (None, EDITABLE_PACK_ID)]
        self.assertTrue(wins_subtitle and wins_dub and only_dub, (wins_subtitle, wins_dub, only_dub))
        line = self.root + '/languages/th/lines/' + wins_subtitle[0]['id']
        saved = self.call('PUT', line + '/subtitle', dict(expectedRevision=self.call('GET', line)['revision'],
                                                         operationId='4' * 32, text='ของเรา'))
        self.assertEqual(OVERRIDE_PACK_ID, saved['effectivePackId'])  # saved, but another pack's text still shows

    def test_game_language_fixture_differs_from_the_selected_pack_language(self):
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64,
                                     game_language='en', language_fixture=True)
        status = self.call('GET', '/v1/maker/studio/status')
        self.assertEqual(('en', False), (status['gameLanguage'], status['gameLanguageIsPack']))
        pack = next(p for p in self.call('GET', '/v1/maker/studio/packs')['packs'] if p['id'] == EDITABLE_PACK_ID)
        self.assertEqual(['en', 'th'], pack['languages'])  # the creator can edit Thai while the game shows English
        english = self.call('GET', self.root + '/languages/en/lines/sample_01')
        self.assertEqual(EDITABLE_PACK_ID, english['subtitlePack'])
        self.assertEqual('th', FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 'u' * 64)
                         .studio.handle(Request(), '/v1/maker/studio/status', {})['gameLanguage'])

    def test_dubs_round_trip_publishes_over_an_outside_dub_and_unpublish_restores_it(self):
        caps = self.call('GET', '/v1/maker/studio/capabilities')
        self.assertIn('dubs', caps['features'])
        voice = self.path / EDITABLE_PACK_ID / 'audio' / 'voice' / 'th' / 'vo_sample_01.wav'
        outside = voice.read_bytes()  # sample_01 already has a dub made outside Studio
        takes = self.line + '/takes'
        page = self.call('GET', takes)
        self.assertEqual(['revision', 'favouriteTakeId', 'publishedTakeId', 'clipRevision', 'items'], list(page))
        status, created = self.call('POST', takes, dict(expectedRevision=page['revision'], operationId='1' * 32, origin='own',
                                                        consent=dict(confirmed=True, statementVersion='consent-1')))
        self.assertEqual(201, status)
        take = created['take']['id']
        audio = takes + '/' + take + '/audio'
        with self.assertRaises(ApiError) as early:
            self.wav(audio, 'render', created['revision'], '2' * 32, make_sine_wav(0.5, 48000))
        self.assertEqual(('not_found', 404), (early.exception.code, early.exception.status))
        source = self.wav(audio, 'source', created['revision'], '3' * 32, make_sine_wav(0.5, 24000))
        with self.assertRaises(ApiError) as wrong:
            self.wav(audio, 'render', source['revision'], '4' * 32, make_sine_wav(0.5, 24000))
        self.assertEqual('unsupported_media_type', wrong.exception.code)
        render = self.wav(audio, 'render', source['revision'], '5' * 32, make_sine_wav(0.5, 48000))
        self.assertTrue(render['take']['renderReady'])
        self.assertEqual(make_sine_wav(0.5, 48000), self.state.studio.handle(Request('GET'), audio, {'kind': 'render'}))
        page = self.call('GET', takes)

        published = self.call('POST', self.line + '/publish', dict(expectedRevision=page['revision'], operationId='6' * 32,
                                                                   expectedClipRevision=page['clipRevision'], takeId=take))
        self.assertEqual((take, False, EDITABLE_PACK_ID), (published['publishedTakeId'], published['live'], published['effectivePackId']))
        self.assertEqual(make_sine_wav(0.5, 48000), voice.read_bytes())
        self.assertEqual(outside, (self.path / EDITABLE_PACK_ID / '_maker_backup' / 'audio' / 'voice' / 'th' / 'vo_sample_01.wav').read_bytes())
        provenance = json.loads((self.path / EDITABLE_PACK_ID / 'voice-provenance.json').read_text(encoding='utf-8'))
        self.assertEqual([dict(lang='th', clip='vo_sample_01', origin='own', statementVersion='consent-1',
                               confirmedAt=created['take']['consent']['confirmedAt'],
                               sha256=hashlib.sha256(voice.read_bytes()).hexdigest(),
                               publishedAt=provenance['clips'][0]['publishedAt'])], provenance['clips'])
        line = self.call('GET', self.line)
        self.assertEqual((take, 1), (line['publishedTakeId'], line['takeCount']))
        page = self.call('GET', takes)
        with self.assertRaises(ApiError) as in_use:
            self.call('DELETE', audio.rsplit('/', 1)[0], query=dict(expectedRevision=page['revision'], operationId='7' * 32))
        self.assertEqual('take_in_use', in_use.exception.code)

        removed = self.call('POST', self.line + '/unpublish', dict(expectedRevision=page['revision'], operationId='8' * 32,
                                                                   expectedClipRevision=page['clipRevision']))
        self.assertIsNone(removed['publishedTakeId'])
        self.assertEqual(outside, voice.read_bytes())  # the outside dub is back
        self.assertEqual([], json.loads((self.path / EDITABLE_PACK_ID / 'voice-provenance.json').read_text(encoding='utf-8'))['clips'])

    def test_unpublish_moves_a_dub_made_outside_studio_aside(self):
        voice = self.path / EDITABLE_PACK_ID / 'audio' / 'voice' / 'th' / 'vo_sample_01.wav'
        outside = voice.read_bytes()
        page = self.call('GET', self.line + '/takes')
        self.assertEqual(0, len(page['items']))
        self.call('POST', self.line + '/unpublish', dict(expectedRevision=page['revision'], operationId='9' * 32,
                                                          expectedClipRevision=page['clipRevision']))
        self.assertFalse(voice.exists())
        self.assertEqual(outside, (self.path / EDITABLE_PACK_ID / '_maker_backup' / 'audio' / 'voice' / 'th' / 'vo_sample_01.wav').read_bytes())
        self.assertFalse(self.call('GET', self.line)['ownedDub'])

    def test_publish_refuses_missing_consent_stale_clip_and_read_only_packs_after_validation(self):
        takes = self.line + '/takes'
        page = self.call('GET', takes)
        _, created = self.call('POST', takes, dict(expectedRevision=page['revision'], operationId='a' * 32, origin='ai-generated',
                                                   consent=dict(confirmed=False, statementVersion='consent-1')))
        take = created['take']['id']
        audio = takes + '/' + take + '/audio'
        source = self.wav(audio, 'source', created['revision'], 'b' * 32, make_sine_wav(0.5, 24000))
        self.wav(audio, 'render', source['revision'], 'c' * 32, make_sine_wav(0.5, 48000))
        page = self.call('GET', takes)
        body = dict(expectedRevision=page['revision'], operationId='d' * 32, expectedClipRevision=page['clipRevision'], takeId=take)
        with self.assertRaises(ApiError) as consent:
            self.call('POST', self.line + '/publish', body)
        self.assertEqual(('consent_required', 403), (consent.exception.code, consent.exception.status))
        with self.assertRaises(ApiError) as stale:
            self.call('POST', self.line + '/publish', dict(body, operationId='e' * 32, expectedClipRevision='0' * 32))
        self.assertEqual('conflict', stale.exception.code)
        reference = self.root.replace(EDITABLE_PACK_ID, READONLY_PACK_ID) + '/languages/th/lines/sample_01'
        with self.assertRaises(ApiError) as bad:
            self.call('POST', reference + '/publish', dict(body, takeId='not-hex'))
        self.assertEqual(400, bad.exception.status)
        with self.assertRaises(ApiError) as read_only:
            self.call('POST', reference + '/publish', body)
        self.assertEqual(403, read_only.exception.status)

    def test_history_is_not_a_subtitle_mutation(self):
        revision = self.call('GET', self.line)['revision']
        with self.assertRaises(ApiError) as caught:
            self.call('PUT', self.line + '/history', dict(expectedRevision=revision, operationId='f' * 32, text='x'))
        self.assertEqual(('not_found', 404), (caught.exception.code, caught.exception.status))
        self.assertNotEqual('x', self.call('GET', self.line)['ownedSubtitle'])

    def wav(self, audio, kind, revision, op, raw):
        return self.state.studio.handle(Request('PUT', wav=raw), audio, dict(kind=kind, expectedRevision=revision, operationId=op))


class StudioFakeNewPackTests(unittest.TestCase):
    """Pack creation, fixtures and effectivePackId on the loopback fake."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='cwy-v2-fake-newpack-')
        self.path = pathlib.Path(self.temp.name)
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64)
        self.root = '/v1/maker/studio/packs/' + EDITABLE_PACK_ID

    def tearDown(self):
        self.temp.cleanup()

    def call(self, method, path, body=None):
        return self.state.studio.handle(Request(method, body), path, {})

    def create(self, op='1', pack='lab.new.pack', language=None, **extra):
        body = dict(operationId=op * 32, id=pack, name='Lab pack', language=language or dict(code='th'), **extra)
        return self.call('POST', '/v1/maker/studio/packs', body)

    def refused(self, code, status, method='POST', path='/v1/maker/studio/packs', body=None):
        with self.assertRaises(ApiError) as caught:
            self.call(method, path, body)
        self.assertEqual((status, code), (caught.exception.status, caught.exception.code))
        return caught.exception

    def packs(self):
        return {p['id']: p for p in self.call('GET', '/v1/maker/studio/packs')['packs']}

    def test_newPacks_is_advertised(self):
        self.assertIn('newPacks', self.call('GET', '/v1/maker/studio/capabilities')['features'])

    def test_creating_a_pack_is_201_pending_and_replays_exactly(self):
        first = self.create()
        self.assertIsInstance(first, Created)
        self.assertEqual(['pack', 'restartRequired', 'live', 'note'], list(first))
        self.assertTrue(first['restartRequired'])
        self.assertFalse(first['live'])
        pack = self.packs()['lab.new.pack']
        self.assertEqual(dict(loaded=False, restartRequired=True, editable=False, studioEditable=False,
                              metadataState='pending', languages=['th']),
                         {k: pack[k] for k in ('loaded', 'restartRequired', 'editable', 'studioEditable', 'metadataState', 'languages')})
        self.assertEqual(first, self.create())
        root = self.path / 'lab.new.pack'
        for name in ('manifest.json', 'data/subtitles/th.json', '.maker/project.json'):
            self.assertTrue((root / name).is_file(), name)
        self.assertFalse((root / 'language.json').exists())
        kinds = [e['type'] for e in self.state.events]
        self.assertEqual(1, kinds.count('maker.pack.created'))
        self.assertEqual(1, kinds.count('maker.catalog.changed'))

    def test_every_pack_route_on_a_pending_pack_is_409_restart_required(self):
        self.create()
        base = '/v1/maker/studio/packs/lab.new.pack'
        for method, path, body in (
                ('GET', base + '/languages/th/lines', None), ('GET', base + '/languages/th/groups', None),
                ('GET', base + '/languages/th/lines/sample_01', None), ('GET', base + '/glossary/th', None),
                ('PATCH', base + '/languages/th/lines/sample_01/workflow',
                 dict(expectedRevision='x', operationId='b' * 32, status='done')),
                ('POST', '/v1/maker/studio/play', dict(id='sample_01', variant='dub', lang='th', packId='lab.new.pack'))):
            self.refused('restart_required', 409, method, path, body)

    def test_a_restart_loads_the_created_pack_and_it_becomes_editable(self):
        self.create()
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 'u' * 64)
        pack = self.packs()['lab.new.pack']
        self.assertTrue(pack['loaded'] and pack['studioEditable'] and not pack['restartRequired'])
        self.assertEqual('ready', pack['metadataState'])
        line = self.call('GET', '/v1/maker/studio/packs/lab.new.pack/languages/th/lines/sample_01')
        self.call('PATCH', '/v1/maker/studio/packs/lab.new.pack/languages/th/lines/sample_01/workflow',
                  dict(expectedRevision=line['revision'], operationId='c' * 32, status='draft'))
        self.assertEqual(self.create()['pack']['id'], 'lab.new.pack')

    def test_collisions_conflicts_and_validation(self):
        self.create()
        self.refused('conflict', 409, body=dict(operationId='1' * 32, id='lab.new.pack', name='Other', language=dict(code='th')))
        self.refused('conflict', 409, body=dict(operationId='2' * 32, id='LAB.NEW.PACK'.lower(), name='Lab pack', language=dict(code='th')))
        (self.path / 'Folder.Case').mkdir()
        self.refused('conflict', 409, body=dict(operationId='3' * 32, id='folder.case', name='X', language=dict(code='th')))
        bad = lambda **kw: dict(dict(operationId='4' * 32, id='ok.pack', name='X', language=dict(code='th')), **kw)
        for body in (bad(id='ab'), bad(id='Upper'), bad(id='con'), bad(id='cwy.example'), bad(id='cwy.demo.x'), bad(name=' '),
                     bad(language=dict(code='en')), bad(language=dict(code='ja')), bad(language=dict(code='th', caption='x')),
                     bad(language=dict(code='en', caption='English', englishName='English')),
                     bad(language=dict(code='ru', caption='Русский', englishName='Russian')),
                     bad(language=dict(code='vi')), bad(language=dict(code='vi', caption='V')),
                     bad(language=dict(code='vi', caption='V', englishName='V', script='Latin1')),
                     bad(language=dict(code='vi', caption='V', englishName='V', fallback=['vi'])), bad(surprise=1),
                     dict(operationId='nope', id='ok.pack', name='X', language=dict(code='th'))):
            self.refused('bad_request', 400, body=body)
        self.assertEqual(['lab.new.pack'], sorted(p for p in self.packs() if p == 'lab.new.pack'))

    def test_a_new_language_gets_a_declaration_and_a_second_pending_pack_cannot_redeclare_it(self):
        lang = dict(code='vi', caption='Tiếng Việt', englishName='Vietnamese', script='Latn', fallback=['en'])
        self.create(pack='lab.vi.one', language=lang)
        declaration = __import__('json').loads((self.path / 'lab.vi.one' / 'language.json').read_text(encoding='utf-8'))
        self.assertEqual(dict(schema=1, code='vi', caption='Tiếng Việt', englishName='Vietnamese', script='latn', fallback=['en']), declaration)
        self.refused('conflict', 409, body=dict(operationId='2' * 32, id='lab.vi.two', name='X', language=lang))

    def test_the_precedence_fixture_makes_another_pack_win_and_save_names_the_winner(self):
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64, precedence_fixture=True)
        base = self.root + '/languages/th/lines/'
        two = self.call('GET', base + 'sample_02')
        self.assertEqual((OVERRIDE, True), (two['subtitlePack'], two['ownedSubtitle'] is not None))
        three = self.call('GET', base + 'sample_03')
        self.assertEqual((OVERRIDE, True), (three['dubPack'], three['ownedDub']))
        four = self.call('GET', base + 'sample_04')
        self.assertEqual((OVERRIDE, False), (four['dubPack'], four['ownedDub']))
        saved = self.call('PUT', base + 'sample_02/subtitle',
                          dict(expectedRevision=two['revision'], operationId='d' * 32, text='mine'))
        self.assertEqual(OVERRIDE, saved['effectivePackId'])
        self.assertEqual(OVERRIDE, self.packs()[OVERRIDE]['id'])
        self.assertFalse(self.packs()[OVERRIDE]['studioEditable'])

    def test_effectivePackId_names_the_selected_pack_when_its_own_save_wins_and_null_when_none_has_text(self):
        line = self.root + '/languages/th/lines/sample_10'
        row = self.call('GET', line)
        first = self.call('PUT', line + '/subtitle', dict(expectedRevision=row['revision'], operationId='e' * 32, text='hello'))
        self.assertEqual(EDITABLE_PACK_ID, first['effectivePackId'])
        row = self.call('GET', line)
        removed = self.call('PUT', line + '/subtitle', dict(expectedRevision=row['revision'], operationId='f' * 32, text=None))
        self.assertIsNone(removed['effectivePackId'])

    def test_game_language_and_language_fixtures(self):
        for code, is_pack in (('en', False), ('ja', False), ('pt-br', False), ('th', True), ('vi', True)):
            state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64, game_language=code)
            status = state.studio.handle(Request(), '/v1/maker/studio/status', {})
            self.assertEqual((code, is_pack), (status['gameLanguage'], status['gameLanguageIsPack']))
        state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64, language_fixture=True, game_language='en')
        packs = {p['id']: p for p in state.studio.handle(Request(), '/v1/maker/studio/packs', {})['packs']}
        self.assertEqual(['en', 'th'], packs[EDITABLE_PACK_ID]['languages'])
        line = state.studio.handle(Request(), self.root + '/languages/en/lines/sample_01', {})
        self.assertEqual('Good morning. Did you sleep well?', line['ownedSubtitle'])

    def test_http_creation_is_201_and_a_replay_is_201(self):
        server = MakerHTTPServer(('127.0.0.1', 0), Handler, self.state)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            client = HttpClient('127.0.0.1', server.server_address[1], 't' * 64)
            body = dict(operationId='7' * 32, id='lab.http.pack', name='Over HTTP', language=dict(code='th'))
            first = client.request('POST', '/v1/maker/studio/packs', json_body=body)
            again = client.request('POST', '/v1/maker/studio/packs', json_body=body)
            self.assertEqual((201, 201), (first[0], again[0]))
            self.assertEqual(first[3], again[3])
            self.assertEqual(409, client.request('POST', '/v1/maker/studio/packs', json_body=dict(body, name='Else'))[0])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_sub_play_without_dub_uses_original_duration_and_selected_subtitle(self):
        line = self.root + '/languages/th/lines/sample_04'
        row = self.call('GET', line)
        saved = self.call('PUT', line + '/subtitle', dict(expectedRevision=row['revision'], operationId='c' * 32, text='My subtitle'))
        played = self.call('POST', '/v1/maker/studio/play', dict(id='sample_04', variant='sub', lang='th', packId=EDITABLE_PACK_ID, expectedRevision=saved['revision']))
        self.assertTrue(played['live'])
        self.assertFalse(played['saved'])
        self.assertEqual(self.state.lines_by_id['sample_04']['durationSec'], played['durationSec'])
        self.assertIn('original voice', played['note'])
        spoken = list(self.state.events)[-1]['data']
        self.assertEqual('My subtitle', spoken['text'])
        self.assertEqual('My subtitle', spoken['textJa'])
        self.refused('read_only_pack', 403, path='/v1/maker/studio/play', body=dict(id='sample_04', variant='sub', lang='th', packId=READONLY_PACK_ID))

    def test_studio_play_previews_selected_pack_names_winner_and_enforces_refusals(self):
        self.assertIn('playback', self.call('GET', '/v1/maker/studio/capabilities')['features'])
        row = self.call('GET', self.root + '/languages/th/lines/sample_01')
        orig = self.call('POST', '/v1/maker/studio/play',
                         dict(id='sample_01', variant='original', lang='th', packId=EDITABLE_PACK_ID, expectedRevision=row['revision']))
        self.assertEqual((row['revision'], '', False, True, None, EDITABLE_PACK_ID, False),
                         (orig['revision'], orig['operationId'], orig['saved'], orig['live'], orig['path'], orig['effectivePackId'], orig['restartRequired']))
        dub = self.call('POST', '/v1/maker/studio/play',
                        dict(id='sample_01', variant='dub', lang='th', packId=EDITABLE_PACK_ID, expectedRevision=row['revision']))
        self.assertEqual((False, True, EDITABLE_PACK_ID), (dub['saved'], dub['live'], dub['effectivePackId']))

        # Precedence fixture: sample_03 has both an editable-pack dub and an override-pack dub; sample_04 has only override-pack dub.
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64, precedence_fixture=True)
        three = self.call('GET', self.root + '/languages/th/lines/sample_03')
        preview = self.call('POST', '/v1/maker/studio/play',
                            dict(id='sample_03', variant='dub', lang='th', packId=EDITABLE_PACK_ID, expectedRevision=three['revision']))
        self.assertEqual(OVERRIDE, preview['effectivePackId'])
        self.assertIn(OVERRIDE, preview['note'])
        self.refused('not_found', 404, path='/v1/maker/studio/play',
                     body=dict(id='sample_04', variant='dub', lang='th', packId=EDITABLE_PACK_ID))
        self.refused('conflict', 409, path='/v1/maker/studio/play',
                     body=dict(id='sample_03', variant='dub', lang='th', packId=EDITABLE_PACK_ID, expectedRevision='stale'))
        self.refused('bad_request', 400, path='/v1/maker/studio/play',
                     body=dict(id='sample_03', variant='dub', lang='th', expectedRevision=three['revision']))

        self.state.studio.activity['paused'] = True
        self.refused('game_paused', 409, path='/v1/maker/studio/play',
                     body=dict(id='sample_03', variant='original', lang='th'))
        self.state.studio.activity['paused'] = False
        self.state.studio.activity['busy'] = True
        self.refused('not_ready', 409, path='/v1/maker/studio/play',
                     body=dict(id='sample_03', variant='original', lang='th'))

    def test_client_log_is_accepted_and_dropped_with_trace_off(self):
        entries = [dict(kind='api', route='studioLines', status=503, ms=3012.2)]
        self.assertEqual(dict(accepted=0, dropped=1, trace=False), self.call('POST', '/v1/maker/studio/client-log', dict(entries=entries)))
        self.refused('not_found', 404, method='GET', path='/v1/maker/studio/client-log')
        self.refused('bad_request', 400, path='/v1/maker/studio/client-log', body=dict(entries='x'))
        self.refused('too_large', 413, path='/v1/maker/studio/client-log', body=dict(entries=entries * 101))


def make_png(width, height):
    """A real (tiny, grey) PNG, stdlib only."""
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    rows = b''.join(b'\x00' + b'\x80' * width for _ in range(height))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 0, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


class StudioFakeExportTests(unittest.TestCase):
    """Contract v2.3: pack info, icon, export check / export / download."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='cwy-v2-fake-export-')
        self.path = pathlib.Path(self.temp.name)
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64)
        self.root = '/v1/maker/studio/packs/' + EDITABLE_PACK_ID

    def tearDown(self):
        self.temp.cleanup()

    def call(self, method, path, body=None, png=None):
        return self.state.studio.handle(Request(method, body, png=png), path, {})

    def refused(self, code, status, method, path, body=None, png=None):
        with self.assertRaises(ApiError) as caught:
            self.call(method, path, body, png)
        self.assertEqual((status, code), (caught.exception.status, caught.exception.code))

    def export(self, op='1', **changes):
        body = dict(operationId=op * 32, expectedVersion='0.1.0', bump='patch', notes='Fixed two subtitles.')
        return self.call('POST', self.root + '/export', dict(body, **changes))

    def test_export_check_lists_every_check_and_the_next_versions(self):
        self.assertIn('export', self.call('GET', '/v1/maker/studio/capabilities')['features'])
        preview = self.call('GET', self.root + '/export')
        self.assertEqual(['manifest', 'subtitles', 'audio', 'provenance', 'size', 'progress', 'links'],
                         [c['id'] for c in preview['checks']])
        self.assertEqual(('0.1.0', dict(patch='0.1.1', minor='0.2.0', major='1.0.0'), True),
                         (preview['version'], preview['nextVersions'], preview['ok']))
        self.assertEqual('warning', next(c for c in preview['checks'] if c['id'] == 'provenance')['severity'])
        self.assertTrue(all(len(c['paths']) <= 20 for c in preview['checks']))
        self.assertIsNone(preview['lastExport'])
        self.refused('read_only_pack', 403, 'GET', '/v1/maker/studio/packs/' + READONLY_PACK_ID + '/export')

    def test_export_bumps_the_manifest_prepends_the_changelog_zips_and_replays(self):
        result = self.export()
        self.assertIsInstance(result, Created)
        self.assertEqual(('0.1.1', '0.1.0', EDITABLE_PACK_ID + '-v0.1.1.zip', True, True),
                         (result['version'], result['previousVersion'], result['fileName'],
                          result['manifestUpdated'], result['changelogUpdated']))
        pack = self.path / EDITABLE_PACK_ID
        self.assertEqual('0.1.1', json.loads((pack / 'manifest.json').read_text())['version'])
        self.assertTrue((pack / 'CHANGELOG.md').read_text().startswith('## 0.1.1 ('))
        self.assertEqual(result, self.export())  # the receipt, not a second export
        self.refused('conflict', 409, 'POST', self.root + '/export',
                     dict(operationId='1' * 32, expectedVersion='0.1.0', bump='minor', notes=None))
        self.refused('conflict', 409, 'POST', self.root + '/export',
                     dict(operationId='2' * 32, expectedVersion='0.1.0', bump='patch', notes=None))
        raw = self.call('GET', self.root + '/exports/' + result['fileName'])
        self.assertIsInstance(raw, Binary)
        self.assertEqual('application/zip', raw.content_type)
        self.assertEqual(result['sha256'], hashlib.sha256(raw).hexdigest())
        names = zipfile.ZipFile(io.BytesIO(raw)).namelist()
        self.assertEqual(result['files'], len(names))
        self.assertTrue(names and all(n.startswith(EDITABLE_PACK_ID + '/') for n in names), names)
        self.assertFalse([n for n in names if '/.' in n or '_maker_backup' in n], names)
        self.assertIn(EDITABLE_PACK_ID + '/CHANGELOG.md', names)
        again = self.export('3', expectedVersion='0.1.1', bump='none', notes='Replaced notes.')
        log = (pack / 'CHANGELOG.md').read_text()
        self.assertEqual(('0.1.1', 1), (again['version'], log.count('## 0.1.1 (')))
        self.assertIn('Replaced notes.', log)
        self.assertEqual(again['fileName'], self.call('GET', self.root + '/export')['lastExport']['fileName'])
        self.refused('not_found', 404, 'GET', self.root + '/exports/' + EDITABLE_PACK_ID + '-v9.9.9.zip')

    def test_an_error_check_blocks_the_export(self):
        bad = self.path / EDITABLE_PACK_ID / 'audio' / 'voice' / 'th' / 'broken.wav'
        bad.write_bytes(b'')
        preview = self.call('GET', self.root + '/export')
        self.assertFalse(preview['ok'])
        self.assertIn('audio/voice/th/broken.wav', next(c for c in preview['checks'] if c['id'] == 'audio')['paths'])
        self.refused('export_blocked', 409, 'POST', self.root + '/export',
                     dict(operationId='1' * 32, expectedVersion='0.1.0', bump='patch', notes=None))
        self.refused('bad_request', 400, 'POST', self.root + '/export',
                     dict(operationId='1' * 32, expectedVersion='0.1.0', bump='huge', notes=None))

    def test_icon_upload_read_backup_and_refusals(self):
        saved = self.call('PUT', self.root + '/icon', png=make_png(64, 64))
        self.assertEqual((True, 64, 64), (saved['hasIcon'], saved['width'], saved['height']))
        self.assertEqual(make_png(64, 64), self.call('GET', self.root + '/icon'))
        self.call('PUT', self.root + '/icon', png=make_png(128, 128))
        self.assertEqual(1, len(list((self.path / EDITABLE_PACK_ID / '_maker_backup').glob('icon-*.png'))))
        self.assertTrue(self.call('GET', self.root + '/info')['hasIcon'])
        self.refused('unsupported_media_type', 415, 'PUT', self.root + '/icon', png=make_png(64, 65))
        self.refused('unsupported_media_type', 415, 'PUT', self.root + '/icon', png=make_png(32, 32))
        self.refused('unsupported_media_type', 415, 'PUT', self.root + '/icon', png=b'GIF89a' + b'0' * 40)
        reference = '/v1/maker/studio/packs/' + READONLY_PACK_ID
        self.refused('read_only_pack', 403, 'PUT', reference + '/icon', png=make_png(64, 64))
        self.refused('read_only_pack', 403, 'GET', reference + '/icon')
        self.refused('not_found', 404, 'GET', '/v1/maker/studio/packs/' + OVERRIDE_PACK_ID + '/icon')

    def test_a_pending_pack_answers_info_and_takes_an_icon_but_export_is_restart_required(self):
        self.call('POST', '/v1/maker/studio/packs', dict(operationId='5' * 32, id='lab.icon.pack', name='Lab pack',
                                                          author='Lab', description='An invented pack.', language=dict(code='th')))
        base = '/v1/maker/studio/packs/lab.icon.pack'
        info = self.call('GET', base + '/info')
        self.assertEqual(('Lab', 'An invented pack.', True, False, None),
                         (info['author'], info['description'], info['pending'], info['hasIcon'], info['changelog']))
        self.call('PUT', base + '/icon', png=make_png(64, 64))
        self.assertTrue(self.call('GET', base + '/info')['hasIcon'])
        self.refused('restart_required', 409, 'GET', base + '/export')
        self.refused('bad_request', 400, 'POST', '/v1/maker/studio/packs', dict(
            operationId='6' * 32, id='lab.other', name='Lab', description='x' * 501, language=dict(code='th')))

    def test_http_download_and_icon_media_types(self):
        result = self.export()
        server = MakerHTTPServer(('127.0.0.1', 0), Handler, self.state)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            client = HttpClient('127.0.0.1', server.server_address[1], 't' * 64)
            status, headers, raw, _ = client.request('GET', self.root + '/exports/' + result['fileName'])
            self.assertEqual((200, 'application/zip', 'no-store'), (status, headers['content-type'], headers['cache-control']))
            self.assertIn('attachment', headers['content-disposition'])
            self.assertEqual(result['sha256'], hashlib.sha256(raw).hexdigest())
            status, _, _, body = client.request('PUT', self.root + '/icon', body=make_png(64, 64), headers={'Content-Type': 'image/png'})
            self.assertEqual((200, True), (status, body['hasIcon']))
            status, headers, raw, _ = client.request('GET', self.root + '/icon')
            self.assertEqual((200, 'image/png', make_png(64, 64)), (status, headers['content-type'], raw))
            status, _, _, body = client.request('PUT', self.root + '/icon', body=make_png(64, 64), headers={'Content-Type': 'image/gif'})
            self.assertEqual((415, 'unsupported_media_type'), (status, body['error']['code']))
            status, _, _, body = client.request('PUT', self.root + '/icon', body=b'0' * (512 * 1024 + 1), headers={'Content-Type': 'image/png'})
            self.assertEqual((413, 'too_large'), (status, body['error']['code']))
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


class StudioFakeVoiceDefaultTests(unittest.TestCase):
    """Contract v2.4: voice origin + consent once per pack (GET/PUT P/voice) and take-create inheritance."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='cwy-v2-fake-voice-')
        self.path = pathlib.Path(self.temp.name)
        self.state = FakeLoaderState(pathlib.Path(__file__).parent.parent, self.path, 't' * 64)
        self.root = '/v1/maker/studio/packs/' + EDITABLE_PACK_ID
        self.takes = self.root + '/languages/th/lines/sample_01/takes'

    def tearDown(self):
        self.temp.cleanup()

    def call(self, method, path, body=None, query=None, wav=None):
        return self.state.studio.handle(Request(method, body, wav=wav), path, query or {})

    def refused(self, code, status, method, path, body=None):
        with self.assertRaises(ApiError) as caught:
            self.call(method, path, body)
        self.assertEqual((status, code), (caught.exception.status, caught.exception.code))
        return caught.exception

    def put(self, op, origin='own', expected=None, root=None, confirmed=True):
        expected = expected or self.call('GET', (root or self.root) + '/voice')['revision']
        consent = None if origin is None else dict(confirmed=confirmed, statementVersion='consent-1')
        return self.call('PUT', (root or self.root) + '/voice', dict(expectedRevision=expected, operationId=op * 32,
                                                                      origin=origin, consent=consent))

    def create_take(self, op, **origin):
        revision = self.call('GET', self.takes)['revision']
        return self.call('POST', self.takes, dict(expectedRevision=revision, operationId=op * 32, **origin))

    def test_voice_default_is_advertised_and_starts_unset_at_a_stable_revision(self):
        self.assertIn('voiceDefault', self.call('GET', '/v1/maker/studio/capabilities')['features'])
        first = self.call('GET', self.root + '/voice')
        self.assertEqual(['revision', 'origin', 'consent', 'updatedAt'], list(first))
        self.assertEqual((None, None, None), (first['origin'], first['consent'], first['updatedAt']))
        self.assertEqual(first, self.call('GET', self.root + '/voice'))
        self.assertFalse((self.path / EDITABLE_PACK_ID / '.maker' / 'voice.json').exists())

    def test_voice_default_set_identical_put_clear_replay_and_conflicts(self):
        initial = self.call('GET', self.root + '/voice')['revision']
        saved = self.put('1', 'ai-assisted', initial)
        self.assertEqual(('ai-assisted', True, 'consent-1'), (saved['origin'], saved['consent']['confirmed'],
                                                              saved['consent']['statementVersion']))
        self.assertNotEqual(initial, saved['revision'])
        self.assertEqual(saved, self.call('GET', self.root + '/voice'))
        stored = json.loads((self.path / EDITABLE_PACK_ID / '.maker' / 'voice.json').read_text(encoding='utf-8'))
        self.assertEqual(dict(schema=1, revision=saved['revision'], origin='ai-assisted', consent=saved['consent'],
                              updatedAt=saved['updatedAt']), stored)
        self.assertEqual(saved, self.put('1', 'ai-assisted', initial))  # replay returns the receipt
        self.refused('conflict', 409, 'PUT', self.root + '/voice', dict(
            expectedRevision=initial, operationId='1' * 32, origin='own', consent=dict(confirmed=True, statementVersion='consent-1')))
        self.refused('conflict', 409, 'PUT', self.root + '/voice', dict(
            expectedRevision=initial, operationId='2' * 32, origin='own', consent=dict(confirmed=True, statementVersion='consent-1')))
        same = self.put('3', 'ai-assisted', saved['revision'])  # identical: no-op, confirmedAt kept
        self.assertEqual(saved, same)
        cleared = self.put('4', None, saved['revision'])
        self.assertEqual((None, None), (cleared['origin'], cleared['consent']))
        self.assertNotIn(cleared['revision'], (initial, saved['revision']))
        stored = json.loads((self.path / EDITABLE_PACK_ID / '.maker' / 'voice.json').read_text(encoding='utf-8'))
        self.assertEqual((1, None, None, cleared['revision']), (stored['schema'], stored['origin'], stored['consent'], stored['revision']))
        receipt = self.call('GET', self.root + '/operations/' + '4' * 32)
        self.assertEqual(('committed', cleared['revision'], True), (receipt['state'], receipt['revision'], receipt['saved']))
        self.assertFalse(self.call('GET', self.root + '/operations/' + '3' * 32)['saved'])

    def test_voice_default_body_is_validated_before_read_only_and_pack_state_refusals(self):
        revision = self.call('GET', self.root + '/voice')['revision']
        good = dict(expectedRevision=revision, operationId='5' * 32, origin='own', consent=dict(confirmed=True, statementVersion='consent-1'))
        for bad in (dict(good, origin=None), dict(good, consent=None), dict(good, origin='robot'),
                    dict(good, consent=dict(confirmed=False, statementVersion='consent-1')),
                    dict(good, consent=dict(confirmed=1, statementVersion='consent-1')),
                    dict(good, consent=dict(confirmed=True, statementVersion='consent-2')),
                    dict(good, consent=dict(confirmed=True, statementVersion='consent-1', extra=1)),
                    dict(good, extra=True), {k: v for k, v in good.items() if k != 'origin'}, dict(good, operationId='X')):
            self.refused('bad_request', 400, 'PUT', self.root + '/voice', bad)
        reference = '/v1/maker/studio/packs/' + READONLY_PACK_ID + '/voice'
        self.refused('bad_request', 400, 'PUT', reference, dict(good, origin='robot'))
        self.refused('read_only_pack', 403, 'PUT', reference, good)
        self.assertIsNone(self.call('GET', reference)['origin'])
        self.refused('not_found', 404, 'GET', '/v1/maker/studio/packs/no-such-pack/voice')
        self.refused('not_found', 404, 'DELETE', self.root + '/voice')
        data = self.state.studio.load(EDITABLE_PACK_ID)
        data['blocked'] = dict(resource='.maker/voice.json', operationId='a' * 32)
        self.state.studio.save(EDITABLE_PACK_ID, data)
        self.refused('recovery_required', 503, 'PUT', self.root + '/voice', good)
        self.call('POST', '/v1/maker/studio/packs', dict(operationId='6' * 32, id='lab.voice.pack', name='Lab', language=dict(code='th')))
        self.refused('restart_required', 409, 'GET', '/v1/maker/studio/packs/lab.voice.pack/voice')
        self.refused('restart_required', 409, 'PUT', '/v1/maker/studio/packs/lab.voice.pack/voice', good)

    def test_take_without_origin_and_consent_needs_a_default_then_inherits_it(self):
        error = self.refused('consent_required', 403, 'POST', self.takes, dict(
            expectedRevision=self.call('GET', self.takes)['revision'], operationId='1' * 32))
        self.assertIn("voice origin and consent first", error.message)
        revision = self.call('GET', self.takes)['revision']
        self.refused('bad_request', 400, 'POST', self.takes, dict(expectedRevision=revision, operationId='2' * 32, origin='own'))
        self.refused('bad_request', 400, 'POST', self.takes, dict(
            expectedRevision=revision, operationId='2' * 32, consent=dict(confirmed=True, statementVersion='consent-1')))
        default = self.put('3', 'ai-generated')
        status, created = self.create_take('4')
        self.assertEqual(201, status)
        take = created['take']
        self.assertEqual(('ai-generated', default['consent'], 'pack'), (take['origin'], take['consent'], take['consentSource']))
        changed = self.put('5', 'own')  # v2.5: a later default change is followed (the v2.5 tests below cover the details)
        listed = self.call('GET', self.takes)['items'][0]
        self.assertEqual(('own', changed['consent'], 'pack'), (listed['origin'], listed['consent'], listed['consentSource']))
        _, explicit = self.create_take('6', origin='own', consent=dict(confirmed=False, statementVersion='consent-1'))
        self.assertEqual(('take', False), (explicit['take']['consentSource'], explicit['take']['consent']['confirmed']))
        self.put('7', None)
        self.refused('consent_required', 403, 'POST', self.takes, dict(
            expectedRevision=self.call('GET', self.takes)['revision'], operationId='8' * 32))

    def test_patch_origin_or_consent_makes_an_inherited_take_a_take_consent(self):
        self.put('1', 'own')
        _, created = self.create_take('2')
        path = self.takes + '/' + created['take']['id']
        trimmed = self.call('PATCH', path, dict(expectedRevision=created['revision'], operationId='3' * 32, trimStartSec=0.1))
        self.assertEqual('pack', trimmed['take']['consentSource'])
        patched = self.call('PATCH', path, dict(expectedRevision=trimmed['revision'], operationId='4' * 32, origin='own'))
        self.assertEqual(('take', 'own', True), (patched['take']['consentSource'], patched['take']['origin'],
                                                  patched['take']['consent']['confirmed']))

    def test_an_inherited_take_publishes_with_the_pack_confirmation_time(self):
        default = self.put('1', 'ai-assisted')
        _, created = self.create_take('2')
        take = created['take']['id']
        audio = self.takes + '/' + take + '/audio'
        source = self.call('PUT', audio, query=dict(kind='source', expectedRevision=created['revision'], operationId='3' * 32),
                           wav=make_sine_wav(0.5, 24000))
        self.call('PUT', audio, query=dict(kind='render', expectedRevision=source['revision'], operationId='4' * 32),
                  wav=make_sine_wav(0.5, 48000))
        page = self.call('GET', self.takes)
        self.call('POST', self.takes.rsplit('/', 1)[0] + '/publish', dict(
            expectedRevision=page['revision'], operationId='5' * 32, expectedClipRevision=page['clipRevision'], takeId=take))
        clips = json.loads((self.path / EDITABLE_PACK_ID / 'voice-provenance.json').read_text(encoding='utf-8'))['clips']
        self.assertEqual([('ai-assisted', 'consent-1', default['consent']['confirmedAt'])],
                         [(c['origin'], c['statementVersion'], c['confirmedAt']) for c in clips])

    # ---- contract v2.5: inheriting takes (consentSource pack) follow every default change ----------------------------

    def line_takes(self, line):
        return self.root + '/languages/th/lines/' + line + '/takes'

    def take_on(self, line, op, **origin):
        revision = self.call('GET', self.line_takes(line))['revision']
        return self.call('POST', self.line_takes(line), dict(expectedRevision=revision, operationId=op * 16, **origin))[1]['take']

    def publish_take(self, line, take_id, op):
        """Source, render and publish take_id on line; op is one hex digit, the operation ids are (op + 1..3) * 16."""
        takes = self.line_takes(line)
        audio = takes + '/' + take_id + '/audio'
        revision = self.call('GET', takes)['revision']
        source = self.call('PUT', audio, query=dict(kind='source', expectedRevision=revision, operationId=(op + '1') * 16),
                           wav=make_sine_wav(0.5, 24000))
        self.call('PUT', audio, query=dict(kind='render', expectedRevision=source['revision'], operationId=(op + '2') * 16),
                  wav=make_sine_wav(0.5, 48000))
        page = self.call('GET', takes)
        self.call('POST', takes.rsplit('/', 1)[0] + '/publish', dict(
            expectedRevision=page['revision'], operationId=(op + '3') * 16, expectedClipRevision=page['clipRevision'], takeId=take_id))

    def provenance(self):
        clips = json.loads((self.path / EDITABLE_PACK_ID / 'voice-provenance.json').read_text(encoding='utf-8'))['clips']
        return {(c['lang'], c['clip']): c for c in clips}

    def pack_events(self, after):
        return [e for e in self.state.events if e['seq'] > after and e['type'] in ('maker.pack.changed', 'maker.take.changed')]

    def test_v25_inheriting_take_follows_a_default_change_and_own_takes_stay(self):
        self.put('1', 'ai-generated')
        inheriting = self.take_on('sample_01', '2a')
        own = self.take_on('sample_01', '2b', origin='own', consent=dict(confirmed=True, statementVersion='consent-1'))
        overridden = self.take_on('sample_01', '2c')
        page = self.call('GET', self.takes)
        self.call('PATCH', self.takes + '/' + overridden['id'], dict(expectedRevision=page['revision'], operationId='2d' * 16,
                                                                     origin='ai-generated'))
        before = {t['id']: t for t in self.call('GET', self.takes)['items']}
        self.assertEqual(('pack', 'take', 'take'), tuple(before[t['id']]['consentSource'] for t in (inheriting, own, overridden)))
        page = self.call('GET', self.takes)
        line = self.call('GET', self.takes.rsplit('/', 1)[0])
        changed = self.put('2', 'own')
        after = self.call('GET', self.takes)
        items = {t['id']: t for t in after['items']}
        self.assertEqual(('own', dict(confirmed=True, statementVersion='consent-1', confirmedAt=changed['consent']['confirmedAt']), 'pack'),
                         (items[inheriting['id']]['origin'], items[inheriting['id']]['consent'], items[inheriting['id']]['consentSource']))
        self.assertEqual(before[inheriting['id']]['updatedAt'], items[inheriting['id']]['updatedAt'])
        self.assertEqual(before[own['id']], items[own['id']])  # own origin/consent: never changes
        self.assertEqual(before[overridden['id']], items[overridden['id']])  # PATCHed with origin: never changes
        self.assertEqual((page['revision'], page['clipRevision']), (after['revision'], after['clipRevision']))
        self.assertEqual(line['revision'], self.call('GET', self.takes.rsplit('/', 1)[0])['revision'])

    def test_v25_published_inheriting_disclosure_follows_and_own_disclosure_stays(self):
        first = self.put('1', 'ai-assisted')
        inheriting = self.take_on('sample_01', '31')
        own = self.take_on('sample_02', '32', origin='ai-generated', consent=dict(confirmed=True, statementVersion='consent-1'))
        self.publish_take('sample_01', inheriting['id'], 'a')
        self.publish_take('sample_02', own['id'], 'b')
        path = self.path / EDITABLE_PACK_ID / 'voice-provenance.json'
        before = self.provenance()
        self.assertEqual(('ai-assisted', first['consent']['confirmedAt']),
                         (before[('th', 'vo_sample_01')]['origin'], before[('th', 'vo_sample_01')]['confirmedAt']))
        pages = [self.call('GET', self.line_takes(x)) for x in ('sample_01', 'sample_02')]
        changed = self.put('2', 'own')
        after = self.provenance()
        followed, kept = after[('th', 'vo_sample_01')], before[('th', 'vo_sample_01')]
        self.assertEqual(('own', 'consent-1', changed['consent']['confirmedAt']),
                         (followed['origin'], followed['statementVersion'], followed['confirmedAt']))
        self.assertEqual((kept['sha256'], kept['publishedAt']), (followed['sha256'], followed['publishedAt']))
        self.assertEqual(list(kept), list(followed))  # same members in the same order
        self.assertEqual(list(before[('th', 'vo_sample_02')].items()), list(after[('th', 'vo_sample_02')].items()))
        own_entry = json.dumps(before[('th', 'vo_sample_02')], ensure_ascii=False, indent=2)
        self.assertIn('\n'.join('    ' + x for x in own_entry.splitlines()), path.read_text(encoding='utf-8'))  # byte-identical
        self.assertEqual([(p['revision'], p['clipRevision'], p['publishedTakeId']) for p in pages],
                         [(p['revision'], p['clipRevision'], p['publishedTakeId'])
                          for p in (self.call('GET', self.line_takes(x)) for x in ('sample_01', 'sample_02'))])

    def test_v251_follow_pack_default_relinks_an_own_take_and_its_published_disclosure(self):
        self.put('1', 'ai-assisted')
        own = self.take_on('sample_02', '51', origin='ai-generated', consent=dict(confirmed=True, statementVersion='consent-1'))
        self.publish_take('sample_02', own['id'], 'd')
        before = self.provenance()[('th', 'vo_sample_02')]
        path = self.line_takes('sample_02') + '/' + own['id']
        revision = self.call('GET', self.line_takes('sample_02'))['revision']
        body = dict(expectedRevision=revision, operationId='e' * 32)
        self.refused('bad_request', 400, 'PATCH', path, dict(body, followPackDefault=False))
        self.refused('bad_request', 400, 'PATCH', path, dict(body, followPackDefault=True, origin='own'))
        result = self.call('PATCH', path, dict(expectedRevision=revision, operationId='f' * 32, followPackDefault=True))
        saved = result[1] if isinstance(result, tuple) else result
        default = self.call('GET', self.root + '/voice')
        self.assertEqual(('ai-assisted', default['consent'], 'pack'),
                         (saved['take']['origin'], saved['take']['consent'], saved['take']['consentSource']))
        after = self.provenance()[('th', 'vo_sample_02')]
        self.assertEqual(('ai-assisted', default['consent']['confirmedAt'], before['sha256'], before['publishedAt']),
                         tuple(after[k] for k in ('origin', 'confirmedAt', 'sha256', 'publishedAt')))
        changed = self.put('2', 'own')  # it follows later changes again
        self.assertEqual(('own', changed['consent']['confirmedAt']),
                         tuple(self.provenance()[('th', 'vo_sample_02')][k] for k in ('origin', 'confirmedAt')))

    def test_v251_follow_pack_default_needs_a_default(self):
        own = self.take_on('sample_01', '61', origin='own', consent=dict(confirmed=True, statementVersion='consent-1'))
        revision = self.call('GET', self.line_takes('sample_01'))['revision']
        self.refused('consent_required', 403, 'PATCH', self.line_takes('sample_01') + '/' + own['id'],
                     dict(expectedRevision=revision, operationId='a' * 32, followPackDefault=True))

    def test_v25_clearing_keeps_inheriting_takes_at_the_last_default_until_the_next_set(self):
        last = self.put('1', 'ai-assisted')
        take = self.take_on('sample_01', '41')
        self.publish_take('sample_01', take['id'], 'c')
        disclosure = self.provenance()[('th', 'vo_sample_01')]
        self.put('2', None)
        listed = self.call('GET', self.takes)['items'][0]
        self.assertEqual(('ai-assisted', last['consent'], 'pack'), (listed['origin'], listed['consent'], listed['consentSource']))
        self.assertEqual(disclosure, self.provenance()[('th', 'vo_sample_01')])
        again = self.put('3', 'ai-generated')
        listed = self.call('GET', self.takes)['items'][0]
        self.assertEqual(('ai-generated', again['consent'], 'pack'), (listed['origin'], listed['consent'], listed['consentSource']))
        self.assertEqual(('ai-generated', again['consent']['confirmedAt'], disclosure['sha256']),
                         tuple(self.provenance()[('th', 'vo_sample_01')][k] for k in ('origin', 'confirmedAt', 'sha256')))

    def test_v25_a_changing_put_emits_one_pack_changed_event_and_a_no_op_emits_none(self):
        first = self.put('1', 'own')
        self.take_on('sample_01', '51')
        start = self.state.seq
        changed = self.put('2', 'ai-generated', first['revision'])
        self.assertEqual([('maker.pack.changed', dict(packId=EDITABLE_PACK_ID, reason='voice-default'))],
                         [(e['type'], e['data']) for e in self.pack_events(start)])  # no maker.take.changed
        start = self.state.seq
        self.assertEqual(changed, self.put('3', 'ai-generated', changed['revision']))  # identical: no-op
        self.assertEqual(changed, self.put('2', 'ai-generated', first['revision']))  # operationId replay: the receipt
        self.assertEqual([], self.pack_events(start))
        cleared = self.put('4', None)
        self.assertEqual([dict(packId=EDITABLE_PACK_ID, reason='voice-default')], [e['data'] for e in self.pack_events(start)])
        start = self.state.seq
        self.put('5', None, cleared['revision'])  # a clear while unset: no-op
        self.assertEqual([], self.pack_events(start))

    def test_http_voice_routes_round_trip(self):
        server = MakerHTTPServer(('127.0.0.1', 0), Handler, self.state)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            client = HttpClient('127.0.0.1', server.server_address[1], 't' * 64)
            status, headers, _, initial = client.request('GET', self.root + '/voice')
            self.assertEqual((200, 'no-store', None), (status, headers['cache-control'], initial['origin']))
            body = dict(expectedRevision=initial['revision'], operationId='9' * 32, origin='own',
                        consent=dict(confirmed=True, statementVersion='consent-1'))
            status, _, _, saved = client.request('PUT', self.root + '/voice', json_body=body)
            self.assertEqual((200, 'own'), (status, saved['origin']))
            status, _, _, error = client.request('PUT', self.root + '/voice', json_body=dict(body, operationId='8' * 32))
            self.assertEqual((409, 'conflict'), (status, error['error']['code']))
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


if __name__ == '__main__':
    unittest.main()
