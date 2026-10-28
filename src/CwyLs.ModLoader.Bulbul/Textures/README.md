# Texture replacement

Puts the PNGs in a content pack onto the game's materials, one frame after `RoomScene` loads.

- `CwyLs.ModLoader.Core/Textures/` — the mapping: paths and JSON in, `TextureRule`s out. No Unity, fully unit-tested.
- `CwyLs.ModLoader.Bulbul/Textures/` — this folder: find the renderer, load the file, write the material, remember how to undo it.

The adapter is wired up with one call, from `BulbulPlugin.Awake`:

```csharp
TextureFeature.Initialize(Config);
```

That binds the config, registers the smoke checks, creates the hidden `DontDestroyOnLoad` host,
arms live reload (the `textures.reload` key and the file watcher) and subscribes to
`SceneManager.sceneLoaded` and `PackManager.PacksResolved` — the event that carries the resolution
the scan produced, so a pass applies exactly the pack list the framework reported. It is idempotent.

## Pack convention

Everything is relative to a pack folder in `<game root>/CWYLS Mods/<packId>/`.

```
textures/<RendererName>/<slot>.png      that PNG into that slot of that renderer
textures/@<MaterialName>/<slot>.png     that PNG into that slot of that MATERIAL ASSET, everywhere
textures/_rules.json                    optional; for anything the folders cannot say
textures/_anything/**                   yours: source art, WIP. Never read as a renderer.
```

**`@` is the one that repaints a worn costume.** A renderer rule can only name an object that
exists in the scene file; a costume prefab's renderers (`Body1`, `Top2`, `Top3`, `Bottom2`) are
instantiated at runtime and no pack can predict them. Their *material* names are stable, so
`textures/@mt_Tee_A/_MainTex.png` reaches the tee whatever renderer happens to be wearing it — and
it is written even when the costume is not currently on, so it is already correct the moment the
game instantiates the prefab.

`<RendererName>` is the name of the **GameObject** that carries the renderer, matched
case-insensitively (`Body`, `Face`, `Top1`). `<slot>` is either a shader property spelled exactly
(`_MainTex`) or one of the aliases below, in any case.

Anything else under `textures/` is ignored in silence — a PNG nested deeper than
`textures/<renderer>/<file>.png`, a `.psd`, a README, the rules file itself. A file that is clearly
meant to be a rule and is not one — `textures/Body/glow.png`, where `glow` is no alias — is logged
once as a warning. The apply summary prints the number of PNGs the packs hold next to the number of
rules made, so a silent miss still shows up as a mismatch.

Folder-convention rules always write material index **0**. Use `_rules.json` for anything else.

### `textures/_rules.json`

```json
[
  { "renderer": "Body",  "materialIndex": 0, "property": "_MainTex", "file": "alt/summer_body.png" },
  { "renderer": "Hair1", "property": "reflect", "file": "Hair1/reflect.png" },
  { "material": "mt_Tee_A", "property": "_MainTex", "file": "tee_white.png" }
]
```

| Member | Required | Meaning |
|---|---|---|
| `renderer` | one of | GameObject name of the renderer. |
| `material` | one of | Material asset name, e.g. `mt_Tee_A`. Exactly one of `renderer`/`material`. |
| `property` | yes | Shader property (`_MainTex`) or an alias (`base`). |
| `file` | yes | PNG path **relative to `textures/`**, so `"my.png"` is `textures/my.png`. |
| `materialIndex` | no | Which of the renderer's materials; `0` by default. Not allowed with `material`. |

Members the framework does not know are an error for that entry; prefix your own with `x-` to have
them ignored. A bad entry costs only that entry. Malformed JSON costs the whole file, with a
`line:column` in the log. Rules-file entries are applied **after** the folder convention, so an
explicit entry beats a file that happens to sit in the matching folder.

## Material property rules: scenery and desk

