# macOS tools

Helper scripts for running and watching the game with CWY:LS Mod Loader on macOS. Install BepInEx
and the loader first with the installer from a release zip (`install.command`; see the top-level
`README.md`). It puts BepInEx 5.4.23.6 and `run_bepinex.sh` into the game folder and shows the one
Steam *Launch Options* line to paste.

Platform notes: the game is a Unity 2022.3 build on the **Mono** backend (not IL2CPP), shipped as a
universal binary. BepInEx runs natively on Apple Silicon (no Rosetta needed); it is injected with
`DYLD_INSERT_LIBRARIES` through `run_bepinex.sh`.

## Scripts

| Script | What it does |
|---|---|
| `launch-mac.sh [--log]` | Starts the game with BepInEx injected (quits a running copy first). `--log` follows `BepInEx/LogOutput.log` afterwards. Steam must be running. Set `GAME_ROOT` when the game is not in Steam's default library |
| `watch-log.sh` / `watch-log.py` | A coloured live view of the loader's console log (below) |
| `migrate-old-install.sh "<game folder>"` | One-time move of an install made before the rename to CWY:LS Mod Loader: old plugin folder and config files to `BepInEx/_old/<stamp>/`, settings copied to the new config names, `CWYPacks` renamed to `CWYLS Mods`. Deletes nothing; safe to run again. The installer runs the same step for you |
| `tests/console-golden.txt` | One line per console event kind, for the parity check below |

```
tools/mac/launch-mac.sh --log        # run with the mod loader, follow the BepInEx log
```

Starting from Steam's *Play* button works once the launch option from the installer is set.

## Watching the console log

    tools/mac/watch-log.sh                 # colour by tag, follows across restarts
    tools/mac/watch-log.sh --quiet         # hide the grey tags (SETUP, TRACE, KEYS, PACKS)
    tools/mac/watch-log.sh --last 200      # start with more history
    tools/mac/watch-log.sh --strict --no-follow <file>   # exit 1 on a line that breaks the grammar

It reads `<game root>/BepInEx/cwy-console.log`, which is **appended** to: the previous session is
rolled to `cwy-console.<yyyyMMdd-HHmmss>.log` and the last five are kept, so the log of the session
a bug report is about still exists after the next launch.

One regex, one tag table, no per-event branches: the viewer parses only the canonical grammar
(`HH:mm:ss.fff  TAG  <primary>  k=v`), colours by TAG, and prints an unknown TAG dim rather than
dropping it. The full tag table with one example line each is in `src/CwyLs.ModLoader/README.md`.

Parity with the C# grammar is a build step. The unit tests regenerate
`tools/mac/tests/console-golden.txt` (one line per event kind, produced by the loader's own
formatter) and `./build.sh` runs

    python3 tools/mac/watch-log.py --strict --no-follow tools/mac/tests/console-golden.txt

so the Python parser and the C# formatter cannot drift apart unnoticed.
