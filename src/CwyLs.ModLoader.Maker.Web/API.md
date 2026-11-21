# Maker API · Dub & Subtitle Studio

DS-00 v1.2 plus the Studio v2 contract (v2–v2.5.1). Generated from `modules/loader/routes.js`.
The v1 routes are used by the v1 HTTP adapter and Wiring view. Studio routes are capability-gated via
`GET /v1/maker/studio/capabilities` and consumed by the Studio v2 UI when their backend feature is installed.
Run `node generate_api.mjs --check` from this directory to check it.

## Opening and boundaries

The console command `maker` opens `http://127.0.0.1:<port>/maker/?k=<one-time nonce>`.
Each `maker` or `maker serve` call mints a fresh 256-bit link, valid for 60 seconds (monotonic clock),
compared in constant time and atomically consumed once. At most 16 unconsumed links are kept; older links expire.
Redeeming it sets a host-only `cwyls-maker-<port>` session cookie (`HttpOnly; SameSite=Strict; Path=/maker/`)
and redirects to `/maker/` without the nonce. The cookie survives page reloads while this Pack Maker session runs.
Cookies have no port isolation; the name includes the port and each router has an independent random cookie value.
Cookie-only requests never authenticate maker API calls; those still require the bearer below.
Without a valid link or session cookie, `/maker/` and `/maker/index.html` show only EN/TH instructions to type
`maker` in the game console. A wrong, expired or reused link shows those instructions even with a valid cookie.
`GET /maker` redirects to `/maker/`, preserving a supplied nonce. Page responses use `Referrer-Policy: no-referrer`;
request traces omit page queries. The loader injects `<meta name="cwyls-maker-token" content="PER_START_RANDOM_TOKEN">`
only into the authenticated page.
The token differs from the API key, changes each start and is never written to disk by the app.
For lab automation, `maker serve` returns the one-time URL without opening a tab: follow its redirect with a
cookie-aware client within 60 seconds, retain the cookie for reloads and read the token from that authenticated page.
Run `maker serve` again for another client or after expiry. See `tools/README.md` for an in-memory Python example.
This prevents unauthenticated loopback token scraping; it does not isolate the browser from programs that can
read its memory/profile or intercept the launch link.
Only text and numbers about game content reach the browser. Play asks the game to speak.
The narrow Studio v2 take-audio GET may return only creator-uploaded WAV bytes from that project's
`.maker/takes/` store. It can never return game audio, reference-pack audio or another path.

The v1 loader writes only editable loaded content packs under `<game root>/CWYLS Mods/<packId>/`:
`data/subtitles/<lang>.json` and `audio/voice/<lang>/<clip>.wav`. Studio v2 also owns creator work under
`.maker/` and published disclosure in root `voice-provenance.json`. The runtime ignores `.maker/`.
Changes to loaded packs are live; a newly created pack is pending and read-only until one restart.
Code mods, failed packs and reference packs are read-only, with a reason from packs.

## Request rules

Loopback only. The promised Host is `127.0.0.1:<port>`; HttpListener may refuse `localhost` before the router.
For `/maker/*` and `/v1/maker/*`, Origin (when present) must equal the loopback http origin at the listener port;
Sec-Fetch-Site (when present) must be same-origin or none, otherwise 403 forbidden_origin.
No CORS headers. Other `/v1/*` routes retain browser refusal.
Every maker API call uses `Authorization: Bearer <maker token>`, otherwise 401 unauthorized.
JSON bodies: `application/json; charset=utf-8`, at most 64 KiB.
Voice bodies: `audio/wav`, at most 16 MiB and 60 s, PCM 16-bit mono/stereo, 8–48 kHz.
Responses use the status and media type printed for each route, always with `Cache-Control: no-store`.

Segments: packId `[a-z0-9][a-z0-9._-]{0,63}`; lang `[a-z]{2,3}(-[a-z0-9]{2,8})?`;
id and clip `[A-Za-z0-9_.()-]{1,128}`. Invalid segments are 400 before calling the backend.

Errors use `{"error":{"code":"bad_request","message":"Explanation"}}`.

| HTTP | Code |
| --- | --- |
| 400 | bad_request |
| 401 | unauthorized |
| 403 | read_only_pack / forbidden_origin / consent_required |
| 404 | not_found |
| 408 | request_timeout (incomplete request body deadline) |
| 409 | not_ready / conflict / take_limit / take_in_use / render_required / restart_required / game_paused |
| 413 | too_large / storage_limit |
| 415 | unsupported_media_type |
| 421 | host_refused |
| 429 | rate_limited |
| 500 | internal |
| 503 | unavailable / metadata_unavailable / recovery_required |

