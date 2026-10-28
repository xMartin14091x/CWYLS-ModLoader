# Core/Ui - the engine-free half of mod windows

How mod windows work, with an example: `src/CwyLs.ModLoader.Bulbul/UI/README.md` → "Mod windows".
Everything here is Unity-free and unit-tested (`src/CwyLs.ModLoader.Tests/Ui*Tests.cs`).

| File | What |
|---|---|
| `CwyWindowSpec.cs` | what a mod asks for: `CwyWindowSpec`, part specs (`CwyTextSpec`, `CwyListSpec`, `CwyChatSpec`, `CwyInputSpec`, `CwyButtonsSpec`/`CwyButtonSpec`, `CwyTabsSpec`), `CwyPartKind` |
| `CwyWindowSpecRules.cs` | id rule, `CWY.Mod.<modId>.<windowId>` names, keys, validation, part heights, minimum size |
| `CwyWindowApi.cs` | what a mod gets: `ICwyWindow`, the part interfaces, `CwyWindowResult`, `CwyChatRole`, `CwyCloseReason`, `UiEventRunner` |
| `PartModels.cs`, `ChatViewModel.cs` | thread-safe part state behind the interfaces; the adapter's views draw it when `Version` moves |
| `WindowModel.cs` | a built window's copied settings, part models, tab rule, body layout |
| `StackLayout.cs` | parts top to bottom: fixed heights, shared rest, minimum height per tab |
| `ScrollState.cs` | follow-the-end scrolling (chat) and top-first scrolling (list) |
| `ChatLayout.cs` | bubble widths, padding, placement; `TypingDots` |
| `TextBoxKeys.cs` | Enter sends, Shift+Enter breaks the line, chords and input-method commits insert nothing |
| `WindowStack.cs` | window order and the window the player is in |
| `EscapeRouting.cs` | who an Escape belongs to; `WindowEscapeGuard` for the game's windowed switch |
| `WindowRectStore.cs` | remembered places (`BepInEx/data/ui/windows.json`); `WindowPlacement` |
| `ThaiMarkCheck.cs` | Thai glyph and tone-mark verdicts for the `s.ui.thai` smoke check |
| `CwyRichText.cs` | `Escape` for untrusted text in rich-text parts |