The same `textures/_rules.json` also takes entries with a **value** instead of a `file`. They write a
colour, tint, float or vector onto every loaded material whose name matches - the window-view
scenery (`mt_env_*`), the far room (`mt_combined_room_far`), the sky (`Skybox`), the desk family
(`mt_desk*`).

```json
[
  { "material": "mt_desk_1", "property": "_Color", "tint": "#C9A27E" },
  { "material": "mt_env_*", "property": "color", "tint": [0.92, 0.96, 1.0] },
  { "material": "mt_env_buil", "property": "emission", "color": "#FFD9A0", "intensity": 2.5 },
  { "material": "mt_env_aurora", "property": "_Contrast", "float": 0.8 },
  { "material": "mt_desk_1", "property": "hsvg", "vector": [0.05, 1.1, 1, 1] }
]
```

| Member | Required | Meaning |
|---|---|---|
| `material` | yes | Material name, or a pattern: `*` any run, `?` one character, case-insensitive. `mt_desk*` also matches `mt_desktop` and `mt_desk_lamp`. No `renderer`: shared materials are what scenery objects have in common. |
| `property` | yes | Shader property (`_Color`) or an alias: `color`/`maincolor` → `_Color`, `basecolor` → `_BaseColor`, `emission`/`emissioncolor` → `_EmissionColor`, `shadowcolor` → `_ShadowColor`, `hsvg` → `_MainTexHSVG`. |
| `tint` | one of | Multiplies the game's own colour channel by channel (keeps its shading). `"#RGB"`, `"#RRGGBB"`, `"#RRGGBBAA"` or `[r, g, b(, a)]`. |
| `color` | one of | Replaces the colour. Same formats; values above 1 are HDR. |
| `float` | one of | A float, range or int property. |
| `vector` | one of | 1-4 numbers (missing ones are 0). |
| `intensity` | no | With `color`/`tint`: multiplies RGB (HDR emission). |

- Colours are as a colour picker shows them (sRGB); Unity converts for the shader.
- `_Color` and `_BaseColor` mirror each other (lilToon and URP keep both); a rule writes both where
  the material declares them.
- The property's type is checked against the live shader (`Shader.GetPropertyType`); a colour rule on
  a float property is one warning and skipped.
- Same pack: a later entry for the same `material` + `property` wins and the earlier one is reported.
  Across packs: the later pack wins (`MaterialPropertyRuleSet.Merge`).
- Writes go to the **shared material asset in memory** (no game file), are backed up the first time,
  and are undone by `RestoreAll()` - so a tint is always computed from the game's colour, never
  compounded. They are applied in the same pass as the texture rules, after its restore.
- Paid content is refused: a material (or texture-rule renderer/material) whose name matches
  `*dlc*`, `*shop*`, `*purchase*` or `*premium*` is one warning naming the rule (`PaidContentGuard`;
  no material of the 1.17.3 PC build matches - the only DLC is the soundtrack).

The pass's console line counts both kinds and names the property rules separately:
`TEXTURE  5 rules applied  failed=0 trigger=RoomScene+2s mode=Shared files=1 props=4`.

### Re-apply triggers (besides the scene and costume ones)

| Seam | Why |
|---|---|
| `WindowChanged` event (on/off of a window view or a time of day; raised from `WindowViewService` via the event bus) | the day/sunset/night objects carry their own lamp-table materials; a view switches renderers on |
| `DecorationService.ChangeDecoration(DecorationSkinType, bool)` | every decoration change |
| `DecorationModelChangerMatTexture.Apply(...)` | sets the shared material's main texture |
| `DecorationModelChangerCommon.Apply(...)` | swaps the desk's shared material and its `mainTexture`/`_EmissionMap` |

The three decoration seams are `SceneryPatchTargets` (`scenery.decoration.change`, `.mattexture`,
`.common`), registered and verified at startup. Each only arms a deferred pass (`TextureHost.Request`,
coalesced); they do nothing until the first scene pass has run or while `[Scenery] Enabled=false`.

