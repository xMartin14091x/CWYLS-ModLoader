# Dub & Subtitle Studio — Stand-in Loader & Contract Test Suite

These tools let the Dub & Subtitle Studio web app be developed and verified without launching the game, and check any server implementation (either `fake_loader.py` or the live BepInEx mod loader) against `CONTRACT.md` (DS-00).

Both scripts require **Python 3.10+** and use the **Python standard library only**.

## Real-loader launch links

The real loader's `maker serve` starts the listener without opening a tab and returns
`Pack Maker: http://127.0.0.1:<port>/maker/?k=<nonce> (...)`. Each call returns a fresh link, usable once
within 60 seconds. The lab helper's `ConsoleFeature.Submit("maker serve")` result (also its `lab-maker.json`
`maker` field) contains this link. Extract that URL and open it directly in the browser, or pass it to an
in-memory cookie-aware client. Do not first fetch it with another client: that spends the link.
If expired or already spent, submit `maker serve` again. A bare `/maker/` no longer gives a token.

For an unattended check, use the returned command result in `answer`:

```python
import http.cookiejar
import re
import urllib.request

launch = re.search(r"http://127[.]0[.]0[.]1:[0-9]+/maker/[?]k=[0-9a-f]{64}", answer).group(0)
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
with client.open(launch, timeout=5) as response:
    html = response.read().decode("utf-8")  # follows the redirect, keeps the session cookie in memory
token = re.search(r'<meta name="cwyls-maker-token" content="([0-9a-f]{64})">', html).group(1)
# Use Authorization: Bearer <token> for maker API calls. Keep client for subsequent page reloads.
```

For a browser and an HTTP smoke client, obtain a separate link for each. Never print or persist the token
or cookie. The fake loaders below remain invented-data stand-ins with their own bootstrap contract.

## Stand-in backends

M4b adds `studio_v2_fake.py` for the v2.1 text-work stand-in: capabilities/status, packs/lines/groups,
workflow, subtitle/history/revert, glossary and operation receipts. State persists under each fake pack's
`.maker/fake-v2.json`; this is not the native transaction journal or proof of native crash safety.
The fake advertises catalog/workflow/subtitles/projects/newPacks, never dubs/playback. Context stays null when
unknown, and live:false states that no game is connected.

Run `python3 tools/contract_test.py --studio` from the web folder for the unchanged v1 25 checks, followed by
the v2 checks. Real-server checks require a disposable pack and the relevant installed capabilities.
Run `python3 tools/test_studio_v2_fake.py` for restart/recovery/schema checks. Node tests include the real
HTTP adapter against the loopback fake. `--port 0` binds an OS-assigned port without a probe race.

M5a adds recovery preview and keep-current resolution under `projects`. `--recovery-fixture` seeds an invented
blocked `sample-recovery` pack (operation ID `aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`) for W3 integration. The standalone
`--studio` contract run enables it and checks preview, stale-token refusal, resolution, replay and an unblocked edit.
The fake archives its aggregate snapshot before clearing its marker; production resolution-journal crash safety
is tested separately by `MakerRecoveryTests`, not by this simulator.

M6a (W2c) adds `POST /v1/maker/studio/packs` (`newPacks`): the fake validates like the loader (3-64 character id,
no reference or device names, no built-in language, new languages need a caption and an English name), writes the
skeleton into its data folder and answers 201 with the pack pending (`loaded:false`, `restartRequired:true`). Every pack
route on a pending pack answers 409 `restart_required` until the fake is started again on the same `--data` folder, which
loads it as an editable pack. Fixtures (all invented, all opt-in): `--precedence-fixture` adds the read-only
`sample-override-pack`, which wins over `sample-thai-dub` for `sample_02` (text), `sample_03` (dub) and `sample_04` (dub
only there); `--game-language CODE` sets the language the fake reports; `--language-fixture` gives `sample-thai-dub`
English text too. A saved subtitle answers `effectivePackId` with the pack that wins afterwards. The standalone `--studio`
contract run starts the fake with all fixtures and creates a pack in it; against a live loader pass `--create-pack` to do
the same (it leaves a pack folder behind).

## `fake_loader.py`

Starts a loopback-only HTTP server at `http://127.0.0.1:<port>/maker/` that:
- Serves `--web <dir>` at `/maker/` (and redirects `/maker` to `/maker/`), injecting `<meta name="cwyls-maker-token" content="<random token>">` into `index.html`.
- Enforces `Host`, `Origin`, `Sec-Fetch-Site`, `Authorization: Bearer <token>`, segment patterns, 64 KiB JSON limits, and 16 MiB / 60 s PCM 16-bit WAV limits.
- Seeds 40 invented lines (`sample_01`–`sample_40`) across morning, focus, break, rain, evening, and click reactions, plus two packs (`sample-thai-dub` editable, `sample-reference-pack` read-only).
- Persists subtitle and voice edits to `<data dir>/<packId>/data/subtitles/<lang>.json` and `<data dir>/<packId>/audio/voice/<lang>/<clip>.wav`.
- Emits `room.ready`, `game.language`, `maker.subtitle.saved`, `maker.voice.saved`, `maker.play`, and a periodic `line.spoken` event every ~20 seconds.

```bash
python3 src/CwyLs.ModLoader.Maker.Web/tools/fake_loader.py \
  --port 8127 \
  --web src/CwyLs.ModLoader.Maker.Web
```

Optional flags:
- `--data <dir>`: folder where pack files are written (defaults to a temporary directory cleaned up on exit).
- `--token <token>`: fixed maker bearer token (defaults to a random 32-byte hex token per start).
- `--tick-sec <seconds>`: background `line.spoken` interval in seconds (default `20`).

## `contract_test.py`

Runs the full contract test suite against a running server and prints a `PASS`/`FAIL` table, exiting non-zero if any check fails. It discovers the editable pack from `/v1/maker/packs` and restores any subtitle or voice clip it modifies.

### Against an existing server (fake or live loader)

```bash
# Reads the bearer token automatically from /maker/ <meta name="cwyls-maker-token">:
python3 src/CwyLs.ModLoader.Maker.Web/tools/contract_test.py --base http://127.0.0.1:8127

# Or pass the token explicitly:
python3 src/CwyLs.ModLoader.Maker.Web/tools/contract_test.py --base http://127.0.0.1:8127 --token <token>
```

### Standalone self-test

When run with no `--base` argument, `contract_test.py` automatically launches `fake_loader.py` on a free loopback port, runs all checks, and stops the server cleanly:

```bash
python3 src/CwyLs.ModLoader.Maker.Web/tools/contract_test.py
```
