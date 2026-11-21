# CwyLs.ModLoader.Api — the local game API plugin

A BepInEx plugin (`cwyls.modloader.api`, `CwyLs.ModLoader.Api.dll` in
`BepInEx/plugins/CwyLsModLoader/`) that hosts the Unity-free API in `src/CwyLs.ModLoader.Core/Api/` on
`127.0.0.1` only. **Off by default.** The wire contract (routes, payloads, errors, security) is
`src/CwyLs.ModLoader.Core/Api/README.md`. Game-agnostic: no `Assembly-CSharp`, no `CwyLs.ModLoader.Bulbul`;
game events, data and language arrive through the framework's mod backends
(`CwyLs.ModLoader/Mods/ModBackends.cs`), which the adapter plugins register in their `Awake`
(soft dependencies order them first; the API re-checks every frame in case one comes late).

## Config (`BepInEx/config/cwyls.modloader.api.cfg`, section `[Api]`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | false: no listener, no port, no key file, no subscriptions. true: listen at start |
| `Port` | `7870` | 1024–65535 on 127.0.0.1; taken → off with one WARN (no hopping) |
| `Transport` | `Auto` | `Auto` = HttpListener, then the TCP server if HttpListener fails for any reason but a taken port; `HttpListener`; `Tcp` |
| `AllowActions` | `false` | let outside programs run act/write actions (reads always allowed). Writes also need `[Data] AllowWrites` and the player's Yes in game |
| `ConsoleAuditReads` | `false` | console line for every successful read; off = trace file only (actions and refusals always print) |

## Console, key, smoke

- `api` / `api status` — on/off and why, endpoint, transport (and fallback reason), key id (never the
  key), key file path, request counts by status, actions in flight, runner and confirmation dialog
  installed or not, event and state numbers, subscriptions.
- `api on` — starts it now (also when `[Api] Enabled=false`: for this session only, on the player's
  explicit request; the config is not changed). Every start mints a new key.
- `api off` — stops it: listener closed, long-polls woken (they answer at once), pending actions no
  longer awaited, key file deleted.
- `api rotate` — new key; clients holding the old one get 401 and must re-read the key file. If the
  new key cannot be written owner-only, the API stops (fail closed).
- **Ctrl+Shift+K** (`[Keys] api.kill.Primary`, no F-key alternate) — the kill switch: same as `api off`.
  Silent while a text field has focus (all hotkeys are).
- Smoke `s.api.selftest` (quiet): off → key file absent (failure if `[Api] Enabled=true` but off, with
  the reason); on → three loopback requests through the real listener: `/v1/info` 200, with an
  `Origin` 403, without the key 401.

Console lines (tag `API`): `on http://127.0.0.1:7870/ transport=… keyFile=… allowActions=… keyId=…`,
`off (<reason>) requests=… keyFile=deleted`, WARN `local API off: port 7870 is taken …`, WARN `local
API uses the TCP fallback: …`, `access key rotated`, and one audit line per action or refused request
(`POST /v1/actions/music.play 202 ms=… client=… tier=act handle=act-…`).

## Pack Maker (`maker`)

Pack Maker's Dub & Subtitle Studio (`MakerFeature`, `MakerCommands`; wire contract in
`src/CwyLs.ModLoader.Core/Api/README.md` under "Pack Maker routes") serves `BepInEx/plugins/CwyLsModLoader/maker/` at
`http://127.0.0.1:<port>/maker/` and `/v1/maker/*`.

- `maker` / `maker open` — opens a `MakerSurface` session (mints a fresh 32-byte hex maker token in memory, installs
  `MakerEventRing` via `MakerBackends.UseEvents`), starts a listener if needed, and opens
  `http://127.0.0.1:<port>/maker/` in the default browser on the main thread (`MainThread.RunOrPost` →
  `Application.OpenURL`). Running `maker` again on the same port re-opens the browser with the existing session and token.
- `maker serve`: the same as `maker`, without opening a browser tab (an open tab reconnects; unattended checks use it).
- `maker off` — closes Pack Maker at once: detaches `ApiRouter.UseMaker(null)`, answers **503** and wakes waiting
  long-polls, cancels backend calls in flight, clears `MakerBackends.UseEvents(null)`, and stops the maker-only listener if
  one was started.
