using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>
    /// One row of a game sound table, as plain data: which table, its name (the enum value such as
    /// <c>LightRain</c>, or a story-music title), and the clip the game ships for it. The adapter
    /// builds these from <c>MasterDataLoader</c>; the planning code never sees a game type.
    /// </summary>
    public sealed class AudioRowDescriptor
    {
        /// <summary>Creates a row.</summary>
        /// <param name="table">The table it belongs to.</param>
        /// <param name="name">The row's name: the enum value name, or the story-music title. Required.</param>
        /// <param name="originalClipName">The name of the clip the game ships (the KanKikuchi managers look players up by it); empty when the row has none.</param>
        /// <param name="originalSeconds">Length of that clip in seconds; 0 when unknown.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
        public AudioRowDescriptor(AudioTable table, string name, string? originalClipName, double originalSeconds)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a row name is required", nameof(name));
            }

            Table = table;
            Name = name;
            OriginalClipName = originalClipName ?? string.Empty;
            OriginalSeconds = originalSeconds > 0 && !double.IsInfinity(originalSeconds) ? originalSeconds : 0;
            Key = KeyOf(table, name);
        }

        /// <summary>The table.</summary>
        public AudioTable Table { get; }

        /// <summary>The row's name, e.g. <c>LightRain</c>.</summary>
        public string Name { get; }

        /// <summary>The name of the game's own clip, e.g. <c>Rainthunder</c> for <c>ThunderRain</c>.</summary>
        public string OriginalClipName { get; }

        /// <summary>The game's clip length in seconds; 0 when unknown.</summary>
        public double OriginalSeconds { get; }

        /// <summary>The row key: <c>&lt;table id&gt;/&lt;name&gt;</c>, e.g. <c>ambient/LightRain</c>.</summary>
        public string Key { get; }

        /// <summary>The key of a row: <c>&lt;table id&gt;/&lt;name&gt;</c>.</summary>
        /// <param name="table">The table.</param>
        /// <param name="name">The row name.</param>
        public static string KeyOf(AudioTable table, string name) => AudioTables.Id(table) + "/" + name;

        /// <inheritdoc />
        public override string ToString() => Key + " (" + OriginalClipName + ")";
    }

    /// <summary>
    /// A pack file offering a replacement clip for a row: <c>audio/ambient/LightRain.ogg</c> claims
    /// the ambient row named <c>LightRain</c> (or the ambient row whose game clip is called
    /// <c>LightRain</c>).
    /// </summary>
    public sealed class AudioCandidate
    {
        /// <summary>Creates a candidate.</summary>
        /// <param name="table">The table the folder belongs to.</param>
        /// <param name="packId">The pack that ships the file.</param>
        /// <param name="file">The file, as the pack index classified it.</param>
        /// <param name="packOrder">The pack's position in load order (later packs win).</param>
        /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="packId"/> is blank.</exception>
        public AudioCandidate(AudioTable table, string packId, PackFile file, int packOrder)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (file is null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            Table = table;
            PackId = packId;
            RelativePath = file.Path;
            Stem = file.Name;
            Extension = file.Extension;
            PackOrder = packOrder;
        }

        /// <summary>The table.</summary>
        public AudioTable Table { get; }

        /// <summary>The pack id.</summary>
        public string PackId { get; }

        /// <summary>The file's path inside the pack, e.g. <c>audio/ambient/LightRain.wav</c>.</summary>
        public string RelativePath { get; }

        /// <summary>The file name without extension: the row it claims.</summary>
        public string Stem { get; }

        /// <summary>Lower-cased extension with the dot, <c>.ogg</c> or <c>.wav</c>.</summary>
        public string Extension { get; }

        /// <summary>The pack's position in load order.</summary>
        public int PackOrder { get; }

        /// <summary><c>&lt;pack&gt;/&lt;path&gt;</c>, for logs.</summary>
        public string Source => PackId + "/" + RelativePath;

        /// <summary>
        /// Every replacement file of the five tables in the given packs, in load order (then the
        /// index's ordinal path order inside a pack).
        /// </summary>
        /// <param name="packs">Pack id and content index, in load order.</param>
        /// <returns>The candidates; never null.</returns>
        public static IReadOnlyList<AudioCandidate> FromPacks(IEnumerable<KeyValuePair<string, PackContentIndex>>? packs)
        {
            var candidates = new List<AudioCandidate>();
            if (packs is null)
            {
                return candidates;
            }

            var order = 0;
            foreach (var pack in packs)
            {
                if (pack.Value is null || string.IsNullOrWhiteSpace(pack.Key))
                {
                    order++;
                    continue;
                }

                foreach (var table in AudioTables.All)
                {
                    foreach (var file in pack.Value.OfKind(AudioTables.KindOf(table)))
                    {
                        candidates.Add(new AudioCandidate(table, pack.Key, file, order));
                    }
                }

                order++;
            }

            return candidates;
        }

        /// <inheritdoc />
        public override string ToString() => Source;
    }
}