The client stops requests/polling after 401 and asks the creator to reopen from the game. It honours Retry-After,
retries reads/idempotent writes at most once, and never retries action POSTs automatically. Drafts survive errors.
Event polls back off after transport failures. Closing aborts pending fetches.

## Data and events

Line = {id, voice, group, durationSec, ja, en, subtitle, subtitlePack, dubPack, dubDurationSec}.
voice, group, durationSec, subtitle, subtitlePack, dubPack and dubDurationSec may be null. JA/EN are read-only text.
Saved = {live, path, durationSec, note}; path and durationSec may be null. A live:false save is never labelled live.
Packs = {packs:[{id,name,version,languages[],editable,readOnlyReason}]}; readOnlyReason may be null.
Status = {gameVersion,loaderVersion,gameLanguage,gameLanguageIsPack,roomReady}.

Events use the same envelope as `/v1/events`: epoch, events, next, latest, oldest, dropped, reset, timedOut.
Poll with next, not latest. Refresh catalogue/status on reset, dropped events or changed epoch; keep subtitle drafts.
Unknown event types are tolerated.

M4c: recovery_required keeps its message and adds error.details = {packId, resource, blockedOperationId}.
The HTTP adapter exposes this as LoaderError.details. Use blockedOperationId for recovery, never parse the message.
M5a implements recovery preview and keep-current resolution under projects. Use a fresh resolution operationId
and the preview's expectedRecoveryRevision. Archives are pack-relative, quota-counted, and the result is live:false.
Refetch line/status after resolution; a replay returns its durable receipt and never applies live state.
M5b: an empty 2xx mutation response is rejected locally as LoaderError code uncertain and emits resync;
it is never automatically replayed. Refetch the line and status. A failed receipt finalisation returns the
durable live:false shape so the first reply and its replay stay identical.

Studio v2.1: projects means storage/glossary/operations. Independent newPacks appears when pack creation is available
(when the packs folder exists and is writable). After an uncertain reply, any replay or any live:false,
refetch the line and status. A receipt never retries a live action.
Read-only reasons are human-readable string|null; ownedSubtitle is selected-pack string|null and ownedDub is boolean.
Both the real backend and the Studio dev backend implement v2 catalog reads (capability-gated by `catalog`).

| Type | Data |
| --- | --- |
| line.spoken | voice, text, textJa, language, localizationId, lineId, source, scenarioGroupId, durationSec |
| maker.subtitle.saved | packId, lang, id, removed |
| maker.voice.saved | packId, lang, clip, durationSec, removed |
| maker.play | id, variant, lang, voice |
| game.language | code, isPack |
| room.ready | ready |
| game.focus | focused, paused, busy, reason, observedAt |

## v1 routes

### status: GET /v1/maker/status

Game language and room readiness.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "gameVersion": "1.18.0",
  "loaderVersion": "0.9.2",
  "gameLanguage": "en",
  "gameLanguageIsPack": false,
  "roomReady": true
}
```

### packs: GET /v1/maker/packs

Editable loaded content packs and read-only reasons.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "packs": [
    {
      "id": "sample.studio",
      "name": "Practice studio",
      "version": "0.1.0",
      "languages": [
        "th",
        "en"
      ],
      "editable": true,
      "readOnlyReason": null
    }
  ]
}
```

### lines: GET /v1/maker/lines?lang={lang}&q={q}&filter={filter}&offset={offset}&limit={limit}

lang required; q optional; filter all/missing-subtitle/missing-dub/done; offset >= 0; limit 1–500.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "total": 1,
  "offset": 0,
  "items": [
    {
      "id": "sample_click_001",
      "voice": "sample_voice_001",
      "group": "Sample greetings",
      "durationSec": 3,
      "ja": "練習用の短い挨拶です。",
      "en": "An invented greeting for practice.",
      "subtitle": null,
      "subtitlePack": null,
      "dubPack": null,
      "dubDurationSec": null
    }
  ]
}
```

### line: GET /v1/maker/lines/{id}?lang={lang}

Reference text and subtitle/dub metadata only. No game media.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "id": "sample_click_001",
  "voice": "sample_voice_001",
  "group": "Sample greetings",
  "durationSec": 3,
  "ja": "練習用の短い挨拶です。",
  "en": "An invented greeting for practice.",
  "subtitle": null,
  "subtitlePack": null,
  "dubPack": null,
  "dubDurationSec": null
}
```

