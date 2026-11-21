# Pack Maker · Dub & Subtitle Studio

Unofficial, fan-made CWY:LS Mod Loader workspace. Not made, endorsed or supported by Nestopi.

The default workspace browses voiced lines, edits subtitles, converts creator audio to WAV and asks the game to
play original or dubbed clips. Only reference text and metadata reach the browser; game media stays in the game.

## Run locally

No install, npm dependencies or build step. From the repository root:

```sh
python3 -m http.server 8764 --bind 127.0.0.1 --directory src/CwyLs.ModLoader.Maker.Web
```

Open `http://127.0.0.1:8764/`. Static previews select the mock adapter, seeded with 40 invented reference lines.
Mock Studio edits stay in this browser session; the original mock workspaces retain their existing local storage.

When the loader serves an authenticated `/maker/` page and injects its per-start maker token, the app selects HTTP automatically.
`?backend=mock` or `?backend=http` explicitly selects an adapter; HTTP never falls back to mock on failure.
Use the loader's `maker` console command to open the app with a valid token. No API key file is read.
The command opens a one-time link that expires after 60 seconds; its browser session cookie keeps reloads working.
A bare `/maker/` request without that cookie shows opening instructions. For unattended `maker serve` use,
follow the returned link with a cookie-aware client; see [the lab instructions](tools/README.md#real-loader-launch-links).

DS-05 owns `tools/`. Its `fake_loader.py --web src/CwyLs.ModLoader.Maker.Web` serves the app with a token and
implements the contract without launching the game. See that worker's tool README for options.

## Workspace behaviour

- Search reference text, subtitles, localisation IDs and voice clip names; filter by missing subtitle/dub or done.
  Results are grouped by the supplied group, with ten lines per page.
- Only editable loaded packs can be selected. Read-only reasons appear below the picker. Languages come from
  the selected pack; new packs must first be loaded by restarting the game.
- JA and EN reference text is read-only. Thai editing uses bundled Noto Sans Thai. Above 20 Unicode code points
  per original second, the length hint warns. Save accepts 1–2000 code points; the server validates again.
- Subtitle drafts and own takes are held separately per pack/language/line and survive browsing, refreshes,
  event resync and request failures within this tab. Reloading the page discards unsaved Studio drafts/takes.
- Record or import creator-owned audio, listen locally, then upload explicitly. The existing converter produces
  PCM 16-bit mono WAV at 48 kHz. Takes longer than 60 s are rejected before resampling; recording stops just before the 60 s cap.
  A live acknowledgement produces “live in game”; live:false displays the server note instead.
- The selected language's active subtitle/dub metadata may originate in another loaded pack. Source pack IDs
  are shown. Remove buttons are enabled only for overrides from the selected pack. Saving writes to that pack.
- Progress is across the catalogue for the selected language, independent of search and paging. Expand it for
  each group. Text-only lines count towards subtitles and are excluded from the dub denominator.
- Now speaking follows `line.spoken`; Open line fetches that localisation ID even when outside the current page.
  Room/language events update status. A language mismatch asks the creator to switch the game language.
- Play original / Play my dub ask the game to speak. There is no browser game-audio download endpoint.
  `[Maker] FocusGameOnPlay` also asks the OS to bring the game forward; on Windows this is best effort and focus may be refused.
- Pack, Lines & Voice, Looks, Translate, Scenes, Share, Wiring, Booth and guided views keep the approved prototype
  in mock mode. In HTTP mode, those workspaces show “Coming later” and issue no legacy API calls.

Outfit is the UI family; Noto Sans Thai handles Thai. Both fonts are bundled for loopback-only operation with their
SIL Open Font License notices in `fonts/`. Technical identifiers use the prototype's system monospace fallback.

## Adapter and API

`modules/loader/index.js` selects `mock.js` or `http.js`. Studio only calls the adapter, never fetch or storage.
`modules/loader/routes.js` is the source of truth for the ten DS-00 routes, button wiring and generated [API.md](API.md).
Legacy mock persistence/media helpers remain behind the same boundary and reject use in HTTP mode.

The HTTP adapter sends the injected bearer token, checks request sizes, honours Retry-After, never retries action
POSTs, stops all future requests on 401, and aborts pending requests on close. Long polling uses the contract event
cursor and refreshes catalogue/status on reset, dropped events or epoch change without replacing subtitle drafts.

## Checks

From this directory:

```sh
node generate_api.mjs --check
node --test tests/*.test.mjs
```

`tests/browser.mjs` is a zero-dependency CDP acceptance runner. It attaches to an isolated Chrome on loopback port
9234 and uses `Emulation.setDeviceMetricsOverride` at 1440 × 1000, 768 × 1024 and 390 × 844. Start the static server
above, then run `node tests/browser.mjs`. For DS-05's fake listener, set `DS_HTTP=1` and
`DS_URL=http://127.0.0.1:8765/maker/`. `DS_SHOTS` sets an absolute screenshot directory; the default is the ticket's
external `research/2026-11-07/dub-studio/shots/ds-04/` directory. It never uses a virtual-time screenshot budget.
The recorder check supplies a synthetic audio stream and never accesses a real microphone.

The runner covers browsing, drafts, saving Thai, the length warning, importing/resampling a synthetic stereo tone,
uploading, playing both variants, structured events, recording, all workspaces and expanded progress. Screenshots
include the Studio editor at each size. It asserts no horizontal page scroll, console exceptions or external requests.
Real microphone permissions/devices, physical iPad/iPhone behaviour and actual game playback remain DS-07 checks.

Repository checks, from its root:

```sh
dotnet build -c Release
dotnet test -c Release
```
