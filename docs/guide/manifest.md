<sub>[Guide](README.md) &nbsp;·&nbsp; [Getting started](getting-started.md) &nbsp;·&nbsp; **Manifest** &nbsp;·&nbsp; [Lifecycle](lifecycle.md) &nbsp;·&nbsp; [Helper](helper.md) &nbsp;·&nbsp; [Troubleshooting](troubleshooting.md) &nbsp;·&nbsp; [Versioning](versioning.md) &nbsp;·&nbsp; [API reference](../api/README.md)</sub>

# `manifest.json`

Every pack and every code mod has one, at the top of its folder. The framework reads it strictly:
an unknown member, a wrong type or a bad version skips the pack with a message that names the
problem, because a pack that half-loads over a typo is worse than one that says why it did not
load. This page shows three example manifests, then every field, the `language.json` of a language
pack, and every reason a pack or mod is skipped and what to do about it.

<!-- Parser: src/CwyLs.ModLoader.Core/Packs/PackManifestParser.cs (strict: :10-16, unknown members :137-164). -->

## Three examples

A **content pack** (`packs/cwy.example/manifest.json`):

```json
{
  "id": "cwy.example",
  "name": "CWY Example Pack",
  "version": "0.1.0",
  "author": "CWY:LS",
  "description": "Reference pack: the folder layout every CWY content pack uses.",
  "gameVersion": "1.18.0",
  "loaderVersion": "0.1.0",
  "dependsOn": []
}
```

A **code mod** (the template's `templates/cwy-mod/manifest.json`, with an optional dependency
added; made-up ids): the same fields plus `entryDll`.

```json
{
  "id": "yourname.mymod",
  "name": "My Mod",
  "version": "0.1.0",
  "author": "yourname",
  "description": "One line: what the mod does.",
  "gameVersion": "1.18.0",
  "loaderVersion": "0.9.0",
  "entryDll": "MyMod.dll",
  "dependsOn": [
    { "id": "yourname.greeter", "minVersion": "1.2.0", "optional": true }
  ]
}
```

A **content pack for a code mod** (made-up ids): data that a code mod reads through
`helper.ContentPacks`. Its own manifest names the mod it belongs to, and may carry your own
metadata in `x-` members.

```json
{
  "id": "yourname.greeter.morning",
  "name": "Morning greetings for Greeter",
  "version": "1.0.0",
  "contentPackFor": { "id": "yourname.greeter", "minVersion": "1.2.0" },
  "x-greetings": ["Good morning!", "Up early today?"]
}
```

## Fields

Only `id`, `name` and `version` are required. Names are case-sensitive (`dependsOn`, not
`dependson`).

| Field | Type | Rules | Meaning |
|---|---|---|---|
| `id` | string | `^[a-z0-9][a-z0-9._-]{2,63}$`: lowercase letters, digits, `.`, `_`, `-`; 3 to 64 characters | Unique and permanent. Other manifests depend on it; your config file (`cwy.mod.<id>.cfg`) and data folder (`data/mods/<id>/`) are named after it. Use the folder name (the loader does not check that they match, but everything is easier when they do) |
| `name` | string | not blank | Shown to people |
| `version` | string, SemVer 2.0.0 | e.g. `1.0.0`, `0.2.0-rc.1` | Your pack's own version; what `minVersion` in other manifests compares against |
| `author` | string | optional | |
| `description` | string | optional, one line | |
| `gameVersion` | string | optional | The game build you made and tested it with, e.g. `1.18.0`. **Informational:** on another build the pack still loads, with one `PACKS` warning naming both versions. Update it once you have tested on the new build |
| `minGameVersion` | SemVer | optional, full `major.minor.patch`, inclusive | Oldest game build the pack supports. On an older game: skipped as `GameVersionTooOld` |
| `maxGameVersion` | SemVer | optional, full `major.minor.patch`, inclusive, not below `minGameVersion` | Newest game build the pack supports. On a newer game: skipped as `GameVersionTooNew`. Set both to the same version to pin exactly one build |
| `loaderVersion` | SemVer | optional | **The framework minimum**: the oldest CWY:LS Mod Loader API that can load this pack or mod. The running API is `0.9.0` (`CwyApi.Version`); anything newer than that is skipped as `CoreVersionTooOld`. Set it to the version you built against. (The name is historical; it is the one minimum-framework field, for packs and code mods alike) |
| `entryDll` | string | optional; a path relative to the folder, forward or back slashes, ending in `.dll`; no absolute path, no drive, no `.`/`..` segments, not blank | Makes the folder a **code mod**: the DLL holding your `CwyMod` class, e.g. `MyMod.dll` or `bin/MyMod.dll`. Matched case-insensitively. The file must exist (`EntryDllMissing` otherwise) |
| `contentPackFor` | string, or `{ "id", "minVersion"? }` | optional; not together with `entryDll`; not also listed in `dependsOn`; no `optional` | This pack is data for the named code mod. It is a required dependency: the code mod loads first, and it must be a code mod |
| `dependsOn` | array | optional; each entry a pack id string, or `{ "id", "minVersion"?, "optional"? }`; no duplicates, not yourself | Packs and mods that must load first. `minVersion` is the oldest acceptable version. `"optional": true` makes it a soft dependency: loaded first when installed, ignored when absent or skipped. An installed optional dependency that is older than `minVersion` still refuses your pack |
| `x-…` | anything | members starting with `x-` | Yours. The framework ignores them; a code mod reads them back from `helper.Manifest.Extensions` |

