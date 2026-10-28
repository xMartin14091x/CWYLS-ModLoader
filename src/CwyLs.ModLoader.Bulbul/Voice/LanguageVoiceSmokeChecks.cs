using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Smoke;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// Language voice set checks, both quiet (nothing plays, nothing on screen changes, the game's language is
    /// never switched):
    /// <list type="bullet">
    /// <item><c>th.voice.sets</c> - the language sets the packs ship, how many clips loaded or failed.</item>
    /// <item><c>th.voice.resolve</c> - through the game's own (patched) <c>GetAudioClip</c>, a few
    /// names resolve to the set's clip under the set's language and to the game's own clip under a
    /// language with no set. Both branches run in one call by pretending the language
    /// (<see cref="LanguageVoices.Probe{T}"/>), so the check needs no language switch.</item>
    /// </list>
    /// </summary>
    internal static class LanguageVoiceSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "TH";

        /// <summary>How many names the resolve check proves.</summary>
        internal const int ProbeCount = 5;

        private const string SetsId = "th.voice.sets";
        private const string ResolveId = "th.voice.resolve";

        /// <summary>How many loaded clips are scanned for names the game also has.</summary>
        private const int ScanLimit = 200;

        /// <summary>Registers both checks. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(SetsId, Phase, Sets));
            SmokeRegistry.Register(new SmokeCheck(ResolveId, Phase, Resolve));
        }

        /// <summary>The sets, their load state and the session's hit/fallback counts.</summary>
        private static SmokeCheckResult Sets()
        {
            if (!VoiceFeature.Enabled)
            {
                return SmokeCheckResult.Fail(SetsId, VoiceFeature.ConfigSection + "." + VoiceFeature.ConfigKeyEnabled + "=false");
            }

            if (!LanguageVoices.Enabled)
            {
                return VoiceFeature.LanguageVoicesEnabled
                    ? SmokeCheckResult.Fail(SetsId, "language voices are off: " + LanguageVoices.DisabledReason)
                    : SmokeCheckResult.Pass(SetsId, "switched off (" + LanguageVoices.DisabledReason + ")");
            }

            if (LanguageVoices.Codes.Count == 0)
            {
                return SmokeCheckResult.Pass(SetsId, LanguageVoices.PlanSummary + " (nothing to do)");
            }

            var parts = new List<string>();
            var failed = 0;
            foreach (var code in LanguageVoices.Codes)
            {
                var counts = LanguageVoices.CountsFor(code);
                failed += counts.Failed;
                parts.Add(counts.ToString());
            }

            var detail = string.Join("; ", parts)
                + "; in force: " + (LanguageVoices.ActiveCode.Length == 0 ? "none" : LanguageVoices.ActiveCode)
                + string.Format(
                    CultureInfo.InvariantCulture,
                    "; hits {0}, fallbacks {1}, missing {2}",
                    LanguageVoices.Hits,
                    LanguageVoices.Fallbacks,
                    LanguageVoices.Missing)
                + (LanguageVoices.LastFallback.Length == 0 ? string.Empty : "; last fallback " + LanguageVoices.LastFallback)
                + (LanguageVoices.LastSummary.Length == 0 ? string.Empty : "; " + LanguageVoices.LastSummary);

            return failed == 0 ? SmokeCheckResult.Pass(SetsId, detail) : SmokeCheckResult.Fail(SetsId, detail);
        }

        /// <summary>Both branches of the selection, through the game's own lookup.</summary>
        private static SmokeCheckResult Resolve()
        {
            if (!VoiceFeature.PatchApplied)
            {
                return SmokeCheckResult.Fail(ResolveId, "the lookup is not patched: " + VoiceFeature.PatchDetail);
            }

            if (!LanguageVoices.Enabled)
            {
                return VoiceFeature.LanguageVoicesEnabled
                    ? SmokeCheckResult.Fail(ResolveId, "language voices are off: " + LanguageVoices.DisabledReason)
                    : SmokeCheckResult.Pass(ResolveId, "switched off (" + LanguageVoices.DisabledReason + ")");
            }

            if (LanguageVoices.Codes.Count == 0)
            {
                return SmokeCheckResult.Pass(ResolveId, "no language set to resolve (" + LanguageVoices.PlanSummary + ")");
            }

            var instance = VoiceSeam.TryGetInstance(out var error);
            if (instance is null)
            {
                return SmokeCheckResult.Fail(ResolveId, error);
            }

            var code = PickLanguage(out var sample);
            if (code.Length == 0)
            {
                return LanguageVoices.ActiveCode.Length > 0
                    ? SmokeCheckResult.Fail(
                        ResolveId,
                        LanguageVoices.ActiveCode + " is selected but none of its clips has loaded yet ("
                        + LanguageVoices.CountsFor(LanguageVoices.ActiveCode) + "); run again in a few seconds")
                    : SmokeCheckResult.Pass(
                        ResolveId,
                        "not proven: no set has a loaded clip (a set loads while its language is selected; select it and run again)");
            }

            var other = OtherLanguage(code);
            var proven = new List<string>();

            foreach (var pair in sample)
            {
                var name = pair.Key;
                var setClip = pair.Value as AudioClip;
                var gameClip = VoiceSeam.TryGetGameClip(instance, name) as AudioClip;
                if (setClip == null || gameClip == null)
                {
                    continue;
                }

                // What each branch must return: a mod's runtime clip outranks everything; otherwise
                // the set's clip under the set's language, and a global pack clip or the game's own
                // clip under a language with no set.
                var registered = VoiceFeature.TryGet(name, out var registeredClip) ? VoiceFeature.EntryOf(name) : null;
                var expectedInSet = registered != null && registered.IsRuntime ? registeredClip : setClip;
                var expectedElsewhere = registered != null ? registeredClip : gameClip;

                var underSet = LanguageVoices.Probe(code, () => VoiceSeam.TryInvokeGetAudioClip(instance, name, out _)) as AudioClip;
                var underOther = LanguageVoices.Probe(other, () => VoiceSeam.TryInvokeGetAudioClip(instance, name, out _)) as AudioClip;

                if (!ReferenceEquals(underSet, expectedInSet))
                {
                    return SmokeCheckResult.Fail(
                        ResolveId,
                        "under " + code + ", GetAudioClip('" + name + "') returned " + Describe(underSet, setClip, gameClip)
                        + ", expected " + Describe(expectedInSet, setClip, gameClip));
                }

                if (!ReferenceEquals(underOther, expectedElsewhere))
                {
                    return SmokeCheckResult.Fail(
                        ResolveId,
                        "under " + Label(other) + ", GetAudioClip('" + name + "') returned " + Describe(underOther, setClip, gameClip)
                        + ", expected " + Describe(expectedElsewhere, setClip, gameClip));
                }

                proven.Add(name + " (" + setClip.length.ToString("0.0", CultureInfo.InvariantCulture) + " s vs "
                    + gameClip.length.ToString("0.0", CultureInfo.InvariantCulture) + " s)");
                if (proven.Count >= ProbeCount)
                {
                    break;
                }
            }

            if (proven.Count == 0)
            {
                return SmokeCheckResult.Fail(
                    ResolveId,
                    "none of the first " + sample.Count + " loaded " + code + " clips has a game clip of the same name to compare with");
            }

            return SmokeCheckResult.Pass(
                ResolveId,
                proven.Count + " name(s): under " + code + " the set's clip, under " + Label(other)
                + " the game's (by reference): " + string.Join(", ", proven));
        }

        /// <summary>The set to prove: the one in force when it has loaded clips, else any set that has.</summary>
        private static string PickLanguage(out IReadOnlyList<KeyValuePair<string, object>> sample)
        {
            var active = LanguageVoices.ActiveCode;
            if (active.Length > 0)
            {
                sample = LanguageVoices.LoadedSample(active, ScanLimit);
                return sample.Count > 0 ? active : string.Empty;
            }

            foreach (var code in LanguageVoices.Codes)
            {
                sample = LanguageVoices.LoadedSample(code, ScanLimit);
                if (sample.Count > 0)
                {
                    return code;
                }
            }

            sample = Array.Empty<KeyValuePair<string, object>>();
            return string.Empty;
        }

        /// <summary>A language with no set: English, else Japanese, else "none".</summary>
        private static string OtherLanguage(string code)
        {
            foreach (var candidate in new[] { "en", "ja" })
            {
                if (!string.Equals(candidate, code, StringComparison.Ordinal) && !LanguageVoices.HasSet(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static string Label(string code) => code.Length == 0 ? "no language" : code;

        private static string Describe(AudioClip? clip, AudioClip setClip, AudioClip gameClip)
        {
            if (clip == null)
            {
                return "nothing";
            }

            if (ReferenceEquals(clip, setClip))
            {
                return "the set's clip";
            }

            return ReferenceEquals(clip, gameClip) ? "the game's clip" : "another clip ('" + clip!.name + "')";
        }
    }
}
