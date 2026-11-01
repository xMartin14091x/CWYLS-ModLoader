# Backups — a copy of the save at every launch

Each time the game starts with the loader, the game's whole save folder is copied to
`CWYLS Backups/save-YYYYMMDD-HHMMSS/` next to the save folder itself, before the game can write to it. Old copies are
pruned with restic-style rules (keep the last 5, plus one per day, week and month). A launch whose
save is byte-for-byte the same as the newest backup makes no new copy.

Entry point: `SaveBackupFeature.Initialize(Config)`, called once from `ModLoaderPlugin.Awake` right
after `ModLoaderPaths.EnsureCreated()`. Everything that touches files is Unity-free and lives in
`src/CwyLs.ModLoader.Core/Backups/` (names, rules, policy, marker, copy and hash, scan, runner, console
text), with tests in `src/CwyLs.ModLoader.Tests/SaveBackup*Tests.cs` that run the copy and the prune
against temp folders. The feature reads **no game member**: only Unity's `Application.persistentDataPath`
and `Application.version`. It adds no patch target.

## 1. What, when, where

| | |
|---|---|
| What | `Application.persistentDataPath/SaveData`, all of it: every Steam account, the demo and release folders, the game's own `.bak` files. Symbolic links are not followed (normally there are none) |
| Live save, macOS | `~/Library/Application Support/nestopi/Chill With You/SaveData` |
| Live save, Windows | `%USERPROFILE%\AppData\LocalLow\nestopi\Chill With You\SaveData` |
| Backups | `Application.persistentDataPath/CWYLS Backups/`, next to `SaveData`: macOS `~/Library/Application Support/nestopi/Chill With You/CWYLS Backups`, Windows `%USERPROFILE%\AppData\LocalLow\nestopi\Chill With You\CWYLS Backups`. Outside the game folder, so uninstalling or reinstalling the game in Steam leaves them alone (Martin, 2026-11-01: "keep the backup somewhere safe") |
| When | once per launch, synchronously, in the first plugin's `Awake` (below). About 200 KB, a few milliseconds; the SETUP line prints `ms=` |
| Steam Cloud | never sees the backups: they are outside `SaveData`, the folder Steam Cloud syncs (its marker file is in `SaveData/Release`). Unverified: that Steam's Auto-Cloud rules for this game cover nothing else in that folder. The runner refuses to run if the backups folder is inside the save folder or the other way round |

**Why `Awake` is early enough.** BepInEx 5 starts the chainloader from `UnityEngine.Application`'s
static constructor (`[Preloader.Entrypoint]` = `UnityEngine.CoreModule.dll`, `Application`, `.cctor`),
and every CWY plugin hard-depends on `cwyls.modloader`, so `ModLoaderPlugin.Awake` is the first CWY code
to run, at frame 0. Every game save write goes through `SaveDataIO.Save<T>` (a VContainer singleton
registered by `ProjectRegister`) into `BulbulConstant.SaveDirectoryPath`, which calls
`SteamUser.GetSteamID()`, so it needs the game's DI scope built and Steam initialised by `SteamManager`.
Both happen in the first scenes, after the chainloader returns. Measured on game 1.17.3 (trace of
2026-10-27, Full level): the plugin `Awake`s at frame 0, `SteamManager.Initialize` at frame 5460,
`SaveDataManager.Load*` at frame 5469, and the first write, `SaveDataManager.SaveLimitedTimeEventData`,
at frame 15966, 16.8 s after the loader's `Awake` (`SaveHeroineData` at 17.1 s). The save boot path
(`ProjectRegister`, `BulbulConstant`, `SaveDataManager`, `EntryBehavior`) is unchanged in 1.18.0;
`SaveDataIO` only gained a flush-to-disk.

## 2. One backup

```
CWYLS Backups/
  save-20261101-140312/
    SaveData/...          an exact copy: every folder and file, each file's bytes and last-write time
    backup.json           the marker
```

```json
{
  "kind": "cwyls.save-backup",
  "schema": 1,
  "created": "2026-11-01T14:03:12.345+07:00",
  "loaderVersion": "0.9.0",
  "gameVersion": "1.18.0",
  "files": 46,
  "bytes": 188416,
  "contentSha256": "…64 hex digits…"
}
```

The name and `created` are the local time. `contentSha256` is SHA-256 over every file under `SaveData`,
in ordinal order of its relative path written with `/`: for each file, the UTF-8 path, one `0x00` byte,
the length as 8 bytes little-endian, then the bytes (`SaveBackupContent`). A rename, a move or a changed
byte all change it; empty folders do not count.

