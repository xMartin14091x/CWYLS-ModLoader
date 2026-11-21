import {routes,studioRoutes} from './modules/loader/routes.js';
import {writeFile,readFile} from 'node:fs/promises';
const intro=`# Maker API · Dub & Subtitle Studio

DS-00 v1.2 plus the Studio v2 contract (v2–v2.5.1). Generated from \`modules/loader/routes.js\`.
The v1 routes are used by the v1 HTTP adapter and Wiring view. Studio routes are capability-gated via
\`GET /v1/maker/studio/capabilities\` and consumed by the Studio v2 UI when their backend feature is installed.
Run \`node generate_api.mjs --check\` from this directory to check it.

## Opening and boundaries

The console command \`maker\` opens \`http://127.0.0.1:<port>/maker/?k=<one-time nonce>\`.
Each \`maker\` or \`maker serve\` call mints a fresh 256-bit link, valid for 60 seconds (monotonic clock),
compared in constant time and atomically consumed once. At most 16 unconsumed links are kept; older links expire.
Redeeming it sets a host-only \`cwyls-maker-<port>\` session cookie (\`HttpOnly; SameSite=Strict; Path=/maker/\`)
and redirects to \`/maker/\` without the nonce. The cookie survives page reloads while this Pack Maker session runs.
Cookies have no port isolation; the name includes the port and each router has an independent random cookie value.
Cookie-only requests never authenticate maker API calls; those still require the bearer below.
Without a valid link or session cookie, \`/maker/\` and \`/maker/index.html\` show only EN/TH instructions to type
\`maker\` in the game console. A wrong, expired or reused link shows those instructions even with a valid cookie.
\`GET /maker\` redirects to \`/maker/\`, preserving a supplied nonce. Page responses use \`Referrer-Policy: no-referrer\`;
request traces omit page queries. The loader injects \`<meta name="cwyls-maker-token" content="PER_START_RANDOM_TOKEN">\`
only into the authenticated page.
The token differs from the API key, changes each start and is never written to disk by the app.
For lab automation, \`maker serve\` returns the one-time URL without opening a tab: follow its redirect with a
cookie-aware client within 60 seconds, retain the cookie for reloads and read the token from that authenticated page.
Run \`maker serve\` again for another client or after expiry. See \`tools/README.md\` for an in-memory Python example.
This prevents unauthenticated loopback token scraping; it does not isolate the browser from programs that can
read its memory/profile or intercept the launch link.
Only text and numbers about game content reach the browser. Play asks the game to speak.
The narrow Studio v2 take-audio GET may return only creator-uploaded WAV bytes from that project's
\`.maker/takes/\` store. It can never return game audio, reference-pack audio or another path.

The v1 loader writes only editable loaded content packs under \`<game root>/CWYLS Mods/<packId>/\`:
\`data/subtitles/<lang>.json\` and \`audio/voice/<lang>/<clip>.wav\`. Studio v2 also owns creator work under
\`.maker/\` and published disclosure in root \`voice-provenance.json\`. The runtime ignores \`.maker/\`.
Changes to loaded packs are live; a newly created pack is pending and read-only until one restart.
Code mods, failed packs and reference packs are read-only, with a reason from packs.

## Request rules

Loopback only. The promised Host is \`127.0.0.1:<port>\`; HttpListener may refuse \`localhost\` before the router.
For \`/maker/*\` and \`/v1/maker/*\`, Origin (when present) must equal the loopback http origin at the listener port;
Sec-Fetch-Site (when present) must be same-origin or none, otherwise 403 forbidden_origin.
No CORS headers. Other \`/v1/*\` routes retain browser refusal.
Every maker API call uses \`Authorization: Bearer <maker token>\`, otherwise 401 unauthorized.
JSON bodies: \`application/json; charset=utf-8\`, at most 64 KiB.
Voice bodies: \`audio/wav\`, at most 16 MiB and 60 s, PCM 16-bit mono/stereo, 8–48 kHz.
Responses use the status and media type printed for each route, always with \`Cache-Control: no-store\`.

Segments: packId \`[a-z0-9][a-z0-9._-]{0,63}\`; lang \`[a-z]{2,3}(-[a-z0-9]{2,8})?\`;
id and clip \`[A-Za-z0-9_.()-]{1,128}\`. Invalid segments are 400 before calling the backend.

Errors use \`{"error":{"code":"bad_request","message":"Explanation"}}\`.

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

Events use the same envelope as \`/v1/events\`: epoch, events, next, latest, oldest, dropped, reset, timedOut.
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
Both the real backend and the Studio dev backend implement v2 catalog reads (capability-gated by \`catalog\`).

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
`;
let doc=intro;
const appendRoutes=items=>{
for(const [key,r] of Object.entries(items)){
 doc+=`\n### ${key}: ${r.method} ${r.path}\n\n${r.description}\n\nRequest:\n\n`;
 doc+=r.request===null?'No body.\n':typeof r.request==='string'?r.request+'\n':'```json\n'+JSON.stringify(r.request,null,2)+'\n```\n';
 doc+=`\n${r.status} response example (${r.mediaType}):\n\n`;
 doc+=typeof r.response==='string'?r.response+'\n':'```json\n'+JSON.stringify(r.response,null,2)+'\n```\n';
}
};
appendRoutes(routes);
doc+='\n## Studio v2 routes (capability-gated)\n';
appendRoutes(studioRoutes);
const target=new URL('./API.md',import.meta.url);
const count=Object.keys(routes).length+Object.keys(studioRoutes).length;
if(process.argv.includes('--check')){if(await readFile(target,'utf8')!==doc)throw new Error('API.md is stale; run node generate_api.mjs.');console.log(`API.md matches routes.js (${count} routes).`);}
else{await writeFile(target,doc);console.log(`Generated API.md (${count} routes).`);}