### Console: `scenery` (and `tex scan` / `tex export` once routed)

```
scenery scan [pattern ...]      loaded materials of the scenery, desk and window families (or the patterns):
                                 name [family] shader  users N (active)  _Color _MainTex _EmissionColor _MainTexHSVG
scenery export [pattern|all]    every property of every match -> BepInEx/data/materials.json (schema 1)
scenery list                    property rules: applied, problems
scenery restore                 restore textures and properties to the game's own
scenery apply                   run the pass now
```

### Config

| Key | Default | Effect |
|---|---|---|
| `Scenery.Enabled` | `true` | Property rules and the window/decoration re-apply. Off restores every property rule at the next pass (switching it arms one). `Textures.Enabled=false` still turns the whole pass off. |

### Smoke checks (phase D)

| Id | Kind | Proves |
|---|---|---|
| `d.scenery.rules` | quiet | the packs' property rules parsed without problems (vacuous without rules) |
| `d.scenery.applied` | quiet | every rule reached a material and every written slot still holds our value |
| `d.scenery.seams` | quiet | the three decoration seams are patched |
| `d.scenery.restore` | loud | restore empties every property backup, then the pass re-applies |

## Slot aliases

Verified against lilToon's property names and the game's `mt_*` materials that use it
(game 1.17.x). A slot starting with `_` is passed to the shader verbatim, so you never have to wait
for an alias.

| Alias | Property | Where the game uses it |
|---|---|---|
| `base`, `main` | `_MainTex` | `mt_Body` → `t_body_base.png`, `mt_Face` → `Face_2_base.png`, `mt_Hair` → `Hair_base.png` |
| `basemap` | `_BaseMap` | hidden SRP mirror of `_MainTex`; the game sets it to the same texture |
| `basecolor` | `_BaseColorMap` | hidden SRP mirror of `_MainTex` |
| `main2`, `main3` | `_Main2ndTex`, `_Main3rdTex` | unused by Satone's materials |
| `shadow` | `_ShadowColorTex` | **empty in every `mt_*` material** — see the note below |
| `shadow2`, `shadow3` | `_Shadow2ndColorTex`, `_Shadow3rdColorTex` | unused |
| `shadowborder` | `_ShadowBorderMask` | `mt_Face` → `Face_2_shadow.png`, `mt_Hair` → `Hair_shadow.png` |
| `shadowstrength`, `shadowblur` | `_ShadowStrengthMask`, `_ShadowBlurMask` | unused |
| `rim` | `_RimColorTex` | `mt_Body` → `t_body_shadow.png` |
| `rimshade` | `_RimShadeMask` | `mt_Hair` → `Hair_reflect.png` |
| `backlight` | `_BacklightColorTex` | `mt_Body` → `t_body_shadow.png` |
| `reflect`, `reflection` | `_ReflectionColorTex` | `mt_Hair` → `Hair_reflect.png`, `mt_Hairpin` |
| `emission`, `emission2` | `_EmissionMap`, `_Emission2ndMap` | `mt_Headphone_cat` |
| `normal`, `bump` | `_BumpMap` | unused |
| `matcap`, `matcap2` | `_MatCapTex`, `_MatCap2ndTex` | `mt_Hairpin`, `mt_Headphone_cat` use the 2nd |
| `matcapmask`, `matcap2mask` | `_MatCapBlendMask`, `_MatCap2ndBlendMask` | `mt_Hairpin` |
| `smoothness` | `_SmoothnessTex` | `mt_Hairpin` |
| `metallic` | `_MetallicGlossMap` | unused |
| `alphamask` | `_AlphaMask` | unused |
| `outline` | `_OutlineTex` | unused |

