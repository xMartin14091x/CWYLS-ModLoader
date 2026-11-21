# When the game updates

What to do when a Steam update of *Chill with You: Lo-Fi Story* lands, from "is anything broken?" to a
fixed release. For maintainers of CWY:LS Mod Loader and of forks.

## 1. Know what the loader already does

- At every start the loader checks each game method and field it relies on (359 in 0.9.x). A moved one
  switches off only the feature that needs it, with a line in `BepInEx/LogOutput.log`:
  `PATCH TARGET MISSING <id>: <detail>`, and `targets: N ok, M missing`.
- It also compares the running game's version with the one the tables were verified against
  (`GameVersionGate.ExpectedGameVersion`). A different version never blocks anything; it only explains
  why a target might have moved.
- One exception, in Pack Maker only: its paid-DLC gate trusts its markers only on the build they were made
  for (`MakerDlcMarkers.MadeForGameVersion`, the same version). On a newer build, or when the version
  cannot be read, it hides every line that no classified scenario group speaks (room and activity lines
  among them) until a loader release confirms the new build. Lines of groups with a known free type stay
  listed.
- Content packs carry their own `gameVersion` and are checked separately (`docs/guide/versioning.md`).

## 2. Check the new build offline

```sh
tools/seamcheck/seamcheck.sh                                        # the game installed on this Mac
tools/seamcheck/seamcheck.sh --platform win --managed <Managed dir>   # a copy of a Windows install's Managed folder
```

Exit code `0` means every target resolved on that platform; `1` names each missing target. No game code
runs and nothing is written to the game folder. Keep your copy of the Windows `Managed` folder outside
the repository (it is the game's own code).

## 3. If every target is found

1. Set `GameVersionGate.ExpectedGameVersion` to the new version, and the `gameVersion` of the packs and
   templates in this repository. If the update adds paid DLC content, add its groups or lines to
   `MakerDlcMarkers.DlcDay` first: the version bump is what tells Pack Maker's DLC gate the markers fit.
2. `./build.sh --no-deploy`: build, tests, seamcheck.
3. In game, on macOS **and** Windows: start the game, open the console (Ctrl+Shift+C), run `smoke run`
   and read `BepInEx/smoke-report.json`. Checks that need an event (a pomodoro, a line in the story box)
   report "not exercised" until you trigger one.
4. Update the README's "Tested on" table, then release (§5).

## 4. If a target is missing

1. Read the `PATCH TARGET MISSING` line: it names the member and what was expected.
2. Find where the member went in the new build. Decompile locally if you need to, and never commit
   decompiled code or game files.
3. Update the target table (`*PatchTargets.cs`) and the code that uses it, rebuild, and run seamcheck
   again on both platforms until nothing is missing. Then continue with §3.
4. Saves must stay loadable by the game without mods: never add pack data to save structures.

## 5. Release

1. Bump `CwyLsVersion` in `Directory.Build.props` and `CwyApi.PluginVersionText`.
2. `tools/release/make-release.sh`: build, tests, seamcheck, then the zips and `SHA256SUMS` in `dist/`.
3. Run both installers against a test copy of the game (`install.command --game <copy> --install`,
   `install.ps1 -GameRoot <copy> -Action Install`), then status, uninstall and uninstall everything.
4. Publish the release, upload the same zips to Nexus Mods, and post the notes in Discord `#releases`.

The installers stamp the installed DLLs with the install time. The zips carry one fixed file date (so
builds are reproducible), and BepInEx caches each plugin's details by that date; without the stamp an
update would keep the previous version's cached details.
