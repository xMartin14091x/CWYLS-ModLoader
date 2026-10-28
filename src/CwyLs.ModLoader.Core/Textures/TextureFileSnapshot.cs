using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// What the texture files of the installed packs looked like at one moment: a path and the
    /// file's last-write stamp, for every PNG and <c>_rules.json</c> a pack ships.
    /// <para>
    /// This is the pure half of the live-reload loop. The Unity side reads the disk and applies
    /// the result; deciding <em>what moved</em> - which files changed, appeared or were deleted -
    /// happens here, where it is unit-tested off-engine.
    /// </para>
    /// </summary>
    public sealed class TextureFileSnapshot
    {
        /// <summary>Stamp value meaning "the file could not be stat'ed"; treated as its own value.</summary>
        public const long MissingStamp = 0L;

        private static readonly Dictionary<string, long> NoFiles = new Dictionary<string, long>(StringComparer.Ordinal);

        private readonly Dictionary<string, long> _files;

        private TextureFileSnapshot(Dictionary<string, long> files)
        {
            _files = files;
        }

        /// <summary>A reading with no files in it - the state before the first scan.</summary>
        public static TextureFileSnapshot Empty { get; } = new TextureFileSnapshot(NoFiles);

        /// <summary>
        /// Takes a reading.
        /// </summary>
        /// <param name="files">
        /// Path to last-write stamp (UTC ticks, or <see cref="MissingStamp"/>). Paths are compared
        /// ordinally; pass them in one consistent form. Null or empty yields <see cref="Empty"/>.
        /// A repeated path keeps the last stamp given.
        /// </param>
        public static TextureFileSnapshot Of(IEnumerable<KeyValuePair<string, long>>? files)
        {
            if (files is null)
            {
                return Empty;
            }

            var copy = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file.Key))
                {
                    continue;
                }

                copy[file.Key] = file.Value;
            }

            return copy.Count == 0 ? Empty : new TextureFileSnapshot(copy);
        }

        /// <summary>How many files this reading holds.</summary>
        public int Count => _files.Count;

        /// <summary>The files, path to stamp. A live view of an immutable snapshot; never null.</summary>
        public IReadOnlyDictionary<string, long> Files => _files;

        /// <summary>The stamp recorded for a path, or <see cref="MissingStamp"/> when it was not in this reading.</summary>
        /// <param name="path">A path in the same form the snapshot was built with.</param>
        public long StampOf(string? path) =>
            path != null && _files.TryGetValue(path, out var stamp) ? stamp : MissingStamp;

        /// <summary>
        /// What changed between an earlier reading and this one.
        /// </summary>
        /// <param name="previous">
        /// The earlier reading. Null is treated as <see cref="Empty"/>, which reports every file
        /// as new - correct for the first reload of a session.
        /// </param>
        public TextureFileDiff DiffFrom(TextureFileSnapshot? previous)
        {
            var before = previous ?? Empty;

            var changed = new List<string>();
            var added = new List<string>();
            var removed = new List<string>();

            foreach (var file in _files)
            {
                if (!before._files.TryGetValue(file.Key, out var was))
                {
                    added.Add(file.Key);
                }
                else if (was != file.Value)
                {
                    changed.Add(file.Key);
                }
            }

            foreach (var file in before._files)
            {
                if (!_files.ContainsKey(file.Key))
                {
                    removed.Add(file.Key);
                }
            }

            // Sorted so two runs over the same change produce the same message.
            changed.Sort(StringComparer.Ordinal);
            added.Sort(StringComparer.Ordinal);
            removed.Sort(StringComparer.Ordinal);

            return new TextureFileDiff(_files.Count, changed, added, removed);
        }
    }

    /// <summary>
    /// The difference between two <see cref="TextureFileSnapshot"/>s: how many files there are now,
    /// and which of them changed, appeared or went away.
    /// </summary>
    public sealed class TextureFileDiff
    {
        private static readonly string[] NoPaths = new string[0];

        /// <summary>How many paths a summary names before it says "and N more".</summary>
        public const int MaxNamedPaths = 6;

        /// <summary>Creates a difference.</summary>
        /// <param name="total">How many files the newer reading holds.</param>
        /// <param name="changed">Paths present in both readings with a different stamp.</param>
        /// <param name="added">Paths only in the newer reading.</param>
        /// <param name="removed">Paths only in the older reading.</param>
        public TextureFileDiff(
            int total,
            IReadOnlyList<string>? changed,
            IReadOnlyList<string>? added,
            IReadOnlyList<string>? removed)
        {
            Total = total < 0 ? 0 : total;
            Changed = changed ?? NoPaths;
            Added = added ?? NoPaths;
            Removed = removed ?? NoPaths;
        }

        /// <summary>A difference between two empty readings.</summary>
        public static TextureFileDiff None { get; } = new TextureFileDiff(0, NoPaths, NoPaths, NoPaths);

        /// <summary>How many texture files the packs hold now.</summary>
        public int Total { get; }

        /// <summary>Files that exist in both readings but were written in between, sorted.</summary>
        public IReadOnlyList<string> Changed { get; }

        /// <summary>Files that appeared, sorted.</summary>
        public IReadOnlyList<string> Added { get; }

        /// <summary>Files that were deleted or renamed away, sorted.</summary>
        public IReadOnlyList<string> Removed { get; }

        /// <summary>True when nothing moved on disk between the two readings.</summary>
        public bool IsEmpty => Changed.Count == 0 && Added.Count == 0 && Removed.Count == 0;

        /// <summary>
        /// The counts, as the reload's log line reports them:
        /// <c>3 file(s) (1 changed on disk, 0 new, 0 removed)</c>.
        /// </summary>
        public string Counts() =>
            $"{Total} file(s) ({Changed.Count} changed on disk, {Added.Count} new, {Removed.Count} removed)";

        /// <summary>The counts plus the paths that moved, capped at <see cref="MaxNamedPaths"/>.</summary>
        public string Summarize()
        {
            if (IsEmpty)
            {
                return Counts();
            }

            var named = new List<string>(MaxNamedPaths);
            var more = 0;

            Collect(Changed, "~", named, ref more);
            Collect(Added, "+", named, ref more);
            Collect(Removed, "-", named, ref more);

            var list = string.Join(", ", named.ToArray());
            return Counts() + ": " + (more == 0 ? list : list + " and " + more + " more");
        }

        /// <inheritdoc />
        public override string ToString() => Summarize();

        private static void Collect(IReadOnlyList<string> paths, string mark, List<string> named, ref int more)
        {
            foreach (var path in paths)
            {
                if (named.Count >= MaxNamedPaths)
                {
                    more++;
                    continue;
                }

                named.Add(mark + path);
            }
        }
    }
}
