#!/usr/bin/env python3
"""Contract test suite for the CWY:LS Dub & Subtitle Studio Maker API (DS-00/DS-05).

Verifies any server (fake_loader.py now, or the live BepInEx loader in DS-07)
against CONTRACT.md. Uses the Python standard library only.

Usage:
    python3 contract_test.py --base http://127.0.0.1:8127 [--token TOKEN]
    python3 contract_test.py   # auto-starts fake_loader.py on an ephemeral port
"""
import argparse
import http.client
import io
import json
import math
import pathlib
import re
import socket
import struct
import subprocess
import sys
import time
import urllib.parse
import wave

META_TOKEN_RE = re.compile(
    r'<meta\s+[^>]*name=["\']cwyls-maker-token["\'][^>]*content=["\']([^"\']+)["\']'
    r'|<meta\s+[^>]*content=["\']([^"\']+)["\'][^>]*name=["\']cwyls-maker-token["\']',
    re.IGNORECASE,
)


def make_pcm16_wav(
    duration_sec: float = 0.5,
    sample_rate: int = 24000,
    channels: int = 1,
    sampwidth: int = 2,
) -> bytes:
    nframes = max(1, int(round(duration_sec * sample_rate)))
    buf = io.BytesIO()
    with wave.open(buf, 'wb') as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(sampwidth)
        wav.setframerate(sample_rate)
        if sampwidth == 2:
            frame = struct.pack('<' + 'h' * channels, *([1200] * channels))
        else:
            frame = bytes([128] * channels * sampwidth)
        wav.writeframes(frame * nframes)
    return buf.getvalue()


ONLY_PACK = None


class HttpClient:
    def __init__(self, host: str, port: int, token: str | None = None):
        self.host = host
        self.port = port
        self.token = token

    @property
    def default_host_header(self) -> str:
        return f'{self.host}:{self.port}'

    def request(
        self,
        method: str,
        path: str,
        *,
        body: bytes | None = None,
        json_body: object | None = None,
        headers: dict[str, str] | None = None,
        auth: bool = True,
        host_header: str | None = None,
        timeout: float = 10.0,
    ) -> tuple[int, dict[str, str], bytes, dict | None]:
        req_headers: dict[str, str] = {
            'Host': host_header if host_header is not None else self.default_host_header,
            'Connection': 'close',
        }
        if auth and self.token:
            req_headers['Authorization'] = f'Bearer {self.token}'
        if json_body is not None:
            body = json.dumps(json_body, ensure_ascii=False).encode('utf-8')
            req_headers['Content-Type'] = 'application/json; charset=utf-8'
        if body is not None and 'Content-Length' not in (headers or {}):
            req_headers['Content-Length'] = str(len(body))
        if headers:
            req_headers.update(headers)

        conn = http.client.HTTPConnection(self.host, self.port, timeout=timeout)
        try:
            conn.putrequest(method, path, skip_host=True, skip_accept_encoding=True)
            for k, v in req_headers.items():
                conn.putheader(k, v)
            conn.endheaders()
            if body:
                try:
                    conn.send(body)
                except (BrokenPipeError, ConnectionResetError):
                    # Server may reject over-limit bodies after inspecting Content-Length.
                    pass
            resp = conn.getresponse()
            raw = resp.read()
            resp_headers = {k.lower(): v for k, v in resp.getheaders()}
            parsed_json = None
            ctype = resp_headers.get('content-type', '').lower()
            if 'application/json' in ctype and raw:
                try:
                    parsed_json = json.loads(raw.decode('utf-8'))
                except Exception:
                    parsed_json = None
            return resp.status, resp_headers, raw, parsed_json
        finally:
            conn.close()


