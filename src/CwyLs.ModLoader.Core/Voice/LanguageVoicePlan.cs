using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// The language-scoped voice sets the loaded packs ship: <c>audio/voice/&lt;lang&gt;/&lt;VoiceName&gt;.ogg</c>
    /// (or <c>.wav</c>), decided from the pack indexes alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file directly in <c>audio/voice/</c> is a global override (<see cref="VoiceLoadPlan"/>) and
    /// is never part of a language set; <see cref="PackContentIndex"/> classifies a file one folder
    /// deeper as <see cref="PackContentKind.Other"/>, so the global preload never sees it. This plan
    /// reads exactly those files, from <see cref="PackContentIndex.Files"/>.
    /// </para>
    /// <para>
    /// The folder name is a language code in any spelling <see cref="SubtitleLanguages.Normalize"/>
    /// accepts (<c>th</c>, <c>TH</c>, <c>thai</c> are one set). Whether the code names a language this
    /// session can display is not decided here: the plan is built before the language registry, and an
    /// unknown code simply never becomes the active language.
    /// </para>
    /// <para>
    /// Within one language the rules match <see cref="VoiceLoadPlan"/>: later pack over earlier pack,
    /// and within one pack the later path (ordinal) wins; every loser becomes a
    /// <see cref="VoiceLoadSkip"/> naming its winner, so the outcome never depends on load timing.
    /// </para>
    /// </remarks>
    public sealed class LanguageVoicePlan
    {
        /// <summary>The folder every language set lives under: <c>audio/voice/</c>.</summary>
        public const string VoiceFolder = PackContentIndex.VoiceFolder;

        /// <summary>What <see cref="Summarize"/> says when no pack ships a language set.</summary>
        public const string NothingSummary = "no pack ships audio/voice/<lang>/";

        /// <summary>The longest folder name accepted as a language code.</summary>
        public const int MaxCodeLength = 16;

        private static readonly LanguageVoiceSet[] NoSets = new LanguageVoiceSet[0];
        private static readonly VoiceLoadSkip[] NoSkips = new VoiceLoadSkip[0];

        private readonly Dictionary<string, LanguageVoiceSet> _byCode;

        private LanguageVoicePlan(IReadOnlyList<LanguageVoiceSet> sets, IReadOnlyList<VoiceLoadSkip> skipped)
        {
            Sets = sets;
            Skipped = skipped;
            _byCode = new Dictionary<string, LanguageVoiceSet>(StringComparer.Ordinal);

            var total = 0;
            foreach (var set in sets)
            {
                _byCode[set.Code] = set;
                total += set.Count;
            }

            Count = total;
        }

        /// <summary>A plan with no language sets.</summary>
        public static LanguageVoicePlan Empty { get; } = new LanguageVoicePlan(NoSets, NoSkips);

        /// <summary>One set per language code, ordinally sorted by code.</summary>
        public IReadOnlyList<LanguageVoiceSet> Sets { get; }

        /// <summary>The files under a language folder that will not be loaded, with reasons.</summary>
        public IReadOnlyList<VoiceLoadSkip> Skipped { get; }

        /// <summary>Total clips across every set.</summary>
        public int Count { get; }

        /// <summary>The language codes that have a set, ordinally sorted.</summary>
        public IReadOnlyList<string> Codes
        {
            get
            {
                var codes = new string[Sets.Count];
                for (var i = 0; i < Sets.Count; i++)
                {
                    codes[i] = Sets[i].Code;
                }

                return codes;
            }
        }

        /// <summary>The set for a language code, in any spelling <see cref="SubtitleLanguages.Normalize"/> accepts.</summary>
        /// <param name="code">A language code.</param>
        /// <param name="set">The set, or null.</param>
        /// <returns>True when a pack ships a set for that language.</returns>
        public bool TryGet(string? code, out LanguageVoiceSet? set)
        {
            var normalized = SubtitleLanguages.Normalize(code);
            if (normalized.Length > 0 && _byCode.TryGetValue(normalized, out var found))
            {
                set = found;
                return true;
            }

            set = null;
            return false;
        }

        /// <summary>True when a pack ships a set for this language code.</summary>
        /// <param name="code">A language code in any accepted spelling.</param>
        public bool Has(string? code) => TryGet(code, out _);

        /// <summary>
        /// Builds the sets from the loaded packs, in load order.
        /// </summary>
        /// <param name="packs">
        /// Pack id to that pack's content index, in the order the packs were resolved. Null, and
        /// entries with a blank id or a null index, yield nothing rather than an exception.
        /// </param>
        public static LanguageVoicePlan Build(IEnumerable<KeyValuePair<string, PackContentIndex>>? packs)
        {
            if (packs is null)
            {
                return Empty;
            }

            var skipped = new List<VoiceLoadSkip>();
            var byCode = new Dictionary<string, List<VoiceLoadStep>>(StringComparer.Ordinal);

            foreach (var pack in packs)
            {
                var packId = pack.Key;
                var index = pack.Value;
                if (string.IsNullOrWhiteSpace(packId) || index is null)
                {
                    continue;
                }

                foreach (var file in index.Files)
                {
                    if (!TryParsePath(file.Path, out var folder, out var fileName, out var reason))
                    {
                        if (reason != null)
                        {
                            skipped.Add(new VoiceLoadSkip(packId, file.Path, reason));
                        }

                        continue;
                    }

                    if (!TryNormalizeCode(folder, out var code, out reason))
                    {
                        skipped.Add(new VoiceLoadSkip(packId, file.Path, reason!));
                        continue;
                    }

                    if (!VoiceLoadPlan.TryLoaderFor(file.Extension, out var loader))
                    {
                        skipped.Add(new VoiceLoadSkip(
                            packId,
                            file.Path,
                            "'" + file.Extension + "' is not a voice format we load (.ogg or .wav)"));
                        continue;
                    }

                    if (!VoiceNameRules.TryFromFileName(fileName, out var name, out reason) || name is null)
                    {
                        skipped.Add(new VoiceLoadSkip(packId, file.Path, reason ?? "the file name is not a voice name"));
                        continue;
                    }

                    if (!byCode.TryGetValue(code, out var steps))
                    {
                        steps = new List<VoiceLoadStep>();
                        byCode.Add(code, steps);
                    }

                    steps.Add(new VoiceLoadStep(packId, file.Path, name!, loader));
                }
            }

            if (byCode.Count == 0)
            {
                return skipped.Count == 0 ? Empty : new LanguageVoicePlan(NoSets, skipped);
            }

            var codes = new List<string>(byCode.Keys);
            codes.Sort(StringComparer.Ordinal);

            var sets = new List<LanguageVoiceSet>(codes.Count);
            foreach (var code in codes)
            {
                sets.Add(Resolve(code, byCode[code], skipped));
            }

            return new LanguageVoicePlan(sets, skipped);
        }

        /// <summary>
        /// Splits a pack-relative path into its language folder and file name, when it lies exactly
        /// one folder below <c>audio/voice/</c>.
        /// </summary>
        /// <param name="normalisedPath">A forward-slash relative path, as <see cref="PackFile.Path"/> holds it.</param>
        /// <param name="folder">The language folder name as written, or empty.</param>
        /// <param name="fileName">The file name, or empty.</param>
        /// <param name="reason">
        /// Why a path under <c>audio/voice/</c> is refused (nested too deep), or null when the path is
        /// simply not a language-set file (outside <c>audio/voice/</c>, or a global voice file).
        /// </param>
        /// <returns>True when the path is <c>audio/voice/&lt;folder&gt;/&lt;file&gt;</c>.</returns>
        public static bool TryParsePath(string? normalisedPath, out string folder, out string fileName, out string? reason)
        {
            folder = string.Empty;
            fileName = string.Empty;
            reason = null;

            if (string.IsNullOrEmpty(normalisedPath)
                || !normalisedPath!.StartsWith(VoiceFolder, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = normalisedPath.Substring(VoiceFolder.Length);
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                // audio/voice/<file>: a global override, VoiceLoadPlan's business.
                return false;
            }

            if (rest.IndexOf('/', slash + 1) >= 0)
            {
                reason = "nested deeper than audio/voice/<lang>/; a language set takes direct children only";
                return false;
            }

            folder = rest.Substring(0, slash);
            fileName = rest.Substring(slash + 1);

            // Hidden files (.DS_Store, ._Voice_X.ogg from a macOS copy) are not content; saying
            // nothing about them keeps the skip list about files an author actually wrote.
            if (fileName.StartsWith(".", StringComparison.Ordinal))
            {
                folder = string.Empty;
                fileName = string.Empty;
                return false;
            }

            return folder.Length > 0 && fileName.Length > 0;
        }

        /// <summary>
        /// The canonical language code for a folder name: normalised with
        /// <see cref="SubtitleLanguages.Normalize"/>, then checked to be a plausible code
        /// (letters, digits and <c>-</c>, at most <see cref="MaxCodeLength"/> characters).
        /// </summary>
        /// <param name="folder">The folder name as written in the pack.</param>
        /// <param name="code">The canonical code, or empty.</param>
        /// <param name="reason">Why the folder is not a language code, or null.</param>
        /// <returns>True when <paramref name="folder"/> names a language code.</returns>
        public static bool TryNormalizeCode(string? folder, out string code, out string? reason)
        {
            code = SubtitleLanguages.Normalize(folder);
            if (code.Length == 0 || code.Length > MaxCodeLength)
            {
                reason = "'" + folder + "' is not a language code (audio/voice/<lang>/, e.g. th)";
                code = string.Empty;
                return false;
            }

            foreach (var c in code)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok)
                {
                    reason = "'" + folder + "' is not a language code (letters, digits and '-' only)";
                    code = string.Empty;
                    return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// One line for a log or a smoke detail, e.g. <c>th: 120 clip(s) from my.lang; 0 skipped</c>.
        /// </summary>
        public string Summarize()
        {
            if (Sets.Count == 0)
            {
                return Skipped.Count == 0
                    ? NothingSummary
                    : NothingSummary + "; " + Skipped.Count.ToString(CultureInfo.InvariantCulture) + " file(s) skipped";
            }

            var parts = new List<string>(Sets.Count);
            foreach (var set in Sets)
            {
                parts.Add(set.ToString());
            }

            return string.Join(", ", parts) + "; "
                + Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped";
        }

        /// <inheritdoc />
        public override string ToString() => Summarize();

        /// <summary>Last claim on a name wins; the earlier claims become skips naming the winner.</summary>
        private static LanguageVoiceSet Resolve(string code, List<VoiceLoadStep> candidates, List<VoiceLoadSkip> skipped)
        {
            var winners = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < candidates.Count; i++)
            {
                winners[candidates[i].Name] = i;
            }

            var steps = new List<VoiceLoadStep>(winners.Count);
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var winnerIndex = winners[candidate.Name];
                if (winnerIndex != i)
                {
                    var winner = candidates[winnerIndex];
                    skipped.Add(new VoiceLoadSkip(
                        candidate.PackId,
                        candidate.RelativePath,
                        "overridden by " + winner.PackId + "/" + winner.RelativePath));
                    continue;
                }

                steps.Add(candidate);
            }

            return new LanguageVoiceSet(code, steps);
        }
    }
}
