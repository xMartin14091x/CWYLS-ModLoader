using System;
using BepInEx;
using CwyLs.ModLoader.Bulbul.UI;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using UnityEngine;
using Framework = CwyLs.ModLoader.Console;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// Escape in fullscreen closes the CWY console only (by design), and closes or leaves a mod
    /// window only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game reads Escape directly every frame in <c>RoomGameManager.Update</c>
    /// and calls <c>SettingService.ChangeWindowMode(Window)</c>, which
    /// saves the setting and drops a fullscreen game to windowed. <c>Input.GetKeyDown</c> is a device
    /// poll, so nothing the console does with the key event can hide it from the game.
    /// </para>
    /// <para>
    /// The least invasive fix is a prefix on <c>ChangeWindowMode</c> that skips the call only when
    /// all of these hold: the game asked for <c>Window</c>, Escape went down this frame, and the
    /// console used that Escape (<see cref="Framework.ConsoleFeature.EscapeGuard"/>). Every other call
    /// - the settings window's own Window/Fullscreen buttons, Escape while the console is closed or
    /// while the player types in one of the game's fields - runs untouched. Each skip writes one
    /// trace-file line (<c>console/escapeKept</c>).
    /// </para>
    /// <para>
    /// Mod windows extend the rule to every CWY surface: the switch is also skipped when a mod window
    /// owned that Escape (<see cref="ModWindows.EscapeGuard"/>, recorded each frame by the mod-window
    /// manager: a window's text box had the keyboard, or the player was in a window that closes on
    /// Escape). The record says <c>by=console</c> or <c>by=window</c>.
    /// </para>
    /// </remarks>
    internal static class ConsoleEscapePatch
    {
        /// <summary>Harmony id of this patch.</summary>
        internal const string HarmonyId = BulbulPlugin.PluginGuid + ".console";

        private static readonly object Gate = new object();
        private static Harmony? _harmony;

        /// <summary>True when the prefix is in place.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>
        /// Registers the target and applies the prefix. Idempotent; a missing target is logged and
        /// leaves the game's Escape behaviour as it was.
        /// </summary>
        internal static void Apply()
        {
            lock (Gate)
            {
                if (_harmony != null)
                {
                    return;
                }

                _harmony = new Harmony(HarmonyId);
            }

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (console)", ConsolePatchTargets.All);
            PatchTargetVerifier.VerifyAll(ConsolePatchTargets.All, new ReflectionMemberResolver());

            try
            {
                var target = AccessTools.Method(
                    typeof(Game.SettingService),
                    ConsolePatchTargets.ChangeWindowModeMethod,
                    new[] { typeof(Game.WindowModeType) });
                if (target is null)
                {
                    CwyLsModLoader.Log.LogError(
                        "CONSOLE SettingService.ChangeWindowMode(WindowModeType) not found; Escape in fullscreen "
                        + "will also switch the game to windowed while the console is open");
                    return;
                }

                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(ConsoleEscapePatch), nameof(ChangeWindowModePrefix)));
                Applied = true;
                ConsoleEvents.File("console", "escapeGuard", ("patched", "SettingService.ChangeWindowMode"));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"CONSOLE could not patch SettingService.ChangeWindowMode: {ex.GetType().Name}: {ex.Message}. "
                    + "Escape in fullscreen will also switch the game to windowed while the console is open");
            }
        }

        /// <summary>
        /// Harmony prefix: false (skip the game's method) only when the console or a mod window used
        /// this frame's Escape and the game asked for windowed mode. Never throws; on any error the
        /// game's own behaviour runs.
        /// </summary>
        /// <param name="__0">The window mode the game asked for.</param>
        /// <returns>False to skip the game's switch; true to let it run.</returns>
        internal static bool ChangeWindowModePrefix(Game.WindowModeType __0)
        {
            try
            {
                var frame = Time.frameCount;
                var toWindowed = __0 == Game.WindowModeType.Window;
                var escapeDown = UnityInput.Current.GetKeyDown(KeyCode.Escape);
                var guard = Framework.ConsoleFeature.EscapeGuard;
                var byConsole = guard.ShouldBlockWindowedSwitch(frame, toWindowed, escapeDown, Framework.ConsoleFeature.IsOpen);
                var byWindow = !byConsole && ModWindows.EscapeGuard.ShouldBlockWindowedSwitch(frame, toWindowed, escapeDown);
                if (!byConsole && !byWindow)
                {
                    return true;
                }

                ConsoleEvents.File(
                    "console",
                    "escapeKept",
                    ("frame", frame),
                    ("by", byConsole ? "console" : "window"),
                    ("claimed", guard.ClaimedFrame == frame),
                    ("consoleOpen", Framework.ConsoleFeature.IsOpen),
                    ("fullscreen", Screen.fullScreen),
                    ("blocked", guard.BlockedCount + ModWindows.EscapeGuard.BlockedCount));
                return false;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"CONSOLE Escape guard failed, the game's window switch runs: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }
    }
}