### subtitle: PUT /v1/maker/packs/{packId}/subtitles/{lang}/{id}

1–2000 characters. Reloads the loaded pack live.

Request:

```json
{
  "text": "คำทักทายสำหรับฝึกซ้อม"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "live": true,
  "path": "data/subtitles/th.json",
  "durationSec": null,
  "note": "Subtitle reloaded."
}
```

### deleteSubtitle: DELETE /v1/maker/packs/{packId}/subtitles/{lang}/{id}

Removes the selected pack subtitle override.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "live": true,
  "path": "data/subtitles/th.json",
  "durationSec": null,
  "note": "Subtitle reloaded."
}
```

### voice: PUT /v1/maker/packs/{packId}/voice/{lang}/{clip}

PCM signed 16-bit, mono/stereo, 8–48 kHz; at most 16 MiB and 60 s.

Request:

audio/wav raw bytes

200 response example (application/json; charset=utf-8):

```json
{
  "live": true,
  "path": "audio/voice/th/sample_voice_001.wav",
  "durationSec": 3,
  "note": "Subtitle reloaded."
}
```

### deleteVoice: DELETE /v1/maker/packs/{packId}/voice/{lang}/{clip}

Removes the selected pack voice override.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "live": true,
  "path": "audio/voice/th/sample_voice_001.wav",
  "durationSec": null,
  "note": "Subtitle reloaded."
}
```

### play: POST /v1/maker/play

variant original or dub. The game plays audio; no game audio is returned.

Request:

```json
{
  "id": "sample_click_001",
  "variant": "original",
  "lang": "th"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "live": true,
  "path": null,
  "durationSec": null,
  "note": "Playing in game."
}
```

### events: GET /v1/maker/events?after={after}&wait={wait}&limit={limit}

Same envelope as /v1/events. after >= 0; wait 0–25; limit 1–500. Poll next, refresh on reset/dropped/epoch change.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "epoch": "sample-start",
  "events": [
    {
      "seq": 1,
      "time": "2026-11-07T00:00:00Z",
      "type": "room.ready",
      "data": {
        "ready": true
      }
    }
  ],
  "next": 1,
  "latest": 1,
  "oldest": 1,
  "dropped": 0,
  "reset": false,
  "timedOut": false
}
```

## Studio v2 routes (capability-gated)

### studioRecovery: GET /v1/maker/studio/packs/{packId}/operations/{blockedOperationId}/recovery

Bounded identifiers and fingerprints only. No archived bytes are returned. Part of projects.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "recoveryRevision": "0123456789abcdef0123456789abcdef",
  "blockedOperationId": "11111111111111111111111111111111",
  "resources": []
}
```

### studioResolve: POST /v1/maker/studio/packs/{packId}/operations/{blockedOperationId}/resolve

Archive current and intended versions before retiring the blocked intent. Changed previews return 409; no live action is attempted.

Request:

```json
{
  "operationId": "22222222222222222222222222222222",
  "expectedRecoveryRevision": "0123456789abcdef0123456789abcdef",
  "strategy": "keep-current"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "resolved": true,
  "operationId": "22222222222222222222222222222222",
  "blockedOperationId": "11111111111111111111111111111111",
  "revision": "sample-resolution",
  "archivePath": ".maker/recovery/11111111111111111111111111111111/22222222222222222222222222222222",
  "live": false
}
```

### studioCapabilities: GET /v1/maker/studio/capabilities

Installed Studio v2 features and fixed limits. uiText is false: lists, search, groups, progress and line routes cover only localization ids referenced by scenario rows. UI text stays in packs and exports. features remains the installed-backend array. A missing Studio backend is 503; an empty installed backend advertises no features.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "contract": 2,
  "features": [],
  "uiText": false,
  "limits": {
    "jsonBytes": 65536,
    "wavBytes": 16777216,
    "wavSeconds": 60,
    "subtitleRevisions": 20,
    "takesPerLine": 3,
    "projectBytes": 2147483648
  },
  "storageSchema": 1,
  "consentVersion": "consent-1"
}
```

### studioStatus: GET /v1/maker/studio/status

Cached focus, pause and busy facts. Nullable means unknown; stale does not prove pause.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "gameVersion": "1.18.0",
  "loaderVersion": "0.9.2",
  "gameLanguage": "th",
  "gameLanguageIsPack": true,
  "roomReady": true,
  "focused": true,
  "paused": false,
  "busy": false,
  "reason": null,
  "observedAt": "2026-11-08T00:00:00Z",
  "stale": false,
  "focusOnPlay": true
}
```

