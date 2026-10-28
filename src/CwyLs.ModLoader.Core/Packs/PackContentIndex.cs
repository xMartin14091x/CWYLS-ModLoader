using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// The classified contents of one pack, built from a plain list of relative file paths.
    /// <para>
    /// Path-only by design: it takes a listing, never a directory, so the conventions are unit
    /// testable without a filesystem and identical on macOS and Windows. Paths are normalised to
    /// forward slashes, de-duplicated case-insensitively (both platforms we ship on have
    /// case-insensitive filesystems by default), and ordered ordinally so two scans of the same
    /// pack always produce the same index.
    /// </para>
    /// <para>The conventions, in the order they are tested:</para>
    /// <list type="table">
    ///   <item><term><c>manifest.json</c></term><description><see cref="PackContentKind.Manifest"/></description></item>
    ///   <item><term><c>textures/**.png</c></term><description><see cref="PackContentKind.Texture"/> (any depth)</description></item>
    ///   <item><term><c>audio/voice/*.ogg</c>, <c>audio/voice/*.wav</c></term><description><see cref="PackContentKind.Voice"/></description></item>
    ///   <item><term><c>audio/music/_tracks.json</c></term><description><see cref="PackContentKind.MusicTracks"/></description></item>
    ///   <item><term><c>audio/music/*</c></term><description><see cref="PackContentKind.Music"/></description></item>
    ///   <item><term><c>audio/ambient/*.ogg|wav</c></term><description><see cref="PackContentKind.AmbientAudio"/></description></item>
    ///   <item><term><c>audio/ambient_se/*.ogg|wav</c></term><description><see cref="PackContentKind.AmbientSeAudio"/></description></item>
    ///   <item><term><c>audio/se/system/*.ogg|wav</c></term><description><see cref="PackContentKind.SystemSeAudio"/></description></item>
    ///   <item><term><c>audio/se/timer/*.ogg|wav</c></term><description><see cref="PackContentKind.TimerSeAudio"/></description></item>
    ///   <item><term><c>audio/direction_music/*.ogg|wav</c></term><description><see cref="PackContentKind.DirectionMusicAudio"/></description></item>
    ///   <item><term><c>data/localization.json</c></term><description><see cref="PackContentKind.Localization"/></description></item>
    ///   <item><term><c>data/novel.json</c></term><description><see cref="PackContentKind.Novel"/></description></item>
    ///   <item><term><c>data/scenario_groups.json</c></term><description><see cref="PackContentKind.ScenarioGroups"/></description></item>
    ///   <item><term><c>data/triggers.json</c></term><description><see cref="PackContentKind.Triggers"/></description></item>
    ///   <item><term><c>data/edit/*.json</c></term><description><see cref="PackContentKind.EditData"/> (direct children)</description></item>
    ///   <item><term><c>ui/_sprites.json</c></term><description><see cref="PackContentKind.UiSprites"/></description></item>
    ///   <item><term><c>ui/**.png</c></term><description><see cref="PackContentKind.UiSprite"/> (any depth)</description></item>
    ///   <item><term><c>language.json</c></term><description><see cref="PackContentKind.Language"/></description></item>
    ///   <item><term>anything else</term><description><see cref="PackContentKind.Other"/></description></item>
    /// </list>
    /// Every match is case-insensitive. A folder convention takes direct children only unless the
    /// table says "any depth", so <c>audio/ambient/old/rain.ogg</c> is <see cref="PackContentKind.Other"/>.
    /// An unrecognised file is never an error: packs carry READMEs, source art and notes, and a
    /// pack must not fail to load because of them.
    /// </summary>
    public sealed class PackContentIndex
    {
        /// <summary>The manifest file name every pack folder must contain.</summary>
        public const string ManifestFileName = "manifest.json";

        /// <summary>Folder holding textures; searched recursively.</summary>
        public const string TexturesFolder = "textures/";

        /// <summary>Folder holding voice clips; direct children only.</summary>
        public const string VoiceFolder = "audio/voice/";

        /// <summary>Folder holding music tracks; direct children only.</summary>
        public const string MusicFolder = "audio/music/";

        /// <summary>Master-data localization rows.</summary>
        public const string LocalizationPath = "data/localization.json";

        /// <summary>Master-data novel rows.</summary>
        public const string NovelPath = "data/novel.json";

        /// <summary>Master-data scenario-group rows.</summary>
        public const string ScenarioGroupsPath = "data/scenario_groups.json";

        /// <summary>The pack's language declarations.</summary>
        public const string LanguagePath = "language.json";

        /// <summary>Folder holding EditData files; direct children ending in <c>.json</c>.</summary>
        public const string EditDataFolder = "data/edit/";

        /// <summary>The pack's cutscene triggers.</summary>
        public const string TriggersPath = "data/triggers.json";

        /// <summary>Folder holding ambient-loop replacements; direct children, <c>.ogg</c>/<c>.wav</c>.</summary>
        public const string AmbientFolder = "audio/ambient/";

        /// <summary>Folder holding ambient one-shot replacements; direct children, <c>.ogg</c>/<c>.wav</c>.</summary>
        public const string AmbientSeFolder = "audio/ambient_se/";

        /// <summary>Folder holding system-sound replacements; direct children, <c>.ogg</c>/<c>.wav</c>.</summary>
        public const string SystemSeFolder = "audio/se/system/";

        /// <summary>Folder holding timer-sound replacements; direct children, <c>.ogg</c>/<c>.wav</c>.</summary>
        public const string TimerSeFolder = "audio/se/timer/";

        /// <summary>Folder holding story-music replacements; direct children, <c>.ogg</c>/<c>.wav</c>.</summary>
        public const string DirectionMusicFolder = "audio/direction_music/";

        /// <summary>The pack's music track list, inside <see cref="MusicFolder"/>.</summary>
        public const string MusicTracksPath = "audio/music/_tracks.json";

        /// <summary>Folder holding UI sprite replacements; searched recursively for <c>.png</c>.</summary>
        public const string UiFolder = "ui/";

        /// <summary>The UI sprite rules, inside <see cref="UiFolder"/>.</summary>
        public const string UiSpritesPath = "ui/_sprites.json";

        private static readonly PackFile[] NoFiles = new PackFile[0];

        private readonly Dictionary<PackContentKind, List<PackFile>> _byKind;

        private PackContentIndex(IReadOnlyList<PackFile> files, Dictionary<PackContentKind, List<PackFile>> byKind)
        {
            Files = files;
            _byKind = byKind;
        }

        /// <summary>An index over no files at all.</summary>
        public static PackContentIndex Empty { get; } =
            new PackContentIndex(NoFiles, new Dictionary<PackContentKind, List<PackFile>>());

        /// <summary>Every file in the pack, ordinally ordered by path.</summary>
        public IReadOnlyList<PackFile> Files { get; }

        /// <summary>Number of files in the pack.</summary>
        public int Count => Files.Count;

        /// <summary>Textures under <c>textures/</c>.</summary>
        public IReadOnlyList<PackFile> Textures => OfKind(PackContentKind.Texture);

        /// <summary>Voice clips under <c>audio/voice/</c>.</summary>
        public IReadOnlyList<PackFile> Voices => OfKind(PackContentKind.Voice);

        /// <summary>Music tracks under <c>audio/music/</c>.</summary>
        public IReadOnlyList<PackFile> Music => OfKind(PackContentKind.Music);

        /// <summary>Files the conventions do not recognise. Carried for diagnostics, never loaded.</summary>
        public IReadOnlyList<PackFile> Other => OfKind(PackContentKind.Other);

        /// <summary><c>data/localization.json</c>, or null when the pack has none.</summary>
        public PackFile? Localization => Single(PackContentKind.Localization);

        /// <summary><c>data/novel.json</c>, or null when the pack has none.</summary>
        public PackFile? Novel => Single(PackContentKind.Novel);

        /// <summary><c>data/scenario_groups.json</c>, or null when the pack has none.</summary>
        public PackFile? ScenarioGroups => Single(PackContentKind.ScenarioGroups);

        /// <summary><c>language.json</c>, or null when the pack declares no language.</summary>
        public PackFile? Language => Single(PackContentKind.Language);

        /// <summary>EditData files under <c>data/edit/</c>, ordinally ordered by path.</summary>
        public IReadOnlyList<PackFile> EditData => OfKind(PackContentKind.EditData);

        /// <summary>Ambient-loop replacements under <c>audio/ambient/</c>.</summary>
        public IReadOnlyList<PackFile> AmbientAudio => OfKind(PackContentKind.AmbientAudio);

        /// <summary>Ambient one-shot replacements under <c>audio/ambient_se/</c>.</summary>
        public IReadOnlyList<PackFile> AmbientSeAudio => OfKind(PackContentKind.AmbientSeAudio);

        /// <summary>System-sound replacements under <c>audio/se/system/</c>.</summary>
        public IReadOnlyList<PackFile> SystemSeAudio => OfKind(PackContentKind.SystemSeAudio);

        /// <summary>Timer-sound replacements under <c>audio/se/timer/</c>.</summary>
        public IReadOnlyList<PackFile> TimerSeAudio => OfKind(PackContentKind.TimerSeAudio);

        /// <summary>Story-music replacements under <c>audio/direction_music/</c>.</summary>
        public IReadOnlyList<PackFile> DirectionMusicAudio => OfKind(PackContentKind.DirectionMusicAudio);

        /// <summary><c>audio/music/_tracks.json</c>, or null when the pack has none.</summary>
        public PackFile? MusicTracks => Single(PackContentKind.MusicTracks);

        /// <summary>UI sprite replacements under <c>ui/</c> (any depth).</summary>
        public IReadOnlyList<PackFile> UiSprites => OfKind(PackContentKind.UiSprite);

        /// <summary><c>ui/_sprites.json</c>, or null when the pack has none.</summary>
        public PackFile? UiSpriteRules => Single(PackContentKind.UiSprites);

        /// <summary><c>data/triggers.json</c>, or null when the pack has none.</summary>
        public PackFile? Triggers => Single(PackContentKind.Triggers);

        /// <summary>True when the listing contained a root <c>manifest.json</c>.</summary>
        public bool HasManifest => Single(PackContentKind.Manifest) != null;

        /// <summary>Builds an index from a listing of paths relative to the pack root.</summary>
        /// <param name="relativePaths">
        /// Relative paths in any separator style; null, blank and duplicate entries are dropped.
        /// Null itself yields <see cref="Empty"/>.
        /// </param>
        public static PackContentIndex Build(IEnumerable<string?>? relativePaths)
        {
            if (relativePaths is null)
            {
                return Empty;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<PackFile>();

            foreach (var raw in relativePaths)
            {
                var path = Normalise(raw);
                if (path is null || !seen.Add(path))
                {
                    continue;
                }

                files.Add(new PackFile(path, Classify(path)));
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

            var byKind = new Dictionary<PackContentKind, List<PackFile>>();
            foreach (var file in files)
            {
                if (!byKind.TryGetValue(file.Kind, out var bucket))
                {
                    bucket = new List<PackFile>();
                    byKind.Add(file.Kind, bucket);
                }

                bucket.Add(file);
            }

            return new PackContentIndex(files, byKind);
        }

        /// <summary>Every file of one kind, ordinally ordered by path. Never null.</summary>
        /// <param name="kind">The kind to filter by.</param>
        public IReadOnlyList<PackFile> OfKind(PackContentKind kind) =>
            _byKind.TryGetValue(kind, out var bucket) ? bucket : (IReadOnlyList<PackFile>)NoFiles;

        /// <summary>
        /// Classifies one already-normalised relative path. Exposed so a loader can explain why a
        /// file it was handed is treated the way it is.
        /// </summary>
        /// <param name="normalisedPath">A forward-slash relative path, as produced by <see cref="Build"/>.</param>
        public static PackContentKind Classify(string normalisedPath)
        {
            if (string.IsNullOrEmpty(normalisedPath))
            {
                return PackContentKind.Other;
            }

            if (Matches(normalisedPath, ManifestFileName))
            {
                return PackContentKind.Manifest;
            }

            if (StartsWith(normalisedPath, TexturesFolder) && EndsWith(normalisedPath, ".png"))
            {
                return PackContentKind.Texture;
            }

            if (IsDirectChildOf(normalisedPath, VoiceFolder) &&
                (EndsWith(normalisedPath, ".ogg") || EndsWith(normalisedPath, ".wav")))
            {
                return PackContentKind.Voice;
            }

            // The track list lives among the tracks; it must win over the folder rule below.
            if (Matches(normalisedPath, MusicTracksPath))
            {
                return PackContentKind.MusicTracks;
            }

            if (IsDirectChildOf(normalisedPath, MusicFolder))
            {
                return PackContentKind.Music;
            }

            if (IsDirectAudioChildOf(normalisedPath, AmbientFolder)) { return PackContentKind.AmbientAudio; }

            if (IsDirectAudioChildOf(normalisedPath, AmbientSeFolder)) { return PackContentKind.AmbientSeAudio; }

            if (IsDirectAudioChildOf(normalisedPath, SystemSeFolder)) { return PackContentKind.SystemSeAudio; }

            if (IsDirectAudioChildOf(normalisedPath, TimerSeFolder)) { return PackContentKind.TimerSeAudio; }

            if (IsDirectAudioChildOf(normalisedPath, DirectionMusicFolder)) { return PackContentKind.DirectionMusicAudio; }

            if (Matches(normalisedPath, LocalizationPath)) { return PackContentKind.Localization; }

            if (Matches(normalisedPath, NovelPath)) { return PackContentKind.Novel; }

            if (Matches(normalisedPath, ScenarioGroupsPath)) { return PackContentKind.ScenarioGroups; }

            if (Matches(normalisedPath, TriggersPath)) { return PackContentKind.Triggers; }

            if (IsDirectChildOf(normalisedPath, EditDataFolder) && EndsWith(normalisedPath, ".json"))
            {
                return PackContentKind.EditData;
            }

            // The rules file lives among the sprites; it must win over the folder rule below.
            if (Matches(normalisedPath, UiSpritesPath)) { return PackContentKind.UiSprites; }

            if (StartsWith(normalisedPath, UiFolder) && EndsWith(normalisedPath, ".png"))
            {
                return PackContentKind.UiSprite;
            }

            if (Matches(normalisedPath, LanguagePath)) { return PackContentKind.Language; }

            return PackContentKind.Other;
        }

        /// <summary>
        /// Normalises one listing entry: backslashes become forward slashes and any leading
        /// <c>./</c> or <c>/</c> is dropped. Returns null for an entry that is not a usable
        /// relative path: null, blank, one that escapes the pack root with <c>..</c>, or one that
        /// would mean something else on Windows - a <c>:</c> anywhere (<c>C:/x</c> and <c>C:x</c> are
        /// rooted or drive-relative there; <c>a.txt:s</c> is a stream), a segment that is a device
        /// name (<c>con</c>, <c>nul.png</c>, <c>com1.ogg</c>) or ends in a dot or space
        /// (<see cref="SafeFileNames"/>).
        /// </summary>
        /// <param name="raw">A path as the caller produced it.</param>
        public static string? Normalise(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var path = raw!.Trim().Replace('\\', '/');

            // Checked before the leading "/" is stripped: "C:/x" must not become a relative "C:/x".
            if (path.IndexOf(':') >= 0)
            {
                return null;
            }

            while (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path.Substring(2);
            }

            path = path.TrimStart('/');

            if (path.Length == 0 || path.EndsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            // A listing should never contain "..", but a pack is untrusted input and a traversal
            // must not become a load path later on.
            if (path == ".." ||
                path.StartsWith("../", StringComparison.Ordinal) ||
                path.IndexOf("/../", StringComparison.Ordinal) >= 0 ||
                path.EndsWith("/..", StringComparison.Ordinal))
            {
                return null;
            }

            foreach (var segment in path.Split('/'))
            {
                if (SafeFileNames.ProblemWith(segment) != null)
                {
                    return null;
                }
            }

            return path;
        }

        private PackFile? Single(PackContentKind kind)
        {
            var bucket = OfKind(kind);
            return bucket.Count == 0 ? null : bucket[0];
        }

        private static bool Matches(string path, string expected) =>
            string.Equals(path, expected, StringComparison.OrdinalIgnoreCase);

        private static bool StartsWith(string path, string prefix) =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static bool EndsWith(string path, string suffix) =>
            path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        private static bool IsDirectChildOf(string path, string folder) =>
            StartsWith(path, folder) && path.IndexOf('/', folder.Length) < 0;

        /// <summary>A direct child of <paramref name="folder"/> that is an OGG or WAV clip.</summary>
        private static bool IsDirectAudioChildOf(string path, string folder) =>
            IsDirectChildOf(path, folder) && (EndsWith(path, ".ogg") || EndsWith(path, ".wav"));
    }
}
