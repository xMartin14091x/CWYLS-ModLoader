using System;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Voice;
using HarmonyLib;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// The Harmony patches on <c>AudioManager&lt;VoiceManager&gt;.GetAudioClip(string)</c>: a prefix
    /// that answers with a registered or language-set clip and skips the original, and a postfix
    /// that records the clip that will play (for the subtitle hold).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A prefix, rather than writing into the game's <c>_audioClipDict</c>, for three reasons: it
    /// covers a brand-new voice name as well as an override, it never mutates the game's own cache
    /// (which <c>LoadAudioClipAsync</c> replaces wholesale at startup, discarding anything written
    /// before it), and it can be switched off at runtime.
    /// </para>
    /// <para>
    /// Everything here is static, including the Harmony instance that owns it, because the game
    /// destroys BepInEx plugin components about 0.2 s after boot.
    /// </para>
    /// </remarks>
    internal static class VoicePatches
    {
        /// <summary>How many prefix failures are logged before the rest are counted silently.</summary>
        private const int MaxLoggedErrors = 3;

        private static int _errors;
        private static int _postfixErrors;
        private static VoiceChoice _lastChoice = VoiceChoice.Game;

        /// <summary>True once the prefix is in the game's lookup path.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>What was patched, or why it could not be. Never null.</summary>
        internal static string Detail { get; private set; } = "not attempted";

        /// <summary>How many lookups this prefix has answered with a registered clip.</summary>
        internal static long Hits { get; private set; }

        /// <summary>The last voice name the prefix served, for the smoke detail. Never null.</summary>
        internal static string LastHit { get; private set; } = string.Empty;

        /// <summary>
        /// Applies the prefix to the closed generic method. Idempotent: a second call reports the
        /// first result rather than patching twice.
        /// </summary>
        /// <param name="harmony">The (static) Harmony instance that will own the patch.</param>
        /// <returns>True when the prefix is applied.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="harmony"/> is null.</exception>
        internal static bool Apply(Harmony harmony)
        {
            if (harmony is null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            if (Applied)
            {
                return true;
            }

            if (!VoiceSeam.TryResolve(out var error))
            {
                Detail = "PATCH TARGET MISSING: " + VoiceSeam.Describe() + " - " + error;
                CwyLsModLoader.Log.LogError("VOICE " + Detail);
                return false;
            }

            try
            {
                // The postfix sees the final answer - ours or the game's - so it records the clip
                // that really plays (the subtitle hold follows it).
                harmony.Patch(
                    VoiceSeam.GetAudioClipMethod,
                    prefix: new HarmonyMethod(typeof(VoicePatches), nameof(GetAudioClipPrefix)),
                    postfix: new HarmonyMethod(typeof(VoicePatches), nameof(GetAudioClipPostfix)));

                Applied = true;
                Detail = "prefixed and postfixed " + VoiceSeam.Describe();
                return true;
            }
            catch (Exception ex)
            {
                Detail = "could not patch " + VoiceSeam.Describe() + ": " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError("VOICE " + Detail);
                return false;
            }
        }

        /// <summary>
        /// Harmony prefix. Answers with a registered clip when the name is ours, otherwise lets the
        /// game's own lookup run.
        /// </summary>
        /// <param name="__instance">
        /// The audio manager the call is on, typed as <see cref="object"/> because this assembly
        /// does not reference AudioManager.dll. Mono shares the compiled body of a generic method
        /// between reference-type instantiations, so a patch on
        /// <c>AudioManager&lt;VoiceManager&gt;</c> can be reached by <c>BGMManager</c> and
        /// <c>SEManager</c> too; the type check is what keeps a registered voice name from
        /// answering a music lookup.
        /// </param>
        /// <param name="audioPath">The path or name the game asked for.</param>
        /// <param name="__result">The clip to return instead.</param>
        /// <returns>False to skip the original method, true to let it run.</returns>
        private static bool GetAudioClipPrefix(object __instance, string audioPath, ref AudioClip __result)
        {
            try
            {
                if (!VoiceFeature.Enabled || __instance is null)
                {
                    return true;
                }

                var voiceManagerType = VoiceSeam.VoiceManagerType;
                if (voiceManagerType is null || !voiceManagerType.IsInstanceOfType(__instance))
                {
                    return true;
                }

                // Read by the postfix of this same call; overwritten below when we answer.
                _lastChoice = VoiceChoice.Game;

                var name = VoiceNameRules.KeyFromAudioPath(audioPath);
                if (name is null)
                {
                    return true;
                }

                // Precedence (LanguageVoiceRules): a mod's runtime clip, then the set of the
                // language in force, then a global pack clip, then the game. The language set is only
                // asked when no runtime clip outranks it, so its fallback trace never lies.
                var registered = VoiceFeature.TryGet(name, out var registeredClip) ? VoiceFeature.EntryOf(name) : null;
                AudioClip? languageClip = null;
                var language = LanguageVoiceRules.AsksLanguageSet(registered)
                    ? LanguageVoices.ResolveForPlay(name, out languageClip)
                    : default;

                var choice = LanguageVoiceRules.Choose(registered, language);
                AudioClip? clip;
                string source;
                switch (choice)
                {
                    case VoiceChoice.Language:
                        clip = languageClip;
                        source = (language.Step?.PackId ?? "language") + "/" + language.Code;
                        break;
                    case VoiceChoice.Runtime:
                    case VoiceChoice.Pack:
                        clip = registeredClip;
                        source = registered?.Source ?? string.Empty;
                        break;
                    default:
                        return true;
                }

                if (clip == null)
                {
                    return true;
                }

                __result = clip!;
                _lastChoice = choice;
                Hits++;
                LastHit = name;

                if (!VoicePlayback.IsSuppressed)
                {
                    TraceBus.EmitFields(
                        VoiceFeature.TraceSys,
                        "hit",
                        ("name", name),
                        ("source", source),
                        ("choice", choice.ToString()),
                        ("lang", language.Code),
                        ("path", audioPath));
                }

                return false;
            }
            catch (Exception ex)
            {
                // A throwing prefix would break every sound in the game, so this one always falls
                // back to the original lookup. The first few failures are logged in full; after
                // that only the count is kept, because this runs on every clip the game asks for.
                _errors++;
                if (_errors <= MaxLoggedErrors)
                {
                    CwyLsModLoader.Log.LogError(
                        "VOICE lookup prefix failed for '" + audioPath + "' (" + _errors + "): "
                        + ex.GetType().Name + ": " + ex.Message
                        + (_errors == MaxLoggedErrors ? " - further failures will not be logged" : string.Empty));
                }

                return true;
            }
        }

        /// <summary>
        /// Harmony postfix: records the clip the voice manager is about to play - whoever
        /// answered - for the subtitle hold (<see cref="VoicePlayback"/>). Never changes the result.
        /// </summary>
        /// <param name="__instance">The audio manager the call is on (see the prefix about BGM/SE).</param>
        /// <param name="audioPath">The path or name the game asked for.</param>
        /// <param name="__result">The clip the lookup returns.</param>
        private static void GetAudioClipPostfix(object __instance, string audioPath, AudioClip __result)
        {
            try
            {
                if (!VoiceFeature.Enabled || __instance is null || __result == null || VoicePlayback.IsSuppressed)
                {
                    return;
                }

                var voiceManagerType = VoiceSeam.VoiceManagerType;
                if (voiceManagerType is null || !voiceManagerType.IsInstanceOfType(__instance))
                {
                    return;
                }

                VoicePlayback.Record(
                    VoiceNameRules.KeyFromAudioPath(audioPath) ?? audioPath ?? string.Empty,
                    __result.length,
                    _lastChoice.ToString());
            }
            catch (Exception ex)
            {
                // Recording is a nicety for the subtitle hold; the lookup's answer is already final.
                _postfixErrors++;
                var message = "VOICE playback record failed for '" + audioPath + "' (" + _postfixErrors + "): "
                    + ex.GetType().Name + ": " + ex.Message;
                if (_postfixErrors <= MaxLoggedErrors)
                {
                    CwyLsModLoader.Log.LogWarning(message);
                }
                else
                {
                    CwyLsModLoader.Log.LogDebug(message);
                }
            }
        }
    }
}