class ContractSuite:
    def __init__(self, client: HttpClient, cli_token: str | None = None):
        self.client = client
        self.cli_token = cli_token
        self.results: list[tuple[str, bool, str]] = []

    def check(self, name: str, fn) -> None:
        try:
            detail = fn()
            self.results.append((name, True, detail or 'OK'))
        except AssertionError as exc:
            self.results.append((name, False, str(exc) or 'Assertion failed'))
        except Exception as exc:
            self.results.append((name, False, f'{type(exc).__name__}: {exc}'))

    @staticmethod
    def assert_error(status: int, data: dict | None, expected_status: int, expected_code: str | None = None) -> None:
        assert status == expected_status, f'Expected HTTP {expected_status}, got {status} ({data})'
        assert isinstance(data, dict) and isinstance(data.get('error'), dict), f'Missing error envelope: {data}'
        err = data['error']
        assert isinstance(err.get('code'), str) and err['code'], f'Missing error.code: {data}'
        assert isinstance(err.get('message'), str) and err['message'], f'Missing error.message: {data}'
        if expected_code is not None:
            assert err['code'] == expected_code, f"Expected error code '{expected_code}', got '{err['code']}'"

    def run_all(self) -> bool:
        # 1. GET /maker redirect
        def test_maker_redirect():
            st, hdrs, _, _ = self.client.request('GET', '/maker', auth=False)
            assert st in (301, 302, 303, 307, 308), f'Expected redirect status on GET /maker, got {st}'
            loc = hdrs.get('location', '')
            assert loc.startswith('/maker/'), f'Expected Location starting with /maker/, got {loc!r}'
            return f'{st} -> {loc}'

        self.check('GET /maker redirects to /maker/', test_maker_redirect)

        # 2. GET /maker/ serves HTML with injected token meta
        def test_maker_index_and_token():
            st, hdrs, raw, _ = self.client.request('GET', '/maker/', auth=False)
            assert st == 200, f'Expected 200 on GET /maker/, got {st}'
            assert 'text/html' in hdrs.get('content-type', '').lower(), f"Expected text/html, got {hdrs.get('content-type')}"
            html = raw.decode('utf-8', errors='replace')
            match = META_TOKEN_RE.search(html)
            assert match, 'Missing <meta name="cwyls-maker-token" content="..."> in /maker/ HTML'
            extracted = (match.group(1) or match.group(2) or '').strip()
            assert len(extracted) >= 16, 'Extracted maker token is unexpectedly short'
            if not self.client.token:
                self.client.token = extracted
            return f'token len={len(self.client.token)}'

        self.check('GET /maker/ serves index.html with token meta', test_maker_index_and_token)

        if not self.client.token and self.cli_token:
            self.client.token = self.cli_token

        # 3. Wrong Host -> 421 host_refused. In the real game (Mono's HttpListener) a Host that matches no listener
        # prefix is refused with a bare 400 before the loader sees it; also a refusal, so accepted.
        def test_wrong_host():
            st, _, _, data = self.client.request(
                'GET',
                '/v1/maker/status',
                host_header=f'evil.example:{self.client.port}',
            )
            if st == 400 and not (isinstance(data, dict) and 'error' in data):
                return '400 from the HTTP server (Mono HttpListener), before the loader'
            self.assert_error(st, data, 421, 'host_refused')
            return '421 host_refused'

        self.check('Rule: wrong Host header -> 421 host_refused', test_wrong_host)

        # 4. localhost:<port> Host accepted
        def test_localhost_host():
            st, _, _, data = self.client.request(
                'GET',
                '/v1/maker/status',
                host_header=f'localhost:{self.client.port}',
            )
            # Contract v1.2: localhost is not promised; either the loader answers, or the HTTP server refuses it first.
            if st in (400, 404) and not (isinstance(data, dict) and 'error' in data):
                return f'{st} from the HTTP server (allowed since contract v1.2)'
            assert st == 200 and isinstance(data, dict), f'Expected 200 or an HTTP-server refusal for localhost, got {st}'
            return f'200 with Host: localhost:{self.client.port}'

        self.check('Rule: Host localhost:<port> answered or refused by the server', test_localhost_host)

        # 5. Other-port Origin -> 403 forbidden_origin
        def test_other_port_origin():
            st, hdrs, _, data = self.client.request(
                'GET',
                '/v1/maker/status',
                headers={'Origin': 'http://127.0.0.1:1'},
            )
            self.assert_error(st, data, 403, 'forbidden_origin')
            assert 'access-control-allow-origin' not in hdrs, 'Must not emit CORS headers'
            return '403 forbidden_origin'

        self.check('Rule: other-port Origin -> 403 forbidden_origin', test_other_port_origin)

        # 6. Cross-site Sec-Fetch-Site -> 403 forbidden_origin
        def test_sec_fetch_site():
            st, hdrs, _, data = self.client.request(
                'GET',
                '/v1/maker/status',
                headers={'Sec-Fetch-Site': 'cross-site'},
            )
            self.assert_error(st, data, 403, 'forbidden_origin')
            assert 'access-control-allow-origin' not in hdrs, 'Must not emit CORS headers'
            return '403 forbidden_origin'

        self.check('Rule: Sec-Fetch-Site: cross-site -> 403 forbidden_origin', test_sec_fetch_site)

        # 7. Same-origin browser request accepted without CORS headers
        def test_same_origin_accepted():
            st, hdrs, _, _ = self.client.request(
                'GET',
                '/v1/maker/status',
                headers={
                    'Origin': f'http://127.0.0.1:{self.client.port}',
                    'Sec-Fetch-Site': 'same-origin',
                },
            )
            assert st == 200, f'Expected 200 for same-origin request, got {st}'
            assert 'access-control-allow-origin' not in hdrs, 'Must not emit CORS headers'
            return '200 (no CORS headers)'

        self.check('Rule: same-origin Origin + Sec-Fetch-Site accepted', test_same_origin_accepted)

        # 7b. Non-maker /v1/* route still refuses browsers -> 403. With [Api] Enabled=false the listener serves Pack
        # Maker only, and every other /v1 route is 404 not_found: closed either way.
        def test_non_maker_v1_refuses_browser():
            st, _, _, data = self.client.request(
                'GET',
                '/v1/status',
                headers={
                    'Origin': f'http://127.0.0.1:{self.client.port}',
                    'Sec-Fetch-Site': 'same-origin',
                },
            )
            if st == 404 and isinstance(data, dict) and 'Pack Maker only' in str(data.get('error', {}).get('message', '')):
                return '404 not_found (maker-only listener: the local game API is off)'
            self.assert_error(st, data, 403)
            return f'403 ({data["error"]["code"]})'

        self.check('Rule: non-maker /v1/* refuses browser requests -> 403', test_non_maker_v1_refuses_browser)

        # 8. Missing Bearer token -> 401 unauthorized
        def test_missing_token():
            st, _, _, data = self.client.request('GET', '/v1/maker/status', auth=False)
            self.assert_error(st, data, 401, 'unauthorized')
            return '401 unauthorized'

        self.check('Rule: missing Bearer token -> 401 unauthorized', test_missing_token)

        # 9. Wrong Bearer token -> 401 unauthorized
        def test_wrong_token():
            st, _, _, data = self.client.request(
                'GET',
                '/v1/maker/status',
                headers={'Authorization': 'Bearer invalid-token-value'},
            )
            self.assert_error(st, data, 401, 'unauthorized')
            return '401 unauthorized'

        self.check('Rule: wrong Bearer token -> 401 unauthorized', test_wrong_token)

        # 10. GET /v1/maker/status response headers & contract fields
        def test_status_route():
            st, hdrs, _, data = self.client.request('GET', '/v1/maker/status')
            assert st == 200 and isinstance(data, dict), f'Expected 200 JSON, got {st}'
            ctype = hdrs.get('content-type', '').lower().replace(' ', '')
            assert ctype == 'application/json;charset=utf-8', f'Expected application/json; charset=utf-8, got {ctype}'
            assert 'no-store' in hdrs.get('cache-control', '').lower(), 'Missing Cache-Control: no-store'
            for str_key in ('gameVersion', 'loaderVersion', 'gameLanguage'):
                assert isinstance(data.get(str_key), str) and data[str_key], f'Invalid {str_key}: {data}'
            for bool_key in ('gameLanguageIsPack', 'roomReady'):
                assert isinstance(data.get(bool_key), bool), f'Invalid {bool_key}: {data}'
            return f"game={data['gameVersion']} lang={data['gameLanguage']} ready={data['roomReady']}"

        self.check('Route: GET /v1/maker/status schema & headers', test_status_route)

        # 11. GET /v1/maker/packs schema & editable/read-only discovery
        editable_pack_id: str | None = None
        readonly_pack_id: str | None = None

        def test_packs_route():
            nonlocal editable_pack_id, readonly_pack_id
            st, _, _, data = self.client.request('GET', '/v1/maker/packs')
            assert st == 200 and isinstance(data, dict), f'Expected 200 JSON, got {st}'
            packs = data.get('packs')
            assert isinstance(packs, list) and len(packs) >= 1, f'Expected non-empty packs list: {data}'
            for p in packs:
                assert isinstance(p.get('id'), str) and p['id'], f'Invalid pack id: {p}'
                assert isinstance(p.get('name'), str) and p['name'], f'Invalid pack name: {p}'
                assert isinstance(p.get('version'), str) and p['version'], f'Invalid pack version: {p}'
                assert isinstance(p.get('languages'), list), f'Invalid pack languages: {p}'
                assert isinstance(p.get('editable'), bool), f'Invalid pack editable: {p}'
                if p['editable']:
                    if ONLY_PACK is None or p['id'] == ONLY_PACK:
                        editable_pack_id = editable_pack_id or p['id']
                else:
                    assert isinstance(p.get('readOnlyReason'), str) and p['readOnlyReason'], (
                        f'Read-only pack must provide readOnlyReason: {p}'
                    )
                    readonly_pack_id = readonly_pack_id or p['id']
            assert editable_pack_id is not None, 'No editable pack found in /v1/maker/packs' + (f' (--pack {ONLY_PACK})' if ONLY_PACK else '')
            assert readonly_pack_id is not None, 'No read-only pack found in /v1/maker/packs'
            return f'editable={editable_pack_id}, readOnly={readonly_pack_id}'

        self.check('Route: GET /v1/maker/packs (editable + read-only)', test_packs_route)

        # 12. GET /v1/maker/lines parameter validation
        def test_lines_validation():
            st1, _, _, d1 = self.client.request('GET', '/v1/maker/lines')
            self.assert_error(st1, d1, 400, 'bad_request')
            st2, _, _, d2 = self.client.request('GET', '/v1/maker/lines?lang=INVALID_LANG')
            self.assert_error(st2, d2, 400, 'bad_request')
            st3, _, _, d3 = self.client.request('GET', '/v1/maker/lines?lang=th&limit=0')
            self.assert_error(st3, d3, 400, 'bad_request')
            st4, _, _, d4 = self.client.request('GET', '/v1/maker/lines?lang=th&limit=501')
            self.assert_error(st4, d4, 400, 'bad_request')
            st5, _, _, d5 = self.client.request('GET', '/v1/maker/lines?lang=th&filter=invalid-filter')
            self.assert_error(st5, d5, 400, 'bad_request')
            return '400 bad_request on invalid lang/limit/filter'

        self.check('Route: GET /v1/maker/lines query validation -> 400', test_lines_validation)

        # 13. GET /v1/maker/lines pagination, search, filters & Line schema
        all_lines: list[dict] = []

        def test_lines_catalog():
            nonlocal all_lines
            st, _, _, data = self.client.request('GET', '/v1/maker/lines?lang=th&offset=0&limit=500')
            assert st == 200 and isinstance(data, dict), f'Expected 200 JSON, got {st}'
            assert isinstance(data.get('total'), int) and data['total'] > 0, f'Expected total > 0: {data}'
            assert data.get('offset') == 0, f'Expected offset 0: {data}'
            items = data.get('items')
            assert isinstance(items, list) and len(items) > 0, 'Expected non-empty items list'
            required_keys = {
                'id', 'voice', 'group', 'durationSec', 'ja', 'en',
                'subtitle', 'subtitlePack', 'dubPack', 'dubDurationSec',
            }
            for item in items:
                missing = required_keys - set(item.keys())
                assert not missing, f'Line missing keys {missing}: {item}'
                assert isinstance(item['id'], str) and item['id']
                assert isinstance(item['ja'], str) and isinstance(item['en'], str)
            all_lines = items

            # Check filters
            for fmode in ('all', 'missing-subtitle', 'missing-dub', 'done'):
                fst, _, _, fdata = self.client.request(f'GET', f'/v1/maker/lines?lang=th&filter={fmode}&limit=100')
                assert fst == 200 and isinstance(fdata, dict), f'Filter {fmode} failed: {fst}'
                for row in fdata.get('items', []):
                    if fmode == 'missing-subtitle':
                        assert not row.get('subtitle'), f'missing-subtitle returned line with subtitle: {row}'
                    elif fmode == 'missing-dub':
                        assert row.get('dubPack') is None, f'missing-dub returned line with dubPack: {row}'
                    elif fmode == 'done':
                        assert row.get('subtitle'), f'done returned line without subtitle: {row}'

            # Check search query q
            target_id = all_lines[0]['id']
            qst, _, _, qdata = self.client.request(
                'GET',
                f'/v1/maker/lines?lang=th&q={urllib.parse.quote(target_id)}&limit=50',
            )
            assert qst == 200 and any(r['id'] == target_id for r in qdata.get('items', [])), (
                f'Search q={target_id} did not return target line'
            )
            return f'total={data["total"]} lines verified across all filters'

        self.check('Route: GET /v1/maker/lines pagination, search & filters', test_lines_catalog)

        # 14. GET /v1/maker/lines/{id} (200 and 404)
        def test_line_by_id():
            assert all_lines, 'No lines loaded from catalog'
            sample_id = all_lines[0]['id']
            st, _, _, data = self.client.request('GET', f'/v1/maker/lines/{sample_id}?lang=th')
            assert st == 200 and isinstance(data, dict) and data.get('id') == sample_id, (
                f'Expected line {sample_id}, got {st} {data}'
            )
            st404, _, _, d404 = self.client.request('GET', '/v1/maker/lines/no_such_line_id_99999?lang=th')
            self.assert_error(st404, d404, 404, 'not_found')
            return f'200 ({sample_id}) & 404 (unknown)'

        self.check('Route: GET /v1/maker/lines/{id} (200 & 404)', test_line_by_id)

        # 15. Path traversal & segment validation -> 400/404
        def test_traversal_and_segments():
            pack = editable_pack_id or 'sample-thai-dub'
            traversal_paths = [
                '/v1/maker/packs/../subtitles/th/sample_01',
                f'/v1/maker/packs/{pack}/voice/th/..%2f..%2fsecret',
                f'/v1/maker/packs/{pack}/subtitles/th/..',
                '/v1/maker/lines/..',
            ]
            for tpath in traversal_paths:
                st, _, _, _ = self.client.request('GET', tpath)
                assert st in (400, 404), f'Traversal path {tpath} returned unexpected HTTP {st}'

            # Invalid segment formats -> 400 bad_request
            bad_segments = [
                ('PUT', '/v1/maker/packs/UPPERCASE_PACK/subtitles/th/sample_01', {'text': 'hi'}),
                ('PUT', f'/v1/maker/packs/{pack}/subtitles/INVALID_LANG/sample_01', {'text': 'hi'}),
                ('PUT', f'/v1/maker/packs/{pack}/subtitles/th/bad$id!', {'text': 'hi'}),
            ]
            for method, bpath, bjson in bad_segments:
                st, _, _, bdata = self.client.request(method, bpath, json_body=bjson)
                self.assert_error(st, bdata, 400, 'bad_request')
            return 'traversal -> 400/404, invalid segments -> 400'

        self.check('Rule: traversal -> 400/404 & invalid segments -> 400', test_traversal_and_segments)

        # 15b. CONTRACT v1.1: parentheses are valid in id/clip (game names like Voice_MainScenario_12(5)_001);
        # an unknown id with them must pass segment validation (404, never 400), percent-encoded ones stay 400.
        def test_parentheses_segments():
            st, _, _, bdata = self.client.request('GET', '/v1/maker/lines/Voice_MainScenario_12(5)_001?lang=th')
            self.assert_error(st, bdata, 404, 'not_found')
            st, _, _, _ = self.client.request('GET', '/v1/maker/lines/Voice_MainScenario_12%285%29_001?lang=th')
            assert st == 400, f'percent-encoded parentheses returned HTTP {st}, expected 400'
            return 'parentheses accepted (404 for unknown id), %28/%29 refused (400)'

        self.check('Rule: parentheses in id/clip accepted (v1.1)', test_parentheses_segments)

        # 16. Read-only pack -> 403 read_only_pack
        def test_readonly_pack():
            assert readonly_pack_id, 'No read-only pack available'
            line_id = all_lines[0]['id'] if all_lines else 'sample_01'
            st1, _, _, d1 = self.client.request(
                'PUT',
                f'/v1/maker/packs/{readonly_pack_id}/subtitles/th/{line_id}',
                json_body={'text': 'Should be refused'},
            )
            self.assert_error(st1, d1, 403, 'read_only_pack')

            st2, _, _, d2 = self.client.request(
                'PUT',
                f'/v1/maker/packs/{readonly_pack_id}/voice/th/vo_sample_01',
                body=make_pcm16_wav(0.2),
                headers={'Content-Type': 'audio/wav'},
            )
            self.assert_error(st2, d2, 403, 'read_only_pack')
            return f'403 read_only_pack on {readonly_pack_id}'

        self.check('Rule: writes to read-only pack -> 403 read_only_pack', test_readonly_pack)

        # 17. Over-limit JSON body (> 64 KiB) -> 413 too_large
        def test_overlimit_json():
            pack = editable_pack_id or 'sample-thai-dub'
            line_id = all_lines[0]['id'] if all_lines else 'sample_01'
            huge_body = json.dumps({'text': 'a' * 70000}).encode('utf-8')
            assert len(huge_body) > 65536
            st, _, _, data = self.client.request(
                'PUT',
                f'/v1/maker/packs/{pack}/subtitles/th/{line_id}',
                body=huge_body,
                headers={'Content-Type': 'application/json; charset=utf-8'},
            )
            self.assert_error(st, data, 413, 'too_large')
            return f'413 too_large ({len(huge_body)} bytes)'

        self.check('Rule: JSON body > 64 KiB -> 413 too_large', test_overlimit_json)

        # 18. Subtitle text validation (empty or > 2000 chars) -> 400 bad_request
        def test_subtitle_text_bounds():
            pack = editable_pack_id or 'sample-thai-dub'
            line_id = all_lines[0]['id'] if all_lines else 'sample_01'
            st1, _, _, d1 = self.client.request(
                'PUT',
                f'/v1/maker/packs/{pack}/subtitles/th/{line_id}',
                json_body={'text': ''},
            )
            self.assert_error(st1, d1, 400, 'bad_request')

            st2, _, _, d2 = self.client.request(
                'PUT',
                f'/v1/maker/packs/{pack}/subtitles/th/{line_id}',
                json_body={'text': 'ก' * 2001},
            )
            self.assert_error(st2, d2, 400, 'bad_request')
            return '400 bad_request on 0 and 2001 chars'

        self.check('Rule: subtitle length 1..2000 chars enforced -> 400', test_subtitle_text_bounds)

        # 19. Subtitle PUT + DELETE lifecycle & events (restores state)
        def test_subtitle_lifecycle():
            assert editable_pack_id and all_lines, 'Missing editable pack or lines'
            candidate = next((x for x in all_lines if x.get('subtitle') is None), all_lines[-1])
            line_id = candidate['id']
            orig_sub = candidate.get('subtitle') if candidate.get('subtitlePack') == editable_pack_id else None

            _, _, _, ev_before = self.client.request('GET', '/v1/maker/events?after=0&wait=0&limit=1')
            cursor = ev_before.get('latest', 0) if isinstance(ev_before, dict) else 0

            test_text = 'บทบรรยายทดสอบจากชุดตรวจสัญญา (contract_test)'
            try:
                st_put, _, _, saved_put = self.client.request(
                    'PUT',
                    f'/v1/maker/packs/{editable_pack_id}/subtitles/th/{line_id}',
                    json_body={'text': test_text},
                )
                assert st_put == 200 and isinstance(saved_put, dict), f'PUT subtitle failed: {st_put} {saved_put}'
                assert saved_put.get('live') is True, f'Expected live=True: {saved_put}'
                assert saved_put.get('path') == 'data/subtitles/th.json', f'Unexpected path: {saved_put}'
                assert isinstance(saved_put.get('note'), str) and saved_put['note'], f'Missing note: {saved_put}'

                st_line, _, _, after_put = self.client.request('GET', f'/v1/maker/lines/{line_id}?lang=th')
                assert st_line == 200 and after_put.get('subtitle') == test_text, (
                    f'Line did not reflect saved subtitle: {after_put}'
                )
                assert after_put.get('subtitlePack') == editable_pack_id

                st_del, _, _, saved_del = self.client.request(
                    'DELETE',
                    f'/v1/maker/packs/{editable_pack_id}/subtitles/th/{line_id}',
                )
                assert st_del == 200 and isinstance(saved_del, dict) and saved_del.get('live') is True

                st_line2, _, _, after_del = self.client.request('GET', f'/v1/maker/lines/{line_id}?lang=th')
                assert st_line2 == 200 and after_del.get('subtitle') != test_text, (
                    f'Subtitle was not removed on DELETE: {after_del}'
                )

                st_ev, _, _, ev_data = self.client.request('GET', f'/v1/maker/events?after={cursor}&wait=0&limit=100')
                assert st_ev == 200 and isinstance(ev_data, dict)
                sub_events = [
                    e for e in ev_data.get('events', [])
                    if e.get('type') == 'maker.subtitle.saved' and e.get('data', {}).get('id') == line_id
                ]
                assert any(e['data'].get('removed') is False for e in sub_events), 'Missing maker.subtitle.saved (removed=False)'
                assert any(e['data'].get('removed') is True for e in sub_events), 'Missing maker.subtitle.saved (removed=True)'
                return f'PUT & DELETE verified on {line_id}'
            finally:
                if orig_sub:
                    self.client.request(
                        'PUT',
                        f'/v1/maker/packs/{editable_pack_id}/subtitles/th/{line_id}',
                        json_body={'text': orig_sub},
                    )
                else:
                    self.client.request(
                        'DELETE',
                        f'/v1/maker/packs/{editable_pack_id}/subtitles/th/{line_id}',
                    )

        self.check('Route: PUT & DELETE subtitles + maker.subtitle.saved events', test_subtitle_lifecycle)

        # 20. Bad WAV -> 415 unsupported_media_type
        def test_bad_wav():
            pack = editable_pack_id or 'sample-thai-dub'
            st1, _, _, d1 = self.client.request(
                'PUT',
                f'/v1/maker/packs/{pack}/voice/th/vo_sample_10',
                body=b'not a valid wav header or payload',
                headers={'Content-Type': 'audio/wav'},
            )
            self.assert_error(st1, d1, 415, 'unsupported_media_type')

            # 8-bit PCM WAV (must be 16-bit PCM)
            wav_8bit = make_pcm16_wav(duration_sec=0.2, sampwidth=1)
            st2, _, _, d2 = self.client.request(
                'PUT',
                f'/v1/maker/packs/{pack}/voice/th/vo_sample_10',
                body=wav_8bit,
                headers={'Content-Type': 'audio/wav'},
            )
            self.assert_error(st2, d2, 415, 'unsupported_media_type')
            return '415 unsupported_media_type on non-WAV & 8-bit WAV'

        self.check('Rule: bad WAV bytes or format -> 415 unsupported_media_type', test_bad_wav)

        # 21. Over-limit WAV body (> 16 MiB or > 60 s) -> 413 too_large
        def test_overlimit_wav():
            pack = editable_pack_id or 'sample-thai-dub'
            oversized = b'\x00' * (16 * 1024 * 1024 + 64)
            st, _, _, data = self.client.request(
                'PUT',
                f'/v1/maker/packs/{pack}/voice/th/vo_sample_10',
                body=oversized,
                headers={'Content-Type': 'audio/wav'},
                timeout=20.0,
            )
            self.assert_error(st, data, 413, 'too_large')

            wav_61s = make_pcm16_wav(duration_sec=61.0, sample_rate=8000, channels=1, sampwidth=2)
            st2, _, _, data2 = self.client.request(
                'PUT',
                f'/v1/maker/packs/{pack}/voice/th/vo_sample_10',
                body=wav_61s,
                headers={'Content-Type': 'audio/wav'},
            )
            assert st2 in (413, 400), f'Expected 413 or 400 for >60s WAV, got {st2} ({data2})'
            return f'413 too_large (>16 MiB) & {st2} (>60s WAV)'

        self.check('Rule: WAV body > 16 MiB or > 60 s -> 413 too_large', test_overlimit_wav)

        # 22. Voice PUT, POST /v1/maker/play (original + dub), Voice DELETE & events
        def test_voice_and_play_lifecycle():
            assert editable_pack_id and all_lines, 'Missing editable pack or lines'
            voiced_candidates = [x for x in all_lines if x.get('voice') and x.get('dubPack') is None]
            target = voiced_candidates[0] if voiced_candidates else next(x for x in all_lines if x.get('voice'))
            line_id = target['id']
            clip = target['voice']

            _, _, _, ev_before = self.client.request('GET', '/v1/maker/events?after=0&wait=0&limit=1')
            cursor = ev_before.get('latest', 0) if isinstance(ev_before, dict) else 0

            wav_bytes = make_pcm16_wav(duration_sec=0.75, sample_rate=24000, channels=1, sampwidth=2)
            try:
                st_put, _, _, saved_put = self.client.request(
                    'PUT',
                    f'/v1/maker/packs/{editable_pack_id}/voice/th/{clip}',
                    body=wav_bytes,
                    headers={'Content-Type': 'audio/wav'},
                )
                assert st_put == 200 and isinstance(saved_put, dict), f'PUT voice failed: {st_put} {saved_put}'
                assert saved_put.get('live') is True
                assert saved_put.get('path') == f'audio/voice/th/{clip}.wav', f'Unexpected path: {saved_put}'
                assert isinstance(saved_put.get('durationSec'), (int, float)) and saved_put['durationSec'] > 0

                st_line, _, _, after_put = self.client.request('GET', f'/v1/maker/lines/{line_id}?lang=th')
                assert st_line == 200 and after_put.get('dubPack') == editable_pack_id, (
                    f'Line did not reflect saved dub: {after_put}'
                )
                assert isinstance(after_put.get('dubDurationSec'), (int, float)) and after_put['dubDurationSec'] > 0

                # Test POST /v1/maker/play for both original and dub
                # A real game answers 409 not_ready while Satone is still speaking: wait and retry (up to ~15 s).
                for variant in ('original', 'dub'):
                    for _attempt in range(30):
                        st_play, _, _, play_res = self.client.request(
                            'POST',
                            '/v1/maker/play',
                            json_body={'id': line_id, 'variant': variant, 'lang': 'th'},
                        )
                        if not (st_play == 409 and isinstance(play_res, dict)
                                and play_res.get('error', {}).get('code') == 'not_ready'):
                            break
                        time.sleep(0.5)
                    assert st_play == 200 and isinstance(play_res, dict), f'Play ({variant}) failed: {st_play} {play_res}'
                    assert play_res.get('live') is True
                    assert isinstance(play_res.get('note'), str) and play_res['note']

                # Negative play cases
                st_bad_var, _, _, d_bad_var = self.client.request(
                    'POST',
                    '/v1/maker/play',
                    json_body={'id': line_id, 'variant': 'invalid', 'lang': 'th'},
                )
                self.assert_error(st_bad_var, d_bad_var, 400, 'bad_request')

                st_no_line, _, _, d_no_line = self.client.request(
                    'POST',
                    '/v1/maker/play',
                    json_body={'id': 'no_such_line_99999', 'variant': 'original', 'lang': 'th'},
                )
                self.assert_error(st_no_line, d_no_line, 404, 'not_found')

                # DELETE voice clip
                st_del, _, _, saved_del = self.client.request(
                    'DELETE',
                    f'/v1/maker/packs/{editable_pack_id}/voice/th/{clip}',
                )
                assert st_del == 200 and isinstance(saved_del, dict) and saved_del.get('live') is True

                st_line2, _, _, after_del = self.client.request('GET', f'/v1/maker/lines/{line_id}?lang=th')
                # Another loaded pack may dub the same clip (a real game has cwy.thai); then it takes over. The dub must
                # just no longer come from this pack.
                assert st_line2 == 200 and after_del.get('dubPack') != editable_pack_id, (
                    f'Dub was not removed on DELETE: {after_del}'
                )

                # Verify maker.voice.saved and maker.play events
                st_ev, _, _, ev_data = self.client.request('GET', f'/v1/maker/events?after={cursor}&wait=0&limit=100')
                assert st_ev == 200 and isinstance(ev_data, dict)
                events = ev_data.get('events', [])
                voice_events = [
                    e for e in events
                    if e.get('type') == 'maker.voice.saved' and e.get('data', {}).get('clip') == clip
                ]
                play_events = [
                    e for e in events
                    if e.get('type') == 'maker.play' and e.get('data', {}).get('id') == line_id
                ]
                assert any(e['data'].get('removed') is False for e in voice_events), 'Missing maker.voice.saved (removed=False)'
                assert any(e['data'].get('removed') is True for e in voice_events), 'Missing maker.voice.saved (removed=True)'
                assert len(play_events) >= 2, f'Expected maker.play events for original & dub, got {play_events}'
                return f'voice PUT/DELETE & play verified on {line_id} ({clip})'
            finally:
                if target.get('dubPack') is None:
                    self.client.request(
                        'DELETE',
                        f'/v1/maker/packs/{editable_pack_id}/voice/th/{clip}',
                    )

        self.check('Route: PUT/DELETE voice + POST /v1/maker/play + events', test_voice_and_play_lifecycle)

        # 23. GET /v1/maker/events envelope & parameter bounds
        def test_events_route():
            st1, _, _, d1 = self.client.request('GET', '/v1/maker/events?wait=26')
            self.assert_error(st1, d1, 400, 'bad_request')
            st2, _, _, d2 = self.client.request('GET', '/v1/maker/events?limit=0')
            self.assert_error(st2, d2, 400, 'bad_request')
            st3, _, _, d3 = self.client.request('GET', '/v1/maker/events?limit=501')
            self.assert_error(st3, d3, 400, 'bad_request')

            st, _, _, data = self.client.request('GET', '/v1/maker/events?after=0&wait=0&limit=50')
            assert st == 200 and isinstance(data, dict), f'Expected 200 JSON, got {st}'
            for key in ('epoch', 'events', 'next', 'latest', 'oldest', 'dropped', 'reset', 'timedOut'):
                assert key in data, f'Missing envelope key {key}: {data}'
            assert isinstance(data['events'], list) and len(data['events']) > 0
            ev0 = data['events'][0]
            for ekey in ('seq', 'time', 'type', 'data'):
                assert ekey in ev0, f'Event missing {ekey}: {ev0}'
            return f'latest={data["latest"]} events={len(data["events"])}'

        self.check('Route: GET /v1/maker/events envelope & parameter bounds', test_events_route)

        return all(ok for _, ok, _ in self.results)

    def print_table(self) -> None:
        width = max((len(name) for name, _, _ in self.results), default=40)
        sep = '-' * (width + 42)
        print(sep)
        print(f'{"CHECK":<{width}} | STATUS | DETAIL')
        print(sep)
        for name, ok, detail in self.results:
            status = 'PASS' if ok else 'FAIL'
            print(f'{name:<{width}} | {status:<6} | {detail}')
        print(sep)
        passed = sum(1 for _, ok, _ in self.results if ok)
        total = len(self.results)
        print(f'Summary: {passed}/{total} checks passed.')


