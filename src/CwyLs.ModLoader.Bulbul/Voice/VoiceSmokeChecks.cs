using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Core.Voice;
using CwyLs.ModLoader.Packs;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// The Phase-1 voice checks: proof, in the running game, that a pack clip is registered, that
    /// the prefix is really in the game's own lookup path, and that the game will play it.
    /// </summary>
    /// <remarks>
    /// The clips come from <c>packs/cwy.example</c>, deployed by <c>./deploy-packs.sh</c>. The
    /// Ogg check is conditional on purpose: the example Ogg is produced with ffmpeg, which not
    /// every machine has, and a missing fixture must read as "not proven here", not as a failure.
    /// </remarks>
    internal static class VoiceSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "P1";

        /// <summary>The WAV clip shipped in <c>packs/cwy.example/audio/voice/</c>.</summary>
        internal const string TestClipName = "Voice_CWY_Test";

        private const string PatchedId = "p1.voice.patched";
        private const string RegisteredId = "p1.voice.registered";
        private const string ResolveId = "p1.voice.resolve";
        private const string PlayId = "p1.voice.play";
        private const string OggId = "p1.voice.ogg";

        private const string NoOggFixture = "no ogg fixture (ffmpeg absent)";
        private const int MaxListedNames = 12;

        /// <summary>Registers every voice check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(PatchedId, Phase, Patched));
            SmokeRegistry.Register(new SmokeCheck(RegisteredId, Phase, Registered));
            SmokeRegistry.Register(new SmokeCheck(ResolveId, Phase, Resolve));

            // Loud: this one makes Satone's voice manager play a clip out loud. It runs on an
            // explicit smoke trigger only, never on a pass the player did not ask for.
            SmokeRegistry.Register(new SmokeCheck(PlayId, Phase, Play), loud: true);
            SmokeRegistry.Register(new SmokeCheck(OggId, Phase, Ogg));
        }

        /// <summary>The closed-generic lookup was found and prefixed.</summary>
        private static SmokeCheckResult Patched()
        {
            if (!VoiceFeature.Enabled)
            {
                return SmokeCheckResult.Fail(
                    PatchedId,
                    $"{VoiceFeature.ConfigSection}.{VoiceFeature.ConfigKeyEnabled}=false, so nothing was patched");
            }

            return VoiceFeature.PatchApplied
                ? SmokeCheckResult.Pass(PatchedId, VoiceFeature.PatchDetail)
                : SmokeCheckResult.Fail(PatchedId, VoiceFeature.PatchDetail);
        }

        /// <summary>At least one clip from a content pack is registered.</summary>
        private static SmokeCheckResult Registered()
        {
            var names = new List<string>();
            foreach (var entry in VoiceFeature.Snapshot())
            {
                if (entry.Priority == VoicePriority.Pack)
                {
                    names.Add(entry.Name + " <- " + entry.Source);
                }
            }

            if (names.Count == 0)
            {
                var summary = string.IsNullOrEmpty(VoiceFeature.LastSummary)
                    ? "the preload never ran"
                    : VoiceFeature.LastSummary;

                return SmokeCheckResult.Fail(
                    RegisteredId, $"no pack voice clips are registered ({summary}); deploy packs/cwy.example");
            }

            return SmokeCheckResult.Pass(RegisteredId, $"{names.Count}: {Join(names)}");
        }

        /// <summary>
        /// The game's own lookup returns our clip: the patched <c>GetAudioClip</c> is invoked
        /// through reflection on the live <c>VoiceManager</c>, and the result has to be the very
        /// object we registered, not merely a clip of the same name.
        /// </summary>
        private static SmokeCheckResult Resolve() => ResolvesThroughTheGame(ResolveId, TestClipName);

        /// <summary>The game plays the clip - the tester listens for the beep.</summary>
        private static SmokeCheckResult Play()
        {
            if (!VoiceFeature.TryGet(TestClipName, out var ours))
            {
                return SmokeCheckResult.Fail(
                    PlayId, $"'{TestClipName}' is not registered; deploy packs/cwy.example and restart");
            }

            var player = VoiceSeam.TryPlay(TestClipName, out var error);
            if (player is null)
            {
                return SmokeCheckResult.Fail(PlayId, error);
            }

            return SmokeCheckResult.Pass(
                PlayId,
                $"VoiceManager.Play('{TestClipName}') -> {player.GetType().Name} "
                + $"({ours.length:0.##}s, {ours.frequency}Hz)");
        }

        /// <summary>
        /// The Ogg path, proven end to end in the engine - but only when the fixture exists.
        /// </summary>
        private static SmokeCheckResult Ogg()
        {
            var name = FirstOggVoiceName();
            if (name is null)
            {
                return SmokeCheckResult.Pass(OggId, NoOggFixture);
            }

            return ResolvesThroughTheGame(OggId, name);
        }

        private static SmokeCheckResult ResolvesThroughTheGame(string id, string name)
        {
            if (!VoiceFeature.PatchApplied)
            {
                return SmokeCheckResult.Fail(id, "the lookup is not patched: " + VoiceFeature.PatchDetail);
            }

            if (!VoiceFeature.TryGet(name, out var ours))
            {
                return SmokeCheckResult.Fail(
                    id, $"'{name}' is not registered; deploy packs/cwy.example and restart");
            }

            var instance = VoiceSeam.TryGetInstance(out var error);
            if (instance is null)
            {
                return SmokeCheckResult.Fail(id, error);
            }

            // A probe, not a line: keep it out of the playback record the subtitle hold reads.
            AudioClip? returned;
            using (VoicePlayback.Suppress())
            {
                returned = VoiceSeam.TryInvokeGetAudioClip(instance, name, out error) as AudioClip;
            }

            if (returned is null)
            {
                return SmokeCheckResult.Fail(
                    id, string.IsNullOrEmpty(error) ? "GetAudioClip returned something that is not an AudioClip" : error);
            }

            if (!ReferenceEquals(returned, ours))
            {
                return SmokeCheckResult.Fail(
                    id,
                    $"GetAudioClip('{name}') returned '{returned.name}', which is not the clip we registered "
                    + "- the prefix is not being reached");
            }

            return SmokeCheckResult.Pass(
                id,
                $"GetAudioClip('{name}') returned our clip by reference "
                + $"({ours.samples} samples x{ours.channels} @{ours.frequency}Hz from {VoiceFeature.SourceOf(name)})");
        }

        /// <summary>
        /// The voice name of the first <c>.ogg</c> in any loaded pack, or null when no pack ships
        /// one - which is what "the fixture was never generated" looks like from in here.
        /// </summary>
        private static string? FirstOggVoiceName()
        {
            foreach (var pack in PackManager.Loaded)
            {
                foreach (var file in pack.Index.Voices)
                {
                    if (!string.Equals(file.Extension, ".ogg", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (VoiceNameRules.TryFromFileName(file.FileName, out var name, out _) && name != null)
                    {
                        return name;
                    }
                }
            }

            return null;
        }

        private static string Join(List<string> names)
        {
            var shown = Math.Min(names.Count, MaxListedNames);
            var parts = new string[shown];
            for (var i = 0; i < shown; i++)
            {
                parts[i] = names[i];
            }

            var joined = string.Join(", ", parts);
            return names.Count > shown ? joined + " and " + (names.Count - shown) + " more" : joined;
        }
    }
}
