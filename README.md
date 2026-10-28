<div align="center">

```
 ██████╗ ██╗    ██╗ ██╗   ██╗     ██╗      ███████╗
██╔════╝ ██║    ██║ ╚██╗ ██╔╝ ██╗ ██║      ██╔════╝
██║      ██║ █╗ ██║  ╚████╔╝  ╚═╝ ██║      ███████╗
██║      ██║███╗██║   ╚██╔╝   ██╗ ██║      ╚════██║
╚██████╗ ╚███╔███╔╝    ██║    ╚═╝ ███████╗ ███████║
 ╚═════╝  ╚══╝╚══╝     ╚═╝        ╚══════╝ ╚══════╝
```

**M O D &nbsp; L O A D E R**

*A mod loader for* **Chill With You: Lo-Fi Story**, *built so your mods keep working when the game updates.*

![version](https://img.shields.io/badge/version-0.9.0--preview-f5a9c2?style=flat-square)
![platforms](https://img.shields.io/badge/platforms-macOS%20%7C%20Windows-b9a7e8?style=flat-square)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.6-8fb8e8?style=flat-square)
![game](https://img.shields.io/badge/game-1.17.3-9fd9c2?style=flat-square)
![licence](https://img.shields.io/badge/licence-PolyForm%20Noncommercial-e8c48f?style=flat-square)

[**Download**](https://github.com/xMartin14091x/CWYLS-ModLoader/releases) &nbsp;·&nbsp; [**Make mods**](docs/guide/) &nbsp;·&nbsp; [**API reference**](docs/api/) &nbsp;·&nbsp; [**Report a problem**](https://github.com/xMartin14091x/CWYLS-ModLoader/issues)

</div>

> [!IMPORTANT]
> **Unofficial fan project.** Not made, endorsed or supported by Nestopi, so please don't contact them about it.
> Runs on the **full Steam release only**; it switches itself off on the free demo.

> [!NOTE]
> **0.9 is a developer preview.** Everything below works today; the mod API may still change before 1.0.

---

## ✦ Why

Every game update can move the code that mods hook into. Without a loader, every modder has to dig through the
game again, repair their mod and hope it doesn't damage a save. **CWY:LS Mod Loader does that work once.**
Mods talk to a stable API and plain content folders, and the loader keeps track of the game's internals for each
game version.

## ✦ What it does

| | |
|---|---|
| **Content packs** | A folder with a `manifest.json`, no code at all: text and new languages, voice clips, sound effects, extra music for the player, texture swaps, scenery colour grading, new dialogue lines and short scenes. |
| **Code mods** | A C# entry point with a helper API: game events, Satone's lines and expressions, mod windows, console commands, and game actions behind a safety gate. |
| **Version safety** | All 339 game methods the loader relies on are checked at startup, and offline with `tools/seamcheck`. If an update moves one, only that feature switches off, with a clear log line, instead of breaking the game. |
| **Save safety** | Mod content never goes into your save files, so they keep loading without mods. |
| **In-game console** | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd>: pack and mod status, cutscene replay with `scene play`, diagnostics. |
| **Diagnostic trace** | For bug reports, off by default. Turn it on with `trace on`, and off again with `trace off`. |
| **Local game API** | For tools on your own computer. Off by default, and only reachable from this computer. |

## ▶ Install

Download the zip for your system from **[Releases](https://github.com/xMartin14091x/CWYLS-ModLoader/releases)** and unzip it.

<table>
<tr><th>macOS</th><th>Windows</th></tr>
<tr valign="top"><td>

1. Quit the game. Double-click **`install.command`**. If macOS blocks it, right-click it, then **Open** and **Open** again.
2. Choose **Install or update**.
3. In Steam, right-click the game, then **Properties… › Launch Options**, and press <kbd>⌘</kbd>+<kbd>V</kbd>. The installer has already copied the line for you.
4. Press **Play**.

</td><td>

1. Quit the game. Double-click **`install.bat`**. If SmartScreen appears, choose **More info › Run anyway**.
2. Choose **Install or update**.
3. Press **Play** in Steam. There's no launch option to set on Windows.

</td></tr>
</table>

The installer works **offline**:
- it brings BepInEx 5.4.23.6, unmodified;
- it never touches your saves;
- it can uninstall again, moving files aside without deleting anything.

Mods go in the **`CWYLS Mods`** folder inside the game folder.

## ⌘ Make mods

- **Start here:** the [author's guide](docs/guide/), and [`packs/README.md`](packs/README.md) for content packs.
- **Examples:**
  - [`cwy.example`](packs/cwy.example): every kind of content;
  - [`cwy.demo.audio`](packs/cwy.demo.audio): sounds and music;
  - [`cwy.demo.scenery`](packs/cwy.demo.scenery): colour grading;
  - [`cwy.demo.script`](packs/cwy.demo.script): scenes.
- **Code mods:** copy the [template](templates/cwy-mod), study the [working sample](mods-src/cwy.sample.hello), and keep the [API reference](docs/api/) open.

Rules for mods made for or with the loader: **free of charge**, **no game files inside**, **no paid-content unlocks**.

## ◈ Build from source

You need the **.NET 10 SDK**, the game installed through Steam, and BepInEx installed in it (run the installer once).

```sh
./build.sh --no-deploy            # build, run the tests and the offline seam check
./build.sh                        # the same, then copy the loader into the game (macOS)
tools/release/make-release.sh     # the release zips, into dist/
```

The build compiles against the game's own files **on your machine**; none of them are in this repository.

## ☾ Roadmap

**0.9 · now:** the loader, content packs, the code-mod API (preview), the installers.

**1.0 · next:**
- a frozen, versioned mod API;
- game actions for music, room and timer;
- cutscene triggers and conditions;
- editing any game table;
- hot reload;
- a bridge for voice packs.

**After that:** language packs, Thai subtitles first, built on top of the loader.

## ⚖ Licence and credits

[PolyForm Noncommercial 1.0.0](LICENSE.md), with the Required Notices at the top of that file:
- no commercial use;
- mods made for or with the loader must be free;
- copies and changed versions must keep the **demo lock**.

See also [`NOTICE.md`](NOTICE.md).

Built on [BepInEx](https://github.com/BepInEx/BepInEx) with [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop),
[HarmonyX](https://github.com/BepInEx/HarmonyX), [MonoMod](https://github.com/MonoMod/MonoMod) and
[Mono.Cecil](https://github.com/jbevain/cecil). Their licences are in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
*Chill With You: Lo-Fi Story* and everything in it belongs to Nestopi Inc.; none of it is included here.

**Contributors and AI coding tools:** read [`AGENTS.md`](AGENTS.md) first.