**The one that surprises people:** this game never fills lilToon's `_ShadowColorTex`. The PNGs it
*calls* "shadow" — `t_body_shadow.png`, `Face_2_shadow.png`, `Hair_shadow.png` — are bound to
`_BacklightColorTex`, `_RimColorTex` and, on the face and hair, `_ShadowBorderMask`. To replace what
the game shipped, use `rim` / `backlight` / `shadowborder`. `shadow` writes a slot that was empty.

## Renderer names

Observed in the game's `RoomScene` (game 1.17.x). Every one of these has
**one** material, at index 0. "active" is the state saved in the scene; the game switches costume
pieces at runtime, which is exactly why the lookup includes inactive objects.

| Renderer | Scene path | Material | Active | What it is |
|---|---|---|---|---|
| `Body` | `Character/Character/Body` | `mt_Body` | yes | Satone's body/skin mesh — what the example pack repaints |
| `Face` | `Character/Character/Face` | `mt_Face` | yes | the face plane |
| `Hair1` | `Character/Character/Hair1` | `mt_Hair` | yes | hair |
| `Hair_type2` | `Character/Character/Hair_type2` | `mt_Hair` | no | alternative hairstyle |
| `Top1` | `Character/Character/Top1` | `mt_Outfit` | yes | shirt |
| `Paka1` | `Character/Character/Paka1` | `mt_Outfit` | yes | cardigan |
| `Bottom1` | `Character/Character/Bottom1` | `mt_Outfit` | yes | skirt/trousers |
| `Socks` | `Character/Character/Socks` | `mt_Outfit` | yes | socks |
| `HairPin_Shoot`, `HairPin_Dot`, `HairPin_Done` | `Character/Character/HairPin_*` | `mt_Hairpin` | no | hairpins |
| `m_Headphone` | `…/Character_Head/m_Headphone` **and** `…/Character_Spine2/m_Headphone` | `mt_Items` | yes / no | **two renderers share this name** |
| `m_Headphone_cat` | `…/Character_Head/m_Headphone_cat` | `mt_Headphone_cat` | no | cat-ear headphones |

`Top1`, `Paka1`, `Bottom1` and `Socks` all share `mt_Outfit`, and `Face` shares `mt_Face` with the
~40 expression meshes under `Character/group9/` (`Eye_*`, `Mouth_*`, `Eyeblow_*`). Because we write
instance materials, a rule on `Top1` repaints **only** `Top1`; give each renderer its own rule (or
its own file) if you want them all.

### Ambiguous names

When more than one renderer carries the name, the log gets a warning listing every candidate path
and the one that was used. The choice is, in order: an **active** renderer whose path contains
`Heroine` or `Character`, then any renderer whose path contains one of those, then the first found.
`m_Headphone` is the case that actually happens — both are under `Character`, so the active one
under `Character_Head` wins.

## The costume problem, and what writes where

The first in-game run applied every rule, verified each one off the live material, logged success —
and the player saw nothing. The reason is in the game's costume code (`Bulbul.CostumeChangeService`):

`CostumeChangeService._defaultCostumeObjects` resolves, in `RoomScene`, to exactly
**`Body`, `Bottom1`, `Paka1`, `Socks`, `Top1`** — the renderers a pack names. On any non-default
costume, `TryChangeModel` calls `DeactivateDefaultCostume()` (`SetActive(false)` on all five) and
`InstantiateAsync`es `Costume_Polo_001` / `Costume_Tee_001` under `Character/Character`. Those
prefabs' renderers have **different names**:

| Default (may be inactive) | Costume prefab | Material |
|---|---|---|
| `Body` → `mt_Body` | `Body1` | **`mt_Body` — the same asset** |
| `Top1` → `mt_Outfit` | `Top3` (Polo) / `Top2` (Tee) | `mt_Polo_A` / `mt_Tee_A`, and swapped again at runtime |
| `Bottom1` → `mt_Outfit` | `Bottom2` | `mt_Jeans` |
| `Socks` → `mt_Outfit` | `Socks` | `mt_Outfit` |