**Unchanged saves.** The live save is hashed first. When the hash equals the newest backup's
`contentSha256`, no copy is made and the log says `save unchanged since save-…`. Only the newest backup
is compared; a newest backup without a valid hash counts as different.

**Crash safety.** The copy is written to `save-YYYYMMDD-HHMMSS.partial-<8 hex>`, read back and checked
against what was written, then `backup.json` is written, and only then is the folder renamed to its final
name. A crash before the rename leaves a `.partial-` folder that is never taken for a backup and never
deleted (it is listed as "an unfinished backup" by `saves list`; delete it by hand). After a failure in the
same run, the run removes its own `.partial-` folder. Nothing is pruned after a failed backup.
Files are not forced to disk one by one: that cost about 5 ms per file on macOS (230 ms per backup) against
4 ms in all without it. A crash of the game is still safe (the data is already with the operating system);
only a power cut or OS crash within the first seconds after a launch could leave the newest backup with
files that never reached the disk. Its hash then no longer matches, so the next launch makes a fresh copy.

## 3. Retention

restic `forget` semantics: a backup is kept when **any** rule keeps it; every other backup is deleted.

| Rule | Default | Keeps |
|---|---|---|
| `KeepLast` | 5 | the 5 newest backups (at least 1: the newest is always kept) |
| `KeepDaily` | 7 | for the last 7 days **that have backups**, the newest backup of each day (local calendar day) |
| `KeepWeekly` | 3 | for the last 3 weeks that have backups, the newest of each week |
| `KeepMonthly` | 3 | for the last 3 months that have backups, the newest of each month |

The algorithm is restic's `ApplyPolicy`: walk the backups newest first; each period rule keeps a backup
when its period differs from the last period that rule kept and it still has slots left. Days and
months with no launch do not use up a slot. 0 turns a period rule off.

**Weeks are ISO 8601 weeks**, Monday to Sunday, as in restic. Week 1 is the week with the year's first
Thursday, so the week-year can differ from the calendar year: Thursday 31 Dec 2026 and Friday 1 Jan 2027
are both `2026-W53`; Monday 30 Dec 2024 is `2025-W01`. Chosen over "Sunday to Saturday" because it is the
international standard, what restic does, and locale-independent.

Not copied from restic: since 0.16 restic also keeps the oldest snapshot when a rule has slots left
over; this feature follows the table above only.

### Worked example (the defaults)

Sunday 1 Nov 2026, 21:40: the 14th launch with a changed save. The backups, newest first, and why each stays:

| Backup | Kept by |
|---|---|
| `save-20261101-214000` (Sun, new) | last, daily 2026-11-01, weekly 2026-W44, monthly 2026-11 |
| `save-20261101-091500` (Sun) | last |
| `save-20261031-200500` (Sat) | last, daily 2026-10-31, monthly 2026-10 |
| `save-20261030-193000` (Fri) | last, daily 2026-10-30 |
| `save-20261030-081000` (Fri) | last |
| `save-20261028-220000` (Wed) | daily 2026-10-28 |
| `save-20261025-180000` (Sun) | daily 2026-10-25, weekly 2026-W43 (a Sunday ends its ISO week) |
| `save-20261021-180000` (Wed) | daily 2026-10-21 |
| `save-20261014-180000` (Wed) | daily 2026-10-14 (the 7th day with a backup), weekly 2026-W42 |
| `save-20261002-180000` (Fri) | — **deleted** (daily and weekly are used up; October is already kept by the 31st) |
| `save-20260920-180000` (Sun) | monthly 2026-09 (the newest of September) |
| `save-20260905-100000` (Sat) | — **deleted** |
| `save-20260815-100000` (Sat) | — **deleted** (monthly is used up: Nov, Oct, Sep) |
| `save-20260704-100000` (Sat) | — **deleted** |

10 kept, 4 deleted: `SETUP  save backed up  src=backups files=46 size="184 KB" kept=10 removed=4 …`.
This exact case is a unit test (`SaveBackupPolicyTests.The_README_worked_example_holds_exactly`) and an
IO test against a temp folder (`SaveBackupRunnerTests.Retention_prunes_the_README_worked_example`).

### What is never deleted

