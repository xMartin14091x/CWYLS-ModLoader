using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>One track of the game's own music list (<c>MusicMaster</c>), as plain data.</summary>
    public sealed class SoundtrackTrack
    {
        /// <summary>Creates a track.</summary>
        /// <param name="title">The title the music list shows.</param>
        /// <param name="credit">The artist credit.</param>
        /// <param name="clipName">The clip's asset name (often the file name, e.g. <c>Without You_main32_full</c>).</param>
        /// <param name="uuid">The track's UUID.</param>
        /// <param name="isOriginalSoundtrack">True when the game tags it <c>AudioTag.Original</c> (the original soundtrack).</param>
        public SoundtrackTrack(string? title, string? credit, string? clipName, string? uuid, bool isOriginalSoundtrack)
        {
            Title = title ?? string.Empty;
            Credit = credit ?? string.Empty;
            ClipName = clipName ?? string.Empty;
            Uuid = uuid ?? string.Empty;
            IsOriginalSoundtrack = isOriginalSoundtrack;
        }

        /// <summary>The title.</summary>
        public string Title { get; }

        /// <summary>The artist credit.</summary>
        public string Credit { get; }

        /// <summary>The clip's asset name.</summary>
        public string ClipName { get; }

        /// <summary>The UUID.</summary>
        public string Uuid { get; }

        /// <summary>True for the original-soundtrack tracks (<c>AudioTag.Original</c>).</summary>
        public bool IsOriginalSoundtrack { get; }

        /// <inheritdoc />
        public override string ToString() => "'" + Title + "'" + (Credit.Length > 0 ? " (" + Credit + ")" : string.Empty);
    }

    /// <summary>
    /// The game's own music, used to recognise paid content: the game's soundtrack is sold as a
    /// Steam DLC, so no pack may re-list one of its tracks or swap a story cue that plays one.
    /// </summary>
    public sealed class SoundtrackCatalog
    {
        /// <summary>Shortest normalised name a prefix match is allowed to use, so "a" cannot match everything.</summary>
        public const int MinPrefixLength = 4;

        private readonly List<SoundtrackTrack> _tracks;

        /// <summary>Creates a catalog.</summary>
        /// <param name="tracks">The game's tracks; null entries are dropped.</param>
        public SoundtrackCatalog(IEnumerable<SoundtrackTrack?>? tracks)
        {
            _tracks = new List<SoundtrackTrack>();
            if (tracks is null)
            {
                return;
            }

            foreach (var track in tracks)
            {
                if (track != null)
                {
                    _tracks.Add(track);
                }
            }
        }

        /// <summary>A catalog with no tracks: the game's music list has not been read.</summary>
        public static SoundtrackCatalog Empty { get; } = new SoundtrackCatalog(null);

        /// <summary>The tracks.</summary>
        public IReadOnlyList<SoundtrackTrack> Tracks => _tracks;

        /// <summary>True when the game's music list was read (it is never empty in a real game).</summary>
        public bool IsKnown => _tracks.Count > 0;

        /// <summary>How many tracks are tagged original soundtrack.</summary>
        public int OriginalCount
        {
            get
            {
                var count = 0;
                foreach (var track in _tracks)
                {
                    if (track.IsOriginalSoundtrack)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>The track whose title or clip name equals <paramref name="text"/> once normalised, or null.</summary>
        /// <param name="text">A title, clip name or file stem.</param>
        public SoundtrackTrack? FindExact(string? text)
        {
            var wanted = AudioPaidContent.Normalize(text);
            if (wanted.Length == 0)
            {
                return null;
            }

            foreach (var track in _tracks)
            {
                if (wanted == AudioPaidContent.Normalize(track.Title) || wanted == AudioPaidContent.Normalize(track.ClipName))
                {
                    return track;
                }
            }

            return null;
        }

        /// <summary>
        /// The track whose normalised title or clip name <paramref name="text"/> starts with (at least
        /// <see cref="MinPrefixLength"/> characters), or null: <c>Without You_main32_full</c> plays
        /// <c>Without You</c>.
        /// </summary>
        /// <param name="text">A story-music title or clip name.</param>
        public SoundtrackTrack? FindPrefixOf(string? text)
        {
            var wanted = AudioPaidContent.Normalize(text);
            if (wanted.Length == 0)
            {
                return null;
            }

            foreach (var track in _tracks)
            {
                foreach (var name in new[] { track.Title, track.ClipName })
                {
                    var normalised = AudioPaidContent.Normalize(name);
                    if (normalised.Length >= MinPrefixLength && wanted.StartsWith(normalised, StringComparison.Ordinal))
                    {
                        return track;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The paid-content rule for audio (project rule: no paid content is ever modded).
    /// </summary>
    /// <remarks>
    /// <para>
    /// How the game marks paid audio (game 1.17.3): the only DLC is the original soundtrack,
    /// <c>NestopiSystem.Steam.BulbulSteamDLC.DLC_1 = 3718890</c>. The game never asks for it in
    /// play - <c>NestopiSystem.Steam.SteamManager.IsInstalledDLC</c> has no caller - so the soundtrack's
    /// tracks are the game's own music list (<c>MusicMaster</c>, tag <c>Original</c> = 1 on 31 of 43
    /// rows) and the story cue that plays one (<c>DirectionMusicMaster</c>'s only row,
    /// <c>main32_Music</c> -&gt; clip <c>Without You_main32_full</c>). No ambient, ambient-SE,
    /// system-SE or timer-SE row carries a purchase or DLC flag (their row types hold only an enum and
    /// a clip, e.g. <c>AmbientSoundMasterData</c>); ambients of point-purchase environments are
    /// bought with in-game points and are treated as free.
    /// </para>
    /// <para>
    /// So: pack audio never replaces a game music track (it only adds pack tracks), refuses a story-music
    /// row that plays a soundtrack track, and refuses a pack track that re-lists any game track.
    /// There is no override switch.
    /// </para>
    /// </remarks>
    public static class AudioPaidContent
    {
        /// <summary>Steam app id of the game's only DLC, the original soundtrack (<c>BulbulSteamDLC.DLC_1</c>).</summary>
        public const uint SoundtrackDlcAppId = 3718890;

        /// <summary>
        /// Why replacing this row's clip is refused, or null when it is allowed. Only story music can
        /// be paid; every other table is free (see the type remarks).
        /// </summary>
        /// <param name="row">The game's row.</param>
        /// <param name="catalog">The game's music list.</param>
        public static string? CheckRowReplacement(AudioRowDescriptor row, SoundtrackCatalog catalog)
        {
            if (row is null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            if (row.Table != AudioTable.DirectionMusic)
            {
                return null;
            }

            if (catalog is null || !catalog.IsKnown)
            {
                return "story music '" + row.Name + "' cannot be checked against the soundtrack (the game's music list is not loaded), so it is not replaced";
            }

            var track = catalog.FindPrefixOf(row.OriginalClipName) ?? catalog.FindPrefixOf(row.Name);
            if (track is null)
            {
                return null;
            }

            return "story music '" + row.Name + "' plays the soundtrack track " + track
                + ", part of the original soundtrack sold as Steam DLC " + SoundtrackDlcAppId + "; paid content is never replaced";
        }

        /// <summary>
        /// Why listing a pack track is refused, or null when it is allowed: a pack may not re-list a
        /// track of the game's music list under its title, clip name or file name.
        /// </summary>
        /// <param name="title">The title the pack gives the track.</param>
        /// <param name="fileStem">The file name without extension.</param>
        /// <param name="catalog">The game's music list.</param>
        public static string? CheckMusicTrack(string? title, string? fileStem, SoundtrackCatalog catalog)
        {
            if (catalog is null)
            {
                return null;
            }

            var track = catalog.FindExact(title) ?? catalog.FindExact(fileStem);
            if (track is null)
            {
                return null;
            }

            return track.IsOriginalSoundtrack
                ? "re-lists the soundtrack track " + track + ", part of the original soundtrack sold as Steam DLC "
                  + SoundtrackDlcAppId + "; paid content is never re-listed"
                : "re-lists the game's own track " + track + "; packs add new music only";
        }

        /// <summary>
        /// Drops rich-text tags (the game writes some titles as <c>Cosmic Radio &lt;size=14.5&gt;-Fan Art…-&lt;/size&gt;</c>),
        /// lower-cases and keeps letters and digits only, so <c>Without You</c>, <c>without-you</c>
        /// and <c>WithoutYou</c> compare equal.
        /// </summary>
        /// <param name="text">Any text.</param>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text!.Length);
            var inTag = false;
            foreach (var c in text)
            {
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }

                if (inTag)
                {
                    inTag = c != '>';
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }
    }
}
