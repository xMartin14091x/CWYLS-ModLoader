CWY:LS Mod Loader __VERSION__
A mod loader for Chill With You: Lo-Fi Story (full Steam version).

Unofficial fan project. Not made, endorsed or supported by Nestopi; please do not contact
Nestopi about it. This is a DEVELOPER PREVIEW: the mod API can still change before 1.0.


INSTALL
-------
macOS  (Apple Silicon or Intel)
  1. Quit the game.
  2. Double-click "install.command".
     If macOS says it cannot be opened: right-click it, choose Open, then Open again.
  3. Choose "Install or update" (arrow keys and Enter, or press 1).
  4. Do the one manual step it shows: in Steam, right-click the game > Properties... >
     Launch Options, and paste the line it copied for you.
  5. Start the game from Steam.
  Tip: a bigger Terminal window (or Command +) shows the full animated screens.

Windows
  1. Quit the game.
  2. Double-click "install.bat". If Windows SmartScreen appears: More info > Run anyway.
  3. Choose [1] Install or update.
  4. Start the game from Steam.

The installer puts BepInEx 5.4.23.6 (unmodified) and the mod loader into the game folder.
It never touches your save files.


MODS
----
Put mods in the "CWYLS Mods" folder inside the game folder (the installer can open it for
you), one folder per mod. The game reads them when it starts.
Only install mods you trust: a code mod can do anything a program on your computer can do.


DOES NOT RUN ON THE DEMO
------------------------
The loader only runs on the full Steam release. On the free demo it switches itself off and
the game runs normally.


BUG REPORTS
-----------
1. Run the installer again, choose [2] Diagnostic trace, then [1] on.
   (Or, in the game, open the console with Control+Shift+C and type: trace on)
2. Restart the game and make the problem happen again.
3. Send the newest file from BepInEx/trace, plus BepInEx/LogOutput.log.
4. Turn the trace off again ([2] then [3], or "trace off" in the console).
Trace files can contain what you did in the game. Text you type into notes is not recorded
unless you turn that on yourself.


UNINSTALL
---------
Run the installer and choose [4] (mod loader only) or [5] (mod loader and BepInEx).
Nothing is deleted: files are moved into a dated folder inside the game folder, which you
can delete yourself. On macOS, also clear the game's Launch Options in Steam.


SAVES
-----
The loader keeps mod content out of the game's save files, so your saves stay loadable
without mods. Each time the game starts with the loader, your save folder is also copied
to "CWYLS Backups" next to it (the last 5, then one per day, week and month). It sits outside
the game folder, so uninstalling the game keeps your backups.
To restore one, quit the game first; type "saves" in the console to see how.


LICENCE
-------
PolyForm Noncommercial 1.0.0 with the Additional Terms in LICENSE.md; see also NOTICE.md.
Never sold, in any form; videos and streams are fine, paid ones included; mods made for or
with the loader must be free; the demo lock stays. BepInEx and the libraries it bundles
keep their own licences: see THIRD-PARTY-NOTICES.md.