Anything else is refused as `InvalidManifest`, and the message lists the members that are allowed.

<!-- Members: PackManifestParser.cs:22-42; id: PackManifest.cs:35, parser :75-81; name/version :83-84, :166-212; min>max :89-94; dependsOn :230-344 (self :249-252, duplicate :254-261, optional :317-327); entryDll :345-366 + src/CwyLs.ModLoader.Core/Packs/EntryDllPath.cs:27-76, case-insensitive match EntryDllPath.cs:78-102; contentPackFor :368-404; x- :406-419; gameVersion warning: packs/README.md "Game versions", src/CwyLs.ModLoader.Core/Packs/PackResolver.cs:17-20; loaderVersion check: PackResolver.cs:122-129, src/CwyLs.ModLoader.Core/Mods/CwyApi.cs:24,:46. -->

### Versions

Versions compare by SemVer precedence: build metadata (`1.17.3+steam.1`) is ignored and a
pre-release sorts below its release (`1.18.0-beta.1` < `1.18.0`). A game that reports only `1.18` is
read as `1.18.0`; if the game's version cannot be read at all, no pack is skipped for it.

### Reading your manifest from code

`helper.Manifest` is the parsed manifest: `Id`, `Name`, `Version` (a `SemVer`), `Author`,
`Description`, `GameVersion`, `LoaderVersion`, `EntryDll`, `ContentPackFor`, `DependsOn`
and `Extensions`. The `x-` values arrive as parsed JSON: a `JsonObject`, a list, a string, a
`long` or `double`, a `bool`, or null.

```csharp
if (helper.Manifest.Extensions.TryGetValue("x-homepage", out object? value) && value is string homepage)
{
    helper.Log.Trace("homepage: " + homepage);
}
```

For a code mod with content packs, each `ICwyModInfo` in `helper.ContentPacks` has the pack's own
`Manifest` (with its `Extensions`) and its `DirectoryPath`.

<!-- ICwyModManifest: src/CwyLs.ModLoader/Mods/ICwyModHelper.cs:73-107; ContentPacks filter: src/CwyLs.ModLoader/Mods/ModHelper.cs:78-94. -->

## `language.json` (a separate file, not a manifest member)

A content pack can add a language the game does not ship. It declares the language in
`language.json` next to its manifest and ships the text in `data/subtitles/<code>.json`. The
language then appears in the game's own language selector, under its `caption`. An example
declaration (a made-up pack, `yourname.lang.vi`):

```json
{
  "schema": 1,
  "code": "vi",
  "caption": "Tiếng Việt",
  "englishName": "Vietnamese",
  "script": "Latn",
  "fallback": ["en"]
}
```

