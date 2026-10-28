using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>One preset file the game can apply.</summary>
    public sealed class GradingCatalogEntry
    {
        /// <summary>The source name used for the player's own presets (<c>grade save</c>).</summary>
        public const string UserSource = "user";

        /// <summary>Creates an entry.</summary>
        /// <param name="source"><see cref="UserSource"/>, or the id of the pack that ships the file.</param>
        /// <param name="stem">File name without <c>.json</c>, e.g. <c>warm-film</c>.</param>
        /// <param name="path">Full path of the file; the catalogue never reads it.</param>
        /// <param name="order">Load order among packs (later wins a plain-name lookup); ignored for user presets.</param>
        /// <exception cref="ArgumentException">A string is blank.</exception>
        public GradingCatalogEntry(string source, string stem, string path, int order)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("a catalogue entry needs a source, a name and a path");
            }

            Source = source;
            Stem = stem;
            Path = path;
            Order = order;
        }

        /// <summary><see cref="UserSource"/> or a pack id.</summary>
        public string Source { get; }

        /// <summary>File name without extension.</summary>
        public string Stem { get; }

        /// <summary>Full path of the file.</summary>
        public string Path { get; }

        /// <summary>Pack load order.</summary>
        public int Order { get; }

        /// <summary>True for the player's own preset.</summary>
        public bool IsUser => string.Equals(Source, UserSource, StringComparison.OrdinalIgnoreCase);

        /// <summary>The unambiguous name: <c>&lt;source&gt;:&lt;stem&gt;</c>.</summary>
        public string QualifiedName => Source + ":" + Stem;

        /// <inheritdoc />
        public override string ToString() => QualifiedName;
    }

    /// <summary>
    /// The presets the game knows: the player's own (<c>BepInEx/data/grading/</c>) and every loaded
    /// pack's <c>grading/*.json</c>. Resolves what <c>grade apply &lt;name&gt;</c> means.
    /// <para>
    /// A plain name (<c>warm-film</c>) picks the player's own preset of that name if there is one,
    /// otherwise the pack preset from the pack loaded last (the usual "later pack wins" rule); the
    /// lookup says which others it shadowed. A qualified name (<c>cwy.demo.scenery:warm-film</c>,
    /// <c>user:mine</c>) picks exactly that file. Case-insensitive throughout.
    /// </para>
    /// </summary>
    public sealed class GradingCatalog
    {
        private readonly List<GradingCatalogEntry> _entries;

        /// <summary>Builds a catalogue.</summary>
        /// <param name="entries">Every preset file found; null means none. Duplicate qualified names keep the last.</param>
        public GradingCatalog(IEnumerable<GradingCatalogEntry>? entries)
        {
            var byQualified = new Dictionary<string, GradingCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry is null)
                    {
                        continue;
                    }

                    if (!byQualified.ContainsKey(entry.QualifiedName))
                    {
                        order.Add(entry.QualifiedName);
                    }

                    byQualified[entry.QualifiedName] = entry;
                }
            }

            _entries = new List<GradingCatalogEntry>(order.Count);
            foreach (var name in order)
            {
                _entries.Add(byQualified[name]);
            }

            // User presets first, then packs in load order, each group by name.
            _entries.Sort((a, b) =>
            {
                if (a.IsUser != b.IsUser)
                {
                    return a.IsUser ? -1 : 1;
                }

                var byOrder = a.IsUser ? 0 : a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : string.Compare(a.Stem, b.Stem, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>An empty catalogue.</summary>
        public static GradingCatalog Empty { get; } = new GradingCatalog(null);

        /// <summary>Every entry: user presets first, then packs in load order. Never null.</summary>
        public IReadOnlyList<GradingCatalogEntry> Entries => _entries;

        /// <summary>Finds the preset a name means.</summary>
        /// <param name="name">A plain or qualified name; a trailing <c>.json</c> is ignored.</param>
        /// <param name="shadowed">The other entries with the same plain name that lost; empty when none.</param>
        /// <returns>The entry, or null when nothing matches.</returns>
        public GradingCatalogEntry? Find(string? name, out IReadOnlyList<GradingCatalogEntry> shadowed)
        {
            var none = new GradingCatalogEntry[0];
            shadowed = none;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var key = name!.Trim();
            if (key.EndsWith(GradingFormat.Extension, StringComparison.OrdinalIgnoreCase))
            {
                key = key.Substring(0, key.Length - GradingFormat.Extension.Length);
            }

            var colon = key.IndexOf(':');
            if (colon > 0)
            {
                foreach (var entry in _entries)
                {
                    if (string.Equals(entry.QualifiedName, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }

                return null;
            }

            GradingCatalogEntry? best = null;
            var losers = new List<GradingCatalogEntry>();
            foreach (var entry in _entries)
            {
                if (!string.Equals(entry.Stem, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (best is null || Beats(entry, best))
                {
                    if (best != null)
                    {
                        losers.Add(best);
                    }

                    best = entry;
                }
                else
                {
                    losers.Add(entry);
                }
            }

            shadowed = losers.Count == 0 ? none : losers.ToArray();
            return best;
        }

        private static bool Beats(GradingCatalogEntry candidate, GradingCatalogEntry current)
        {
            if (candidate.IsUser != current.IsUser)
            {
                return candidate.IsUser;
            }

            return candidate.Order > current.Order;
        }
    }
}
