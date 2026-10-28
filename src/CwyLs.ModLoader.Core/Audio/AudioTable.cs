using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>
    /// The game's sound tables a pack can replace clips in. Each is one list on
    /// <c>Bulbul.MasterDataLoader</c> whose rows carry an <c>AudioClip</c> field (game 1.17.3).
    /// </summary>
    /// <remarks>
    /// Members are appended, never renumbered: the value reaches trace records.
    /// </remarks>
    public enum AudioTable
    {
        /// <summary>Ambient loops (<c>AmbientMasterList</c>, rows keyed by <c>AmbientSoundType</c>): rain, wind, radio noise.</summary>
        Ambient = 0,

        /// <summary>Ambient one-shots (<c>AmbientSeMasterList</c>, keyed by <c>AmbientSeType</c>): keyboard, cup, pages.</summary>
        AmbientSe = 1,

        /// <summary>System sounds (<c>SystemSeMasterList</c>, keyed by <c>SystemSeType</c>): click, cancel, level up.</summary>
        SystemSe = 2,

        /// <summary>Timer sounds (<c>TimerSeMasterList</c>, keyed by <c>TimerSeType</c>): start, finish, pause.</summary>
        TimerSe = 3,

        /// <summary>Story ("direction") music (<c>DirectionMusicDataList</c>, keyed by <c>Title</c>).</summary>
        DirectionMusic = 4,
    }

    /// <summary>Names, folders and aliases of the <see cref="AudioTable"/> values.</summary>
    public static class AudioTables
    {
        private static readonly AudioTable[] AllTables =
        {
            AudioTable.Ambient, AudioTable.AmbientSe, AudioTable.SystemSe, AudioTable.TimerSe, AudioTable.DirectionMusic,
        };

        /// <summary>Every table, in declaration order.</summary>
        public static IReadOnlyList<AudioTable> All => AllTables;

        /// <summary>
        /// The short id used in row keys, console commands and logs: <c>ambient</c>, <c>ambient_se</c>,
        /// <c>system_se</c>, <c>timer_se</c>, <c>direction_music</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        public static string Id(AudioTable table)
        {
            switch (table)
            {
                case AudioTable.Ambient: return "ambient";
                case AudioTable.AmbientSe: return "ambient_se";
                case AudioTable.SystemSe: return "system_se";
                case AudioTable.TimerSe: return "timer_se";
                case AudioTable.DirectionMusic: return "direction_music";
                default: return table.ToString().ToLowerInvariant();
            }
        }

        /// <summary>The pack folder whose direct <c>.ogg</c>/<c>.wav</c> children replace this table's clips.</summary>
        /// <param name="table">The table.</param>
        public static string FolderOf(AudioTable table)
        {
            switch (table)
            {
                case AudioTable.Ambient: return PackContentIndex.AmbientFolder;
                case AudioTable.AmbientSe: return PackContentIndex.AmbientSeFolder;
                case AudioTable.SystemSe: return PackContentIndex.SystemSeFolder;
                case AudioTable.TimerSe: return PackContentIndex.TimerSeFolder;
                case AudioTable.DirectionMusic: return PackContentIndex.DirectionMusicFolder;
                default: throw new ArgumentOutOfRangeException(nameof(table), table, "not an audio table");
            }
        }

        /// <summary>The pack content kind the index gives this table's files.</summary>
        /// <param name="table">The table.</param>
        public static PackContentKind KindOf(AudioTable table)
        {
            switch (table)
            {
                case AudioTable.Ambient: return PackContentKind.AmbientAudio;
                case AudioTable.AmbientSe: return PackContentKind.AmbientSeAudio;
                case AudioTable.SystemSe: return PackContentKind.SystemSeAudio;
                case AudioTable.TimerSe: return PackContentKind.TimerSeAudio;
                case AudioTable.DirectionMusic: return PackContentKind.DirectionMusicAudio;
                default: throw new ArgumentOutOfRangeException(nameof(table), table, "not an audio table");
            }
        }

        /// <summary>
        /// True for the tables whose clips play as long-running loops (ambient beds, story music):
        /// a switch moves a playing loop to the new clip at the same position. One-shots simply use
        /// the new clip the next time they play.
        /// </summary>
        /// <param name="table">The table.</param>
        public static bool IsLoop(AudioTable table) =>
            table == AudioTable.Ambient || table == AudioTable.DirectionMusic;

        /// <summary>
        /// Reads a table from a console argument or a row key prefix. Accepts the id
        /// (<c>ambient_se</c>), the enum name (<c>AmbientSe</c>), and the aliases <c>ambientse</c>,
        /// <c>se</c>, <c>system</c>, <c>timer</c>, <c>direction</c>, <c>story</c>, <c>storymusic</c>;
        /// case-insensitive.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="table">The table, when recognised.</param>
        /// <returns>True when <paramref name="text"/> names a table.</returns>
        public static bool TryParse(string? text, out AudioTable table)
        {
            table = AudioTable.Ambient;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            switch (text!.Trim().ToLowerInvariant().Replace("-", "_"))
            {
                case "ambient":
                case "ambientbgm":
                    table = AudioTable.Ambient;
                    return true;
                case "ambient_se":
                case "ambientse":
                    table = AudioTable.AmbientSe;
                    return true;
                case "system_se":
                case "systemse":
                case "system":
                case "se":
                    table = AudioTable.SystemSe;
                    return true;
                case "timer_se":
                case "timerse":
                case "timer":
                    table = AudioTable.TimerSe;
                    return true;
                case "direction_music":
                case "directionmusic":
                case "direction":
                case "story":
                case "storymusic":
                case "story_music":
                    table = AudioTable.DirectionMusic;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The table whose pack files have this content kind.</summary>
        /// <param name="kind">A pack content kind.</param>
        /// <param name="table">The table, when the kind is one of the five replacement kinds.</param>
        /// <returns>True when <paramref name="kind"/> belongs to a table.</returns>
        public static bool TryFromKind(PackContentKind kind, out AudioTable table)
        {
            foreach (var candidate in AllTables)
            {
                if (KindOf(candidate) == kind)
                {
                    table = candidate;
                    return true;
                }
            }

            table = AudioTable.Ambient;
            return false;
        }
    }
}
