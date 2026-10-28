# Content packs and code mods

A content pack is a folder of your own files plus a `manifest.json`. It contains no game files and
patches nothing by itself: CwyLs.ModLoader reads it and applies it in memory while the game runs.

A **code mod** is the same folder with an `entryDll` in its manifest: a DLL holding one class that
derives from `CwyMod`. Packs and code mods share one dependency graph and the same
version gates; a code mod's content folders load exactly like a content pack's. How mods are loaded
and what their helper offers: `src/CwyLs.ModLoader/Mods/README.md`. Start one from `templates/cwy-mod/`;
the working example is `packs/cwy.sample.hello/` (built from `mods-src/cwy.sample.hello/`).
The author's guide is `docs/guide/` and the generated API reference `docs/api/`.

- **In this repo:** `packs/<id>/` holds example and test packs that are checked in.
- **At runtime:** the game reads `<game root>/CWYLS Mods/<id>/`. Nothing is read from this repo.
- `./deploy-packs.sh` copies every `packs/<id>/` that has a `manifest.json` into `CWYLS Mods/`.
  `./build.sh --deploy-packs` builds and deploys the plugins and then runs it, which is the one
  command to use after changing both code and packs.
- Each pack folder is **replaced**, not merged: a file you deleted from the repo pack does not
  survive in the deployed copy.
- **Adding or changing a pack needs a game restart** — with one exception: textures reload live,
  see below.

## Editing textures live

Textures are the one thing you can change without restarting. Edit the PNG **in place** under
`<game root>/CWYLS Mods/<packId>/textures/…`, save it, and look at the game: each pack's `textures/`
folder is watched and the file is re-read and re-applied within a second. Adding, renaming and
deleting files works the same way — the packs are rescanned, so a new file becomes a rule and a
deleted one is undone back to the game's own texture.

Press **Ctrl+Shift+R** (or **F8**, the alternate; on macOS an F-key needs Fn) to force a reload at
any time. Editing in the
repo works too: `./deploy-packs.sh` writes into `CWYLS Mods/` and the watcher sees that like any other
save.

The detail — the two detectors, the `Textures.WatchFiles` / `Textures.WatchPollSeconds` settings and
what the log lines mean — is in `src/CwyLs.ModLoader.Bulbul/Textures/README.md`.

## Make a pack

1. Copy `packs/cwy.example/` to `packs/<your id>/`.
2. Edit `manifest.json`: set `id` (use the folder name; the loader keys on the id, not the folder), `name` and `version`.
3. Put your files in the folders described in `packs/cwy.example/README.md`.
4. `./deploy-packs.sh` (or `./build.sh --deploy-packs`) with the game closed, then start the game and
   read the `PACKS` lines in `BepInEx/cwy-console.log`, or type `packs list` in the console (Ctrl+Shift+C).

## manifest.json

Only `id`, `name` and `version` are required.

| Member | Type | Meaning |
|---|---|---|
| `id` | string, `^[a-z0-9][a-z0-9._-]{2,63}$` | Unique and stable. Other packs depend on this string, so it must never change. Use the folder name. |
| `name` | string | Shown to humans. |
| `version` | SemVer 2.0.0 | Your pack's version, e.g. `1.0.0`, `0.2.0-rc.1`. |
| `author` | string | Optional. |
| `description` | string | Optional, one line. |
| `gameVersion` | string | Optional. The game build you made and tested it with, e.g. `1.17.3`. Informational: on a different build the pack still loads, with one warning naming both versions (see below). |
| `minGameVersion` | SemVer | Optional. The oldest game build the pack supports, inclusive, e.g. `1.17.0`. On an older game the pack is skipped as `GameVersionTooOld`. |
| `maxGameVersion` | SemVer | Optional. The newest game build the pack supports, inclusive, e.g. `1.17.99`. On a newer game the pack is skipped as `GameVersionTooNew`. Must not be below `minGameVersion`; use the same version for both to pin exactly one build. |
| `loaderVersion` | SemVer | Optional. The oldest CWY:LS Mod Loader API that can load this pack or mod (the API is **0.9.0** now; 0.x until CWY:LS Mod Loader 1.0). An older framework skips it instead of half-loading it. The one minimum-framework field for packs and code mods alike. |
| `entryDll` | string | Optional. Makes the pack a **code mod**: the DLL's path relative to the pack folder, e.g. `MyMod.dll` or `bin/MyMod.dll` (forward or back slashes; no absolute path, no `..`; must end in `.dll`). |
| `contentPackFor` | string or object | Optional. This pack is data for a code mod: `"host.mod"` or `{"id": "host.mod", "minVersion": "1.2.0"}`. It is a required dependency, must name a code mod, cannot be combined with `entryDll`, and must not also appear in `dependsOn`. The code mod finds such packs in `helper.ContentPacks`. |
| `dependsOn` | array | Optional. `{"id": "other.pack", "minVersion": "1.2.0"}`, or just `"other.pack"` when any version will do. Dependencies load first. Add `"optional": true` for a soft dependency: it loads first when installed and is ignored when it is absent or was skipped (an installed one older than `minVersion` still refuses the dependent). |
| `x-…` | anything | Optional. Members prefixed `x-` are yours; the framework ignores them, and a code mod can read them back from `helper.Manifest.Extensions`. |