### studioPacks: GET /v1/maker/studio/packs

Loaded packs plus Maker-created pending packs and Studio metadata state.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "packs": [
    {
      "id": "sample.studio",
      "name": "Practice studio",
      "version": "0.1.0",
      "languages": [
        "th"
      ],
      "editable": true,
      "readOnlyReason": null,
      "loaded": true,
      "restartRequired": false,
      "studioEditable": true,
      "studioReadOnlyReason": null,
      "metadataState": "ready"
    }
  ]
}
```

### studioLines: GET /v1/maker/studio/packs/{packId}/languages/{lang}/lines?q={q}&group={group}&filter={filter}&offset={offset}&limit={limit}&catalogRevision={catalogRevision}

Pack-scoped workflow filters and revision-consistent paging. Optional q, group and catalogRevision.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "total": 1,
  "offset": 0,
  "catalogRevision": "sample-catalog",
  "items": []
}
```

### studioLine: GET /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}?occurrenceId={occurrenceId}

Full selected-pack state and optional occurrence context. occurrenceId is optional. readOnly (also on list items) is null, or id_not_a_file_name when the line id cannot name a Studio file (a Windows device name or a trailing dot): the line is listed and playable, but its takes, history and edits answer 400.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "id": "sample_click_001",
  "voice": "sample_voice_001",
  "group": "Sample greetings",
  "durationSec": 3,
  "ja": "練習用の短い挨拶です。",
  "en": "An invented greeting for practice.",
  "subtitle": null,
  "subtitlePack": null,
  "dubPack": null,
  "dubDurationSec": null,
  "ownedSubtitle": null,
  "ownedDub": false,
  "workflow": {
    "status": "draft",
    "starred": false
  },
  "takeCount": 0,
  "revision": "sample-revision",
  "clipRevision": "sample-clip",
  "publishedTakeId": null,
  "publishedTakeRevision": null,
  "context": {
    "occurrences": [],
    "selectedOccurrenceId": null,
    "mood": null,
    "pose": null,
    "source": null
  },
  "readOnly": null
}
```

### studioGroups: GET /v1/maker/studio/packs/{packId}/languages/{lang}/groups

Scene groups and additive pack-scoped progress.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "catalogRevision": "sample-catalog",
  "overall": {
    "totalLines": 1,
    "dubbableLines": 1,
    "subtitlePresent": 0,
    "dubPresent": 0,
    "todo": 1,
    "draft": 0,
    "done": 0,
    "needsRetake": 0,
    "starred": 0
  },
  "groups": []
}
```

### studioWorkflow: PATCH /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/workflow

Four states: todo, draft, done, needs-retake. At least one patch field is required.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "status": "draft",
  "starred": false
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "workflow": {
    "status": "draft",
    "starred": false
  }
}
```

### studioSubtitle: PUT /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/subtitle

text is 1–2000 code points; null removes. A real change appends history.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "text": "คำทักทายสำหรับฝึกซ้อม"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "saved": true,
  "live": true,
  "path": "data/subtitles/th.json",
  "durationSec": null,
  "note": "Saved.",
  "publishedTakeId": null,
  "effectivePackId": "sample.studio",
  "restartRequired": false
}
```

### studioHistory: GET /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/history?offset={offset}&limit={limit}

At most 20 revisions including deletions; text null represents a removal.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "total": 1,
  "offset": 0,
  "items": [
    {
      "revisionId": "0123456789abcdef0123456789abcdef",
      "createdAt": "2026-11-08T00:00:00Z",
      "text": "คำทักทายสำหรับฝึกซ้อม",
      "source": "studio"
    }
  ],
  "historyGap": false
}
```

### studioRevert: POST /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/history/{revisionId}/revert

Reapplies the retained value and creates a new revision.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "saved": true,
  "live": true,
  "path": "data/subtitles/th.json",
  "durationSec": null,
  "note": "Saved.",
  "publishedTakeId": null,
  "effectivePackId": "sample.studio",
  "restartRequired": false
}
```