| Member | Type | Rules | Meaning |
|---|---|---|---|
| `schema` | number | optional; `1` is the only version | The file format version |
| `code` | string | **required**; a language code such as `vi`, `id` or `es-mx` (2 or 3 letters, then optional `-` parts), lower-cased when read; not one of the game's own seven (`ja`, `en`, `zh-hans`, `zh-hant`, `pt-br`, `ko`, `ru`) | The language's code: the name of its text table, the key in a Satone line's `Texts`, and what `helper.Language.CurrentCode` reports. A game language's rows are changed through `data/localization.json` instead |
| `caption` | string | **required**, not blank | What the language selector shows: the language's own name for itself (its endonym), e.g. `Tiếng Việt` or `ไทย` |
| `englishName` | string | **required**, not blank | The English name, for logs |
| `script` | string | optional; a 4-letter ISO 15924 script name, e.g. `Latn`, `Thai` | Picks the script helpers that `lineBreaks: "auto"` applies |
| `fallback` | string or array of strings | optional; codes, no duplicates, not the language itself; default `["en"]` | Where a missing row comes from, in order. May name another pack language or one of the game's own. The first of the game's own languages in the chain (English if there is none) is the **base language**: what the save file holds, and whose fonts, typing speed and news the game uses |
| `font` | object `{ "gameAssets": … }` | optional; `gameAssets` is a name or an array of names | Font assets the game already ships that can draw this language, in preference order. Leave it out when the game's default fonts already cover the script. Your own font file (`"file"`) is refused: not supported yet |
| `marks` | string | optional; a `.json` path inside the pack | A mark-positioning table, for scripts whose stacked marks the game's text renderer draws on top of each other. The format is documented on `MarkTable` in `src/CwyLs.ModLoader.Core/Subtitles/MarkTable.cs` |
| `lineBreaks` | string | optional; `auto` (default), `none`, or a helper id (today `thai`) | `auto` picks the line-break helper registered for `script`, if there is one; `none` shows the text exactly as written |
| `x-…` | anything | members starting with `x-` | Yours; ignored |

- The file may also hold an **array** of such objects, to declare several languages in one pack.
- Like the manifest, it is strict: an unknown member (other than `x-…`), a wrong type or a bad
  value is an error that names the file and the member, and none of the file's languages is
  added (the rest of the pack still loads). A missing or broken `marks` table only drops the table.
- A code belongs to the first pack that declares it; a later pack declaring the same code is
  refused, and the log names both packs.
- The text table `data/subtitles/<code>.json` is a flat object from localization id to text, for
  the game's rows and for your own:

  ```json
  { "cwy_example_01_001": "Xin chào từ Mod Loader." }
  ```

- The player's choice is kept by the framework. The game's save file always holds the base
  language, so the game still opens normally without the loader or without your pack.

<!-- Parser: src/CwyLs.ModLoader.Core/Language/LanguageDeclarationParser.cs (members :49-66, code rule :69-70 + :238-248, game codes rejected :187-193, script :198-203, lineBreaks :205-211, marks :213-223, fallback :250-296, font :298-350, strict :368-382, array and duplicates :98-142); LanguageDeclaration.cs; game codes: src/CwyLs.ModLoader.Core/Subtitles/SubtitleLanguages.cs (InGame); line-break ids: src/CwyLs.ModLoader.Core/Language/LineBreakHelpers.cs:29-58; base language and chain: src/CwyLs.ModLoader.Core/Language/PackLanguage.cs:39-52, LanguageRegistry.cs:223-240; first pack keeps a code: LanguageRegistry.cs:66; runtime, selector and save guard: src/CwyLs.ModLoader.Bulbul/Language/README.md; text table: src/CwyLs.ModLoader.Bulbul/MasterData/README.md "data/subtitles/<lang>.json". -->

## Why a pack or mod did not load

Two stages can refuse a folder. Both write a line, and `mods` / `packs list` in the console show
the result.

### Stage 1: the resolver (before anything loads)

The whole folder is skipped, content and code. Startup line:
`PACKS  N loaded (id@ver, …); M skipped (id: reason, …)`, plus one warning line per skip with the
detail. A skipped pack also takes down every pack that requires it (they are skipped as
`MissingDependency`, naming the reason).

