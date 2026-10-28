# Mods/Facets — further helper facets

The base helper (`../README.md`) has eleven further facets on `ICwyModHelper`. The API version stays
`CwyApi.Version` 0.9.0: only the framework implements these interfaces, so adding members is
additive. One file per facet, so each feature edits only its own file.

| Facet | File | State | Backed by |
|---|---|---|---|
| `Harmony` | `CwyModHarmony.cs` | **working** | HarmonyX, id = mod id |
| `Conditions` | `CwyModConditions.cs` | parse + tokens work; evaluate = `Unavailable` | `ConditionRegistry.Shared` |
| `Actions` | `CwyModActions.cs` | working: list, register, run (runner installed by `ActionsFeature` while `[Actions] Enabled`) | `ActionRegistry.Shared`, `ActionServices.Shared` |
| `Scenes` | `CwyModScenes.cs` | working: register, number lookup, `Play`, `IsFlagSet`, `SetFlag`; rows run through the `ReadSentence` seam (`ScenarioFeature`) | `ScenarioCommandRegistry.Shared` |
| `EditData` | `CwyModEditData.cs` | slot | `ModFacetBackends` |
| `Audio` | `CwyModAudio.cs` | working: Rows, MusicTracks, ReplaceClip, Select, Reload (AudioFacetBackend) | `ModFacetBackends` |
| `UiSkin` | `CwyModUiSkin.cs` | slot | `ModFacetBackends` |
| `Camera` | `CwyModCamera.cs` | slot | `ModFacetBackends` |
| `VoiceStream` | `CwyModVoiceStream.cs` | slot | `ModFacetBackends` |
| `Mic` | `CwyModMic.cs` | slot (`IsEnabled`, `Route`) | `ModFacetBackends` |
| `Api` | `CwyModApi.cs` | working: `IsRunning`, `Endpoint` (`ApiFacetBackend` in CwyLs.ModLoader.Api; never the key) | `ModFacetBackends` |
| Grading | CwyModGrading.cs | working | GradingFeature via ModFacetBackends |

`Ui` (`ICwyModFacets.cs`) is backed by `ModFacetBackends.Use<IModWindowBackend>("windows")` (`Bulbul/UI/ModWindows.cs`); the older `CreateWindow(options, out problem)` is deprecated (Notice).

## Shapes every new facet follows

- `ICwyModFacet`: `IsAvailable` and a never-empty `Status` line. Without a backend a facet answers
  `IsAvailable = false`, its documented "not available" status, and logs one `MODS <id>: … not
  available` warning per mod on first use of a call that does nothing.
- Anything that registers returns a `CwyRegistration` (`IDisposable`): dispose = unregister. A refused
  registration comes back inactive with `Problem` set (and one `MODS` warning); nothing throws for a
  taken name. Everything still registered is taken back when the mod stops. Hotkeys: the table has no
  removal, so disposing stops the callback and the key stays listed until restart.
- Results with a reason are objects (`ConditionParseResult`, `ConditionResult`, `ActionResult`), not
  `out string problem`.
- Framework refusals in the MainThread facet (mod not running, queue full) are
  `MainThreadRejectedException` (Core/Threading); `PostAfter` returns a `DelayedWork` handle.

## Plugging a backend in

In the feature's own `Initialize` (called from a plugin's `Awake`, before mods load):
`ModFacetBackends.Use<ICwyModAudio>("audio", new AudioFacetBackend());` where the backend implements
the facet interface. Add members to the interface and forward them in the `Mod<Facet>` proxy in the
same file (`BackendFor("call")` returns null after the one warning when no backend is installed).

## Harmony take-back

`helper.Harmony.Instance` is `new Harmony(<modId>)`, created on first use. When the mod fails in its
constructor or `Entry`, and at `Application.quitting`, `ModHelper.Deactivate` calls
`ModHarmony.TakeBack`: `UnpatchSelf()` for the mod's id, then `Unpatch(original, patchMethod)` for every
remaining patch whose patch method is declared in one of the mod's assemblies
(`ModRuntime.TryFindByAssembly`) under any id. Never an id-less `UnpatchAll`. A failure to remove one
patch is one `MODS` warning; the count goes to the trace record `mods/harmony` and
`mods/deactivated harmonyPatches=N`.
