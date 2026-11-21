# Core/Api — the local game API, wire contract v1 (S-API)

The game's local HTTP API for programs on the same computer: an MCP server, a companion program
running outside the game, sidecars and smoke runs. This folder is the Unity-free part (router,
checks, transports, event ring, state document, handles); the BepInEx plugin
`src/CwyLs.ModLoader.Api/` hosts it in the game (config, console, kill switch).

**Stability.** Everything below is the `/v1` contract and freezes at CWY:LS Mod Loader 1.0. Within `/v1`
changes are additive only: new routes, new members, new event types, new error codes. Clients must
ignore members and event types they do not know. A removed or re-meant member bumps the schema
number (`stateSchema`, key-file `schema`) or the path (`/v2`).

## Security model

| Control | Rule | Where |
|---|---|---|
| Off by default | `[Api] Enabled=false`: no listener, no open port, no key file, no subscriptions | plugin |
| Loopback only | binds the literal `127.0.0.1` (never `localhost`, `*`, `+`, `0.0.0.0`); nothing on `::1` or LAN addresses (checked on Mono 6.13; the unit tests open sockets on 127.0.0.1 only) | `ApiTransports.cs` |
| Port taken | the API stays off with one WARN; it never moves to another port. The probe also catches a program listening on `0.0.0.0` (macOS lets a 127.0.0.1 bind succeed next to it) | `ApiPortProbe` |
| Browsers refused | any `Origin` header, or `Sec-Fetch-Site` / `Sec-Fetch-Mode`: **403** `browser_refused`; no CORS headers are ever sent | `ApiRouter` |
| DNS rebinding | `Host` must be exactly `127.0.0.1:<port>`: **421** `host_refused` (HttpListener already refuses a foreign Host itself: 400 on Mono, 404 on .NET) | `ApiRouter` |
| Access key | per start (and `api rotate`): 32 random bytes as 64 hex, in the standard `Authorization` header with the scheme word `Bearer` (RFC 6750), compared in constant time: **401** `unauthorized` + `WWW-Authenticate`. Never in a URL, never logged; `ToString()` is redacted | `ApiToken` |
| Key file | `BepInEx/data/api/token.json`, folder 0700 and file 0600 set while the file is still empty (Windows: protected DACL owner + SYSTEM); if that fails the file is removed and the API stays off; deleted on stop and quit | `ApiKeyFile` |
| Strict input | origin-form targets only, no `%`/`..`/`//` in paths, unknown or repeated query parameters refused, GET has no body, POST body ≤ 64 KiB, `Content-Length` required (chunked: **411**), `application/json` only (**415**), strict UTF-8 and strict RFC 8259 JSON (`JsonReader`). TCP gives the whole request 5 s from accept; HttpListener gives a body 5 s after its complete head, using a dedicated watchdog; a body that misses its deadline (stalled or dripping) gets **408** `request_timeout` on both, and no handler acts on that body | `ApiRouter`, `ApiTarget`, `ApiHttpParser`, transports |
| Rate limits | after the key check only: reads 20/s (burst 40), event polls 10/s (burst 20), action starts 4/s (burst 8); 4 long-polls at once; 8 actions in flight; 16 requests at once per transport: **429** + `Retry-After`, or **503** | `ApiRateLimiter`, transports |
| Tiers | read actions always; act/write only with `[Api] AllowActions=true`; anything that needs the player's confirmation (write tier, destructive) is **refused** while no in-game confirmation dialog is installed (never auto-approved); writes also need `[Data] AllowWrites` (checked by the runner) | `ApiActionCatalog.Decide` + `ActionGate` |
| Main thread | requests never touch Unity: `/v1/state` is served from a document the main thread publishes; actions go to the runner, which waits ≤ 2 s (wall clock) for the main thread; the API's own main-thread work (state rebuild) is timed against a 4 ms budget and backs off | `ApiRuntime`, `ApiRefreshPolicy` |
| Privacy | `/v1/state` and events never carry the text of notes, the diary, todos, habits or calendar entries (titles included): ids, counts, lengths, dates and states only. Profile, account and other mods' `extra` sections are left out | `ApiStateProjection`, `ApiEventMapper` |
| Audit | one record per request (bus `api/request`, tag `API`): method, path (no query), status, ms, client, transport, tier/handle/error; never a header value or body. A request the HttpListener watchdog timed out before a handler ran has no record; its trace diagnostic names the method, path and the 408 sent, as it does when a handler's answer is dropped for that 408 | `ApiAuditLog`, transports |