### studioTakes: GET /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/takes

At most three creator takes. Favourite and published are independent. clipRevision (for publish/unpublish) is null when the clip cannot be named right now.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "favouriteTakeId": null,
  "publishedTakeId": null,
  "clipRevision": "sample-clip",
  "items": [
    {
      "id": "0123456789abcdef0123456789abcdef",
      "origin": "own",
      "consent": {
        "confirmed": true,
        "statementVersion": "consent-1",
        "confirmedAt": "2026-11-08T00:00:00Z"
      },
      "sourceRevision": "sample-source",
      "sourceDurationSec": 3,
      "renderDurationSec": 2.8,
      "trimStartSec": 0.1,
      "trimEndSec": 2.9,
      "evenOutVolume": false,
      "sourcePresent": true,
      "renderReady": true,
      "createdAt": "2026-11-08T00:00:00Z",
      "updatedAt": "2026-11-08T00:00:00Z",
      "consentSource": "take"
    }
  ]
}
```

### studioCreateTake: POST /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/takes

Reserves one of three slots. Origin is own, ai-assisted or ai-generated. v2.4: omit both origin and consent to inherit the pack voice default (take.consentSource pack; v2.5: it follows later changes of the default); 403 consent_required when the pack has none, 400 when only one of the two is sent. take.consentSource is take or pack.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "origin": "own",
  "consent": {
    "confirmed": true,
    "statementVersion": "consent-1"
  }
}
```

201 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "take": {
    "id": "0123456789abcdef0123456789abcdef",
    "origin": "own",
    "consent": {
      "confirmed": true,
      "statementVersion": "consent-1",
      "confirmedAt": "2026-11-08T00:00:00Z"
    },
    "sourceRevision": "sample-source",
    "sourceDurationSec": 3,
    "renderDurationSec": 2.8,
    "trimStartSec": 0.1,
    "trimEndSec": 2.9,
    "evenOutVolume": false,
    "sourcePresent": true,
    "renderReady": true,
    "createdAt": "2026-11-08T00:00:00Z",
    "updatedAt": "2026-11-08T00:00:00Z",
    "consentSource": "take"
  }
}
```

### studioTakeAudio: PUT /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/takes/{takeId}/audio?kind={kind}&expectedRevision={expectedRevision}&operationId={operationId}

kind source or render. Render must be PCM16 48 kHz mono; source retains the v1 WAV bounds.

Request:

audio/wav raw bytes

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "take": {
    "id": "0123456789abcdef0123456789abcdef",
    "origin": "own",
    "consent": {
      "confirmed": true,
      "statementVersion": "consent-1",
      "confirmedAt": "2026-11-08T00:00:00Z"
    },
    "sourceRevision": "sample-source",
    "sourceDurationSec": 3,
    "renderDurationSec": 2.8,
    "trimStartSec": 0.1,
    "trimEndSec": 2.9,
    "evenOutVolume": false,
    "sourcePresent": true,
    "renderReady": true,
    "createdAt": "2026-11-08T00:00:00Z",
    "updatedAt": "2026-11-08T00:00:00Z",
    "consentSource": "take"
  }
}
```

### studioReadTakeAudio: GET /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/takes/{takeId}/audio?kind={kind}

Only bytes under this project's .maker/takes store. Never game, reference-pack or arbitrary-path audio.

Request:

No body.

200 response example (audio/wav):

audio/wav raw bytes

### studioTake: PATCH /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/takes/{takeId}

Omitted trim fields stay unchanged; explicit null clears them. Either trim change invalidates the prepared render. v2.4: sending origin or consent overrides an inherited default and sets consentSource take. v2.5.1: followPackDefault true (without origin and consent) puts the take back on the pack default (consentSource pack, the current default now and its later changes; a published take's voice-provenance.json disclosure follows in the same commit); 403 consent_required when the pack has no default.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "origin": "own",
  "consent": {
    "confirmed": true,
    "statementVersion": "consent-1"
  },
  "trimStartSec": 0.1,
  "trimEndSec": 2.9,
  "evenOutVolume": false
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "take": {
    "id": "0123456789abcdef0123456789abcdef",
    "origin": "own",
    "consent": {
      "confirmed": true,
      "statementVersion": "consent-1",
      "confirmedAt": "2026-11-08T00:00:00Z"
    },
    "sourceRevision": "sample-source",
    "sourceDurationSec": 3,
    "renderDurationSec": 2.8,
    "trimStartSec": 0.1,
    "trimEndSec": 2.9,
    "evenOutVolume": false,
    "sourcePresent": true,
    "renderReady": true,
    "createdAt": "2026-11-08T00:00:00Z",
    "updatedAt": "2026-11-08T00:00:00Z",
    "consentSource": "take"
  }
}
```

### studioDeleteTake: DELETE /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/takes/{takeId}?expectedRevision={expectedRevision}&operationId={operationId}

Published takes must be unpublished or replaced first. No automatic eviction.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "removed": true
}
```

