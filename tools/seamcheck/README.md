# seamcheck

Resolves every patch target the CWY plugins register against a game's installed `Managed/`
assemblies, without running the game. Same checker and same wording as the in-game startup check
(`PatchTargetChecker`, `targets: N ok, M missing`, `PATCH TARGET MISSING <id>: <detail>`).

```
tools/seamcheck/seamcheck.sh                                   # the local macOS install
tools/seamcheck/seamcheck.sh --platform win --managed <dir>    # a Windows install's Managed folder
tools/seamcheck/seamcheck.sh --managed <dir> [--platform mac|win] [--json out.json] [--verbose]
```

Exit code: `0` every target resolved, `1` a registered target is missing or a trace
static-constructor hazard has no exclusion, `2` tool error (build failure, failed control,
unreadable or unenumerable table, bad folder).

The script builds the tool, and with it every project under `src/`, into `$SEAMCHECK_ARTIFACTS`
(default `$TMPDIR/cwy-seamcheck`), never into `src/*/bin|obj`. Game assemblies are read only through
`System.Reflection.MetadataLoadContext`: no game code runs, nothing is written to the game folder.

## How the registered targets are found

1. **Discovery by IL.** Every assembly next to the tool that references `CwyLs.ModLoader` (all `src/*`
   projects, by a wildcard `ProjectReference`) is scanned for calls to
   `PatchTargetRegistry.Register(owner, table)`. A plugin table added later is picked up with no
   edit here.
2. **Tables read as data.** The instruction that supplies `table` must be a static property, field
   or parameterless method (every registration today is `Register(owner, X.All)`). The tool
   evaluates that one member in its own runtime. `PatchTarget` tables are strings only, so no game
   code is needed. The first registration of an id wins and later copies are warned about, as in game.
3. **Contract.** A `Register` call whose table is anything else (a local, an inline list, LINQ) is a
   tool error naming the call site. A call that forwards its own parameter is listed as a
   registration API and is not an error.

Trade-off: this runs the static table getters of our own plugin assemblies in a .NET 10 process
(BepInEx.dll is referenced from the BepInEx installed in the game, `BepInEx/core`, or from
`-p:BepInExCorePath=...`, so a getter that logs still compiles). Alternatives considered: a
hand-kept list in the tool (drifts silently as tables are added), or a central manifest in each
plugin (more files to keep in step). Known offline difference: `BulbulVoiceTargets`
builds its closed-generic type names through `VoiceSeam`, which cannot load AudioManager in the tool
and falls back to its constant names (`AudioManager`1[[KanKikuchi.AudioManager.VoiceManager, AudioManager]]`).
The report prints that plugin warning as a `note:` line. The constant names resolve to the same types.

## How a target is resolved

`MetadataMemberResolver` is the offline twin of the game's `ReflectionMemberResolver`: the same
binding flags, parameter matching and messages, with signatures rendered by the game's own
`CwyLs.ModLoader.TypeNames` (called from CwyLs.ModLoader.dll). `ResolverParityTests` runs both resolvers on
26 target shapes and fails on any difference in outcome or wording.

**Controls** (a failed control is exit 2):
- negative: `Bulbul.HeroineService.SeamcheckNegativeControlNoSuchMethod` must be missing, with exactly
  the method-level message (so the game assembly was read and the type was found);
- positive: `mscorlib!System.String.Concat(string, string)` must resolve, signature included.

## Trace preflight

The Trace plugin prefixes about 2,600 game methods at plugin load. On Mono, patching a method
compiles it, and compiling it runs the declaring type's static constructor early. The preflight
runs the plugin's own candidate rules and `TraceFilter` (default config), compiled in from
`src/CwyLs.ModLoader.Trace/TraceCandidates.cs`, over each platform's assemblies. It then lists every
class it would touch whose static constructor calls into game state: a `*LifetimeScope` member,
`get_Instance`, or `Object/GameObject.Find*`. This is a heuristic over direct calls only.

A hazard that no exclusion covers on the platform fails the run (exit 1) with one line:

```
TRACE HAZARD <type> on <platform>: <init> .cctor calls <calls>; the trace would run it at plugin load
(<n> method(s) patched); declare an exclusion in src/CwyLs.ModLoader.Trace/TraceExclusions.cs
```

It also checks each declared exclusion (`src/CwyLs.ModLoader.Trace/TraceExclusions.cs`). `needed` means
the exclusion applies, the type exists and the hazard is present. `stale-*` or
`NOT-APPLIED-BUT-HAZARD` is printed as a WARN; in the second case the uncovered hazard also fails
the run as above.

## Checking a Windows install

The Windows build of the game has its own `Managed/` folder:
`<game folder>\Chill With You_Data\Managed\` (with Steam's default library,
`C:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story\Chill With You_Data\Managed\`).
Point `--managed` at it (or at a copy of its DLLs on your machine), with `--platform win` so the
Windows trace exclusions apply. The game's DLLs belong to Nestopi: read them where they are
or keep a local copy for this check only; never commit or share them.

## Tests

```
dotnet test tools/seamcheck/Seamcheck.Tests -c Release --artifacts-path <dir outside src/>
```

(With Homebrew's .NET on macOS, set `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec` first.)

They cover the parity with the game resolver, the IL scan and its contract, the table loader, the
controls, exit codes, the JSON report, and the Trace rules (`TraceExclusions`, `TraceCandidates`,
`TraceFailureText`, which `src/CwyLs.ModLoader.Tests` cannot reach because it references Core only).
Tests that read a real `Managed/` folder show as skipped when the folder is not on the machine.

The projects are not in `CwyLs.ModLoader.sln`. `build.sh` runs `seamcheck.sh` (on the local macOS
install) and `Seamcheck.Tests` after the unit tests; a missing seam or an uncovered trace hazard
stops the build.
