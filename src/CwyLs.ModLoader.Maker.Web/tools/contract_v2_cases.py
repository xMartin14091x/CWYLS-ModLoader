"""Studio v2 protocol checks (text work, recovery, M5d dubs, v2.3 export, v2.4/v2.5 voice default). Run via contract_test.py --studio."""
import hashlib
import io
import zipfile
import secrets
from urllib.parse import quote
from fake_loader import make_sine_wav


def run_studio_checks(client, suite_type, pack_id=None, create_pack=False):
    suite = suite_type(client)
    s = '/v1/maker/studio'

    def call(method, path, body=None, status=200, code=None, **kwargs):
        actual, headers, raw, data = client.request(method, path, json_body=body, **kwargs)
        assert actual == status, (actual, data)
        assert headers.get('cache-control') == 'no-store'
        assert 'application/json' in headers.get('content-type', '')
        if code: assert data['error']['code'] == code, data
        return data

    def capabilities():
        data = call('GET', s + '/capabilities')
        assert data['contract'] == 2 and data['storageSchema'] == 1
        assert {'catalog', 'workflow', 'subtitles', 'dubs', 'projects', 'playback'} <= set(data['features'])
        assert 'newPacks' in data['features'] or not create_pack
        state = call('GET', s + '/status')
        assert all(k in state for k in ('focused', 'paused', 'busy', 'stale', 'observedAt'))
    suite.check('v2 capabilities and nullable activity', capabilities)

    packs = call('GET', s + '/packs')['packs']
    pack = next(p for p in packs if p['studioEditable'] and (pack_id is None or p['id'] == pack_id))
    p = s + '/packs/' + pack['id']
    lines = p + '/languages/th/lines'
    first = call('GET', lines)
    line_id = first['items'][0]['id']
    line = lines + '/' + line_id

    def read_models():
        assert pack['readOnlyReason'] is None and pack['studioReadOnlyReason'] is None
        page = call('GET', lines + '?limit=2')
        assert len(page['items']) == 2 and page['total'] == first['total']
        row = call('GET', line)
        assert row['ownedSubtitle'] is None or isinstance(row['ownedSubtitle'], str)
        assert type(row['ownedDub']) is bool
        groups = call('GET', p + '/languages/th/groups')
        assert sum(g['progress']['totalLines'] for g in groups['groups']) == groups['overall']['totalLines']
        assert groups['overall']['dubbableLines'] < groups['overall']['totalLines']
        assert all(g['labelKey'] is None or g['labelKey'].startswith('group.') for g in groups['groups'])
    suite.check('v2 pack, paged lines, owned types and group progress', read_models)

    def guards():
        newpacks = 'newPacks' in call('GET', s + '/capabilities')['features']
        call('POST', s + '/packs', {}, status=400 if newpacks else 503, code='bad_request' if newpacks else 'unavailable')
        call('GET', s + '/status', status=401, code='unauthorized', auth=False)
        call('GET', s + '/status', headers={'Origin': 'http://evil.example'}, status=403, code='forbidden_origin')
        call('GET', lines + '?limit=0', status=400, code='bad_request')
        call('GET', lines + '?filter=warnings', status=400, code='bad_request')
        call('GET', line + '?unknown=x', status=400, code='bad_request')
        call('GET', p + '/operations/BAD', status=400, code='bad_request')
        reference = next(x for x in packs if not x['studioEditable'])
        target = s + '/packs/' + reference['id'] + '/languages/th/lines/' + line_id
        revision = call('GET', target)['revision']
        call('PATCH', target + '/workflow', dict(expectedRevision=revision,
             operationId=secrets.token_hex(16), status='done'), status=403, code='read_only_pack')
    suite.check('v2 authentication, read-only and input refusals', guards)

    def workflow():
        row = call('GET', line)
        op = secrets.token_hex(16)
        body = dict(expectedRevision=row['revision'], operationId=op, status='draft', starred=True)
        saved = call('PATCH', line + '/workflow', body)
        assert saved['workflow'] == dict(status='draft', starred=True)
        assert call('PATCH', line + '/workflow', body) == saved
        call('PATCH', line + '/workflow', dict(body, status='done'), status=409, code='conflict')
        call('PATCH', line + '/workflow', dict(body, operationId=secrets.token_hex(16)), status=409, code='conflict')
        call('GET', lines + '?catalogRevision=' + quote(first['catalogRevision']), status=409, code='conflict')
        assert line_id in [x['id'] for x in call('GET', lines + '?filter=starred')['items']]
        assert call('GET', p + '/operations/' + op)['state'] == 'committed'
    suite.check('v2 workflow, stale tabs, receipt replay and paging conflicts', workflow)

    def history():
        old_text = call('GET', line)['ownedSubtitle']
        def save(text):
            revision = call('GET', line)['revision']
            return call('PUT', line + '/subtitle', dict(expectedRevision=revision,
                        operationId=secrets.token_hex(16), text=text))
        try:
            saved = save('ดาวกระดาษ\n🙂')
            assert saved['saved'] is True and saved['live'] is False
            assert call('GET', line)['ownedSubtitle'] == 'ดาวกระดาษ\n🙂'
            before = call('GET', line + '/history')
            save('ดาวกระดาษ\n🙂')
            assert call('GET', line + '/history')['total'] == before['total']
            save(None)
            entries = call('GET', line + '/history')['items']
            assert entries[-1]['text'] is None
            wanted = next(e for e in entries if e['text'] == 'ดาวกระดาษ\n🙂')
            body = dict(expectedRevision=call('GET', line)['revision'], operationId=secrets.token_hex(16))
            result = call('POST', line + '/history/' + wanted['revisionId'] + '/revert', body)
            assert call('POST', line + '/history/' + wanted['revisionId'] + '/revert', body) == result
            assert call('GET', line)['ownedSubtitle'] == wanted['text']
            for i in range(21): save('Revision ' + str(i))
            assert call('GET', line + '/history')['total'] == 20
        finally:
            save(old_text)
    suite.check('v2 Unicode subtitle, null deletion, no-op, revert and history bound', history)

    def glossary():
        path = p + '/glossary/th'
        original = call('GET', path)
        entry = dict(id='paper', sourceJa=None, sourceEn='paper', target='กระดาษ', note=None)
        body = dict(expectedRevision=original['revision'], operationId=secrets.token_hex(16), entries=[entry])
        saved = call('PUT', path, body)
        assert call('PUT', path, body) == saved
        assert call('GET', path)['entries'] == [entry]
        call('PUT', path, dict(body, operationId=secrets.token_hex(16), expectedRevision=saved['revision'],
                              entries=[entry, entry]), status=400, code='bad_request')
        call('PUT', path, dict(expectedRevision=saved['revision'], operationId=secrets.token_hex(16),
                              entries=original['entries']))
    suite.check('v2 glossary persistence, replay and duplicate refusal', glossary)

    def events():
        data = call('GET', '/v1/maker/events?after=0&limit=500')
        kinds = {x['type'] for x in data['events']}
        assert {'maker.workflow.changed', 'maker.history.changed', 'maker.glossary.changed'} <= kinds
        for event in data['events']:
            if event['type'] == 'maker.history.changed':
                assert len(event['data']['revisionId']) == 32
    suite.check('v2 mutation events use the shared scalar ring', events)

    def dubs():
        voiced = next(x for x in call('GET', lines + '?limit=500')['items'] if x['voice'])
        at = lines + '/' + quote(voiced['id'], safe='')
        page = call('GET', at + '/takes')
        assert list(page) == ['revision', 'favouriteTakeId', 'publishedTakeId', 'clipRevision', 'items'], page
        while len(page['items']) >= 3:  # a reused pack: free a slot first
            stale = page['items'][-1]['id']
            call('DELETE', at + '/takes/' + stale + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
            page = call('GET', at + '/takes')
        created = call('POST', at + '/takes', dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16), origin='own',
                                                   consent=dict(confirmed=True, statementVersion='consent-1')), status=201)
        take = created['take']['id']
        audio = at + '/takes/' + take + '/audio'

        def upload(kind, revision, raw, status=200, code=None):
            query = '?kind=' + kind + '&expectedRevision=' + quote(revision) + '&operationId=' + secrets.token_hex(16)
            actual, headers, _, data = client.request('PUT', audio + query, body=raw, headers={'Content-Type': 'audio/wav'})
            assert actual == status and headers.get('cache-control') == 'no-store', (actual, data)
            if code: assert data['error']['code'] == code, data
            return data
        upload('render', created['revision'], make_sine_wav(0.4, 48000), status=404, code='not_found')
        source = upload('source', created['revision'], make_sine_wav(0.4, 24000))
        upload('render', source['revision'], make_sine_wav(0.4, 24000), status=415, code='unsupported_media_type')
        render = upload('render', source['revision'], make_sine_wav(0.4, 48000))
        assert render['take']['renderReady'] is True
        actual, headers, raw, _ = client.request('GET', audio + '?kind=render')
        assert actual == 200 and headers.get('content-type', '').startswith('audio/wav') and headers.get('cache-control') == 'no-store'
        assert headers.get('x-content-type-options') == 'nosniff' and raw[:4] == b'RIFF'
        favourite = call('PUT', at + '/favourite', dict(expectedRevision=render['revision'], operationId=secrets.token_hex(16), takeId=take))
        assert favourite['favouriteTakeId'] == take
        page = call('GET', at + '/takes')
        assert page['publishedTakeId'] is None and page['clipRevision']
        body = dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16), expectedClipRevision=page['clipRevision'], takeId=take)
        call('POST', at + '/publish', dict(body, expectedClipRevision='0' * 32), status=409, code='conflict')
        published = call('POST', at + '/publish', body)
        assert published['publishedTakeId'] == take and published['path'].endswith('/' + voiced['voice'] + '.wav')
        assert set(published) == {'revision', 'operationId', 'saved', 'live', 'path', 'durationSec', 'note',
                                  'publishedTakeId', 'effectivePackId', 'restartRequired'}
        assert call('POST', at + '/publish', body) == published  # the receipt, never applied again
        page = call('GET', at + '/takes')
        assert page['publishedTakeId'] == take
        call('DELETE', at + '/takes/' + take + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16),
             status=409, code='take_in_use')
        removed = call('POST', at + '/unpublish', dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16),
                                                        expectedClipRevision=page['clipRevision']))
        assert removed['publishedTakeId'] is None
        page = call('GET', at + '/takes')
        call('DELETE', at + '/takes/' + take + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
        kinds = {x['type'] for x in call('GET', '/v1/maker/events?after=0&limit=500')['events']}
        assert {'maker.take.changed', 'maker.take.published'} <= kinds
    suite.check('v2 dubs: takes, WAV upload/download, favourite, publish/unpublish, events', dubs)

    def playback():
        voiced = next(x for x in call('GET', lines + '?limit=500')['items'] if x['voice'] and x['ownedDub'])
        row = call('GET', lines + '/' + quote(voiced['id'], safe=''))
        played = call('POST', s + '/play', dict(id=voiced['id'], variant='dub', lang='th',
                                                packId=pack['id'], expectedRevision=row['revision']))
        assert set(played) == {'revision', 'operationId', 'saved', 'live', 'path', 'durationSec', 'note',
                               'publishedTakeId', 'effectivePackId', 'restartRequired'}, played
        assert (played['revision'], played['operationId'], played['saved'], played['live'],
                played['path'], played['restartRequired']) == (row['revision'], '', False, True, None, False), played
        orig = call('POST', s + '/play', dict(id=voiced['id'], variant='original', lang='th',
                                              packId=pack['id'], expectedRevision=row['revision']))
        assert orig['saved'] is False and orig['live'] is True and orig['path'] is None, orig
        call('POST', s + '/play', dict(id=voiced['id'], variant='dub', lang='th',
                                       packId=pack['id'], expectedRevision='stale-revision'), status=409, code='conflict')
        call('POST', s + '/play', dict(id=voiced['id'], variant='dub', lang='th',
                                       expectedRevision=row['revision']), status=400, code='bad_request')
        call('POST', s + '/play', dict(id='no_such_line_999', variant='original', lang='th'), status=404, code='not_found')
    suite.check('v2 playback: original, dub, revision check and refusals', playback)

    def pack_states():
        for item in call('GET', s + '/packs')['packs']:
            assert set(item) == {'id', 'name', 'version', 'languages', 'editable', 'readOnlyReason', 'loaded',
                                 'restartRequired', 'studioEditable', 'studioReadOnlyReason', 'metadataState'}, item
            assert item['metadataState'] in ('ready', 'pending', 'unavailable', 'recovery-required'), item
            assert item['loaded'] or not item['studioEditable'], item
            assert item['restartRequired'] == (item['metadataState'] == 'pending'), item
    suite.check('v2 pack list shape and metadata states', pack_states)
    if any(p['id'] == 'sample-override-pack' for p in packs):
        def precedence():
            base = s + '/packs/' + pack['id'] + '/languages/th/lines/'
            two = call('GET', base + 'sample_02')
            assert two['subtitlePack'] == 'sample-override-pack' and two['ownedSubtitle'] is not None, two
            three = call('GET', base + 'sample_03')
            assert three['dubPack'] == 'sample-override-pack' and three['ownedDub'] is True, three
            four = call('GET', base + 'sample_04')
            assert four['dubPack'] == 'sample-override-pack' and four['ownedDub'] is False, four
            saved = call('PUT', base + 'sample_02/subtitle', dict(expectedRevision=two['revision'],
                         operationId=secrets.token_hex(16), text=two['ownedSubtitle']))
            assert saved['effectivePackId'] == 'sample-override-pack', saved
        suite.check('v2 another pack winning subtitle and dub, and effectivePackId', precedence)
    if create_pack:
        def creation():
            name = 'contract.new.' + secrets.token_hex(4)
            body = dict(operationId=secrets.token_hex(16), id=name, name='Contract test pack', language=dict(code='th'))
            created = call('POST', s + '/packs', body, status=201)
            assert list(created) == ['pack', 'restartRequired', 'live', 'note'] and created['restartRequired'] and not created['live'], created
            entry = created['pack']
            assert (entry['loaded'], entry['studioEditable'], entry['restartRequired'], entry['metadataState']) == (False, False, True, 'pending'), entry
            assert call('POST', s + '/packs', body, status=201) == created
            call('POST', s + '/packs', dict(body, name='Another name'), status=409, code='conflict')
            call('POST', s + '/packs', dict(body, operationId=secrets.token_hex(16)), status=409, code='conflict')
            call('POST', s + '/packs', dict(body, operationId=secrets.token_hex(16), id=name.upper()), status=400, code='bad_request')
            call('POST', s + '/packs', dict(body, operationId=secrets.token_hex(16), id='ab'), status=400, code='bad_request')
            call('POST', s + '/packs', dict(body, operationId=secrets.token_hex(16), id=name + '.en', language=dict(code='en')), status=400, code='bad_request')
            call('POST', s + '/packs', dict(body, operationId=secrets.token_hex(16), id=name + '.x', surprise=1), status=400, code='bad_request')
            listed = next(x for x in call('GET', s + '/packs')['packs'] if x['id'] == name)
            assert listed == entry, (listed, entry)
            base = s + '/packs/' + name
            call('GET', base + '/languages/th/lines', status=409, code='restart_required')
            call('GET', base + '/languages/th/groups', status=409, code='restart_required')
            call('PATCH', base + '/languages/th/lines/' + line_id + '/workflow', dict(expectedRevision='x',
                 operationId=secrets.token_hex(16), status='done'), status=409, code='restart_required')
            kinds = [e['type'] for e in call('GET', '/v1/maker/events?after=0&limit=500')['events']]
            assert 'maker.pack.created' in kinds and 'maker.catalog.changed' in kinds, kinds
        suite.check('v2 pack creation, replay, conflicts and restart_required', creation)
    else:
        print('Pack creation case not run (it leaves a pack folder behind on a real loader; pass --create-pack).')
    if any(p['id'] == 'sample-recovery' for p in packs):
        def recovery():
            blocked = 'a' * 32
            path = s + '/packs/sample-recovery/operations/' + blocked
            preview = call('GET', path + '/recovery')
            assert preview['blockedOperationId'] == blocked
            assert set(preview) == {'recoveryRevision', 'blockedOperationId', 'resources'}
            request = dict(operationId=secrets.token_hex(16),
                           expectedRecoveryRevision=preview['recoveryRevision'], strategy='keep-current')
            call('POST', path + '/resolve', dict(request, strategy='use-intended'), status=400, code='bad_request')
            call('POST', path + '/resolve', dict(request, expectedRecoveryRevision='0' * 32), status=409, code='conflict')
            resolved = call('POST', path + '/resolve', request)
            assert resolved['resolved'] and resolved['live'] is False
            assert resolved['archivePath'].startswith('.maker/recovery/' + blocked + '/')
            assert call('POST', path + '/resolve', request) == resolved
            call('GET', path + '/recovery', status=404, code='not_found')
            state = call('GET', s + '/packs/sample-recovery/languages/th/lines/sample_01')
            call('PATCH', s + '/packs/sample-recovery/languages/th/lines/sample_01/workflow',
                 dict(expectedRevision=state['revision'], operationId=secrets.token_hex(16), status='draft'))
        suite.check('v2 recovery preview, keep-current, replay and unblocked editing', recovery)
    else:
        print('Recovery fixture absent: recovery protocol case not run (use the disposable fake fixture).')
    if 'export' in call('GET', s + '/capabilities')['features']:
        def export():  # contract v2.3: bumps the disposable pack's version and leaves a zip in CWYLS Mods/.maker-exports/
            preview = call('GET', p + '/export')
            assert {c['id'] for c in preview['checks']} == {'manifest', 'subtitles', 'audio', 'provenance', 'size', 'progress', 'links'}, preview
            assert preview['ok'] and preview['errorCount'] == 0, preview['checks']
            body = dict(operationId=secrets.token_hex(16), expectedVersion=preview['version'], bump='patch', notes='Contract test export.')
            result = call('POST', p + '/export', body, status=201)
            assert set(result) == {'packId', 'version', 'previousVersion', 'fileName', 'sizeBytes', 'sha256', 'files', 'downloadable',
                                   'manifestUpdated', 'changelogUpdated', 'warningCount', 'note'}, result
            stem = pack['id'] + '-v' + preview['nextVersions']['patch']  # <packId>-v<version>.zip, or -by-<author> when the id lacks the slug
            assert (result['version'], result['previousVersion']) == (preview['nextVersions']['patch'], preview['version']), result
            assert result['fileName'] == stem + '.zip' or result['fileName'].startswith(stem + '-by-'), result
            assert result['manifestUpdated'] and result['changelogUpdated'] and result['downloadable'], result
            assert call('POST', p + '/export', body, status=201) == result  # the receipt, not a second export
            actual, headers, raw, _ = client.request('GET', p + '/exports/' + quote(result['fileName'], safe=''))
            assert actual == 200 and headers.get('content-type', '').startswith('application/zip'), (actual, headers)
            assert headers.get('cache-control') == 'no-store' and 'attachment' in headers.get('content-disposition', '')
            assert hashlib.sha256(raw).hexdigest() == result['sha256'] and len(raw) == result['sizeBytes']
            names = zipfile.ZipFile(io.BytesIO(raw)).namelist()
            assert names and all(n.startswith(pack['id'] + '/') for n in names), names
            assert not [n for n in names if '.maker/' in n], names
            call('POST', p + '/export', dict(body, operationId=secrets.token_hex(16)), status=409, code='conflict')
        suite.check('v2.3 export: check, patch bump with notes, replay, zip download, stale version', export)
    else:
        print('Export case not run (capabilities lack "export").')
    if 'voiceDefault' in call('GET', s + '/capabilities')['features']:
        def voice_default():  # contract v2.4: sets the disposable pack's voice default, then puts the previous one back
            voiced = next(x for x in call('GET', lines + '?limit=500')['items'] if x['voice'])
            at = lines + '/' + quote(voiced['id'], safe='')
            previous = call('GET', p + '/voice')
            assert set(previous) == {'revision', 'origin', 'consent', 'updatedAt'}, previous

            def put(expected, origin, status=200, code=None, op=None):
                consent = None if origin is None else dict(confirmed=True, statementVersion='consent-1')
                return call('PUT', p + '/voice', dict(expectedRevision=expected, operationId=op or secrets.token_hex(16),
                                                      origin=origin, consent=consent), status=status, code=code)
            op = secrets.token_hex(16)
            saved = put(previous['revision'], 'ai-assisted' if previous['origin'] != 'ai-assisted' else 'own', op=op)
            take = None
            try:
                assert saved['consent']['confirmed'] is True and saved['consent']['statementVersion'] == 'consent-1', saved
                assert saved['revision'] != previous['revision'] and call('GET', p + '/voice') == saved
                assert put(previous['revision'], saved['origin'], op=op) == saved  # the receipt
                put(previous['revision'], 'own', status=409, code='conflict')
                call('PUT', p + '/voice', dict(expectedRevision=saved['revision'], operationId=secrets.token_hex(16),
                                               origin='own', consent=None), status=400, code='bad_request')
                page = call('GET', at + '/takes')
                while len(page['items']) >= 3:  # a reused pack: free a slot first
                    stale = page['items'][-1]['id']
                    call('DELETE', at + '/takes/' + stale + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
                    page = call('GET', at + '/takes')
                call('POST', at + '/takes', dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16), origin='own'),
                     status=400, code='bad_request')
                created = call('POST', at + '/takes', dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16)), status=201)
                take = created['take']['id']
                assert (created['take']['origin'], created['take']['consent'], created['take']['consentSource']) == (
                    saved['origin'], saved['consent'], 'pack'), created
                listed = next(t for t in call('GET', at + '/takes')['items'] if t['id'] == take)
                assert listed['consentSource'] == 'pack', listed
            finally:
                if take:
                    page = call('GET', at + '/takes')
                    call('DELETE', at + '/takes/' + take + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
                current = call('GET', p + '/voice')
                if current['origin'] != previous['origin']:
                    restored = put(current['revision'], previous['origin'])
                    assert restored['origin'] == previous['origin'], restored
            if previous['origin'] is None:
                page = call('GET', at + '/takes')
                call('POST', at + '/takes', dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16)),
                     status=403, code='consent_required')
        suite.check('v2.4 voice default: set, replay, stale, take inherits (consentSource pack), cleared', voice_default)

        def voice_default_follows():  # contract v2.5: changes the disposable pack's voice default, then puts the previous one back
            voiced = next(x for x in call('GET', lines + '?limit=500')['items'] if x['voice'])
            at = lines + '/' + quote(voiced['id'], safe='')
            previous = call('GET', p + '/voice')

            def put(expected, origin):
                consent = None if origin is None else dict(confirmed=True, statementVersion='consent-1')
                return call('PUT', p + '/voice', dict(expectedRevision=expected, operationId=secrets.token_hex(16),
                                                      origin=origin, consent=consent))
            created_ids = []
            try:
                first = previous if previous['origin'] is not None else put(previous['revision'], 'own')
                page = call('GET', at + '/takes')
                while len(page['items']) >= 2:  # a reused pack: free two slots first
                    stale = page['items'][-1]['id']
                    call('DELETE', at + '/takes/' + stale + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
                    page = call('GET', at + '/takes')
                inheriting = call('POST', at + '/takes', dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16)), status=201)
                created_ids.append(inheriting['take']['id'])
                own = call('POST', at + '/takes', dict(expectedRevision=inheriting['revision'], operationId=secrets.token_hex(16), origin='own',
                                                       consent=dict(confirmed=True, statementVersion='consent-1')), status=201)
                created_ids.append(own['take']['id'])
                before = call('GET', at + '/takes')
                own_before = next(t for t in before['items'] if t['id'] == own['take']['id'])
                assert (own_before['origin'], own_before['consentSource']) == ('own', 'take'), own_before
                assert next(t for t in before['items'] if t['id'] == inheriting['take']['id'])['consentSource'] == 'pack', before
                cursor = call('GET', '/v1/maker/events?after=0&wait=0&limit=1').get('latest', 0)
                changed = put(first['revision'], 'ai-generated' if first['origin'] != 'ai-generated' else 'ai-assisted')
                assert changed['origin'] != first['origin'] and changed['revision'] != first['revision'], (first, changed)
                after = call('GET', at + '/takes')
                assert after['revision'] == before['revision'], (before['revision'], after['revision'])  # no take-page change
                followed = next(t for t in after['items'] if t['id'] == inheriting['take']['id'])
                assert (followed['origin'], followed['consentSource']) == (changed['origin'], 'pack'), followed
                assert followed['consent'] == dict(confirmed=True, statementVersion='consent-1',
                                                   confirmedAt=changed['consent']['confirmedAt']), (followed, changed)
                assert next(t for t in after['items'] if t['id'] == own['take']['id']) == own_before, after
                events = call('GET', '/v1/maker/events?after=' + str(cursor) + '&wait=0&limit=500')['events']
                assert any(e['type'] == 'maker.pack.changed' and e['data'].get('packId') == pack['id']
                           and e['data'].get('reason') == 'voice-default' for e in events), events
            finally:
                for take_id in created_ids:
                    page = call('GET', at + '/takes')
                    call('DELETE', at + '/takes/' + take_id + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
                current = call('GET', p + '/voice')
                if current['origin'] != previous['origin']:
                    restored = put(current['revision'], previous['origin'])
                    assert restored['origin'] == previous['origin'], restored
        suite.check('v2.5 voice default follows: inheriting take switches, own-consent take stays, pack.changed event', voice_default_follows)

        def voice_default_relink():  # contract v2.5.1: PATCH T followPackDefault puts an own-consent take back on the default
            voiced = next(x for x in call('GET', lines + '?limit=500')['items'] if x['voice'])
            at = lines + '/' + quote(voiced['id'], safe='')
            previous = call('GET', p + '/voice')
            take = None
            try:
                current = previous
                if previous['origin'] is None:
                    current = call('PUT', p + '/voice', dict(expectedRevision=previous['revision'], operationId=secrets.token_hex(16),
                                                             origin='ai-assisted', consent=dict(confirmed=True, statementVersion='consent-1')))
                page = call('GET', at + '/takes')
                while len(page['items']) >= 3:  # a reused pack: free a slot first
                    stale = page['items'][-1]['id']
                    call('DELETE', at + '/takes/' + stale + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
                    page = call('GET', at + '/takes')
                own_origin = 'own' if current['origin'] != 'own' else 'ai-generated'
                created = call('POST', at + '/takes', dict(expectedRevision=page['revision'], operationId=secrets.token_hex(16), origin=own_origin,
                                                           consent=dict(confirmed=True, statementVersion='consent-1')), status=201)
                take = created['take']['id']
                body = dict(expectedRevision=created['revision'], operationId=secrets.token_hex(16))
                call('PATCH', at + '/takes/' + take, dict(body, followPackDefault=False), status=400, code='bad_request')
                call('PATCH', at + '/takes/' + take, dict(body, followPackDefault=True, origin='own'), status=400, code='bad_request')
                saved = call('PATCH', at + '/takes/' + take, dict(expectedRevision=created['revision'], operationId=secrets.token_hex(16),
                                                                  followPackDefault=True))
                assert (saved['take']['origin'], saved['take']['consent'], saved['take']['consentSource']) == (
                    current['origin'], current['consent'], 'pack'), (saved, current)
                assert saved['revision'] != created['revision'], saved
            finally:
                if take:
                    page = call('GET', at + '/takes')
                    call('DELETE', at + '/takes/' + take + '?expectedRevision=' + quote(page['revision']) + '&operationId=' + secrets.token_hex(16))
                now = call('GET', p + '/voice')
                if now['origin'] != previous['origin']:
                    consent = None if previous['origin'] is None else dict(confirmed=True, statementVersion='consent-1')
                    call('PUT', p + '/voice', dict(expectedRevision=now['revision'], operationId=secrets.token_hex(16),
                                                   origin=previous['origin'], consent=consent))
        suite.check('v2.5.1 re-link: followPackDefault puts an own-consent take back on the pack default', voice_default_relink)
    else:
        print('Voice default case not run (capabilities lack "voiceDefault").')
    suite.print_table()
    return all(ok for _, ok, _ in suite.results)
