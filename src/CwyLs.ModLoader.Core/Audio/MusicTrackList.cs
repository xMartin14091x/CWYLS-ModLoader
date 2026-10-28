using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>The game's music-list category a pack track is filed under (the game's <c>AudioTag</c>).</summary>
    public enum PackMusicTag
    {
        /// <summary><c>AudioTag.Other</c> (4). The default.</summary>
        Other = 4,

        /// <summary><c>AudioTag.Special</c> (2).</summary>
        Special = 2,

        /// <summary><c>AudioTag.Original</c> (1).</summary>
        Original = 1,
    }

    /// <summary>One entry of <c>audio/music/_tracks.json</c>.</summary>
    public sealed class MusicTrackEntry
    {
        internal MusicTrackEntry(string file, string title, string credit, PackMusicTag tag)
        {
            File = file;
            Title = title;
            Credit = credit;
            Tag = tag;
        }

        /// <summary>The file name inside <c>audio/music/</c>, e.g. <c>rainy-desk.ogg</c>.</summary>
        public string File { get; }

        /// <summary>The title the music list shows.</summary>
        public string Title { get; }

        /// <summary>The artist credit; may be empty.</summary>
        public string Credit { get; }

        /// <summary>The category.</summary>
        public PackMusicTag Tag { get; }
    }

    /// <summary>What <see cref="MusicTrackList.Parse"/> produced: the entries and every problem.</summary>
    public sealed class MusicTrackListResult
    {
        internal MusicTrackListResult(IReadOnlyList<MusicTrackEntry> entries, IReadOnlyList<string> errors)
        {
            Entries = entries;
            Errors = errors;
        }

        /// <summary>The valid entries, in file order.</summary>
        public IReadOnlyList<MusicTrackEntry> Entries { get; }

        /// <summary>One line per problem (<c>tracks[1].title: ...</c>); a bad entry is dropped, the rest are kept.</summary>
        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>
    /// Parses a pack's <c>audio/music/_tracks.json</c>: titles, credits and categories for the
    /// pack's music files.
    /// </summary>
    /// <remarks>
    /// <code>
    /// { "schema": 1,
    ///   "tracks": [ { "file": "rainy-desk.ogg", "title": "Rainy Desk", "credit": "CWY demo", "tag": "other" } ] }
    /// </code>
    /// A bare array of entries is accepted too. Members starting <c>x-</c> are the author's notes and
    /// ignored; any other unknown member is an error (a misspelled <c>tittle</c> must not pass
    /// silently). A music file the list does not name is still added, titled after its file name.
    /// </remarks>
    public static class MusicTrackList
    {
        /// <summary>The schema this parser reads.</summary>
        public const int Schema = 1;

        /// <summary>Longest title or credit accepted.</summary>
        public const int MaxTextLength = 120;

        /// <summary>Parses the document.</summary>
        /// <param name="json">The file's text.</param>
        /// <returns>The entries and the problems; never null.</returns>
        public static MusicTrackListResult Parse(string? json)
        {
            var entries = new List<MusicTrackEntry>();
            var errors = new List<string>();

            if (json is null)
            {
                errors.Add("no text");
                return new MusicTrackListResult(entries, errors);
            }

            if (!JsonReader.TryParse(json, out var root, out var parseError))
            {
                errors.Add(parseError ?? "not JSON");
                return new MusicTrackListResult(entries, errors);
            }

            IList<object?>? tracks = null;
            if (root is JsonObject obj)
            {
                foreach (var pair in obj)
                {
                    switch (pair.Key)
                    {
                        case "schema":
                            if (!(pair.Value is long schema) || schema != Schema)
                            {
                                errors.Add("schema: must be " + Schema + ", not " + Describe(pair.Value));
                            }

                            break;
                        case "tracks":
                            tracks = pair.Value as IList<object?>;
                            if (tracks is null)
                            {
                                errors.Add("tracks: must be an array, not " + JsonReader.TypeName(pair.Value));
                            }

                            break;
                        default:
                            if (!pair.Key.StartsWith("x-", StringComparison.Ordinal))
                            {
                                errors.Add(pair.Key + ": unknown member (use an x- prefix for notes)");
                            }

                            break;
                    }
                }

                if (tracks is null && !obj.ContainsKey("tracks"))
                {
                    errors.Add("tracks: missing");
                }
            }
            else if (root is IList<object?> array)
            {
                tracks = array;
            }
            else
            {
                errors.Add("the top-level value must be an object or an array, not " + JsonReader.TypeName(root));
            }

            if (tracks is null)
            {
                return new MusicTrackListResult(entries, errors);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < tracks.Count; i++)
            {
                var at = "tracks[" + i + "]";
                if (!(tracks[i] is JsonObject entry))
                {
                    errors.Add(at + ": must be an object, not " + JsonReader.TypeName(tracks[i]));
                    continue;
                }

                var parsed = ParseEntry(entry, at, errors);
                if (parsed is null)
                {
                    continue;
                }

                if (!seen.Add(parsed.File))
                {
                    errors.Add(at + ".file: '" + parsed.File + "' is listed twice");
                    continue;
                }

                entries.Add(parsed);
            }

            return new MusicTrackListResult(entries, errors);
        }

        /// <summary>Reads a category name: <c>other</c>, <c>special</c> or <c>original</c>, case-insensitive.</summary>
        /// <param name="text">The text.</param>
        /// <param name="tag">The category.</param>
        /// <returns>True when recognised.</returns>
        public static bool TryParseTag(string? text, out PackMusicTag tag)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "other":
                    tag = PackMusicTag.Other;
                    return true;
                case "special":
                    tag = PackMusicTag.Special;
                    return true;
                case "original":
                    tag = PackMusicTag.Original;
                    return true;
                default:
                    tag = PackMusicTag.Other;
                    return false;
            }
        }

        private static MusicTrackEntry? ParseEntry(JsonObject entry, string at, List<string> errors)
        {
            string? file = null;
            string? title = null;
            var credit = string.Empty;
            var tag = PackMusicTag.Other;
            var ok = true;

            foreach (var pair in entry)
            {
                switch (pair.Key)
                {
                    case "file":
                        file = pair.Value as string;
                        break;
                    case "title":
                        title = pair.Value as string;
                        if (title is null)
                        {
                            errors.Add(at + ".title: must be a string, not " + JsonReader.TypeName(pair.Value));
                            ok = false;
                        }

                        break;
                    case "credit":
                        if (pair.Value is string text)
                        {
                            credit = text.Trim();
                        }
                        else if (pair.Value != null)
                        {
                            errors.Add(at + ".credit: must be a string, not " + JsonReader.TypeName(pair.Value));
                            ok = false;
                        }

                        break;
                    case "tag":
                        if (!(pair.Value is string tagText) || !TryParseTag(tagText, out tag))
                        {
                            errors.Add(at + ".tag: must be \"other\", \"special\" or \"original\", not " + Describe(pair.Value));
                            ok = false;
                        }

                        break;
                    case "uuid":
                        errors.Add(at + ".uuid: packs do not choose UUIDs; the framework derives cwy-… ids from the pack and file");
                        ok = false;
                        break;
                    default:
                        if (!pair.Key.StartsWith("x-", StringComparison.Ordinal))
                        {
                            errors.Add(at + "." + pair.Key + ": unknown member (use an x- prefix for notes)");
                            ok = false;
                        }

                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(file))
            {
                errors.Add(at + ".file: required (a file name inside audio/music/)");
                return null;
            }

            var normalised = PackContentIndex.Normalise(file);
            if (normalised is null || normalised.IndexOf('/') >= 0)
            {
                errors.Add(at + ".file: '" + file + "' must be a plain file name inside audio/music/ (no folders, no ':' or '..')");
                return null;
            }

            if (title != null)
            {
                title = title.Trim();
                if (title.Length == 0)
                {
                    errors.Add(at + ".title: must not be blank");
                    ok = false;
                }
                else if (title.Length > MaxTextLength)
                {
                    errors.Add(at + ".title: longer than " + MaxTextLength + " characters");
                    ok = false;
                }
            }

            if (credit.Length > MaxTextLength)
            {
                errors.Add(at + ".credit: longer than " + MaxTextLength + " characters");
                ok = false;
            }

            if (!ok)
            {
                return null;
            }

            return new MusicTrackEntry(normalised, title ?? StemOf(normalised), credit, tag);
        }

        internal static string StemOf(string fileName)
        {
            var dot = fileName.LastIndexOf('.');
            return dot <= 0 ? fileName : fileName.Substring(0, dot);
        }

        private static string Describe(object? value) =>
            value is string s ? "\"" + s + "\"" : value is long n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : JsonReader.TypeName(value);
    }
}