## Calling it

1. Read `BepInEx/data/api/token.json`:
   `{"schema":1,"baseUrl":"http://127.0.0.1:7870/","port":7870,"transport":"httplistener","token":"<64 hex>","pid":1234,"createdAt":"…"}`.
2. Send every request to `baseUrl` with the headers `Host: 127.0.0.1:<port>` (clients set it from the
   URL), `Authorization` = the scheme word `Bearer`, one space, the `token` value, and optionally
   `X-CWY-Client: <name>` (`[A-Za-z0-9._-]{1,32}`, shown in the audit line).
3. Do not use a proxy (Python `httpx`: `trust_env=False`; `urllib`: `ProxyHandler({})`).
4. On **401**, re-read the file once (the key changes on every start and on `api rotate`), then give up.

Every response is `application/json; charset=utf-8` with `Cache-Control: no-store`. Errors are
`{"error":{"code":"…","message":"…"}}`.

| Code | Status | Meaning |
|---|---|---|
| `bad_request` | 400 | target, query, body, UTF-8 or JSON malformed; GET with a body |
| `invalid_args` | 400 | arguments do not fit the action's parameters |
| `unauthorized` | 401 | no key or the wrong one |
| `browser_refused` | 403 | `Origin`, `Sec-Fetch-Site` or `Sec-Fetch-Mode` present |
| `denied` | 403 | the tier gate refuses API callers (`[Api] AllowActions=false`) |
| `confirmation_unavailable` | 403 | needs the player's Yes in game, and no in-game dialog is installed |
| `not_found` | 404 | no such route, action or handle (handles expire 10 min after they finish) |
| `method_not_allowed` | 405 | see `Allow` |
| `request_timeout` | 408 | the request body did not arrive within its 5 s deadline |
| `idempotency_conflict` | 409 | the `Idempotency-Key` was used with another action or other arguments |
| `length_required` | 411 | chunked body |
| `too_large` | 413 | body over 64 KiB |
| `unsupported_media_type` | 415 | body not `application/json` |
| `host_refused` | 421 | `Host` is not `127.0.0.1:<port>` |
| `rate_limited` | 429 | a rate limit or a concurrency cap; see `Retry-After` |
| `internal` | 500 | the API failed on this request (details in the trace file) |
| `unavailable` | 503 | stopping, too many requests at once, or no action runner installed |

## Routes