So a rule can never *name* the renderer the player is looking at. What the two renderers do share is
the **material asset** — which is why `WriteMode` exists and why it defaults to `Shared`.

- **Shared** (default, `renderer.sharedMaterials[i]`): the loaded material asset, **in memory only —
  no game file is ever written**. Reaches `Body1` too, because it uses the same `mt_Body`. Undone by
  `RestoreAll()` like anything else.
- **Instance** (`renderer.materials[i]`): this renderer only. Correct, precise, and invisible when
  the renderer is one of the deactivated defaults.
- **Both**: the asset first, then an instance.

> **The trap that cost a build.** `renderer.materials` does not merely *return* copies — it
> **instantiates them and rebinds the renderer**, so `sharedMaterials` afterwards yields
> `mt_Body (Instance)`, not the asset. An "instance first, then shared" order therefore writes the
> instance twice and never touches the asset: in the first build, `mt_Body` id `23774` (which the
> worn costume's `Body1` uses) stayed untouched while we happily patched instance id `-26852`, and
> nothing changed on screen. The shared write now always goes **first**, on the asset, and the
> `TEXTURE` line prints `mat=<id>` — with `-><id> REBOUND` if the renderer ever moves off the asset
> again, so the trap cannot recur silently.

**Consequence for outfits:** `Body` textures reach every costume through `mt_Body`. `Top1`/`Bottom1`
renderer rules only reach the **default** costume — the Polo and Tee prefabs use different
materials, and `CostumeSkinChanger` re-assigns `Top3`'s `sharedMaterial` from `CostumeSkinDB` on
every skin change. **To repaint a worn costume's shirt, use a `@material` rule**, e.g.
`textures/@mt_Tee_A/_MainTex.png` for the tee or `textures/@mt_Polo_A/_MainTex.png` for the polo.

A `@material` rule writes **every loaded material with that name** — the asset plus any
`" (Instance)"` copy — keyed on each material's own instance id, so one asset shared by ten
renderers is written once, backed up once and restored once.

## Costume-change re-apply

Four Harmony postfixes, all on **synchronous** methods (`TryChangeModel` is an `async UniTask<bool>`,
so a postfix on it would fire when the state machine is *created*, not when the costume exists):

| Seam | Fires when |
|---|---|
| `CostumeChangeService.AttachCostumeBonesToCharacter` | the new prefab's renderers are in the hierarchy |
| `CostumeChangeService.ActivateDefaultCostume` | the five default objects come back on |
| `CostumeChangeService.TryChangeSkin` | the whole costume change has finished |
| `CostumeSkinChanger.ChangeSkin` | the renderer's shared material is replaced — the shirt swaps material |

Each only *arms* a deferred pass (`TextureHost.Request`); none walks the scene inside the game's own
call stack. Missing seams are logged and skipped — a game update costs the re-apply, not the
feature. The log line reads `TEXTURE ... (costume change (ChangeSkin))`.

The four seams are in the patch-target registry (`CostumePatchTargets`:
`textures.costume.attachbones`, `.activatedefault`, `.trychangeskin`, `.changeskin`), registered and
verified in `TextureFeature.Initialize`, so a game update that renames one prints
`PATCH TARGET MISSING` at startup; `CostumePatches` patches the same names from that class.

Two other things can hide a correct write, and both are handled:

- **SRP mirrors.** The game is URP (it ships the URP runtime and renders through a
  `UniversalRenderPipelineAsset`) and every `mt_*` material binds the same texture to
  `_MainTex`, `_BaseMap` **and** `_BaseColorMap`. Which one the compiled pass samples cannot be seen
  from outside (the game ships compiled shaders only), so
  `AlsoSetSrpMirrors` (default on) writes all three wherever the material declares them.
- **`MaterialPropertyBlock`.** A block overrides the material for every property it carries.
  `ClearPropertyBlockTextures` (default on) re-sets a block that *already* holds the property. It
  never adds one, which would make the block authoritative for a slot the material was driving.

## Delayed re-passes

A scene trigger applies at +1 frame and again at **+2 s and +10 s** (`TextureHost.RepassSeconds`).
`CostumeChangeService` instantiates its prefab asynchronously well after `sceneLoaded`, and the game
also ships MagicLightmapSwitcher, which reassigns materials. Before each pass overwrites anything it
audits what the previous one wrote and logs any slot that was reverted:

```
TEXTURES: re-applying (sceneLoaded+2s): Character/Character/Body[0]._MainTex (Shared) previous value was 't_body_base' 2048x2048, we wrote '_MainTex' 1024x1024
```

A manual apply, a live reload and a pack-rescan trigger get one pass, not three.

## Editing textures live

You do not have to restart the game to see a new PNG. Edit the file **in place** under
`<game root>/CWYLS Mods/<packId>/textures/…` and save it: the pack folder is watched and the texture
is back on the character in well under a second.

```
TEXTURES: file changed on disk: my.pack/textures/@mt_Tee_A/_MainTex.png -> reloading
TEXTURES: reloaded 3 file(s) (1 changed on disk, 0 new, 0 removed), applied 3 rule(s), 0 failed in 38 ms (watcher …)
```

- **The key:** `textures.reload` — **Ctrl+Shift+R**, or **F8** (the alternate; on macOS a plain
  F-key is a media key unless Fn is held). Both chords are config entries under `[Keys]`, and both
  are always live. Use it when you want a reload right now, or when you changed something the
  watcher cannot see.
- **Adding, deleting and renaming files works too**, not just editing one in place: a reload rescans
  the packs, so a new `textures/@mt_Tee_A/_MainTex.png` becomes a rule and a deleted one is undone
  back to the game's own texture.
- **Editing in the repo instead?** `./deploy-packs.sh` copies `packs/` over `CWYLS Mods/` and the
  watcher sees those writes exactly like any other — so `save, deploy-packs, look` is a loop too, it
  just has one more step than editing the deployed file.
- **What a reload does:** drop the loader's asset cache (so a file whose bytes changed but whose
  timestamp did not is still re-read), rescan the packs, then restore and apply. The log line says
  how many files there are and how many changed, appeared or went away since the last reload.

