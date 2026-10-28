using System;
using System.Collections.Generic;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// Language voice sets: a read-only postfix on <c>ScenarioReader.StartReady</c> that hands the scenario's
    /// voice names to <see cref="LanguageVoices.Prefetch"/>, so a scene's language clips load before
    /// its first line asks for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StartReady</c> fills <c>_currentGroupLocalizations</c> with the group's rows before it
    /// returns, and every voiced command reads its clip name from there. The first line then waits
    /// for a fade-in, a motion or a typewriter; the game-start greeting, for instance, is readied
    /// before the room's fade-in finishes (<c>HeroineStateGameStartDirection</c>) and read after it.
    /// That is enough time to load a few compressed clips.
    /// </para>
    /// <para>
    /// The postfix reads; it changes nothing the reader uses - not the rows, not their order, not
    /// the direction or music commands - and a failure is logged and swallowed so a scene always
    /// starts.
    /// </para>
    /// </remarks>
    internal static class LanguageVoicePatches
    {
        /// <summary>The reader's field holding the group's localization rows.</summary>
        internal const string GroupLocalizationsFieldName = "_currentGroupLocalizations";

        /// <summary>How many prefetch failures are logged before the rest are counted.</summary>
        private const int MaxLoggedErrors = 3;

        private static AccessTools.FieldRef<Game.ScenarioReader, List<Master.LocalizationData>>? _groupLocalizations;
        private static int _errors;

        /// <summary>True once the postfix is in place.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>What was patched, or why not. Never null.</summary>
        internal static string Detail { get; private set; } = "not attempted";

        /// <summary>Applies the postfix. Idempotent.</summary>
        /// <param name="harmony">The voice feature's Harmony instance.</param>
        /// <returns>True when the postfix is in place.</returns>
        internal static bool Apply(Harmony harmony)
        {
            if (Applied)
            {
                return true;
            }

            try
            {
                var startReady = AccessTools.Method(typeof(Game.ScenarioReader), "StartReady");
                if (startReady is null)
                {
                    Detail = "PATCH TARGET MISSING: Bulbul.ScenarioReader.StartReady";
                    CwyLsModLoader.Log.LogError("VOICE " + Detail);
                    return false;
                }

                _groupLocalizations = AccessTools.FieldRefAccess<Game.ScenarioReader, List<Master.LocalizationData>>(GroupLocalizationsFieldName);
                harmony.Patch(startReady, postfix: new HarmonyMethod(typeof(LanguageVoicePatches), nameof(StartReadyPostfix)));
                Applied = true;
                Detail = "postfixed ScenarioReader.StartReady (prefetch)";
                return true;
            }
            catch (Exception ex)
            {
                Detail = "could not patch ScenarioReader.StartReady: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError("VOICE " + Detail);
                return false;
            }
        }

        /// <summary>Harmony postfix: the scenario's voice names, in row order, go to the front of the load queue.</summary>
        /// <param name="__instance">The scenario reader.</param>
        /// <param name="groupId">The group being readied (the original's parameter name).</param>
        private static void StartReadyPostfix(Game.ScenarioReader __instance, string groupId)
        {
            if (!LanguageVoices.Enabled || _groupLocalizations is null || __instance is null)
            {
                return;
            }

            try
            {
                var rows = _groupLocalizations(__instance);
                if (rows is null || rows.Count == 0)
                {
                    return;
                }

                var names = new List<string>(rows.Count);
                foreach (var row in rows)
                {
                    var voice = row?.Voice;
                    if (!string.IsNullOrEmpty(voice) && !names.Contains(voice!))
                    {
                        names.Add(voice!);
                    }
                }

                LanguageVoices.Prefetch(names, groupId);
            }
            catch (Exception ex)
            {
                // Never let the prefetch stop a scene: the lines simply load on demand instead.
                _errors++;
                var message = "VOICE language prefetch failed for '" + groupId + "' (" + _errors + "): "
                    + ex.GetType().Name + ": " + ex.Message;
                if (_errors <= MaxLoggedErrors)
                {
                    CwyLsModLoader.Log.LogWarning(
                        message + (_errors == MaxLoggedErrors ? " - further failures go to the debug log" : string.Empty));
                }
                else
                {
                    CwyLsModLoader.Log.LogDebug(message);
                }
            }
        }
    }
}
