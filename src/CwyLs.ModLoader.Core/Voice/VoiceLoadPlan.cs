using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// What a preload run will do, decided from the pack indexes alone: which files, in which
    /// order, through which loader, and which files are skipped with what reason.
    /// </summary>
    /// <remarks>
    /// Split out of the plugin so the conventions - name from file name, loader from extension,
    /// later packs override earlier ones - are unit-testable without Unity, and so the log line
    /// and the smoke detail cannot disagree with what was actually attempted.
    /// <para>
    /// Steps keep the order the packs were given in and, within a pack, the order of the index
    /// (ordinal by path). When two files would claim the same voice name - two packs shipping
    /// <c>Voice_A.ogg</c>, or one pack shipping both <c>Voice_A.ogg</c> and <c>Voice_A.wav</c> -
    /// only the last one is planned and the earlier ones become <see cref="Skipped"/> entries
    /// saying who overrode them. That is decided here rather than left to the registry on purpose:
    /// a WAV loads synchronously and an Ogg several frames later, so "whoever registers last wins"
    /// would otherwise make the winner depend on decode timing instead of on load order.
    /// </para>
    /// </remarks>
    public sealed class VoiceLoadPlan
    {
        private static readonly VoiceLoadStep[] NoSteps = new VoiceLoadStep[0];
        private static readonly VoiceLoadSkip[] NoSkips = new VoiceLoadSkip[0];

        /// <summary>What <see cref="Summarize"/> says when there is nothing to do.</summary>
        public const string NothingToLoadSummary = "no voice clips in any pack";

        private VoiceLoadPlan(IReadOnlyList<VoiceLoadStep> steps, IReadOnlyList<VoiceLoadSkip> skipped)
        {
            Steps = steps;
            Skipped = skipped;

            var wav = 0;
            var ogg = 0;
            foreach (var step in steps)
            {
                if (step.Loader == VoiceLoaderKind.WavSync)
                {
                    wav++;
                }
                else
                {
                    ogg++;
                }
            }

            WavCount = wav;
            OggCount = ogg;
        }

        /// <summary>A plan that loads nothing.</summary>
        public static VoiceLoadPlan Empty { get; } = new VoiceLoadPlan(NoSteps, NoSkips);

        /// <summary>The files to load, in the order they must be registered.</summary>
        public IReadOnlyList<VoiceLoadStep> Steps { get; }

        /// <summary>The files that will not be loaded, with reasons.</summary>
        public IReadOnlyList<VoiceLoadSkip> Skipped { get; }

        /// <summary>How many steps go through the synchronous WAV loader.</summary>
        public int WavCount { get; }

        /// <summary>How many steps go through the engine's async decoder.</summary>
        public int OggCount { get; }

        /// <summary>Total number of files to load.</summary>
        public int Count => Steps.Count;

        /// <summary>
        /// Builds a plan from the loaded packs, in load order.
        /// </summary>
        /// <param name="packs">
        /// Pack id to that pack's content index, in the order the packs were resolved. Null, and
        /// entries with a blank id or a null index, yield nothing rather than an exception: a
        /// preload must never be the thing that takes the game down.
        /// </param>
        public static VoiceLoadPlan Build(IEnumerable<KeyValuePair<string, PackContentIndex>>? packs)
        {
            if (packs is null)
            {
                return Empty;
            }

            var candidates = new List<Candidate>();

            foreach (var pack in packs)
            {
                var packId = pack.Key;
                var index = pack.Value;

                if (string.IsNullOrWhiteSpace(packId) || index is null)
                {
                    continue;
                }

                foreach (var file in index.Voices)
                {
                    if (!TryLoaderFor(file.Extension, out var loader))
                    {
                        candidates.Add(Candidate.Skip(
                            packId,
                            file.Path,
                            "'" + file.Extension + "' is not a voice format we load (.ogg or .wav)"));
                        continue;
                    }

                    if (!VoiceNameRules.TryFromFileName(file.FileName, out var name, out var reason) || name is null)
                    {
                        candidates.Add(Candidate.Skip(
                            packId, file.Path, reason ?? "the file name is not a voice name"));
                        continue;
                    }

                    candidates.Add(Candidate.Load(packId, file.Path, name!, loader));
                }
            }

            if (candidates.Count == 0)
            {
                return Empty;
            }

            // Last claim on a name wins; the earlier ones are not loaded at all, so nothing
            // depends on which decoder finishes first.
            var winners = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.Name != null)
                {
                    winners[candidate.Name] = i;
                }
            }

            var steps = new List<VoiceLoadStep>();
            var skipped = new List<VoiceLoadSkip>();

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];

                if (candidate.Name is null)
                {
                    skipped.Add(new VoiceLoadSkip(candidate.PackId, candidate.Path, candidate.Reason!));
                    continue;
                }

                if (winners[candidate.Name] != i)
                {
                    var winner = candidates[winners[candidate.Name]];
                    skipped.Add(new VoiceLoadSkip(
                        candidate.PackId,
                        candidate.Path,
                        "overridden by " + winner.PackId + "/" + winner.Path));
                    continue;
                }

                steps.Add(new VoiceLoadStep(candidate.PackId, candidate.Path, candidate.Name, candidate.Loader));
            }

            return new VoiceLoadPlan(steps, skipped);
        }

        /// <summary>
        /// The loader an extension calls for.
        /// </summary>
        /// <param name="extension">Lower-cased extension including the dot, as <c>PackFile</c> stores it.</param>
        /// <param name="loader">The loader to use.</param>
        /// <returns>False when the extension is not one we load.</returns>
        public static bool TryLoaderFor(string? extension, out VoiceLoaderKind loader)
        {
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                loader = VoiceLoaderKind.WavSync;
                return true;
            }

            if (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase))
            {
                loader = VoiceLoaderKind.EngineAsync;
                return true;
            }

            loader = VoiceLoaderKind.WavSync;
            return false;
        }

        /// <summary>
        /// One line fit for a log or a smoke detail, e.g.
        /// <c>3 clip(s) to load: 1 ogg, 2 wav; 1 skipped</c>.
        /// </summary>
        public string Summarize()
        {
            if (Steps.Count == 0 && Skipped.Count == 0)
            {
                return NothingToLoadSummary;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} clip(s) to load: {1} ogg, {2} wav; {3} skipped",
                Steps.Count,
                OggCount,
                WavCount,
                Skipped.Count);
        }

        /// <inheritdoc />
        public override string ToString() => Summarize();

        /// <summary>
        /// One indexed voice file while the plan is being built: either loadable (it has a
        /// <see cref="Name"/>) or already refused (it has a <see cref="Reason"/>). Kept in file
        /// order so both lists come out in the order a reader expects.
        /// </summary>
        private readonly struct Candidate
        {
            private Candidate(string packId, string path, string? name, string? reason, VoiceLoaderKind loader)
            {
                PackId = packId;
                Path = path;
                Name = name;
                Reason = reason;
                Loader = loader;
            }

            internal string PackId { get; }

            internal string Path { get; }

            /// <summary>The voice name, or null when the file was refused.</summary>
            internal string? Name { get; }

            /// <summary>Why it was refused, or null when it is loadable.</summary>
            internal string? Reason { get; }

            internal VoiceLoaderKind Loader { get; }

            internal static Candidate Load(string packId, string path, string name, VoiceLoaderKind loader) =>
                new Candidate(packId, path, name, null, loader);

            internal static Candidate Skip(string packId, string path, string reason) =>
                new Candidate(packId, path, null, reason, VoiceLoaderKind.WavSync);
        }
    }
}