### Two detectors, and why

| | What it is | Latency | Fails by |
|---|---|---|---|
| `FileSystemWatcher` | one per loaded pack's `textures/` folder, recursive | ~0.4 s (the debounce) | going quiet — on Mono/macOS it can simply stop delivering |
| last-write poll | `Textures.WatchPollSeconds` (2 s), a `stat` per pack texture file | ≤ 2 s | nothing; it is the backstop |

Both run by default. Whichever notices first reloads, and the reload refreshes the file reading they
both compare against, so the other one does not fire again for the same edit. The startup line says
which are live:

```
TEXTURES live reload armed: FileSystemWatcher on 1 folder(s) + 2s poll, 3 file(s) tracked; key 'textures.reload' (see the KEYS line)
```

If the watcher could not be created (descriptor limits, a network volume) that line reads
`2s poll only (no FileSystemWatcher)` and live reload still works, just a little later. Set
`Textures.WatchPollSeconds = 0` to trust the watcher alone, or `Textures.WatchFiles = false` to
leave only the key.

Watcher callbacks arrive on a thread pool thread; nothing Unity is touched there. An event records
the path and the time, and the reload runs on the main thread from the texture runner's `Update`,
`0.4 s` after the last event — one save that writes the file two or three times is one reload.

## Restore semantics

- The first time a slot is written, its previous `Texture` is recorded — `null` included, because
  most lilToon slots ship empty.
- `TextureFeature.RestoreAll()` puts every one of them back, newest first, and forgets the backups.
  It returns how many slots it restored; slots whose material has since been destroyed are skipped.