### studioFavourite: PUT /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/favourite

takeId null clears the favourite. This never publishes audio.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "takeId": "0123456789abcdef0123456789abcdef"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "favouriteTakeId": "0123456789abcdef0123456789abcdef"
}
```

### studioPublish: POST /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/publish

Publishes the selected prepared render and matching voice-provenance.json disclosure.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "expectedClipRevision": "sample-clip",
  "operationId": "0123456789abcdef0123456789abcdef",
  "takeId": "0123456789abcdef0123456789abcdef"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "saved": true,
  "live": true,
  "path": "audio/voice/th/sample_voice_001.wav",
  "durationSec": null,
  "note": "Saved.",
  "publishedTakeId": "0123456789abcdef0123456789abcdef",
  "effectivePackId": "sample.studio",
  "restartRequired": false
}
```

### studioUnpublish: POST /v1/maker/studio/packs/{packId}/languages/{lang}/lines/{id}/unpublish

Removes this pack's dub for the clip: a Studio publication is deleted and what it moved aside is restored; a dub made outside Studio is moved to _maker_backup/. Reconciles provenance.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "expectedClipRevision": "sample-clip",
  "operationId": "0123456789abcdef0123456789abcdef"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "saved": true,
  "live": true,
  "path": "audio/voice/th/sample_voice_001.wav",
  "durationSec": null,
  "note": "Saved.",
  "publishedTakeId": null,
  "effectivePackId": "sample.studio",
  "restartRequired": false
}
```

### studioGlossary: GET /v1/maker/studio/packs/{packId}/glossary/{lang}

Creator-owned glossary; at most 200 entries and 64 KiB encoded.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "entries": [
    {
      "id": "satone",
      "sourceJa": "サトネ",
      "sourceEn": "Satone",
      "target": "ซาโตเนะ",
      "note": null
    }
  ]
}
```

### studioSaveGlossary: PUT /v1/maker/studio/packs/{packId}/glossary/{lang}

Complete bounded replacement with duplicate IDs refused.

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "entries": []
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "entries": []
}
```

### studioCreatePack: POST /v1/maker/studio/packs

Creates a content-only pack. It remains pending and read-only until one restart. author and description (v2.3) are optional.

Request:

```json
{
  "operationId": "0123456789abcdef0123456789abcdef",
  "id": "sample.studio",
  "name": "Practice studio",
  "author": "Creator",
  "description": "An invented practice pack.",
  "language": {
    "code": "th",
    "caption": "ไทย",
    "englishName": "Thai",
    "script": "Thai",
    "fallback": [
      "en"
    ]
  }
}
```

201 response example (application/json; charset=utf-8):

```json
{
  "pack": {
    "id": "sample.studio",
    "name": "Practice studio",
    "version": "0.1.0",
    "languages": [
      "th"
    ],
    "editable": false,
    "readOnlyReason": "restart required",
    "loaded": false,
    "restartRequired": true,
    "studioEditable": false,
    "studioReadOnlyReason": "restart required",
    "metadataState": "pending"
  },
  "restartRequired": true,
  "live": false,
  "note": "Restart the game once to load this pack."
}
```

### studioPackInfo: GET /v1/maker/studio/packs/{packId}/info

Manifest author and description, icon presence, player-pack size and the latest CHANGELOG.md section (changelog null when none). Also answers for a Maker-created pending pack.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "id": "sample.studio",
  "name": "Practice studio",
  "version": "0.1.0",
  "author": "Creator",
  "description": "An invented practice pack.",
  "languages": [
    "th"
  ],
  "hasIcon": true,
  "sizeBytes": 1048576,
  "pending": false,
  "changelog": {
    "version": "0.1.0",
    "notes": "First practice release."
  }
}
```