Anything else is a mistake and the pack is skipped with a message naming the member — a misspelled
`dependson` is more dangerous silently ignored than refused.

### Game versions

Most packs only need `gameVersion`. Game patch releases rarely move anything a pack touches, and
the framework's own patch-target check (`PATCH TARGET MISSING` lines at startup) is what reports a
seam that did move. So:

- **`gameVersion` differs from the running game** → the pack loads, and one warning line says so:
  `PACKS  yourname.mypack: made for game 1.17.2, running 1.17.3 (newer patch version); loaded anyway`.
  Update `gameVersion` once you have tested the pack on the new build.
- **Outside `minGameVersion`/`maxGameVersion`** → the pack is skipped (`GameVersionTooOld` /
  `GameVersionTooNew`). Declare these only when you know the pack breaks outside the range.
- **The game's version is unknown or not a number** → the pack is never skipped for it; it loads
  with a warning that the range was not checked.

Versions compare by SemVer precedence: build metadata (`1.17.3+steam.1`) is ignored, a pre-release
(`1.18.0-beta.1`) sorts below its release, and a game that reports only `1.18` is read as `1.18.0`.
`minGameVersion` and `maxGameVersion` themselves must be full `major.minor.patch`.

## Why a pack did not load

The startup line is `PACKS: N loaded (id@ver, …); M skipped (id: reason, …)`, and each skip also
gets its own warning with the detail. A pack that loaded with a warning (a different `gameVersion`)
gets one `PACKS` line at warning level; it is not a skip.

| Reason | What to do |
|---|---|
| `MissingManifest` | The folder has no readable `manifest.json`. |
| `InvalidManifest` | The detail gives the JSON `line:column` or the offending member. |
| `DuplicateId` | Two folders declare the same `id`; the first one found wins. |
| `GameVersionTooOld` | The game is older than the pack's `minGameVersion`. Update the game, lower `minGameVersion`, or set `Packs.IgnoreGameVersion = true` to test anyway. |
| `GameVersionTooNew` | The game is newer than the pack's `maxGameVersion`. Test the pack on this build and raise `maxGameVersion`, or set `Packs.IgnoreGameVersion = true` to test anyway. |
| `CoreVersionTooOld` | The pack or mod needs a newer CWY:LS Mod Loader API than the one installed (`loaderVersion`). |
| `EntryDllMissing` | The manifest names an `entryDll` that is not in the folder. The whole pack is skipped, content too. |
| `MissingDependency` | A required pack named in `dependsOn` or `contentPackFor` is not installed, or was itself skipped — the detail says which — or `contentPackFor` names a pack that is not a code mod. |
| `DependencyVersionTooLow` | The dependency is installed but older than `minVersion` (optional ones too). |
| `DependencyCycle` | Two or more packs depend on each other; the detail prints the cycle. A cycle that runs through an `optional` dependency is broken by ignoring that edge, so those packs still load. |

A code mod that passes these gates can still be refused when its DLL is loaded: `NoEntryType`,
`MultipleEntryTypes`, `AssemblyLoadFailed`, `AssemblyAlreadyLoaded`, `DependencyFailed`, `Disabled`.
Those come on their own `MODS` lines with the reason, and a summary line
`MODS  1 loaded (…); 0 skipped; 0 failed` follows the pack line. Its content has already loaded by
then. `mods list` in the console shows every code mod's state. (`GameVersionMismatch`, reason 3, was
retired: a different `gameVersion` only warns.)

**Never replace a mod's DLL while the game is running**: the game keeps it memory-mapped. Deploy
with the game closed (`./deploy-packs.sh`), then start it.

## Config

`BepInEx/config/cwyls.modloader.cfg`, section `[Packs]`:

| Key | Default | Effect |
|---|---|---|
| `Enabled` | `true` | Off loads no packs at all. |
| `IgnoreGameVersion` | `false` | On loads packs even when the game is outside their `minGameVersion`/`maxGameVersion` range; each logs a warning instead of being skipped. A different `gameVersion` never needs this. For testing a pack on a build it does not claim to support; it may patch the wrong thing. |

Code mods, same file, section `[Mods]`: `Enabled` (`true`; off loads no mod code, their content still
loads) and `AssemblyLoad` (`LoadFrom`; `Bytes` is a developer switch). A mod's own settings live in
`BepInEx/config/cwy.mod.<id>.cfg`, its data in `BepInEx/data/mods/<id>/`.

## Example and demo packs

| Pack | Shows |
|---|---|
| `cwy.example` | The folder layout every content pack uses, test voice clips and one spoken line in master data |
| `cwy.demo.audio` | Generated rain replacing the game's LightRain/HeavyRain loops, plus one pack music track; A/B with `audio ab`. Format: `src/CwyLs.ModLoader.Bulbul/Audio/README.md` |
| `cwy.demo.scenery` | Two Lightroom-style grading presets and a walnut desk tint; `grade ab`. Format: `src/CwyLs.ModLoader.Bulbul/Grading/README.md` |
| `cwy.demo.script` | A short scene run by the CWY script commands `cwy.flag`, `cwy.wait` and `cwy.defer`; `scene play` |
| `cwy.sample.hello` | The sample code mod, built from `mods-src/cwy.sample.hello/` |

A **language pack** is a content pack with a `language.json`: it adds a language to the game's
language selector. Its format is in `docs/guide/manifest.md` ("`language.json`").
