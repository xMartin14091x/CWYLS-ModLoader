# CWY:LS Mod Loader code-mod template

A copyable starting point for a code mod: a mod is a folder in `<game root>/CWYLS Mods/<id>/` with a
`manifest.json` whose `entryDll` names your DLL. The folder may also hold content (textures, audio,
data) exactly like a content pack. The API is described in `src/CwyLs.ModLoader/Mods/README.md`.

## Start a mod

1. Build the framework once in the checkout: `./build.sh --no-deploy`.
2. Copy this folder, e.g. to `mods-src/yourname.mymod/`.
3. Pick an id: lowercase letters, digits, `.`, `_`, `-`, 3 to 64 characters, e.g. `yourname.mymod`.
   It never changes once people use the mod.
4. Set it in two places: `<ModId>` in `MyMod.csproj`, and `id` in `manifest.json`. Rename the
   assembly (`<AssemblyName>`) if you like, and set `entryDll` to match (`MyMod.dll`).
5. `dotnet build -c Release`. The output `bin/Release/netstandard2.1/pack/<id>/` is the whole mod
   folder. Copy it to `packs/<id>/` in the checkout (then `./deploy-packs.sh`) or straight to
   `<game root>/CWYLS Mods/<id>/`, and restart the game.

Outside the checkout, point the build at it: `dotnet build -c Release -p:CwyRepoRoot=/path/to/checkout/`.
The build also needs the installed game with BepInEx in it: its `Managed` folder and
`<game root>/BepInEx/core`. Both are found in the default Steam library; for a game installed
elsewhere add `-p:SteamGameRoot=/path/to/game/` (or set `-p:GameManagedPath=...` and
`-p:BepInExCorePath=...` one by one).

## What you get

- `ModEntry.cs`: the one class deriving from `CwyMod`, showing config, data, events, a console
  command, a hotkey, a Satone line, and shutdown.
- `manifest.json`: `loaderVersion` is the oldest CWY:LS Mod Loader API your mod needs (0.9.0 now).
  Add `dependsOn` entries for mods or packs you need; `{ "id": "x", "optional": true }` loads
  after `x` when it is installed and ignores it when it is not.

## Rules the loader enforces

- Exactly one concrete `CwyMod` class with a parameterless constructor (`NoEntryType`,
  `MultipleEntryTypes`).
- Do not ship `CwyLs.ModLoader*.dll`, `BepInEx.dll`, `0Harmony.dll`, Unity or game DLLs: every
  reference is `Private=false`. A bundled copy is ignored with one `MODS … ignoring its own copy` warning.
- Private dependencies are fine: add a `PackageReference` (or a `Reference` with `Private=true`) and
  the build copies the DLL into the pack folder. Anything the game, BepInEx or CWY:LS Mod Loader already
  provides is left out, because the loader would use the process's copy anyway: the game ships
  `Newtonsoft.Json`, `System.Text.Json`, `System.Memory`, `Microsoft.Bcl.AsyncInterfaces` and more
  (its `Managed` folder), so your mod uses those. Libraries the game does not ship
  (`System.Collections.Immutable`, `Microsoft.Extensions.*`, `System.Reactive`, a provider SDK) ship
  with your mod and load from your folder.
- Do not ship another mod's DLL. Declare that mod in `dependsOn` and reference its DLL with
  `Private=false`; a bundled copy of another mod's entry DLL is ignored with one `MODS` warning.
- A mod that throws in its constructor or `Entry` is marked failed and its registrations are
  removed; mods that require it are skipped (`DependencyFailed`). The `MODS` console line says why;
  the full stack is in the trace file.
- Changing the DLL needs a game restart: the runtime cannot unload an assembly. Never overwrite
  the DLL while the game runs (the game keeps it memory-mapped): quit, copy, start.
- Code that touches another mod's types (an optional dependency) goes in its own
  `[MethodImpl(MethodImplOptions.NoInlining)]` method, called only when
  `helper.ModRegistry.IsLoaded("other.mod")` is true: Mono may compile a method before it runs.
- Keep an optional dependency's types out of the entry class itself: no interface of it on the class
  that derives from `CwyMod`, no field, property or base type of it there. When that mod is not
  installed, the entry class cannot load and your mod is skipped as `NoEntryType` (the `MODS` line
  names the missing assembly). Put the integration in a separate class and create it only after
  `IsLoaded` returns true.
- Hotkeys are bound in your `.cfg` as `[Hotkeys] <id>.Primary` and `<id>.Alt` (registry id
  `mod:<modId>/<id>`). An id another owner holds is refused, never replaced; a chord another key
  uses still registers, with one warning.

The API is 0.x until CWY:LS Mod Loader 1.0: a minor version may still change it, and
every change is announced first with a deprecation notice in the log.
