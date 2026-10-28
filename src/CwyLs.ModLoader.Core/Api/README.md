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
| Strict input | origin-form targets only, no `%`/`..`/`//` in paths, unknown or repeated query parameters refused, GET has no body, POST body ≤ 64 KiB, `Content-Length` required (chunked: **411**), `application/json` only (**415**), strict UTF-8 and strict RFC 8259 JSON (`JsonReader`) | `ApiRouter`, `ApiTarget`, `ApiHttpParser` |
| Rate limits | after the key check only: reads 20/s (burst 40), event polls 10/s (burst 20), action starts 4/s (burst 8); 4 long-polls at once; 8 actions in flight; 16 requests at once per transport: **429** + `Retry-After`, or **503** | `ApiRateLimiter`, transports |
| Tiers | read actions always; act/write only with `[Api] AllowActions=true`; anything that needs the player's confirmation (write tier, destructive) is **refused** while no in-game confirmation dialog is installed (never auto-approved); writes also need `[Data] AllowWrites` (checked by the runner) | `ApiActionCatalog.Decide` + `ActionGate` |
| Main thread | requests never touch Unity: `/v1/state` is served from a document the main thread publishes; actions go to the runner, which waits ≤ 2 s (wall clock) for the main thread; the API's own main-thread work (state rebuild) is timed against a 4 ms budget and backs off | `ApiRuntime`, `ApiRefreshPolicy` |
| Privacy | `/v1/state` and events never carry the text of notes, the diary, todos, habits or calendar entries (titles included): ids, counts, lengths, dates and states only. Profile, account and other mods' `extra` sections are left out | `ApiStateProjection`, `ApiEventMapper` |
| Audit | one record per request (bus `api/request`, tag `API`): method, path (no query), status, ms, client, transport, tier/handle/error; never a header value or body | `ApiAuditLog` |

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
| `browser_refused` | 403 | `Origin` or `Sec-Fetch-*` present |
| `denied` | 403 | the tier gate refuses API callers (`[Api] AllowActions=false`) |
| `confirmation_unavailable` | 403 | needs the player's Yes in game, and no in-game dialog is installed |
| `not_found` | 404 | no such route, action or handle (handles expire 10 min after they finish) |
| `method_not_allowed` | 405 | see `Allow` |
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
