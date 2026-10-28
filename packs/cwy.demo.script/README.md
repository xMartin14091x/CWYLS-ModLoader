# cwy.demo.script — see CWY script commands run in a scene

A content pack with two tiny scenes that show the CWY script commands doing something you can see:

| Scene | Type | What it shows |
|---|---|---|
| `cwy_demo_script_01` | `ExtraScenario` ep 1001, a long story through the game's own pipeline (fade, story text box, Skip) | `cwy.flag set demo.script.seen` · `cwy.wait 3` (a 3-second silent pause) · a two-choice question · `cwy.defer scene.play group=cwy_demo_script_02` on "Yes, come back" |
| `cwy_demo_script_02` | `MotionScenario` ep 1001, a one-line click-style talk | started by the deferred `scene.play` once scene 01 has ended and the room is idle again |

Text in English and Japanese; the other game languages show the English line. A language pack can
add its own translation of these lines in a `data/subtitles/<code>.json` of its own. No voice: the
lines are unvoiced, so Satone's mouth moves only while the text types.

Why the scene can never pop up on its own: episode 1001 is in the mod range, and `"ContentType":
"SmallTalk_Cicada"` keeps the game (its `ScenarioGroupMasterWrapper`) from offering this Extra
scene through her "wants to talk" state. It is never written into the game's save either (the
script feature's save-log guard), so it plays only when asked.

## Try it

Game closed: deploy with `./deploy-packs.sh`. In the room, open the console (**Ctrl+Shift+C**):

```
scene list                      # cwy_demo_script_01 ExtraScenario ep 1001 (long) ok
scene play cwy_demo_script_01
```

1. The screen fades; Satone: "Hi! This little scene runs on CWY script commands."
2. "Watch: I'll stay quiet for three seconds." — then the scene holds for 3 seconds with that line on screen (`cwy.wait 3`).
3. "That pause was cwy.wait. Want me to come back with one more line?" and two choices.
4. **Yes, come back**: "Okay! When this scene closes, I'll pop back in." The scene fades out; about a
   second after the room is back, she says "I'm back! cwy.defer brought me here once the room was quiet."
   (needs the action runner). **No, that's all**: "Alright. See you around!"
5. `scene flags` lists `demo.script.seen`.

Console lines to expect (tag `SCRIPT`): `playing cwy_demo_script_01 (the game's long-story pipeline)`,
`flag demo.script.seen set`, `cwy.defer scene.play queued; …`, `cwy_demo_script_01 not logged as played
(pack scene; …)`, `cwy.defer scene.play -> Ok via Semantic: playing cwy_demo_script_02 …`,
`playing cwy_demo_script_02 (a click-style talk)`.

The loud smoke check `es.script.demo` (`smoke run loud`) starts scene 01 the same way.
