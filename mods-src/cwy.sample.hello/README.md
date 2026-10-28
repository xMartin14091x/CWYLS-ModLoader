# cwy.sample.hello

The sample code mod for the CWY:LS Mod Loader front door (C1-FRONTDOOR). It uses only the mod helper:
no Harmony, no game types.

- **Welcome.** When the room is ready (`RoomReady`), it waits until Satone can be interrupted
  (checking once a second through `helper.MainThread.PostAfter`, at most 60 s), then she says
  "Welcome back!" with a smile, in the game's own text box. Once per session.
- **Console.** `hello [name]` makes her greet you, by name if you give one.

Expected lines in `BepInEx/cwy-console.log`:

```
MOD   cwy.sample.hello: ready: a welcome once per session when the room is up; console command: hello [name]
MODS  cwy.sample.hello@0.1.0 loaded  type=CwySample.Hello.ModEntry ms=…
MODS  1 loaded (cwy.sample.hello@0.1.0); 0 skipped; 0 failed  api=0.9.0 backends=events,satone,gameData,language,ui
MOD   cwy.sample.hello: welcome queued as line N after 4.0 s
SAY   …
```

## Build

The framework first (the mod compiles against its built DLLs), then the mod:

```
./build.sh --no-deploy
dotnet build mods-src/cwy.sample.hello -c Release
```

The build copies `CwySampleHello.dll`, its `.pdb` and `manifest.json` to `packs/cwy.sample.hello/`.
`./deploy-packs.sh` installs that folder as `<game root>/CWYLS Mods/cwy.sample.hello/`. Edit the
manifest here, not in `packs/`: the pack folder is build output.

`build.sh` builds this project after `CwyLs.ModLoader.sln` (every `mods-src/*/*.csproj`, against the framework
DLLs it just built), so `./build.sh --no-deploy` is enough after changing the framework's public API.
Private dependencies (NuGet packages) would be copied into the pack folder too, minus anything the
game, BepInEx or CWY:LS Mod Loader already provides; this sample has none.
