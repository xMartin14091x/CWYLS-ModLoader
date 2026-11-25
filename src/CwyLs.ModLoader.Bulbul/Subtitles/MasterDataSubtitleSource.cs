using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Packs;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// Publishes the packs' text tables (<c>data/subtitles/&lt;code&gt;.json</c>, merged by the
    /// master-data feature into <see cref="MasterDataFeature.Subtitles"/>) as an
    /// <see cref="ISubtitleSource"/>, so the text seam can ask them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads <see cref="MasterDataFeature.Subtitles"/> on every call rather than holding a copy, so a
    /// pack rescan that re-merges the tables is picked up without re-registering. Registered once,
    /// before <c>Satone.Say</c>'s own source, so packs are asked first.
    /// </para>
    /// <para>
    /// <b>Live entries (Pack Maker, DS-03).</b> When Pack Maker saves or removes one entry, the
    /// editor writes the file and then sets the entry here (<see cref="SetLive"/>): the value every
    /// pack together now gives that line (later pack over earlier, as the merge would), or "none".
    /// Live entries are asked before the merged tables, so the next time the line is shown it is the
    /// new text, with no restart. A pack rescan re-reads every table from disk, which then holds the
    /// same text, so the live entries are dropped there (<see cref="ClearLive"/>). The set is an
    /// immutable snapshot replaced under a lock, so the text seam still reads it without a lock and,
    /// while no entry is live, without a lookup.
    /// </para>
    /// </remarks>
    internal sealed class MasterDataSubtitleSource : ISubtitleSource
    {
        /// <summary>The name the source is registered under.</summary>
        internal const string SourceName = "packs (data/subtitles)";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Dictionary<string, string?>> NoLive =
            new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

        private static MasterDataSubtitleSource? _registered;
        private static Dictionary<string, Dictionary<string, string?>> _live = NoLive;

        /// <summary>True once the source is registered.</summary>
        internal static bool Attached => _registered != null;

        /// <summary>How many live entries Pack Maker has set since the last rescan.</summary>
        internal static int LiveCount
        {
            get
            {
                var total = 0;
                foreach (var table in _live.Values)
                {
                    total += table.Count;
                }

                return total;
            }
        }

        /// <summary>Registers the source with <see cref="SubtitleSources"/>. Idempotent.</summary>
        internal static void Register()
        {
            lock (Gate)
            {
                if (_registered != null)
                {
                    return;
                }

                _registered = new MasterDataSubtitleSource();
                SubtitleSources.Register(_registered, SourceName);
                PackManager.PacksResolved += _ => ClearLive("packs rescanned");
            }
        }

        /// <summary>
        /// Pack Maker: from now on <paramref name="localizationId"/> in <paramref name="lang"/> reads
        /// <paramref name="text"/> from the packs, or nothing when it is null (the next text source and
        /// the language's fallback chain answer). Safe from any thread.
        /// </summary>
        /// <param name="lang">The language code.</param>
        /// <param name="localizationId">The line.</param>
        /// <param name="text">What the packs now give it, or null for nothing.</param>
        internal static void SetLive(string lang, string localizationId, string? text)
        {
            var code = SubtitleLanguages.Normalize(lang);
            lock (Gate)
            {
                var next = new Dictionary<string, Dictionary<string, string?>>(_live, StringComparer.OrdinalIgnoreCase);
                var table = next.TryGetValue(code, out var old)
                    ? new Dictionary<string, string?>(old, StringComparer.Ordinal)
                    : new Dictionary<string, string?>(StringComparer.Ordinal);
                table[localizationId] = text;
                next[code] = table;
                _live = next;
            }
        }

        /// <summary>Drops every live entry (the merged tables hold the same now). Safe from any thread.</summary>
        /// <param name="reason">Why, for the trace.</param>
        internal static void ClearLive(string reason)
        {
            int dropped;
            lock (Gate)
            {
                dropped = LiveCount;
                _live = NoLive;
            }

            if (dropped > 0)
            {
                ConsoleEvents.File(SubtitleFeature.TraceSys, "makerLiveCleared", ("entries", dropped), ("reason", reason));
            }
        }

        /// <summary>
        /// What the packs give a line now: a live entry when Pack Maker set one, else the merged
        /// tables. For the editor and its Play button; the seam uses <see cref="TryGet"/>.
        /// </summary>
        /// <param name="localizationId">The line.</param>
        /// <param name="lang">The language code.</param>
        /// <param name="text">The text, or null.</param>
        /// <returns>True when the packs have text for the line.</returns>
        internal static bool TryGetCurrent(string localizationId, string lang, out string? text)
        {
            if (Maker.PaidDlcContent.HidesLine(localizationId))
            {
                text = null;
                return false;
            }

            if (TryLive(localizationId, lang, out var live, out var hidden))
            {
                text = hidden ? null : live;
                return !hidden;
            }

            return MasterDataFeature.Subtitles.TryGet(localizationId, SubtitleLanguages.Normalize(lang), out text)
                   && !string.IsNullOrEmpty(text);
        }

        /// <inheritdoc />
        public bool TryGet(string localizationId, string lang, out string text)
        {
            text = string.Empty;
            if (Maker.PaidDlcContent.HidesLine(localizationId))
            {
                // Paid DLC content: packs never answer for it (the game shows its own text).
                return false;
            }

            try
            {
                if (TryLive(localizationId, lang, out var live, out var hidden))
                {
                    if (hidden)
                    {
                        return false;
                    }

                    text = live!;
                    return true;
                }

                if (MasterDataFeature.Subtitles.TryGet(localizationId, lang, out var found) && !string.IsNullOrEmpty(found))
                {
                    text = found!;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                // Logged, never rethrown: this runs inside the game's own text binding.
                CwyLsModLoader.Log.LogWarning(
                    $"{SubtitleFeature.LogPrefix} the pack text table threw for '{localizationId}' [{lang}]: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>A live entry: <paramref name="hidden"/> when Pack Maker removed the last one.</summary>
        private static bool TryLive(string localizationId, string lang, out string? text, out bool hidden)
        {
            text = null;
            hidden = false;
            var live = _live;
            if (live.Count == 0 || string.IsNullOrEmpty(lang) || string.IsNullOrEmpty(localizationId)
                || !live.TryGetValue(lang, out var table) || !table.TryGetValue(localizationId, out text))
            {
                return false;
            }

            hidden = string.IsNullOrEmpty(text);
            return true;
        }
    }
}
