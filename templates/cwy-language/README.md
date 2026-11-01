<sub>[Author's guide](../../docs/guide/README.md) &nbsp;·&nbsp; [Translate the game](../../docs/guide/translate.md) &nbsp;·&nbsp; [`language.json` reference](../../docs/guide/manifest.md#languagejson-a-separate-file-not-a-manifest-member)</sub>

# CWY:LS Mod Loader language-pack template

A copyable starting point for a translation: a language pack is a content pack with a `language.json`.
It adds your language to the game's own language list, keeps every other language, and changes no
game file. It is set up for Thai; for another language, change `language.json` as the guide says.

| File | What it is |
|---|---|
| `manifest.json` | The pack's id, name, version and author |
| `language.json` | The language: its code, the name the language list shows, its script, its fallback and the game fonts that draw it |
| `data/subtitles/th.json` | Your text: one row per line, `"line id": "your translation"`. The one row here translates the loader's own example pack, so you can see the format working |

Steps, rules for sharing, and what the loader does for Thai: [docs/guide/translate.md](../../docs/guide/translate.md).
