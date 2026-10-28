# audio/voice — voice clips

Drop `<VoiceName>.ogg` or `<VoiceName>.wav` here, direct children only. The file name without its
extension **is** the voice name the game looks up, so `Voice_Tutorial_1_001.ogg` replaces that line
and any name the game does not have becomes a new line. A name must match `^[A-Za-z0-9_\-]+$` and is
matched case-sensitively, exactly like the game's own clip cache; anything else is skipped with a
reason in `LogOutput.log`.

Later packs override earlier ones, and a clip a mod registered at runtime (one it generated or loaded)
outranks both. Where two files in this folder would claim one name — `X.ogg` next to `X.wav` — only the last
one by path is loaded, so the winner never depends on which decoder finishes first.

Any other file here (this README included) is carried in the pack index as `Other` and never loaded.

## The two fixtures

Both are the same 0.6 s two-tone beep (880 Hz then 587.33 Hz, 5 ms fades). They exist so a launch
can prove the whole path in the engine: `p1.voice.registered`, `p1.voice.resolve` (the game's own
`GetAudioClip` hands back our clip object), `p1.voice.play` and `p1.voice.ogg`.

| File | What it proves | How it was made |
|---|---|---|
| `Voice_CWY_Test.wav` | The synchronous 16-bit PCM WAV path (`AudioLoader.LoadWav`). 44.1 kHz, mono, 16-bit. | Python's `wave` module — no external tool needed, so it can always be regenerated. |
| `Voice_CWY_Test_Ogg.ogg` | The Ogg Vorbis path through the engine's decoder (`AudioLoader.LoadAsync`), which is the format the game's own voice clips use. 44.1 kHz, **stereo**. | `ffmpeg -i Voice_CWY_Test.wav -c:a vorbis -strict -2 -ac 2 Voice_CWY_Test_Ogg.ogg` |

The Ogg is stereo because ffmpeg's built-in Vorbis encoder (used when ffmpeg is built without
`libvorbis`) refuses mono. That is not a rule for packs — a mono Ogg is perfectly fine — only how
this fixture happened to be encoded. If `Voice_CWY_Test_Ogg.ogg` is ever missing, `p1.voice.ogg` passes
with the detail `no ogg fixture (ffmpeg absent)` rather than failing.