- `TextureFeature.Apply()` **restores first, then applies**. So removing a pack, renaming a file or
  rescanning leaves the game's own texture behind rather than a stale one.
- A texture handed to `TextureFeature.Set(...)` is backed up the same way — and is therefore also
  undone by the next apply pass, including the automatic one a scene load or a pack rescan triggers.
  Re-apply after one if it must survive.

## Config

`BepInEx/config/cwyls.modloader.bulbul.cfg`:

| Key | Default | Effect |
|---|---|---|
| `Textures.Enabled` | `true` | Off restores everything and applies nothing. |
| `Textures.LogEachApply` | `true` | Off keeps only the summary line. |
| `Textures.WriteMode` | `Shared` | `Shared` \| `Instance` \| `Both` — see the costume section above. |
| `Textures.AlsoSetSrpMirrors` | `true` | A `_MainTex` write also fills `_BaseMap` and `_BaseColorMap` where declared. |
| `Textures.ClearPropertyBlockTextures` | `true` | Re-set a `MaterialPropertyBlock` that already carries the property. |
| `Textures.Diagnose` | `true` | Dump a `TEXDIAG` block per target renderer on every pass. |
| `Textures.WatchFiles` | `true` | Watch each pack's `textures/` folder and reload by itself on save/add/delete. Off leaves only the `textures.reload` key; switching it back on mid-session takes effect at the next reload (press the key once). |
| `Textures.WatchPollSeconds` | `2` | Seconds between last-write checks of the pack texture files — the backstop for the watcher. `0` turns the poll off. |

`[Keys] textures.reload.Primary` = `Ctrl+Shift+R` and `[Keys] textures.reload.Alt` = `F8` are bound
in the same file by the shared hotkey registry (an old .cfg with the earlier F8-first pair is
migrated once).

### Reading a `TEXDIAG` block

```
TEXDIAG 'Body': 2 renderer(s) with this name
  - Character/Character/Body active=no enabled=yes visible=no   <- chosen
  - Character/Character/Costume_Polo_001(Clone)/.../Body1 active=yes enabled=yes visible=yes
  materials: 1 shared, 1 instance; rule targets index 0
  shared[0] = 'mt_Body' shader 'lilToon' id=-12345
  instance[0] = 'mt_Body (Instance)' shader 'lilToon' id=-67890
  shared   _MainTex: declared=yes value='t_body_base' 2048x2048
  shared   _BaseMap: declared=yes value='t_body_base' 2048x2048
  shared   _BaseColorMap: declared=yes value='t_body_base' 2048x2048
  instance _MainTex: declared=yes value='_MainTex' 1024x1024
  keywords(3): _EMISSION ...
  propertyBlock: no
  CHOSEN RENDERER IS NOT ACTIVE. Active renderers using 'mt_Body' ...: 
    - Character/Character/Costume_Polo_001(Clone)/.../Body1
```

Read it top down: *is the chosen renderer being drawn* (`active`/`enabled`/`visible`), *is it the
only one with that name*, *does the shader declare the property we wrote* (`declared=`), *does a
property block override it*, and — when the chosen renderer is dark — *which active renderers share
its material*, which is how `Body1` gets named even though no rule can say `Body1`.

## What the log looks like

```
TEXTURES runner armed (CWY.TextureRunner, +1 frame)
TEXTURES armed: Textures.Enabled=True, WriteMode=Both, AlsoSetSrpMirrors=True,
                ClearPropertyBlockTextures=True, trigger='RoomScene' + packs changed, re-pass at +2s/+10s
TEXDIAG 'Body': ...                                        (see below)
TEXTURE Body[0]._MainTex <- cwy.example/textures/Body/_MainTex.png (1024x1024)
        [INACTIVE,offscreen] -> instance:_MainTex+instance:_BaseMap+instance:_BaseColorMap
                               +shared:_MainTex+shared:_BaseMap+shared:_BaseColorMap
TEXTURES: 1 file(s): 1 ok, 0 failed; 1 texture, 0 audio; 14875 bytes; 6 ms
```

