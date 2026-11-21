# Pack Maker Studio v2 · `ui/`

The new one-screen Dub & Subtitle Studio (workstream W3, `research/2026-11-08/w3-frontend/PLAN.md`). Vanilla ES modules, no
build step, no dependencies. Unofficial fan project, like the rest of CWY:LS Mod Loader.

**Status: milestone 5d (default flipped to Studio v2 with `FLIPPED = true`; `?ui=1` keeps the v1 page reachable; dub steps and pack creation checked against W2's fake loader on the real HTTP adapter).**
Milestone 5b:
Milestone 5: The page runs on W2's own HTTP adapter
(`modules/loader`, unchanged) against W2's loopback fake loader v2: text work, dubs and pack creation are checked end to end; `playback` is absent in that
loader and the page says so. Milestone 4 content: Subtitles, status, history, packs, events and reconnect as in M3; now also: set up the mic
(permission only on that click; echo cancellation, noise suppression and auto gain off), a silent 3-2-1, recording (AudioWorklet), importing a
file (pick or drop), up to 3 takes per line (a 4th is refused), a waveform, trim handles, even-out, origin and the consent statement (shown in
full; only its button confirms), favourite, publishing, taking a take out of the pack, local playback, and the two Play buttons in W2's order.
The screen follows what the loader offers (`capabilities.features`). Studio v2 is the default page: open `index.html?ui=1` for v1
(`ui/boot.js`; the switch is `FLIPPED` there, see "The default flip").

## Layout

| Path | What |
|---|---|
| `boot.js`, `choose.js` | picks v1 or v2 (`chooseUi(search, FLIPPED)`, tested) and loads the matching stylesheet |
| `main.js` | builds the shell, paints the five zones, routes DOM events to actions |
| `actions.js` | **the only caller of the port**: loading, line selection, autosave, workflow, history, pack creation, events, reconnect |
| `dub.js` | the dub half of the actions: mic, countdown, recording, import, takes, trim, consent, publish, Play (mic, player and decoder are injected, so Node tests use fakes) |
| `store.js`, `selectors.js` | one plain-object store; derived data (visible lines, warnings, current take) |
| `api.js`, `api/model.js` | **the port**: W2's `studio*` method names, one adapter (W2's `modules/loader/index.js` default export, or the dev backend); `model.js` is the only file that knows wire names. W3 writes no HTTP code |
| `logic/` | pure rules, tested in Node: Thai counting and wrap, QA, reading speed, glossary, filters, takes, autosave state machine |
| `audio/` | WAV maths (resample, trim, even out, PCM16 encode/decode), waveform peaks, input levels (`levels.js`), the microphone (`capture.js` + `capture-worklet.js`), file import, local playback |
| `zones/` | the five zones as pure `(state, ctx) -> HTML` functions, plus the sky art and icons |
| `i18n/` | `en.js`, `th.js`, `T()`, game-language follow with a manual toggle |
| `styles/` | mockup A's CSS split by zone |

## Run and test

```sh
# static preview with the in-memory dev backend (no game needed)
python3 -m http.server 8764 --bind 127.0.0.1 --directory src/CwyLs.ModLoader.Maker.Web
# open http://127.0.0.1:8764/index.html?ui=2&backend=dev   (&fixture=paused|unfocused|readonly|firstrun|offline|conflict|nomic|countdown|recording|qa|picker|history|consent|nodubs|micoff|otherpack|spoken|langmismatch, &lang=th|en, &reconnect=<ms>, &micidle=<ms>, &packlangs=th,en)

perl -e 'alarm 120; exec @ARGV' node --test "tests/ui/*.test.mjs"          # logic, copy, dev backend
perl -e 'alarm 330; exec @ARGV' node tests/e2e/run.mjs --matrix --shots <dir>   # 76 screenshots + checks (headless Chrome, CDP)
perl -e 'alarm 120; exec @ARGV' node tests/e2e/perf.mjs                    # 2,169 lines: load, scroll, filters, long tasks
perl -e 'alarm 200; exec @ARGV' node tests/e2e/smoke.mjs                    # content-level click-through (44 steps; uses Chrome's fake microphone)
perl -e 'alarm 220; exec @ARGV' node tests/e2e/smoke-fake.mjs                # the same walk on W2's REAL adapter + fake loader v2 (19 steps; starts python3 tools/fake_loader.py on a free loopback port)
perl -e 'alarm 90; exec @ARGV' node tests/e2e/harness-selftest.mjs         # the harness times out and cleans up
```