- `maker status` — reports whether Pack Maker is open or closed, its URL, when it opened and which listener serves it
  (`on the local API's listener` or `own listener (<transport>; requests …)`), event ring counters (`epoch`, `latest`,
  `dropped`, `refused`, waiting long-polls), whether `catalog` and `editor` backends are installed, and whether
  `BepInEx/plugins/CwyLsModLoader/maker` is `present` or `MISSING`.

**When Pack Maker opens and closes:**

- **Closed at start.** Pack Maker never opens at boot, even when `[Api] Enabled=true`; it opens only on the explicit
  `maker` command. There is no hotkey in this version.
- **Listener choice and `api on` handoff.** If the local API is already running, Pack Maker attaches its `MakerRouter` to
  the API's router (`ApiRouter.UseMaker`). If the local API is off (`[Api] Enabled=false`), `maker` starts a maker-only
  listener on `[Api] Port` (`[Api] Transport`) that writes no `token.json` key file and answers **404** for every route
  outside `/maker` and `/v1/maker/*`. If `api on` is run while the maker-only listener holds `[Api] Port`, the maker-only
  listener stops before the API binds (`BeforeApiStart`) and Pack Maker moves onto the API's listener keeping the **same**
  token (`AfterApiStart`), so an open browser tab stays live; if the API fails to start, Pack Maker restarts its own
  listener, and if the API starts on another port, Pack Maker closes.
- **Closes on:** `maker off`; `api off` when attached to the local API's listener (`OnApiStopping`); the **Ctrl+Shift+K**
  kill switch (`api.kill`), whether on the API's listener or its own maker-only listener; running `maker` after `[Api] Port`
  changed (closes the old session before opening on the new port); and `Application.quitting` (silent on the console, file
  record `api/makerOff` only).

Console lines (tag `API`): `Pack Maker on http://127.0.0.1:7870/maker/ listener=… webFiles=… trigger=…` (`makerOn`),
`Pack Maker off (<reason>)` (`makerOff`), WARN `Pack Maker did not start: …` (`makerOff`), WARN `Pack Maker uses the TCP
fallback: …` (`makerFallback`), WARN `Pack Maker could not open: …` (`makerFailed`).

<!-- Sources: CONTRACT.md:13-18; src/CwyLs.ModLoader.Api/MakerCommands.cs:7-33; src/CwyLs.ModLoader.Api/MakerFeature.cs:20-314; src/CwyLs.ModLoader.Api/ApiFeature.cs:133-147,198-203,221,229,264,314-332; src/CwyLs.ModLoader.Core/Api/Maker/MakerSurface.cs:12-72; src/CwyLs.ModLoader.Core/Api/Maker/MakerRouter.cs:141-180 -->

## Using it from Python (stdlib only)

```python
import json, pathlib, urllib.request
SCHEME = "Bearer"  # RFC 6750 scheme word
info = json.loads(pathlib.Path("<game root>/BepInEx/data/api/token.json").read_text())
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))   # never through a proxy
def call(method, path, body=None):
    req = urllib.request.Request(info["baseUrl"] + path.lstrip("/"), method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": SCHEME + " " + info["token"], "X-CWY-Client": "example",
                 **({"Content-Type": "application/json"} if body is not None else {})})
    with opener.open(req, timeout=30) as r:
        return json.loads(r.read())
state = call("GET", "/v1/state")
page = call("GET", "/v1/events?after=0&wait=20")
```

## Main thread and frame cost

Requests run on thread-pool threads and never touch Unity. One ticker on the shared dispatcher
(owner `api`, framework lane) rebuilds the save-backed part of `/v1/state` from
`ModBackends.GameData.Snapshot()` — 0.25 s after a data change, at least every 5 s, timed against
the 4 ms budget (over budget: the interval doubles up to 8 s; file record `api/stateOverBudget`) —
and republishes `live` on events. Actions are executed by the action runner (`ActionServices`)
with a 2 s wall-clock main-thread timeout.

## Wiring

The `CwyLs.ModLoader.sln` entry and the `build.sh` plugin list. `InternalsVisibleTo
("CwyLs.ModLoader.Api")` for `ModFacetBackends.Use` already exists in `CwyLs.ModLoader/AssemblyInfo.cs`.