Only a folder that is named exactly `save-YYYYMMDD-HHMMSS` (ASCII digits, a real date) **and** holds a
`backup.json` that parses with `"kind": "cwyls.save-backup"` and a `schema` of 1 or more is ours. That is
checked when the folder is listed and again right before it is deleted. A backup that contains a symbolic
link or junction is never deleted either, so a delete can never reach outside it. Everything else in
`CWYLS Backups` — your own folders, files, `save-scene-*`, `.partial-` leftovers, a folder with a broken
marker — is left alone.

## 4. Restoring by hand

There is no restore command: replacing the save while the game runs is unsafe (the game holds the save in
memory and writes it back over the restored files). Restore with the game **closed**.

**macOS**

1. Quit the game.
2. In Finder: Go › Go to Folder… › `~/Library/Application Support/nestopi/Chill With You/`.
3. Rename `SaveData` to `SaveData-before-restore`. Keep it until you are sure.
4. In the same folder, open `CWYLS Backups`, then the backup you want (`save-YYYYMMDD-HHMMSS`), and copy its
   `SaveData` folder into the folder from step 2.
5. Start the game. If Steam reports a Cloud conflict, choose the files on this computer.

**Windows**

1. Quit the game.
2. Press Win+R, enter `%USERPROFILE%\AppData\LocalLow\nestopi\Chill With You` and press Enter.
3. Rename `SaveData` to `SaveData-before-restore`. Keep it until you are sure.
4. In the same folder, open `CWYLS Backups`, then the backup you want, and copy its `SaveData` folder into
   the folder from step 2.
5. Start the game. If Steam reports a Cloud conflict, choose the files on this computer.

The next launch with the loader copies the restored save again when it differs from the newest backup.
`saves` in the console prints both folders for this machine.

## 5. Config, log, console

| Item | Value |
|---|---|
| Config | `[Backups]` in `BepInEx/config/cwyls.modloader.cfg`: `Enabled = true`, `KeepLast = 5` (1-1000), `KeepDaily = 7`, `KeepWeekly = 3`, `KeepMonthly = 3` (0-1000; 0 turns the rule off). BepInEx clamps values outside the range; `SaveBackupRules.Clamp` clamps again. Off: no copies are made and nothing is pruned |
| SETUP (new copy) | `SETUP  save backed up  src=backups files=46 size="184 KB" kept=8 removed=1 ms=9 folder="…/CWYLS Backups/save-20261101-140312"` |
| SETUP (no change) | `SETUP  save unchanged since save-20261101-140312; no new backup  src=backups files=46 size="184 KB" kept=8 removed=0 ms=4 folder="…/CWYLS Backups"` |
| SETUP (other) | `SETUP  disabled  src=backups enabled=false` · `SETUP  no save folder yet; nothing to back up  src=backups saves="…"` (first launch ever) · `SETUP  the save folder is empty; nothing to back up  src=backups saves="…"` |
| WARN | `WARN  BACKUPS save backup failed: <why>; the game starts as usual (old backups untouched)  src=mod-loader` — one line, the game starts normally. Also `BACKUPS n old backup(s) could not be removed: …` and `BACKUPS n link(s) in the save folder were not followed or copied: …` |
| Trace (file only) | the SETUP event (`sys=setup`, `name=backups`) also carries `bytes`, `sha256`, `leftAlone`, `rules` |
| Console | `saves` (framework group): `saves` alone prints what it does, both folders and the restore steps; `saves list` lists every backup with its time, name, size, file count and the rules that keep it (or "removed at the next launch"), the folders it leaves alone and why, and both folders; `saves folder` prints the live save folder and the backups folder |

## 6. Limits

- The game's save is copied when the loader starts, not while you play: one copy per launch at most.
  Progress made during a session is in the next launch's copy.
- Backups only happen when the game is started **with the loader**. A launch without BepInEx makes none.
- `CWYLS Backups` sits next to the save folder, outside the game folder, so uninstalling the game in Steam and
  the mod loader's installer ("Uninstall the mod loader", "Uninstall everything") both leave it alone. To remove
  the backups, delete that folder yourself.
- Times come from the computer's clock. A clock set wrong makes wrongly named backups; the rules then
  sort by those names.
- Another BepInEx plugin that does not depend on the loader could load before it; if one wrote the save
  in its own `Awake`, that write would come before the copy. None is known to.
- The game writes parts of its save at startup (about 17 s after the loader starts, on 1.17.3), after the copy. If
  those writes change bytes every time, the save is never "unchanged" and every launch makes a copy; the
  rules still cap the count (at most 5 + 7 + 3 + 3 = 18 folders, about 3.3 MB at today's size).