To look at the page by hand against the fake loader (it serves the app and injects the token):

```sh
python3 src/CwyLs.ModLoader.Maker.Web/tools/fake_loader.py --port 8127 --web src/CwyLs.ModLoader.Maker.Web --tick-sec 0
# open http://127.0.0.1:8127/maker/index.html?ui=2&backend=http&lang=th
```

The e2e scripts need loopback networking and a Chrome (`CHROME_BIN` overrides the macOS path; set
`CWYLS_CHROME_NO_SANDBOX=1` inside a sandboxed shell). `tests/` is not shipped (`build.sh` leaves it out), so the dev backend
(`tests/dev/dev-backend.js`) is not in a release: `?backend=dev` there is a clean error, never a hidden mock.

## What the loader offers decides what the page offers

`capabilities.features` is the truth (an absent route may answer 404 and is never replaced by fake data). Today's real and fake loaders list
`catalog, workflow, subtitles, projects`. So: no dub deck (banner "not available in this loader yet", mic never asked for), no Play, and
**no new-pack button**: `projects` means storage/history/glossary/operations, only `newPacks` enables creation (W2 v2.1). "Needs retake" is
off on a line without takes. A save answered `live:false` shows the loader's note instead of "live in game", and a freshly opened line says
just "Saved to pack". W2's `labelKey` values (`group.morning` ...) get names in both languages; unknown keys fall back to the title, then the id.
`readOnlyReason` is shown as escaped text and never branched on (it is English today, also in the Thai UI).

## Parity with the v1 page (W3-10)

What the v1 workspace could do in HTTP mode and what Studio v2 does today. (v1's Pack, Lines & Voice, Looks, Translate, Scenes, Share, Wiring
and Booth views are mock-only prototypes: in HTTP mode v1 shows "Coming later", so nothing real is lost there.)