### studioPackIcon: GET /v1/maker/studio/packs/{packId}/icon

The pack's own icon PNG, at most 512 KiB. 404 when the pack has none; 403 for a reference pack.

Request:

No body.

200 response example (image/png):

image/png raw bytes

### studioSavePackIcon: PUT /v1/maker/studio/packs/{packId}/icon

Square PNG, 64–1024 px, at most 512 KiB. The previous icon moves to _maker_backup/. Also works on a Maker-created pending pack. 403 read-only, 413 too large, 415 not an acceptable PNG.

Request:

image/png raw bytes

200 response example (application/json; charset=utf-8):

```json
{
  "hasIcon": true,
  "sizeBytes": 20480,
  "width": 256,
  "height": 256
}
```

### studioExportCheck: GET /v1/maker/studio/packs/{packId}/export

Checks manifest, subtitles, audio, provenance, size, progress and links (at most 20 paths each), the next SemVer versions and what the zip would hold. Each check has code + params for the page to word in its language (message is the English); progress params are {langs:[{lang,dub,voiced,sub,total}]}. 403 read-only, 409 restart_required.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "packId": "sample.studio",
  "version": "0.1.0",
  "nextVersions": {
    "patch": "0.1.1",
    "minor": "0.2.0",
    "major": "1.0.0"
  },
  "ok": true,
  "errorCount": 0,
  "warningCount": 0,
  "checks": [
    {
      "id": "manifest",
      "severity": "ok",
      "message": "Sample Studio 0.1.0.",
      "count": 0,
      "paths": [],
      "code": "manifestOk",
      "params": {
        "name": "Sample Studio",
        "version": "0.1.0"
      }
    },
    {
      "id": "provenance",
      "severity": "ok",
      "message": "1 dub(s) disclosed.",
      "count": 1,
      "paths": [],
      "code": "provCount",
      "params": {
        "n": 1
      }
    }
  ],
  "files": {
    "included": 42,
    "includedBytes": 1048576,
    "excluded": 12,
    "excludedBytes": 524288
  },
  "changelog": {
    "version": "0.1.0",
    "notes": "First practice release."
  },
  "lastExport": null
}
```

### studioExport: POST /v1/maker/studio/packs/{packId}/export

bump none, patch, minor or major; notes null or at most 4000 code points (prepended to CHANGELOG.md). folder is where the zip is saved on this computer (also on lastExport; absent when unknown); downloadable false means over 256 MiB, take it from folder (the download answers 413). The zip has one top folder <packId>/ and leaves out .maker/, every dot path, _maker_backup/ and symlinks. Replaying the same operationId returns the same 201; 409 conflict (stale expectedVersion or reused id), 409 export_blocked (a check is an error).

Request:

```json
{
  "operationId": "0123456789abcdef0123456789abcdef",
  "expectedVersion": "0.1.0",
  "bump": "patch",
  "notes": "Fixed two subtitles."
}
```

201 response example (application/json; charset=utf-8):

```json
{
  "packId": "sample.studio",
  "version": "0.1.1",
  "previousVersion": "0.1.0",
  "fileName": "sample.studio-v0.1.1.zip",
  "sizeBytes": 1048576,
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "files": 42,
  "downloadable": true,
  "manifestUpdated": true,
  "changelogUpdated": true,
  "warningCount": 1,
  "note": "The loaded pack shows the new version after the next game start.",
  "folder": "C:/Games/CWYLS Mods/.maker-exports/sample.studio"
}
```

### studioDownloadExport: GET /v1/maker/studio/packs/{packId}/exports/{fileName}

fileName is <packId>-v<version>.zip, or <packId>-v<version>-by-<authorSlug>.zip when the pack id lacks the author slug, from an export result. 404 when absent; 413 over 256 MiB (the zip stays on disk).

Request:

No body.

200 response example (application/zip):

application/zip raw bytes (Content-Disposition: attachment)

### studioVoiceDefault: GET /v1/maker/studio/packs/{packId}/voice

The pack's voice origin and consent default (.maker/voice.json). origin, consent and updatedAt are null when none is set; revision is then a stable initial id to send with the first PUT.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "origin": "own",
  "consent": {
    "confirmed": true,
    "statementVersion": "consent-1",
    "confirmedAt": "2026-11-09T00:00:00Z"
  },
  "updatedAt": "2026-11-09T00:00:00Z"
}
```

