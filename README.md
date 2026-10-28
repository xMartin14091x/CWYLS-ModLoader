# CWY:LS Mod Loader

A mod loader and modding framework for ***Chill With You: Lo-Fi Story*** (Steam), built on BepInEx 5.

> **Unofficial fan project.** Not made, endorsed or supported by Nestopi. Please don't contact Nestopi about it.
> **0.9 is a developer preview:** the mod API can still change before 1.0.
> **Full game only:** the loader switches itself off on the free demo.

## Why

Every game update can move the code that mods hook into. Without a loader, each modder has to dump the game,
find the right methods again and hope their mod doesn't damage a save. CWY:LS Mod Loader does that work once:
mods talk to a stable API and content-pack folders, and the loader carries the knowledge of the game's internals,
checked against each game version.

## What it does today (0.9)

- **Content packs** — a folder with a `manifest.json`; no code needed. Text and language packs, voice clips,
  sound effects and extra music, texture replacements, scenery colour grading, new dialogue lines and short scenes.
- **Code mods** — a C# entry point with a helper API (events, Satone's lines and expressions, mod windows, console
  commands, game actions behind a safety gate). The API is marked *unstable* until 1.0.
- **Version safety** — every game method the loader relies on is checked at startup (and offline with
  `tools/seamcheck`). If a game update moves one, that feature switches off with a clear log line instead of
  breaking the game.
- **Save safety** — mod content is kept out of the game's save files, so saves stay loadable without mods.
- **In-game console** (Control+Shift+C) — pack and mod status, `scene play`, diagnostics.
- **Diagnostic trace** for bug reports — off by default; `trace on` / `trace off` in the console or the installer.
- **Local game API** for tools on your own computer — off by default, loopback only.

Tested on macOS (Apple Silicon). Windows is in testing for 0.9.

## Install (players)

Download the zip for your system from the Releases page, unzip it, and double-click `install.command` (macOS) or
`install.bat` (Windows). The installer adds BepInEx 5.4.23.6 (unmodified) and the loader to the game folder, never
touches your saves, and can uninstall everything again. The `README.txt` in the zip has the details, including the
one manual Steam step on macOS.

Mods go into the **`CWYLS Mods`** folder inside the game folder.

## Make mods

- Start with [`docs/guide/`](docs/guide/) (author's guide) and [`packs/README.md`](packs/README.md) (content packs).
- Example packs: [`packs/cwy.example`](packs/cwy.example), [`packs/cwy.demo.audio`](packs/cwy.demo.audio),
  [`packs/cwy.demo.scenery`](packs/cwy.demo.scenery), [`packs/cwy.demo.script`](packs/cwy.demo.script).
- A code-mod template: [`templates/cwy-mod`](templates/cwy-mod); a working sample: [`mods-src/cwy.sample.hello`](mods-src/cwy.sample.hello).
- API reference: [`docs/api/`](docs/api/).

Rules for mods made for or with the loader: free of charge, no game files inside, no paid-content unlocks.

## Build from source

Requirements: the .NET 10 SDK, the game installed through Steam, and BepInEx installed in it (run the
installer once). Then:

```sh
./build.sh --no-deploy      # build, run the tests and the offline seam check
./build.sh                  # the same, then copy the loader into the game (macOS)
tools/release/make-release.sh   # the release zips, into dist/
```

The build compiles against the game's own assemblies on your machine; none of them are in this repository.

## Licence

[PolyForm Noncommercial 1.0.0](LICENSE.md) with the Required Notices at the top of that file. No commercial use;
mods made for or with the loader must be free. See [`NOTICE.md`](NOTICE.md) and
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). Contributors and AI coding agents: read [`AGENTS.md`](AGENTS.md)
first.