### `GET /v1/info`
`api` {`version`:1, `basePath`:"/v1", `endpoint`, `stateSchema`:1} · `loader` {`version`, `modApi`} ·
`game` {`version`} · `server` {`transport`, `fallbackReason`, `startedAt`, `uptimeSec`, `keyId`} ·
`capabilities` {`state`, `events`, `actions` {`registered`, `runner`, `allowActions`, `confirmationGate`},
`voiceStreams`:false, `micUtterances`:false} · `events` {`epoch`, `latest`, `oldest`, `capacity`,
`droppedTotal`, `types`[]} · `limits` {`maxBodyBytes`, `maxWaitSec`, `maxEventsPerRead`,
`maxConcurrentWaits`, `maxActionsInFlight`, `actionTimeoutMs`, `ratePerSec` {`read`,`events`,`actions`}}.
`keyId` is a non-secret label (first 8 hex of the key's SHA-256) to notice a rotation.

### `GET /v1/state` (schema 1)
Header members, always in this order: `schema`, `version` (publish counter, 0 before the first),
`capturedAt` (ISO-8601 with offset), `ageMs` (age when served: a stale document after a sleep or a
stalled loop is visible), `frame`. Then:

| Member | Content |
|---|---|
| `ready` | true once the player's save is loaded |
| `dataAvailable` | false when the data plugin is not installed (then only `live` follows) |
| `live` | `roomReady`, `scene`, `language`, `clock` · `satone` {`state`, `since`, `speaking`, `voice`, `localizationId`, `lineAt`} · `scenario` {`running`, `groupId`, `scenarioType`} · `pomodoro` {`type`, `lastEvent`, `paused`, `elapsedSec`, `remainingSec`, `tickAt`} · `music` {`title`, `index`, `shuffle`} · `windows`[] |
| `gameVersion`, `dataAt` | from the data-bus snapshot |
| `player` | `level`, `exp`, `nextLevelExp`, `totalLoginCount`, `lastLoginAt`, `currentWorkSeconds`, `pomodoroTotalWorkSeconds` |
| `pomodoro` | `mode` (`pomodoro`/`countup`), `workMinutes`, `breakMinutes`, `loopCount`, `presetCount` |
| `todos` | `listCount`, `itemCount`, `openCount`, `completedCount`, `lists`[{`id`, `titleLength`, `itemCount`, `openCount`, `completedCount`, `items`[{`id`, `state` (`open`/`complete`), `textLength`, `completedAt`, `expiresAt`}]}] |
| `notes` | `pageCount`, `pages`[{`id`, `order`, `titleLength`, `textLength` (null when the body was not loaded or is withheld)}] |
| `habits` | `count`, `items`[{`id`, `titleLength`, `createdDate`, `scheduledDays`[`monday`…], `recentCompletedCount`, `recentDays`[{`date`, `completed`}]}] |
| `calendar` | `recentWorkSeconds`, `days`[{`date`, `workSeconds`, `completedTodoCount`, `diaryLength` (null when withheld)}] |
| `settings` | `language`, `timeFormat`, `windowMode`, `alwaysOnTop`, `activeFramerate`, `selfTalk`, `pomodoroSe`, `volumes`[{`name`, `value`, `mute`}] |
| `music` | `nowPlaying`, `shuffle`, `playOnStart`, `currentTag`, `favoriteCount`, `excludedCount`, `localTrackCount` |
| `environment` | `windowViews`[], `ambientSounds`[{`type`, `volume`, `mute`}] |
| `story` | `useAuto`, `finishedMainEpisode`, `nextEpisode`, `nextEpisodeUnlockLevel`, `playedScenarioGroupCount` |
| `achievements` | `available`, `unlockedCount` |
| `extraSections` | names of sections other mods publish (their content is not exposed) |

Ids are strings when they exceed 2^53 (todo, list and note ids). Dates are `yyyy-MM-dd`; times ISO-8601.
The save-backed part is rebuilt 0.25 s after a data change and at least every 5 s (longer when a
rebuild costs more than 4 ms); `live` is republished on every event and at least once a second.

### `GET /v1/events?after=<seq>&wait=<s>&limit=<n>`
`after` (default 0) returns events with a higher sequence number; `wait` (0–25 s) long-polls until
the first one arrives; `limit` 1–500 (default 100). Response: `epoch`, `events`[{`seq`, `time`,
`type`, `data`}], `next` (pass as the next `after`), `latest`, `oldest`, `dropped` (events after your
`after` that were overwritten before you read them; the ring keeps 1024), `reset` (your `after` is
beyond `latest`: the API restarted and `epoch` changed; reading starts at `oldest`), `timedOut`.

| `type` | `data` |
|---|---|
| `satone.state` | `prev`, `next` |
| `line.spoken` | `voice`, `text`, `textJa`, `language`, `localizationId`, `source` (`other`/`story`/`selfTalk`/`pomodoro`/`clickReaction`/`tutorial`), `scenarioType`, `scenarioGroupId`, `isStory`, `durationSec` |
| `line.ended` | `voice`, `durationSec` |
| `scenario.started` | `scenarioType`, `groupId`, `readMode` |
| `scenario.ended` | `groupId` |
| `pomodoro.changed` | `kind` (`started`/`workStarted`/`workEnded`/`breakStarted`/`breakEnded`/`completed`/`paused`/`unpaused`), `pomodoroType` |
| `window.changed` | `window`, `active` |
| `music.changed` | `title`, `index`, `shuffle` |
| `save` | `section` |
| `scene.changed` | `scene`, `loaded` |
| `room.ready` | — |
| `data.changed` | `section` (camel case, e.g. `notes`), `reason` (the game's method or signal name) |
| `action.completed` | `handle`, `action`, `tier`, `status` (no arguments, no message) |
| `game.other` | `name` only (a framework event this version does not map) |

Per-second ticks (pomodoro clock, game clock) are not events; read them from `live`.

### `GET /v1/actions`
`count`, `runner`, `allowActions`, `confirmationGate`, `actions`[] sorted by name, each:
`name`, `family`, `owner` (`cwy` or a mod id), `tier` (`read`/`act`/`write`), `destructive`,
`description`, `parameters`[{`name`, `type` (`string`/`integer`/`number`/`boolean`), `required`,
`description`, `enum`?}], `inputSchema` (JSON Schema: `type:object`, `properties`, `required`,
`additionalProperties:false` — usable as an LLM tool's input schema as is), `api` {`allowed`,
`needsConfirmation`, `reason`} (what this API would answer right now).

### `POST /v1/actions/<name>`
Body (optional): `{"args":{…}}`, nothing else. Arguments are bound strictly: every member must be a
parameter (case-insensitive), JSON types must match (`integer` accepts `3` and `3.0`), `enum` values
match case-insensitively, required ones must be present and not null; an optional `null` counts as
absent. Optional header `Idempotency-Key` (1–64 of `[A-Za-z0-9._:-]`, kept 10 min): the same key with
the same action and arguments returns the first handle (**200**, `replayed:true`) instead of running
twice; with a different request it is **409**.

Answers **202** at once with the handle (and `Location: /v1/actions/<handle>`); the action runs
through the runner (caller `Api`, caller id `api:<client>`, 2 s main-thread timeout; writes are
confirmed in game by the player when a dialog is installed).

### `GET /v1/actions/<handle>?wait=<s>`
`handle` (`act-` + 24 hex), `action`, `tier`, `client`, `state` (`pending`/`done`), `replayed`,
`createdAt`, `completedAt`, `result` {`status` (`ok`/`failed`/`invalidArgs`/`notFound`/`denied`/
`declined`/`timedOut`/`notAvailable`), `ok`, `path` (`none`/`semantic`/`uiClick`), `message`, `data`}.
`wait` (0–25 s) long-polls until it is done. Holding a handle is not authorisation (the key is still
required); finished handles are kept 10 minutes.

Not in this version: `/v1/voice/streams…`, `/v1/mic/utterances/<id>`.

## Types (all in `CwyLs.ModLoader.Core.Api`)

| Type | Role |
|---|---|
| `ApiDefaults`, `ApiTransportMode`, `ApiSwitches` | constants and limits; transport choice; the live `[Api] AllowActions` switch the action runner reads |
| `ApiToken` | the access key: mint, constant-time match, redacted `ToString`, `Id` |
| `ApiRequest`, `ApiHeaders`, `ApiBody`, `ApiResponse`, `ApiTarget`, `IApiClock`/`SystemApiClock` | transport-neutral HTTP model |
| `ApiRouter` (+ `ApiRouterOptions`, `ApiInfoFacts`) | the pipeline: checks, routes, limits, one audit record |
| `ApiRuntime` (+ `ApiRuntimeOptions`) | one session: router + ring + live state + state document; `OnEvent`, `OnDataChanged`, `Tick` |
| `ApiEventRing`, `ApiEvent`, `ApiEventPage`, `ApiEventMapper` | the event stream and the stable type mapping |
| `ApiStateStore`, `ApiStateProjection`, `ApiLiveState`, `ApiRefreshPolicy` | `/v1/state` |
| `ApiActionCatalog`, `ApiActionHandles`/`ApiActionHandle`, `ApiIdempotencyCache` | catalog + pre-gate + binding, handles, idempotency |
| `ApiRateLimiter`/`ApiTokenBucket`, `ApiAuditLog`/`ApiAuditRecord`, `ApiCounters` | limits, audit, counters |
| `IApiTransport`, `HttpListenerApiTransport`, `TcpApiTransport`, `ApiHttpParser`, `ApiServer`, `ApiPortProbe`, `ApiStartResult` | listeners and the `Transport=Auto` start rule |
| `ApiKeyFile` | owner-only key file |
| `ApiLoopbackClient` | minimal 127.0.0.1 HTTP client for the smoke self-test and tests |
| `ApiJson`, `ApiRawJson`, `ApiErrorCodes` | ordered JSON writer, error codes |

Trace records (`sys` `api`): `request` (audit; console for actions and refusals, file for successful
reads unless `[Api] ConsoleAuditReads`), `on`, `off`, `rotated`, `kill`, `fallback`, `actionDone`
(file), `stateOverBudget` (file), `keyFileDeleted` (file), `diagnostic` (file). Tests:
`src/CwyLs.ModLoader.Tests/Api*.cs`.

## Pack Maker routes

Pack Maker's Dub & Subtitle Studio (`CwyLs.ModLoader.Core.Api.Maker`, contracts
[`MakerContracts.cs`](Maker/MakerContracts.cs)) serves the browser workspace at `/maker/` and its JSON/WAV API at
`/v1/maker/*`. The browser receives **text and numbers about game content**: game audio, video and images never reach it, and
`POST /v1/maker/play` makes the game speak. Writes touch only `<game root>/CWYLS Mods/<packId>/data/subtitles/<lang>.json`
and `audio/voice/<lang>/<clip>.wav` inside an editable content pack, and apply live in the running game. Studio v2's
approved narrow exception may later return only creator-uploaded take WAVs from that project's `.maker/takes/` store;
M3 does not expose that audio route.

`ApiRouter.UseMaker(MakerRouter?)` hooks `/maker`, `/maker/*`, `/v1/maker` and `/v1/maker/*` into `ApiRouter` right after
the stopping check (`MakerDefaults.Owns`); when `[Api] Enabled=false`, `MakerRouter.Handle` runs on a maker-only
loopback listener that writes no key file and answers **404** for every other path. **All other `/v1/*` routes keep
refusing browsers unchanged** (`Origin`, `Sec-Fetch-Site` or `Sec-Fetch-Mode` → **403** `browser_refused`, `Host` must be `127.0.0.1:<port>`,
and the `/v1` access key is never accepted on `/v1/maker/*` or vice versa).

### How `/maker/` and `/v1/maker/*` differ from `/v1`

| Control | Rule | Where |
|---|---|---|
| Session-only | active only while a `MakerSurface` is open (`maker` console command); closed at boot | `MakerSurface`, plugin |
| `Host` | `127.0.0.1:<port>` or `localhost:<port>`, else **421** `host_refused` (`HttpListener` binds `127.0.0.1` and accepts only `127.0.0.1:<port>`; `localhost:<port>` works on the TCP transport) | `MakerDefaults.IsAcceptedHost`, `MakerRouter` |
| Browsers allowed (same-origin) | `Origin`, if present, must be `http://127.0.0.1:<port>` or `http://localhost:<port>`; `Sec-Fetch-Site`, if present, must be `same-origin` or `none`; else **403** `forbidden_origin`. No CORS headers are ever sent | `MakerDefaults`, `MakerRouter` |
| Maker token | `/v1/maker/*` needs `Authorization: Bearer <maker token>` (32 random bytes as 64 hex, `ApiToken`, constant-time check): **401** `unauthorized` + `WWW-Authenticate: Bearer realm="cwy-maker"`. Minted per `maker` start, held in memory only, never written to disk or logged, and injected into top-level `/maker/` (`index.html`) right after `<head>` as `<meta name="cwyls-maker-token" content="<token>">` | `MakerSurface`, `MakerRouter`, `MakerStaticFiles.InjectToken` |
| Web files (`/maker/`) | `GET /maker` redirects (**302**) to `/maker/`. `GET /maker/*` reads `BepInEx/plugins/CwyLsModLoader/maker/` and returns `ApiResponse.Raw(200, bytes, contentType, …)` with `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` and `Content-Security-Policy: frame-ancestors 'none'`. Segments must match `[A-Za-z0-9][A-Za-z0-9._-]*` (no `..`, dotfiles or `_*`); symlinks/reparse points are refused; allow-listed extensions only, ≤ 8 MiB | `ApiResponse.Raw`, `MakerStaticFiles`, `MakerRouter` |
| Path segments | paths are never percent-decoded (`%`, `\`, `//`, `..` → **400**). Checked before any backend call: `packId` `[a-z0-9][a-z0-9._-]{0,63}`; `lang` `[a-z]{2,3}(-[a-z0-9]{2,8})?`; `id` and `clip` `[A-Za-z0-9_.()-]{1,128}` (parentheses sent raw, e.g. `Voice_MainScenario_12(5)_001`; dot-only names refused) | `ApiTarget.TryParse`, `MakerSegments` |
| Bodies | JSON: `application/json; charset=utf-8`, ≤ 64 KiB, strict UTF-8 and RFC 8259 JSON. Voice: raw `audio/wav`, ≤ 16 MiB and ≤ 60 s (**413** `too_large`), 16-bit PCM mono or stereo at 8–48 kHz (any non-RIFF/WAVE, truncated header or other format is **415** `unsupported_media_type`; empty body or chunked encoding is **400** `bad_request`) | `MakerRouter`, `MakerWavCheck` |
| Rate limits & timeout | after the token check: reads 20/s (burst 40), event polls 10/s (burst 20), writes/plays 10/s (burst 20), at most 4 waiting event long-polls (**429** `rate_limited` + `Retry-After`). Backend calls wait the 2 s main-thread timeout + 1 s grace, then cancel and answer **503** `unavailable`. HttpListener body acquisition has one fixed 5 s deadline; it never times a bodyless long-poll or backend | `MakerDefaults`, `MakerRouter`, `HttpListenerApiTransport` |
| Editable packs | content pack (no `entryDll`) in `CWYLS Mods`, loaded this session, writable folder, not a reference pack (`cwy.example`, `cwy.demo.*`, `cwy.sample.*`); code mods and failed packs are read-only with a reason | `MakerPackRules.Decide` |
| Audit | one `api/request` record per request with client `maker` (unless `X-CWY-Client` is sent); never logs a header value, body or token | `MakerRouter`, `ApiAuditLog` |

### Routes (`/maker` and `/v1/maker/*`)

| Method + path | Body / query | 200 response | Backend |
|---|---|---|---|
| `GET /maker` | — | **302** to `/maker/` | `MakerRouter` |
| `GET /maker/` · `GET /maker/<path>` | — | static file (`ApiResponse.Raw`; token meta injected into `index.html`) | `MakerStaticFiles` |
| `GET /v1/maker/status` | — | `{gameVersion, loaderVersion, gameLanguage, gameLanguageIsPack, roomReady}` | `IMakerCatalog.StatusAsync` |
| `GET /v1/maker/packs` | — | `{packs:[{id, name, version, languages[], editable, readOnlyReason}]}` | `IMakerCatalog.PacksAsync` |
| `GET /v1/maker/lines` | `lang` (required), `q` (≤ 200 chars), `filter` (`all`/`missing-subtitle`/`missing-dub`/`done`), `offset` (≥ 0), `limit` (1–500, default 100) | `{total, offset, items:[Line]}` | `IMakerCatalog.LinesAsync` |
| `GET /v1/maker/lines/{id}` | `lang` (required) | `Line` | `IMakerCatalog.LineAsync` |
| `PUT /v1/maker/packs/{packId}/subtitles/{lang}/{id}` | `{"text":"…"}` (1–2000 Unicode code points) | `Saved` | `IMakerEditor.SaveSubtitleAsync` |
| `DELETE /v1/maker/packs/{packId}/subtitles/{lang}/{id}` | — | `Saved` | `IMakerEditor.SaveSubtitleAsync(null)` |
| `PUT /v1/maker/packs/{packId}/voice/{lang}/{clip}` | raw `audio/wav` (PCM 16-bit, mono or stereo, 8–48 kHz, ≤ 60 s, ≤ 16 MiB) | `Saved` | `IMakerEditor.SaveVoiceAsync` |
| `DELETE /v1/maker/packs/{packId}/voice/{lang}/{clip}` | — | `Saved` | `IMakerEditor.SaveVoiceAsync(null)` |
| `POST /v1/maker/play` | `{"id":"…","variant":"original"\|"dub","lang":"…"}` | `Saved` (`live:true`, `note`) | `IMakerEditor.PlayAsync` |
| `GET /v1/maker/events` | `after` (≥ 0), `wait` (0–25 s), `limit` (1–500, default 100) | same envelope as `/v1/events` | `MakerEventRing` |
| `GET /v1/maker/studio/capabilities` | — | installed v2 features and limits; 503 until the Studio container exists | `MakerStudioBackends` |
| `GET /v1/maker/studio/status` | — | cached language/room plus independent nullable focus, pause and busy facts | `IMakerStudioStatus` |
| `PATCH /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/workflow` | guarded status/star patch | `{revision,workflow}` | `IMakerStudioWorkflow` |
| `PUT …/lines/{id}/subtitle` · `GET …/lines/{id}/history` · `POST …/history/{revisionId}/revert` | guarded text save; page; guarded revert | `StudioSaved` / `HistoryPage` | `IMakerStudioSubtitles` |
| `GET|PUT /v1/maker/studio/packs/{packId}/glossary/{lang}` | read / guarded complete replacement | `Glossary` | `IMakerStudioProjects` |
| `GET /v1/maker/studio/packs/{packId}/operations/{operationId}` | — | committed/pending receipt state | `IMakerStudioProjects` |

`Line` = `{id, voice, group, durationSec, ja, en, subtitle, subtitlePack, dubPack, dubDurationSec}`. Omitted lines
(`*_omit` or a voice name with no clip anywhere) have `group:"(omitted)"` and `durationSec:null`, sort last under
`filter=all`, and are excluded from `missing-subtitle`, `missing-dub` and `done`. `Saved` = `{live, path, durationSec, note}`.

### Events (`/v1/maker/events`)

| `type` | `data` |
|---|---|
| `line.spoken` | as `/v1/events` (`voice`, `text`, `textJa`, `language`, `localizationId`, `source`, `scenarioType`, `scenarioGroupId`, `isStory`, `durationSec`) |
| `maker.subtitle.saved` | `packId`, `lang`, `id`, `removed` |
| `maker.voice.saved` | `packId`, `lang`, `clip`, `durationSec`, `removed` |
| `maker.play` | `id`, `variant`, `lang`, `voice` |
| `game.language` | `code`, `isPack` |
| `room.ready` | `ready` |

### Errors → HTTP

`bad_request` **400** · `unauthorized` **401** · `read_only_pack` **403** · `forbidden_origin` **403** · `not_found` **404**
(also returned when a known maker path is called with the wrong HTTP method, naming the allowed method) · `request_timeout` **408**
(the body did not arrive within its 5 s deadline) · `not_ready` **409** ·
`too_large` **413** · `unsupported_media_type` **415** · `host_refused` **421** · `rate_limited` **429** · `internal` **500** ·
`unavailable` **503**.

### Pack Maker types (`CwyLs.ModLoader.Core.Api` and `.Maker`)

| Type | Role |
|---|---|
| `ApiRouter.UseMaker`, `ApiResponse.Raw` | attaches/detaches `MakerRouter` on the `/v1` router; serves non-JSON byte bodies (`Bytes`, `MediaType`, `BodyBytes()`) for `/maker/*` |
| `IMakerCatalog`, `IMakerEditor`, `IMakerEventSink`, `MakerBackends`, `MakerErrorCodes`, `MakerResult<T>` | adapter contracts, static backend slots (`UseCatalog`, `UseEditor`, `UseEvents`, `Publish`) and error codes (`MakerContracts.cs`) |
| `MakerDefaults`, `MakerHttpCodes`, `MakerSegments` | paths, size/rate/time caps, `Host`/`Origin`/`Sec-Fetch-Site` checks, error-to-status map, segment validators |
| `MakerRouter` (+ `MakerRouterOptions`), `MakerSurface`, `MakerJson` | request pipeline, per-open session (token + event ring + router) and explicit wire serialisers |
| `MakerStaticFiles`, `MakerWavCheck`, `MakerEventRing` | safe static file reader with `<meta name="cwyls-maker-token">` injection, RIFF/WAVE header validator, 1024-event scalar ring |
| `MakerGameActivity`, `MakerGameActivitySnapshot`, `IMakerStudioStatus` | thread-safe activity cache and M3 status seam; status reads never wait for the main thread |
| `MakerProjectStore`, `MakerCommit`, `MakerOperationLedger`, `MakerWorkflowStore`, `MakerGlossaryStore`, `MakerHistoryStore` | M4 `.maker/` sidecars, quota/path checks, guarded revisions, receipts and recoverable multi-file subtitle commits |
| `MakerTakeStore`, `MakerPublishStore`, `MakerClipStore`, `MakerVoiceProvenance` | M5c/M5d takes and creator WAVs under `.maker/takes/`, publication of a take's render as the runtime voice (one recoverable commit with every moved-aside file), per-(language, clip) records shared by alias lines, and the pack-root `voice-provenance.json` disclosure |
| `MakerCatalogView`, `MakerLineRules`, `MakerPackRules`, `MakerPackScanner`, `MakerAudioLength`, `MakerSpokenCache` | Unity-free catalogue rules, omitted-line and editable-pack decisions, pack disk scanner, WAV/Ogg header duration reader |

Tests: `src/CwyLs.ModLoader.Tests/Maker*.cs`.

<!-- Sources: CONTRACT.md:1-71; src/CwyLs.ModLoader.Core/Api/ApiRouter.cs:169-174,229-258; src/CwyLs.ModLoader.Core/Api/ApiHttp.cs:179-231,280,325-352; src/CwyLs.ModLoader.Core/Api/Maker/MakerContracts.cs:14-168; src/CwyLs.ModLoader.Core/Api/Maker/MakerDefaults.cs:11-273; src/CwyLs.ModLoader.Core/Api/Maker/MakerRouter.cs:13-961; src/CwyLs.ModLoader.Core/Api/Maker/MakerStaticFiles.cs:33-275; src/CwyLs.ModLoader.Core/Api/Maker/MakerEventRing.cs:15-82; src/CwyLs.ModLoader.Core/Api/Maker/MakerSurface.cs:12-72; src/CwyLs.ModLoader.Core/Api/Maker/MakerWavCheck.cs:7-124; src/CwyLs.ModLoader.Core/Api/Maker/MakerJson.cs:10-96; src/CwyLs.ModLoader.Core/Api/Maker/Catalog/MakerLineRules.cs:16-143; src/CwyLs.ModLoader.Core/Api/Maker/Catalog/MakerPackRules.cs:108-244; src/CwyLs.ModLoader.Bulbul/Maker/MakerEvents.cs:141-237 -->