### studioSaveVoiceDefault: PUT /v1/maker/studio/packs/{packId}/voice

origin own, ai-assisted or ai-generated with consent {confirmed:true,statementVersion:consent-1} sets the default; origin null and consent null clear it. confirmedAt is set by the loader; an identical PUT changes nothing. v2.5: takes created without origin and consent inherit the default (consentSource pack) and follow every later change, including the disclosure of published ones in voice-provenance.json; clearing leaves them at the last default; takes with their own origin or consent (consentSource take) never change. A change emits maker.pack.changed {packId, reason: voice-default}. 400 invalid body, 403 read-only, 409 conflict (stale revision or reused id), 409 restart_required (pending pack), 503 metadata_unavailable (voice-provenance.json unreadable while a published take inherits the default).

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "origin": "own",
  "consent": {
    "confirmed": true,
    "statementVersion": "consent-1"
  }
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-next",
  "origin": "own",
  "consent": {
    "confirmed": true,
    "statementVersion": "consent-1",
    "confirmedAt": "2026-11-09T00:00:00Z"
  },
  "updatedAt": "2026-11-09T00:00:00Z"
}
```

### studioLabelDubs: POST /v1/maker/studio/packs/{packId}/voice/label

D-148: writes a voice-provenance.json label (the pack default origin, consent-1, confirmedAt now, the file hash) for every shipped dub (audio/voice/<lang>/<clip>.wav|.ogg) without a valid one that is not a Studio publication: hand-copied files, v1 voice uploads, entries with an unknown origin, an old statement or other bytes. expectedRevision and origin are the voice default as the page showed it; the click is the creator's confirmation (consent.confirmed must be true). Studio publications keep the label of their take. labelled is 0 when nothing needed it; paths lists at most 20. Emits maker.pack.changed {packId, reason: voice-label} when it labels. 400 invalid body, 403 consent_required (no pack default: nothing is labelled), 403 read-only, 409 conflict (the default changed, or reused id), 409 restart_required, 503 metadata_unavailable (voice-provenance.json unreadable).

Request:

```json
{
  "expectedRevision": "sample-revision",
  "operationId": "0123456789abcdef0123456789abcdef",
  "origin": "own",
  "consent": {
    "confirmed": true,
    "statementVersion": "consent-1"
  }
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "operationId": "0123456789abcdef0123456789abcdef",
  "revision": "sample-revision",
  "origin": "own",
  "labelled": 1,
  "paths": [
    "audio/voice/th/sample_voice_001.wav"
  ]
}
```

### studioOperation: GET /v1/maker/studio/packs/{packId}/operations/{operationId}

Reconciles an uncertain mutation while its bounded receipt is retained.

Request:

No body.

200 response example (application/json; charset=utf-8):

```json
{
  "state": "committed",
  "revision": "sample-next",
  "saved": true,
  "recoveryRequired": false
}
```

### studioClientLog: POST /v1/maker/studio/client-log

Diagnostics only: the page's own request timings and events, at most 100 flat entries. Written to the game log as MAKER-WEB lines only while the console command maker trace on is set; otherwise accepted and dropped (trace false). Not gated by Studio features.

Request:

```json
{
  "entries": [
    {
      "kind": "api",
      "t": 1200,
      "method": "GET",
      "route": "studioLines",
      "status": 503,
      "ms": 3012.2,
      "code": "unavailable",
      "attempt": 0
    }
  ]
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "accepted": 1,
  "dropped": 0,
  "trace": true
}
```

### studioPlay: POST /v1/maker/studio/play

Transient, never receipt-replayed and never an implicit durable write. variant original: game voice and text; dub: selected pack voice and subtitle (publish the take first); sub: original voice with the selected editable pack current subtitle for lang, falling back to the effective subtitle then game text. sub requires packId and no take. Normal line/DLC, pending/read-only pack, revision and game-readiness checks apply.

Request:

```json
{
  "id": "sample_click_001",
  "variant": "dub",
  "lang": "th",
  "packId": "sample.studio",
  "expectedRevision": "sample-revision"
}
```

200 response example (application/json; charset=utf-8):

```json
{
  "revision": "sample-revision",
  "operationId": "",
  "saved": false,
  "live": true,
  "path": null,
  "durationSec": null,
  "note": "Playing in game.",
  "publishedTakeId": null,
  "effectivePackId": "sample.studio",
  "restartRequired": false
}
```