def find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        '--base',
        type=str,
        default=None,
        help='Base URL of the server, e.g. http://127.0.0.1:8127 (spawns fake_loader.py if omitted).',
    )
    parser.add_argument(
        '--token',
        type=str,
        default=None,
        help='Maker bearer token (optional: read automatically from /maker/ meta tag).',
    )
    parser.add_argument(
        '--pack',
        type=str,
        default=None,
        help='Only write to this editable pack (use a throwaway lab pack against a real game).',
    )
    parser.add_argument('--studio', action='store_true', help='Also run Studio v2 text-work cases against a disposable pack.')
    parser.add_argument('--create-pack', action='store_true',
                        help='With --studio, also create a pack (always done against the spawned fake; against a real loader it leaves a pack folder).')
    args = parser.parse_args()
    global ONLY_PACK
    ONLY_PACK = args.pack

    proc: subprocess.Popen | None = None
    try:
        if args.base:
            parsed = urllib.parse.urlsplit(args.base)
            host = parsed.hostname or '127.0.0.1'
            port = parsed.port or 8127
        else:
            host = '127.0.0.1'
            port = find_free_port()
            fake_script = pathlib.Path(__file__).resolve().parent / 'fake_loader.py'
            proc = subprocess.Popen(
                [sys.executable, str(fake_script), '--port', str(port), '--tick-sec', '1.0']
                + (['--recovery-fixture', '--precedence-fixture', '--language-fixture'] if args.studio else []),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
            )
            deadline = time.monotonic() + 5.0
            ready = False
            while time.monotonic() < deadline:
                try:
                    with socket.create_connection((host, port), timeout=0.2):
                        ready = True
                        break
                except OSError:
                    time.sleep(0.05)
            if not ready:
                print('Failed to start fake_loader.py for self-test.', file=sys.stderr)
                return 2

        client = HttpClient(host=host, port=port, token=args.token)
        suite = ContractSuite(client=client, cli_token=args.token)
        ok = suite.run_all()
        suite.print_table()
        if args.studio:
            from contract_v2_cases import run_studio_checks
            ok = run_studio_checks(client, ContractSuite, args.pack, args.create_pack or proc is not None) and ok
        return 0 if ok else 1
    finally:
        if proc is not None:
            proc.terminate()
            try:
                proc.wait(timeout=3.0)
            except subprocess.TimeoutExpired:
                proc.kill()


if __name__ == '__main__':
    sys.exit(main())
