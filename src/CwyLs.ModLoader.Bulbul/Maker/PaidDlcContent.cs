using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// The paid DLC lines, voice clips and scenario groups the DLC gate protects, for the in-game paths outside Pack Maker: a
    /// content pack can neither override them (the master-data injector refuses such rows) nor answer for them with a subtitle or
    /// a dub. Same gate and markers as Pack Maker (<see cref="MakerCatalogGame.Gate"/>): protected for everyone, owners included,
    /// until an ownership read is added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fail closed: until a verdict is computed, after a failed computation, or when the gate cannot decide, every line, clip and
    /// group counts as protected. The verdict is computed from the game's untouched tables (the injector's baseline, taken before
    /// any pack row is merged; or the loader's own tables when nothing was injected), so a pack override cannot change it, and it
    /// does not depend on the optional master-data feature. It is dropped whenever the game starts a new load
    /// (<see cref="Invalidate"/>) and recomputed on the next question.
    /// </para>
    /// <para>Main thread only: every caller is the game's own text, voice or master-data code.</para>
    /// </remarks>
    internal static class PaidDlcContent
    {
        private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(1);
        private static MakerDlcHidden? _hidden;
        private static DateTime _nextTry = DateTime.MinValue;
        private static string? _lastSummary;

        /// <summary>The current verdict, or null when none is computed (everything counts as protected).</summary>
        internal static MakerDlcHidden? Hidden => _hidden;

        /// <summary>Drops the verdict (a new game load or a release of the tables). The next question recomputes it.</summary>
        internal static void Invalidate()
        {
            _hidden = null;
            _nextTry = DateTime.MinValue;
        }

        /// <summary>Computes the verdict from the given untouched game tables. Never throws; a failure leaves an unknown verdict.</summary>
        /// <param name="localization">The game's own localisation rows.</param>
        /// <param name="novel">The game's own novel rows.</param>
        /// <param name="groups">The game's own scenario groups.</param>
        /// <param name="reason">Why, for the log.</param>
        /// <returns>The verdict (unknown on failure).</returns>
        internal static MakerDlcHidden RefreshFrom(IEnumerable<Master.LocalizationData?> localization, IReadOnlyList<Master.NovelData>? novel,
            IReadOnlyList<Master.ScenarioGroupData>? groups, string reason)
        {
            MakerDlcHidden hidden;
            try
            {
                hidden = MakerCatalogGame.DecideFrom(localization, novel, groups);
            }
            catch (Exception ex)
            {
                hidden = MakerDlcHidden.Unknown("the check failed (" + ex.GetType().Name + ")");
            }

            _hidden = hidden;
            Report(hidden, reason);
            return hidden;
        }

        /// <summary>
        /// Computes the verdict when there is none yet and the game's tables are readable: the injector's baseline when it has one,
        /// otherwise the loader's tables (nothing was merged into them). At most once a second while the tables are not ready.
        /// </summary>
        internal static void Ensure()
        {
            if (_hidden != null || DateTime.UtcNow < _nextTry)
            {
                return;
            }

            _nextTry = DateTime.UtcNow + RetryEvery;
            try
            {
                if (MasterDataInjector.HasBaseline)
                {
                    RefreshFrom(MasterDataInjector.BaseLocalization, MasterDataInjector.BaseNovel, MasterDataInjector.BaseGroups, "baseline");
                    return;
                }

                var loader = MasterDataFeature.ResolveLoader();
                var localization = loader?.LocalizationList;
                if (loader is null || localization is null || loader.NovelMasterList is null || loader.ScenarioGroupMasterList is null
                    || MasterDataInjector.HoldsInstalledCollections(loader))
                {
                    return;
                }

                RefreshFrom(localization.Values, loader.NovelMasterList, loader.ScenarioGroupMasterList, "game tables");
            }
            catch (Exception ex)
            {
                _hidden = MakerDlcHidden.Unknown("the check failed (" + ex.GetType().Name + ")");
                Report(_hidden, "ensure");
            }
        }

        /// <summary>True when a pack must not answer for or override the line: paid DLC, or no verdict yet (fail closed).</summary>
        /// <param name="localizationId">The game's localisation id.</param>
        internal static bool HidesLine(string? localizationId)
        {
            Ensure();
            return MakerDlcHidden.Protects(_hidden, localizationId);
        }

        /// <summary>True when a pack must not answer for the voice clip: paid DLC, or no verdict yet (fail closed).</summary>
        /// <param name="clip">The voice clip name.</param>
        internal static bool HidesVoice(string? clip)
        {
            Ensure();
            return MakerDlcHidden.ProtectsVoice(_hidden, clip);
        }

        /// <summary>True when a pack must not override or extend the scenario group: paid DLC, or no verdict yet (fail closed).</summary>
        /// <param name="groupId">The scenario group id.</param>
        internal static bool HidesGroup(string? groupId)
        {
            Ensure();
            return MakerDlcHidden.ProtectsGroup(_hidden, groupId);
        }

        private static void Report(MakerDlcHidden hidden, string reason)
        {
            var summary = hidden.IsKnown
                ? hidden.LineCount + " line(s), " + hidden.VoiceCount + " voice clip(s), " + hidden.GroupCount + " group(s)"
                : "undecided, so every pack override is held back: " + hidden.Summary;
            if (string.Equals(summary, _lastSummary, StringComparison.Ordinal))
            {
                return;
            }

            _lastSummary = summary;
            var line = MakerCatalog.LogPrefix + " DLC gate (in game): packs never override or answer for paid DLC content: " + summary
                + " (" + reason + "; " + (MakerCatalogGame.DlcFlagReadable ? "DLC flag read" : "no DLC flag in this game build") + ")";
            if (hidden.IsKnown)
            {
                CwyLsModLoader.Log.LogInfo(line);
            }
            else
            {
                CwyLsModLoader.Log.LogWarning(line);
            }
        }
    }
}
