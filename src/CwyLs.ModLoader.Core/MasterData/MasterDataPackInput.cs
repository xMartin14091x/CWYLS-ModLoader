using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// One pack's master-data files, as text. The adapter reads them off disk; the merge takes
    /// only this, so every rule below it is testable without a filesystem or a game.
    /// </summary>
    public sealed class MasterDataPackInput
    {
        private readonly List<SubtitleSource> _subtitles = new List<SubtitleSource>();

        /// <summary>Creates an input for one pack.</summary>
        /// <param name="packId">The pack id, e.g. <c>cwy.example</c>. Required.</param>
        /// <exception cref="ArgumentException"><paramref name="packId"/> is blank.</exception>
        public MasterDataPackInput(string packId)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            PackId = packId;
        }

        /// <summary>The pack these files came from.</summary>
        public string PackId { get; }

        /// <summary>Contents of <c>data/localization.json</c>, or null when the pack has none.</summary>
        public string? LocalizationJson { get; set; }

        /// <summary>Contents of <c>data/novel.json</c>, or null when the pack has none.</summary>
        public string? NovelJson { get; set; }

        /// <summary>Contents of <c>data/scenario_groups.json</c>, or null when the pack has none.</summary>
        public string? ScenarioGroupsJson { get; set; }

        /// <summary>The pack's subtitle tables, in the order they were found.</summary>
        public IReadOnlyList<SubtitleSource> Subtitles => _subtitles;

        /// <summary>True when the pack ships no master data at all.</summary>
        public bool IsEmpty =>
            LocalizationJson is null && NovelJson is null && ScenarioGroupsJson is null && _subtitles.Count == 0;

        /// <summary>Adds one subtitle table.</summary>
        /// <param name="relativePath">Pack-relative path, e.g. <c>data/subtitles/th.json</c>.</param>
        /// <param name="json">The file's contents.</param>
        /// <exception cref="ArgumentException"><paramref name="relativePath"/> is blank.</exception>
        public void AddSubtitles(string relativePath, string? json)
        {
            _subtitles.Add(new SubtitleSource(relativePath, json));
        }

        /// <summary><c>packId (files)</c>.</summary>
        public override string ToString()
        {
            var files = new List<string>(4);
            if (LocalizationJson != null) { files.Add("localization"); }
            if (NovelJson != null) { files.Add("novel"); }
            if (ScenarioGroupsJson != null) { files.Add("scenario_groups"); }
            if (_subtitles.Count > 0) { files.Add(_subtitles.Count + " subtitle table(s)"); }

            return PackId + " (" + (files.Count == 0 ? "no master data" : string.Join(", ", files)) + ")";
        }

        /// <summary>One subtitle file: where it was and what was in it.</summary>
        public sealed class SubtitleSource
        {
            /// <summary>Creates a source.</summary>
            /// <param name="relativePath">Pack-relative path. Required.</param>
            /// <param name="json">The file's contents; null is reported as an empty file.</param>
            /// <exception cref="ArgumentException"><paramref name="relativePath"/> is blank.</exception>
            public SubtitleSource(string relativePath, string? json)
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    throw new ArgumentException("a relative path is required", nameof(relativePath));
                }

                RelativePath = relativePath;
                Json = json;
            }

            /// <summary>Pack-relative path, e.g. <c>data/subtitles/th.json</c>.</summary>
            public string RelativePath { get; }

            /// <summary>The file's contents.</summary>
            public string? Json { get; }

            /// <summary>The path.</summary>
            public override string ToString() => RelativePath;
        }
    }
}