| Reason | What it means | How to fix it |
|---|---|---|
| `MissingManifest` | The folder has no readable `manifest.json` | Add it, at the top of the folder |
| `InvalidManifest` | Not valid JSON, or a member is wrong or unknown; the detail gives `line:column` or the member | Fix the member named in the detail |
| `DuplicateId` | Another folder already claimed this `id`; the first one found wins | Remove the second copy, or give it its own id |
| `GameVersionTooOld` | The game is older than `minGameVersion` | Update the game, or lower `minGameVersion` once you have tested it. `[Packs] IgnoreGameVersion = true` loads it anyway, with a warning, for testing |
| `GameVersionTooNew` | The game is newer than `maxGameVersion` | Test on this build, then raise `maxGameVersion`. Same testing switch as above |
| `CoreVersionTooOld` | `loaderVersion` is newer than the installed CWY:LS Mod Loader API | Update CWY:LS Mod Loader, or lower the field if the pack really works on the older API |
| `EntryDllMissing` | `entryDll` names a file that is not in the folder | Copy the built DLL into the folder, or fix the name |
| `MissingDependency` | A required pack from `dependsOn` or `contentPackFor` is not installed or was itself skipped (the detail says why), or `contentPackFor` names a pack that is not a code mod | Install the dependency, or fix it first |
| `DependencyVersionTooLow` | A dependency is installed but older than `minVersion` (an installed optional one too) | Update the dependency |
| `DependencyCycle` | Packs depend on each other in a loop; the detail prints it. A loop through an `optional` dependency is broken by ignoring that edge, so it loads | Remove one of the dependencies |

A different `gameVersion` is **not** a skip: the pack loads, and one `PACKS` warning says, for
example, `yourname.mypack: made for game 1.17.2, running 1.17.3 (newer patch version); loaded anyway`.

<!-- src/CwyLs.ModLoader.Core/Packs/PackSkipReason.cs:10-59; gate order: PackResolver.cs:12-30, :93-136; cascade: :151-210; cycle through optional: :212-270. Number 3 (GameVersionMismatch) is retired: PackSkipReason.cs:21-23. -->

### Stage 2: the code-mod loader (first frame)

Only for folders with `entryDll` that passed stage 1. The content has already been applied; only
the code does not run. Each skip is one warning line:
`MODS  <id> skipped: <Reason> - <detail>`.

| Reason | What it means | How to fix it |
|---|---|---|
| `NoEntryType` | No concrete class deriving from `CwyMod` with a parameterless constructor. The detail names abstract or generic candidates, or a class that derives from a `CwyMod` in another copy of `CwyLs.ModLoader.dll` | Add one non-abstract, non-generic `CwyMod` subclass; reference `CwyLs.ModLoader` with `Private=false` and do not ship it |
| `MultipleEntryTypes` | More than one such class; the detail lists them | Keep one; make the others abstract or not `CwyMod`s |
| `AssemblyLoadFailed` | The file is not a .NET assembly, vanished, is outside the folder, or loading threw (the stack is in the trace) | Rebuild; check that `entryDll` points at your DLL |
| `AssemblyAlreadyLoaded` | An assembly with the same name, or the very same build, is already loaded: the mod is installed twice, is also in `BepInEx/plugins`, or two mods share an `AssemblyName` | Remove the extra copy, or rename your assembly |
| `DependencyFailed` | A code mod this one requires was skipped or failed | Fix that mod first; the detail names it |
| `Disabled` | Code mods are switched off: `[Mods] Enabled = false` in `cwyls.modloader.cfg` | Switch it back on |

After the last mod, one summary line:
`MODS  1 loaded (cwy.sample.hello@0.1.0); 0 skipped; 0 failed  api=0.9.0 backends=…`.

A mod that passed both stages can still **fail**: its constructor or `Entry` threw. That is not a
skip reason; see [lifecycle.md](lifecycle.md#when-something-throws).

<!-- src/CwyLs.ModLoader.Core/Mods/ModStatus.cs:31-54 (ModSkipReason); skip line: src/CwyLs.ModLoader/Mods/ModLoader.cs:405-419; checks in order :237-381; Disabled :205-211; foreign base hint :497-521; summary :223-234. -->
