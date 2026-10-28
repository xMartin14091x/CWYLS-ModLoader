using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Core.Smoke;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>
    /// The audio feature's checks for the final in-game pass. Quiet: seams, rows replaced and renamed, paid rows
    /// untouched, pack music listed, the save guard. Loud: the A/B switch on a replaced loop (audible).
    /// </summary>
    internal static class AudioSmokeChecks
    {
        /// <summary>Phase label of the checks.</summary>
        internal const string Phase = "D";

        internal const string SeamsId = "d.audio.seams";
        internal const string RowsId = "d.audio.rows";
        internal const string PaidId = "d.audio.paid";
        internal const string MusicId = "d.audio.music";
        internal const string SaveGuardId = "d.audio.saveguard";
        internal const string AbId = "d.audio.ab";

        private const string DeployHint = "deploy packs/cwy.demo.audio and restart";

        /// <summary>Registers the checks. Idempotent (the registry replaces by id).</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(SeamsId, Phase, Seams));
            SmokeRegistry.Register(new SmokeCheck(RowsId, Phase, RowsReplaced));
            SmokeRegistry.Register(new SmokeCheck(PaidId, Phase, PaidUntouched));
            SmokeRegistry.Register(new SmokeCheck(MusicId, Phase, MusicListed));
            SmokeRegistry.Register(new SmokeCheck(SaveGuardId, Phase, SaveGuard));

            // Loud: switches a playing loop between two clips. Restores the pack variant afterwards.
            SmokeRegistry.Register(new SmokeCheck(AbId, Phase, AbSwitch), loud: true, restore: () => AudioFeature.Select("all", AudioVariant.Pack));
        }

        private static SmokeCheckResult Seams()
        {
            if (!AudioFeature.Enabled)
            {
                return SmokeCheckResult.Fail(SeamsId, AudioFeature.ConfigSection + "." + AudioFeature.ConfigKeyEnabled + "=false");
            }

            if (AudioFeature.MissingSeams.Length > 0)
            {
                return SmokeCheckResult.Fail(SeamsId, "missing: " + AudioFeature.MissingSeams);
            }

            if (!PackMusic.Patched)
            {
                return SmokeCheckResult.Fail(SeamsId, "music seams not patched: " + PackMusic.Detail);
            }

            return AudioPlayers.Available
                ? SmokeCheckResult.Pass(SeamsId, AudioTargets.All.Count + " targets ok; " + PackMusic.Detail + "; players live")
                : SmokeCheckResult.Fail(SeamsId, "audio players unavailable: " + AudioPlayers.Error);
        }

        private static SmokeCheckResult RowsReplaced()
        {
            AudioFeature.Apply("smoke");
            if (!AudioFeature.RowsKnown)
            {
                return SmokeCheckResult.NotYetObserved(RowsId, "the game's sound tables are not loaded yet");
            }

            var plan = AudioFeature.Plan;
            if (plan.Assignments.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(RowsId, "no pack replaces a row (" + plan.Summarize() + "); " + DeployHint);
            }

            var problems = new List<string>();
            var ok = new List<string>();
            foreach (var assignment in plan.Assignments)
            {
                var key = assignment.Row.Key;
                var row = AudioFeature.Find(key);
                var replacement = AudioFeature.ReplacementOf(key);
                var original = AudioFeature.OriginalOf(key);
                if (row is null || replacement == null)
                {
                    problems.Add(key + ": " + (row is null ? "row gone" : "clip not decoded"));
                    continue;
                }

                var expected = AudioFeature.IsActive && !IsOriginalSelected(key) ? replacement : original;
                if (!ReferenceEquals(row.Clip, expected))
                {
                    problems.Add(key + ": row holds " + (row.Clip != null ? row.Clip!.name : "nothing") + ", expected the " + (ReferenceEquals(expected, replacement) ? "pack" : "game") + " clip");
                    continue;
                }

                if (original != null && !string.Equals(replacement.name, original.name, StringComparison.Ordinal))
                {
                    problems.Add(key + ": pack clip is named '" + replacement.name + "', the game's '" + original.name + "'");
                    continue;
                }

                ok.Add(key + " (" + replacement.length.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s)");
            }

            return problems.Count == 0
                ? SmokeCheckResult.Pass(RowsId, ok.Count + " row(s): " + string.Join(", ", ok.ToArray()))
                : SmokeCheckResult.Fail(RowsId, string.Join("; ", problems.ToArray()));
        }

        private static SmokeCheckResult PaidUntouched()
        {
            AudioFeature.Apply("smoke");
            if (!AudioFeature.RowsKnown)
            {
                return SmokeCheckResult.NotYetObserved(PaidId, "the game's sound tables are not loaded yet");
            }

            var soundtrack = AudioFeature.Soundtrack;
            if (!soundtrack.IsKnown)
            {
                return SmokeCheckResult.Fail(PaidId, "the game's music list was not read, so nothing could be checked for the soundtrack");
            }

            var paid = new List<string>();
            foreach (var info in AudioFeature.Rows())
            {
                if (!AudioFeature.IsPaid(info.Key))
                {
                    continue;
                }

                paid.Add(info.Key);
                var row = AudioFeature.Find(info.Key);
                var original = AudioFeature.OriginalOf(info.Key);
                if (row != null && original != null && !ReferenceEquals(row.Clip, original))
                {
                    return SmokeCheckResult.Fail(PaidId, info.Key + " is paid content but does not hold the game's clip");
                }
            }

            return SmokeCheckResult.Pass(
                PaidId,
                "soundtrack: " + soundtrack.OriginalCount + " original of " + soundtrack.Tracks.Count + " tracks (Steam DLC "
                + AudioPaidContent.SoundtrackDlcAppId + "); protected story rows: " + (paid.Count == 0 ? "none" : string.Join(", ", paid.ToArray()))
                + "; refused claims: " + AudioFeature.Plan.RefusedCount + " row, " + PackMusic.Plan.RefusedCount + " music");
        }

        private static SmokeCheckResult MusicListed()
        {
            if (!AudioFeature.PackMusicEnabled)
            {
                return SmokeCheckResult.Fail(MusicId, "pack music is off (" + AudioFeature.MissingSeams + ")");
            }

            var tracks = AudioFeature.MusicTracks();
            if (tracks.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(MusicId, "no pack music is planned; " + DeployHint);
            }

            var service = PackMusic.Service;
            if (service is null)
            {
                return SmokeCheckResult.NotYetObserved(MusicId, "the game has not loaded its music list yet");
            }

            var missing = new List<string>();
            foreach (var track in tracks)
            {
                if (!PackMusic.Contains(service, track.Track.Uuid))
                {
                    missing.Add("'" + track.Track.Title + "': " + (track.Problem.Length > 0 ? track.Problem : "not in AllMusicList"));
                }
            }

            return missing.Count == 0
                ? SmokeCheckResult.Pass(MusicId, tracks.Count + " pack track(s) in the music list")
                : SmokeCheckResult.Fail(MusicId, string.Join("; ", missing.ToArray()));
        }

        private static SmokeCheckResult SaveGuard()
        {
            if (!PackMusic.Patched)
            {
                return AudioFeature.PackMusicEnabled
                    ? SmokeCheckResult.Fail(SaveGuardId, "the save guard is not patched: " + PackMusic.Detail)
                    : SmokeCheckResult.Pass(SaveGuardId, "pack music is off, so no pack UUID can reach the save");
            }

            if (!Game.SaveDataManager.HasInstance)
            {
                return SmokeCheckResult.NotYetObserved(SaveGuardId, "the save manager does not exist yet");
            }

            // On copies of the live lists: what the guard writes holds no pack UUID, and the live lists come back whole.
            var setting = Game.SaveDataManager.Instance.MusicSetting;
            var favorites = new List<string>(setting.FavoriteAudioUUIDs);
            var order = new List<string>(setting.PlaylistOrder);
            var excluded = new List<string>(setting.ExcludedFromPlaylistUUIDs);
            var stash = MusicSaveGuard.Strip(favorites, order, excluded);
            foreach (var list in new[] { favorites, order, excluded })
            {
                foreach (var uuid in list)
                {
                    if (PackMusicUuid.IsPackUuid(uuid))
                    {
                        return SmokeCheckResult.Fail(SaveGuardId, "a stripped list still holds " + uuid);
                    }
                }
            }

            MusicSaveGuard.Restore(stash);
            if (!Same(favorites, setting.FavoriteAudioUUIDs) || !Same(order, setting.PlaylistOrder) || !Same(excluded, setting.ExcludedFromPlaylistUUIDs))
            {
                return SmokeCheckResult.Fail(SaveGuardId, "restoring the stripped lists did not give back the live lists");
            }

            return SmokeCheckResult.Pass(
                SaveGuardId,
                "guard on; " + stash.RemovedCount + " pack UUID(s) live, never written; " + PackMusic.GuardRuns + " music save(s) this session, "
                + PackMusic.StrippedSaves + " stripped");
        }

        private static SmokeCheckResult AbSwitch()
        {
            AudioFeature.Apply("smoke");
            AudioRowInfo? target = null;
            foreach (var info in AudioFeature.Rows())
            {
                if (info.HasReplacement && AudioTables.IsLoop(info.Table) && (target is null || info.Playing == AudioPlaying.Replacement))
                {
                    target = info;
                }
            }

            if (target is null)
            {
                return SmokeCheckResult.NotYetObserved(AbId, "no loop row has a pack clip; " + DeployHint);
            }

            var toOriginal = AudioFeature.Select(target.Key, AudioVariant.Original);
            var row = AudioFeature.Find(target.Key);
            var original = AudioFeature.OriginalOf(target.Key);
            if (!toOriginal.Ok || row is null || !ReferenceEquals(row.Clip, original))
            {
                return SmokeCheckResult.Fail(AbId, "switch to original failed: " + (toOriginal.Ok ? "the row does not hold the game clip" : toOriginal.Problem));
            }

            var back = AudioFeature.Select(target.Key, AudioVariant.Pack);
            var replacement = AudioFeature.ReplacementOf(target.Key);
            if (!back.Ok || !ReferenceEquals(row.Clip, replacement))
            {
                return SmokeCheckResult.Fail(AbId, "switch back to pack failed: " + (back.Ok ? "the row does not hold the pack clip" : back.Problem));
            }

            var first = toOriginal.Outcomes.Count > 0 ? toOriginal.Outcomes[0] : null;
            return SmokeCheckResult.Pass(
                AbId,
                target.Key + ": original (" + (first?.Mode.ToString() ?? "?") + ", " + (first?.Players ?? 0) + " player(s)) then pack again; "
                + (target.Playing == AudioPlaying.Replacement ? "it was playing - listen for the change" : "it was not playing; switch the sound on in the game to hear it"));
        }

        private static bool IsOriginalSelected(string key)
        {
            foreach (var info in AudioFeature.Rows())
            {
                if (string.Equals(info.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return info.Variant == AudioVariant.Original;
                }
            }

            return false;
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