All of that is on the **debug/trace** channel; the pass gets one console line, and so does each
reload and each restore:

```
14:02:11.019  TEXTURE  1 rule applied  failed=0 trigger=RoomScene+2s mode=Shared files=1
14:02:11.019  TEXTURE  reloaded 1 changed  applied=1 failed=0 ms=58 trigger=key
14:02:11.019  TEXTURE  restored 3 of 3 slot(s)
```

The per-rule `TEXTURE` lines and the multi-line `TEXDIAG` blocks stay in `LogOutput.log` at Debug
and in the smoke report (`TextureFeature.DiagnosticFor`) — a four-physical-line block is the one
class of line a line-oriented viewer cannot attribute.

The bracket on the `TEXTURE` line is the thing to read first: `[active]` means the renderer written
is being drawn, `[INACTIVE]` means it is not and only the `shared:` writes can be doing any work.

Warnings are emitted once per rule, so a pack whose renderer never appears does not fill the log.

## Smoke checks

| Id | What it proves |
|---|---|
| `p1.textures.rules` | The deployed packs produced at least one rule. |
| `p1.textures.applied` | **Every** rule from the loaded packs — renderer or `@material` form — has its texture (matched by name **and** size) in an albedo slot of at least one **active, enabled** renderer. Fails naming the ones that are not visible. Passes vacuously, and says so, when a pack ships no rules. |
| `p1.textures.material_rule` | A `@material` rule reached a material an **active** renderer is using — the only proof a worn costume was repainted. Passes vacuously when a pack ships no `@material` rules. |
| `p1.textures.restore` | **Loud.** `RestoreAll()` drops the count of active renderers holding *any* pack texture to 0 — and re-applies afterwards, so the run ends with the modded outfit on screen. |
| `p2.textures.reload` | **Loud.** `ReloadAll()` runs without error and leaves the same number of rules applied — a reload is invisible unless a file actually changed. |

A **loud** check changes what the player sees or hears while it runs, so it is only executed on an
explicit smoke trigger (the smoke key or `BepInEx/smoke.run`), never on an automatic pass.

`p1.textures.applied` and `p1.textures.restore` are deliberately **rule-agnostic**: they ask the
scene, never a hard-coded name. The first version read `Body._MainTex` back off the renderer the
rule named — it passed while the player saw nothing (the renderer was deactivated), and then failed
outright once the pack switched to a `@material` rule that never mentions `Body`. Neither failure
was about the texture; both were about a check naming a renderer instead of looking at the scene.

## Public API

```csharp
void  TextureFeature.Initialize(ConfigFile config);           // once, from BulbulPlugin.Awake
void  TextureFeature.Apply();                                 // restore, then re-read packs and apply
int   TextureFeature.ReloadAll();                             // drop the cache, rescan packs, apply; -1 on failure
int   TextureFeature.RestoreAll();                            // undo everything; returns slots restored
bool  TextureFeature.Set(string renderer, string property, Texture2D tex, int materialIndex = 0);
IReadOnlyList<AppliedTexture> TextureFeature.AppliedRules;    // what is on the character right now
TextureRuleSet                TextureFeature.Rules;           // merged rules + rejected files
string                        TextureFeature.LastSummary;     // "TEXTURES: applied 1 rule(s), 0 failed"
string                        TextureFeature.LastReloadSummary; // "TEXTURES: reloaded 3 file(s) (…)"
```

`Apply`, `ReloadAll`, `RestoreAll` and `Set` touch the engine and must be called on the Unity main
thread. The
two automatic triggers go through `TextureHost.Request`, which is safe from any thread and defers a
frame — `sceneLoaded` fires before the scene's own `Start` methods run, and the game swaps costume
renderers in during that window.
