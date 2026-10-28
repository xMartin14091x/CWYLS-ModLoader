using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>
    /// The UUIDs pack music gets: <c>cwy-</c> plus 32 hex digits of SHA-256 over the pack id and the
    /// file's path. Stable across launches (so favourites can be remembered), different for every
    /// pack file, and impossible to confuse with the game's GUIDs (which never contain <c>w</c> or
    /// <c>y</c>).
    /// </summary>
    public static class PackMusicUuid
    {
        /// <summary>Every pack-music UUID starts with this.</summary>
        public const string Prefix = "cwy-";

        /// <summary>The UUID of one pack file. Case-insensitive in both inputs.</summary>
        /// <param name="packId">The pack id.</param>
        /// <param name="relativePath">The path inside the pack, e.g. <c>audio/music/rainy-desk.ogg</c>.</param>
        public static string For(string packId, string relativePath)
        {
            var text = (packId ?? string.Empty).Trim().ToLowerInvariant() + "|"
                + (relativePath ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();

            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            }

            var sb = new StringBuilder(Prefix.Length + 32);
            sb.Append(Prefix);
            for (var i = 0; i < 16; i++)
            {
                sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        /// <summary>True for a pack-music UUID (case-insensitive prefix), the ones a vanilla save must never contain.</summary>
        /// <param name="uuid">Any UUID.</param>
        public static bool IsPackUuid(string? uuid) =>
            uuid != null && uuid.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One pack music track the framework will add to the game's music list.</summary>
    public sealed class PackMusicTrack
    {
        internal PackMusicTrack(string packId, string relativePath, string title, string credit, PackMusicTag tag, int packOrder)
        {
            PackId = packId;
            RelativePath = relativePath;
            Title = title;
            Credit = credit;
            Tag = tag;
            PackOrder = packOrder;
            Uuid = PackMusicUuid.For(packId, relativePath);
        }

        /// <summary>The pack.</summary>
        public string PackId { get; }

        /// <summary>The file inside the pack.</summary>
        public string RelativePath { get; }

        /// <summary>The title.</summary>
        public string Title { get; }

        /// <summary>The credit; may be empty.</summary>
        public string Credit { get; }

        /// <summary>The category.</summary>
        public PackMusicTag Tag { get; }

        /// <summary>The pack's load-order position.</summary>
        public int PackOrder { get; }

        /// <summary>The <c>cwy-…</c> UUID.</summary>
        public string Uuid { get; }

        /// <inheritdoc />
        public override string ToString() => "'" + Title + "' <- " + PackId + "/" + RelativePath;
    }

    /// <summary>A pack's music input: its index and the text of its <c>_tracks.json</c>.</summary>
    public sealed class PackMusicInput
    {
        /// <summary>Creates an input.</summary>
        /// <param name="packId">The pack id.</param>
        /// <param name="index">The pack's content index.</param>
        /// <param name="tracksJson">The text of <c>audio/music/_tracks.json</c>, or null when the pack has none.</param>
        /// <param name="readError">Why the file could not be read, when it exists but failed to read.</param>
        public PackMusicInput(string packId, PackContentIndex index, string? tracksJson, string? readError = null)
        {
            PackId = packId ?? string.Empty;
            Index = index ?? PackContentIndex.Empty;
            TracksJson = tracksJson;
            ReadError = readError;
        }

        /// <summary>The pack id.</summary>
        public string PackId { get; }

        /// <summary>The content index.</summary>
        public PackContentIndex Index { get; }

        /// <summary>The track list's text, or null.</summary>
        public string? TracksJson { get; }

        /// <summary>A read failure of the track list, or null.</summary>
        public string? ReadError { get; }
    }

    /// <summary>A music file or track-list entry that is not used.</summary>
    public sealed class PackMusicSkip
    {
        internal PackMusicSkip(string packId, string path, string reason, bool isPaid, bool isWarning)
        {
            PackId = packId;
            Path = path;
            Reason = reason;
            IsPaid = isPaid;
            IsWarning = isWarning;
        }

        /// <summary>The pack.</summary>
        public string PackId { get; }

        /// <summary>The file or <c>audio/music/_tracks.json</c>.</summary>
        public string Path { get; }

        /// <summary>One line for people.</summary>
        public string Reason { get; }

        /// <summary>True when refused as paid content.</summary>
        public bool IsPaid { get; }

        /// <summary>True when worth a console warning (false for READMEs and dot files in the folder).</summary>
        public bool IsWarning { get; }

        /// <inheritdoc />
        public override string ToString() => PackId + "/" + Path + ": " + Reason;
    }

    /// <summary>
    /// Which pack music files become tracks, with which title, credit, category and UUID.
    /// </summary>
    /// <remarks>
    /// Every direct <c>.ogg</c>/<c>.wav</c> child of <c>audio/music/</c> is a track, titled by its
    /// <c>_tracks.json</c> entry or by its file name. Other audio formats are skipped with a hint to
    /// convert them; notes and dot files are ignored quietly. A track that
    /// re-lists one of the game's tracks is refused (<see cref="AudioPaidContent.CheckMusicTrack"/>).
    /// </remarks>
    public sealed class PackMusicPlan
    {
        /// <summary>Most pack tracks one session adds; the rest are skipped with a warning.</summary>
        public const int MaxTracks = 200;

        private static readonly string[] OtherAudioExtensions = { ".mp3", ".flac", ".m4a", ".aac", ".opus", ".aif", ".aiff", ".wma" };

        private PackMusicPlan(IReadOnlyList<PackMusicTrack> tracks, IReadOnlyList<PackMusicSkip> skipped)
        {
            Tracks = tracks;
            Skipped = skipped;
        }

        /// <summary>A plan with nothing in it.</summary>
        public static PackMusicPlan Empty { get; } = new PackMusicPlan(new PackMusicTrack[0], new PackMusicSkip[0]);

        /// <summary>The tracks, in pack load order then path order.</summary>
        public IReadOnlyList<PackMusicTrack> Tracks { get; }

        /// <summary>What was not used.</summary>
        public IReadOnlyList<PackMusicSkip> Skipped { get; }

        /// <summary>How many were refused as paid content.</summary>
        public int RefusedCount
        {
            get
            {
                var count = 0;
                foreach (var skip in Skipped)
                {
                    if (skip.IsPaid)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Builds the plan.</summary>
        /// <param name="packs">Each pack's input, in load order.</param>
        /// <param name="soundtrack">The game's music list, for the paid check.</param>
        /// <returns>The plan; never null.</returns>
        public static PackMusicPlan Build(IEnumerable<PackMusicInput>? packs, SoundtrackCatalog? soundtrack)
        {
            var tracks = new List<PackMusicTrack>();
            var skipped = new List<PackMusicSkip>();
            if (packs is null)
            {
                return new PackMusicPlan(tracks, skipped);
            }

            var catalog = soundtrack ?? SoundtrackCatalog.Empty;
            var order = 0;
            foreach (var pack in packs)
            {
                if (pack is null)
                {
                    order++;
                    continue;
                }

                var entries = new Dictionary<string, MusicTrackEntry>(StringComparer.OrdinalIgnoreCase);
                if (pack.ReadError != null)
                {
                    skipped.Add(new PackMusicSkip(pack.PackId, PackContentIndex.MusicTracksPath, "could not be read: " + pack.ReadError, false, true));
                }
                else if (pack.TracksJson != null)
                {
                    var list = MusicTrackList.Parse(pack.TracksJson);
                    foreach (var error in list.Errors)
                    {
                        skipped.Add(new PackMusicSkip(pack.PackId, PackContentIndex.MusicTracksPath, error, false, true));
                    }

                    foreach (var entry in list.Entries)
                    {
                        entries[entry.File] = entry;
                    }
                }

                var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in pack.Index.Music)
                {
                    listed.Add(file.FileName);
                    if (file.Extension != ".ogg" && file.Extension != ".wav")
                    {
                        var isAudio = Array.IndexOf(OtherAudioExtensions, file.Extension) >= 0;
                        if (isAudio || entries.ContainsKey(file.FileName))
                        {
                            skipped.Add(new PackMusicSkip(
                                pack.PackId, file.Path,
                                "unsupported format " + file.Extension + "; convert it to .ogg or .wav",
                                false, true));
                        }

                        continue;
                    }

                    entries.TryGetValue(file.FileName, out var entry);
                    var title = entry?.Title ?? file.Name;
                    var refusal = AudioPaidContent.CheckMusicTrack(title, file.Name, catalog);
                    if (refusal != null)
                    {
                        skipped.Add(new PackMusicSkip(pack.PackId, file.Path, refusal, true, true));
                        continue;
                    }

                    if (tracks.Count >= MaxTracks)
                    {
                        skipped.Add(new PackMusicSkip(pack.PackId, file.Path, "more than " + MaxTracks + " pack tracks; not added", false, true));
                        continue;
                    }

                    tracks.Add(new PackMusicTrack(pack.PackId, file.Path, title, entry?.Credit ?? string.Empty, entry?.Tag ?? PackMusicTag.Other, order));
                }

                foreach (var entry in entries.Values)
                {
                    if (!listed.Contains(entry.File))
                    {
                        skipped.Add(new PackMusicSkip(
                            pack.PackId, PackContentIndex.MusicTracksPath,
                            "lists '" + entry.File + "', which is not in audio/music/", false, true));
                    }
                }

                order++;
            }

            return new PackMusicPlan(tracks, skipped);
        }

        /// <summary>The track with this UUID, or null.</summary>
        /// <param name="uuid">A <c>cwy-…</c> UUID.</param>
        public PackMusicTrack? Find(string? uuid)
        {
            foreach (var track in Tracks)
            {
                if (string.Equals(track.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
                {
                    return track;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Where pack tracks go in the game's music list at load: at a remembered playlist position when
    /// there is one, otherwise after every game track - never first, which would make a pack track
    /// the one that starts playing at launch.
    /// </summary>
    public static class PackMusicPlacement
    {
        /// <summary>
        /// Merges pack UUIDs into the game's ordered list.
        /// </summary>
        /// <param name="gameOrder">The game's tracks in the order it is about to load them (sorted by its saved playlist order).</param>
        /// <param name="playlistOrder">The saved playlist order, with remembered pack UUIDs re-inserted.</param>
        /// <param name="packUuids">The pack tracks to place, in pack order.</param>
        /// <returns>The merged order; every input UUID exactly once.</returns>
        public static IReadOnlyList<string> Place(
            IReadOnlyList<string>? gameOrder,
            IReadOnlyList<string>? playlistOrder,
            IReadOnlyList<string>? packUuids)
        {
            var result = new List<string>(gameOrder ?? Array.Empty<string>());
            if (packUuids is null || packUuids.Count == 0)
            {
                return result;
            }

            var order = playlistOrder ?? Array.Empty<string>();
            var positioned = new List<KeyValuePair<int, string>>();
            var unpositioned = new List<string>();
            var seen = new HashSet<string>(result, StringComparer.Ordinal);

            foreach (var uuid in packUuids)
            {
                if (uuid is null || !seen.Add(uuid))
                {
                    continue;
                }

                var index = IndexOf(order, uuid);
                if (index >= 0)
                {
                    positioned.Add(new KeyValuePair<int, string>(index, uuid));
                }
                else
                {
                    unpositioned.Add(uuid);
                }
            }

            // Stable by remembered index, then by pack order.
            positioned.Sort((a, b) => a.Key.CompareTo(b.Key));

            foreach (var pair in positioned)
            {
                var insertAt = result.Count;
                for (var i = 0; i < result.Count; i++)
                {
                    if (IndexOf(order, result[i]) > pair.Key)
                    {
                        insertAt = i;
                        break;
                    }
                }

                result.Insert(insertAt, pair.Value);
            }

            result.AddRange(unpositioned);
            return result;
        }

        private static int IndexOf(IReadOnlyList<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
