using System;
using System.Reflection;
using CwyLs.ModLoader.Core.PackLines;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// Asks the voice feature whether a line's clips would make a sound right now
    /// (<c>LanguageVoices.CanPlay(string)</c> in <c>src/CwyLs.ModLoader.Bulbul/Voice/LanguageVoices.cs</c>).
    /// A pack line whose own clip lives only in a language set (no game clip to fall back on) and has
    /// not loaded yet would play silent, so the gate leaves it out of that pick; it stays owed and
    /// joins a later pick once its clip is in.
    /// </summary>
    /// <remarks>
    /// Bound late, by name, inside this same assembly: <c>LanguageVoices</c> (language voice sets)
    /// was written alongside this feature, so binding by name keeps this file buildable on either
    /// side of that integration. When the method is not there, every line counts as playable and one debug line
    /// says so. After integration the call can become a direct <c>LanguageVoices.CanPlay(name)</c>.
    /// </remarks>
    internal static class PackLinesVoiceCheck
    {
        /// <summary>The voice feature's type.</summary>
        internal const string TypeName = "CwyLs.ModLoader.Bulbul.LanguageVoices";

        /// <summary>Its readiness question.</summary>
        internal const string MethodName = "CanPlay";

        private static Func<string, bool>? _canPlay;
        private static bool _reported;

        /// <summary>What was bound, for <c>lines list</c> and the smoke detail.</summary>
        internal static string Detail { get; private set; } = "not resolved";

        /// <summary>True when the readiness question is bound.</summary>
        internal static bool IsBound => _canPlay != null;

        /// <summary>Binds <c>LanguageVoices.CanPlay(string)</c> when it exists. Idempotent.</summary>
        internal static void Resolve()
        {
            if (_canPlay != null)
            {
                return;
            }

            try
            {
                var type = typeof(PackLinesVoiceCheck).Assembly.GetType(TypeName, throwOnError: false);
                var method = type?.GetMethod(
                    MethodName, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (method is null || method.ReturnType != typeof(bool))
                {
                    Detail = TypeName + "." + MethodName + "(string) not present; every line counts as playable";
                    CwyLsModLoader.Log.LogDebug(PackLineRejection.LogPrefix + " " + Detail);
                    return;
                }

                _canPlay = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), method);
                Detail = "lines wait for their clips (" + TypeName + "." + MethodName + ")";
            }
            catch (Exception ex)
            {
                Detail = "could not bind " + TypeName + "." + MethodName + ": " + ex.GetType().Name + ": " + ex.Message
                         + "; every line counts as playable";
                CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " " + Detail);
            }
        }

        /// <summary>The first of the line's clips that would not make a sound now, or null when all would.</summary>
        /// <param name="line">The line.</param>
        internal static string? FirstSilent(PackLine line)
        {
            var canPlay = _canPlay;
            if (canPlay is null)
            {
                return null;
            }

            foreach (var voice in line.Voices)
            {
                bool ready;
                try
                {
                    ready = canPlay(voice);
                }
                catch (Exception ex)
                {
                    // Cannot tell: treat it as silent (skip this pick) rather than risk a mute line.
                    // The first failure is a WARN; repeats go to the debug log only.
                    var text = TypeName + "." + MethodName + " failed for " + voice
                               + "; such lines are skipped until it answers: " + ex.GetType().Name + ": " + ex.Message;
                    if (!_reported)
                    {
                        _reported = true;
                        CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " " + text);
                    }
                    else
                    {
                        CwyLsModLoader.Log.LogDebug(PackLineRejection.LogPrefix + " " + text);
                    }

                    ready = false;
                }

                if (!ready)
                {
                    return voice;
                }
            }

            return null;
        }
    }
}