| v1 (HTTP mode) | Studio v2 |
|---|---|
| Browse and search by text, id, clip name | yes (search; scene list; status chips) |
| Filters: missing subtitle / missing dub / done | **yes (M5b)**: the chips *No subtitle* and *No dub* use CONTRACT-v2's pack-scoped meaning (`missing-subtitle`: this pack contributes no subtitle, even if another pack does; `missing-dub`: the line has a voice and this pack has no dub), alongside to do / draft / done / needs retake / warnings / starred. Like the other chips they filter the page's own copy of the catalogue, and both click-throughs check that their counts equal the loader's own `missing-*` totals |
| Edit a subtitle, save 1-2000 characters | yes, plus autosave, history and revert, conflict handling, QA and reading speed |
| Remove a subtitle override | yes (clearing the box sends null) |
| Remove a dub override | **not yet** for a dub made outside Studio (W2b decides it in M5d; such a clip does show a filled dub dot and does not count as "No dub"): v2 only takes a *published take* out of the pack, and takes need the `dubs` feature |
| Record or import audio, upload | yes, but only when the loader offers `dubs` (the fake and real loaders do not yet) |
| Play original / my version in the game | yes, only when the loader offers `playback` |
| Progress overall and per group | yes |
| Source pack of an effective subtitle or dub from another pack | **yes (M5b)**: a note under the editor names the pack (by its loader name) when another pack provides the subtitle or the dub the game uses: *only* (this pack has none) or *wins* (this pack has one, but loses). Nothing is shown when this pack is what the game uses. After a save the page asks the loader who wins now, unless our own text already did |
| "Now speaking" with Open line for a line spoken in the game | **yes (M5c)**: the *Satone just said* card follows every `line.spoken` event (the game's own lines too, not only our Play) and its **Open line** button brings that line up: it selects it, scrolls to it, and clears a scene, filter or search that would hide it (with a short message). The open line says so instead of offering a button; a line that is not in the catalogue is named by id and cannot be opened |
| Game language vs pack language mismatch prompt | **yes (M5c)**: while the game's language (status `gameLanguage`) differs from the selected pack language, the right column shows an amber note, the top bar shows a mark beside *Pack language*, and a **Switch to {language}** button appears when the pack has the game's language. Nothing is shown while the loader is unreachable or the status is unknown |
| Event log and wiring labels (diagnostics) | **not yet** (no log panel) |
| Static preview with mock data | v1 mock stays in v1; v2's dev backend lives in `tests/` and is not in a release |
| Thai UI | v1 had one language switch; v2 follows the game language with a manual toggle, fonts bundled |

## Open line and the language notice (M5c)

- **Open line.** `line.spoken` events carry `localizationId`; the page keeps the last one and the card shows its text (the creator's subtitle if there is one, else the Japanese). `actions.openSpoken(id)` does the rest. It works on any line of the loaded catalogue.
- **Language notice.** `selectors.langMismatch(state)` compares `status.gameLanguage` with the pack language (case-insensitive). The wording says that the game will not show this pack's subtitles or play its dubs until the game's language matches, and that editing, listening here and saving still work. **That wording is inferred** from how v1 described the situation ("switch the game language") and from subtitles and dubs being per language; it has not been checked in the game (Claude's in-game check should confirm it). The fake loader always runs in Thai with a Thai-only pack, so against it the click-through can only prove that *no* notice appears; the notice itself is covered on the dev backend (`?packlangs=th,en` gives a two-language pack for the Switch button).

## The default flip

`ui/boot.js` has `FLIPPED = true`. `chooseUi(search, flipped)` (`choose.js`, covered by `tests/ui/choose.test.mjs`) decides: not flipped, v1 is the
default and `?ui=2` opens v2; flipped, v2 is the default and `?ui=1` keeps v1. **It is flipped.** The click-throughs (`smoke.mjs` and `smoke-fake.mjs`)
assert that a plain URL opens Studio v2 and `?ui=1` opens v1.

## Rules this code keeps

- The page never receives game audio, video or images; waveforms are of the creator's own takes only.
- Every interpolated string goes through `esc()`; no inline script, no `eval`, no external request.
- Nothing of the creator's content in `localStorage` (only the UI-language choice).
- The saved subtitle is exactly what was typed; wrapping only decides how it is shown (no zero-width spaces).
- HTTP never falls back to the dev backend; a loader without Studio v2 shows a clear "too old" banner.

## The microphone is let go

The page releases the microphone as soon as a recording finishes or is cancelled, after 60 s with no recording and no count-in (`MIC_IDLE_MS` in `dub.js`; `&micidle=<ms>` shortens it for tests), and as soon as the page is hidden. The tracks end, so the browser's recording indicator goes out, and the halo shows "Mic is off". R or Set up takes it again on the same device, with no new prompt (the permission is already granted). A hidden page never interrupts a recording or a count-in: recording is an explicit action. Fixture `micoff` shows this state.

## Play and publish

T renders, publishes, then asks the game to play. If the game step fails after a publish (paused, unfocused, ...), the message says the take is already in the pack and only playback failed (`t_in_pack_hint`, `t_in_pack_play_failed`). There is no "publish only" button.

## Input levels (the halo) and what they are not

Thresholds in `audio/levels.js`, mine and not loudness measurements: **clip** when a block's peak is at least 0.98 (held 0.6 s); **idle** below an
rms of 0.01 (about -40 dBFS); **noise** when the quietest block of the last 3 s is above 0.03 (about -30 dBFS), i.e. the room hisses even in the
pauses; otherwise **good**. "Even out volume" is RMS levelling towards about -20 dBFS with a soft limiter, not a LUFS measurement. The screenshot
states use a pretend mic set-up (`fixture` sugar in the dev backend only; `&realmic` turns it off).
