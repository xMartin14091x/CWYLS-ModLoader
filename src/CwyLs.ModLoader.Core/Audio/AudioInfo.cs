using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>What a row's players are playing right now.</summary>
    public enum AudioPlaying
    {
        /// <summary>The players could not be read (the audio managers are not up, or a seam is missing).</summary>
        Unknown = 0,

        /// <summary>No player holds either clip.</summary>
        None,

        /// <summary>A player holds the game's own clip.</summary>
        Original,

        /// <summary>A player holds the replacement.</summary>
        Replacement,
    }

    /// <summary>One game sound row, as <c>audio list</c> and the mod facet report it.</summary>
    public sealed class AudioRowInfo
    {
        /// <summary>Creates a row report (the adapter fills these in).</summary>
        /// <param name="row">The game's row.</param>
        /// <param name="replacementSource">Who supplies the replacement; empty when none.</param>
        /// <param name="replacementSeconds">The replacement's length; 0 when none or unknown.</param>
        /// <param name="variant">The selected variant.</param>
        /// <param name="refusal">Why a replacement was refused (paid content); empty when none.</param>
        /// <param name="playing">What its players hold.</param>
        /// <param name="playingCount">How many players hold one of its clips.</param>
        /// <param name="playingVolume">The loudest such player's volume (0..1); -1 when none or unknown.</param>
        /// <exception cref="ArgumentNullException"><paramref name="row"/> is null.</exception>
        public AudioRowInfo(
            AudioRowDescriptor row,
            string? replacementSource,
            double replacementSeconds,
            AudioVariant variant,
            string? refusal,
            AudioPlaying playing,
            int playingCount,
            float playingVolume)
        {
            Row = row ?? throw new ArgumentNullException(nameof(row));
            ReplacementSource = replacementSource ?? string.Empty;
            ReplacementSeconds = replacementSeconds > 0 && !double.IsInfinity(replacementSeconds) ? replacementSeconds : 0;
            Variant = variant;
            Refusal = refusal ?? string.Empty;
            Playing = playing;
            PlayingCount = playingCount < 0 ? 0 : playingCount;
            PlayingVolume = playingVolume;
        }

        /// <summary>The game's row.</summary>
        public AudioRowDescriptor Row { get; }

        /// <summary>The table.</summary>
        public AudioTable Table => Row.Table;

        /// <summary>The row name, e.g. <c>LightRain</c>.</summary>
        public string Name => Row.Name;

        /// <summary>The row key, e.g. <c>ambient/LightRain</c>.</summary>
        public string Key => Row.Key;

        /// <summary>True when a pack or a mod replaces this row.</summary>
        public bool HasReplacement => ReplacementSource.Length > 0;

        /// <summary>Who supplies the replacement (<c>pack/path</c> or <c>mod:id</c>); empty when none.</summary>
        public string ReplacementSource { get; }

        /// <summary>The replacement's length in seconds; 0 when none.</summary>
        public double ReplacementSeconds { get; }

        /// <summary>The selected variant (meaningful when <see cref="HasReplacement"/>).</summary>
        public AudioVariant Variant { get; }

        /// <summary>Why a replacement was refused; empty when none was.</summary>
        public string Refusal { get; }

        /// <summary>True when a replacement was refused as paid content.</summary>
        public bool IsRefused => Refusal.Length > 0;

        /// <summary>What the row's players hold now.</summary>
        public AudioPlaying Playing { get; }

        /// <summary>How many players hold one of its clips.</summary>
        public int PlayingCount { get; }

        /// <summary>The loudest such player's volume (0..1); -1 when none or unknown.</summary>
        public float PlayingVolume { get; }
    }

    /// <summary>What happened to one row in an A/B switch.</summary>
    public enum AudioSwitchMode
    {
        /// <summary>The row already played the requested variant.</summary>
        Unchanged,

        /// <summary>The row now holds the other clip; nothing was playing it.</summary>
        RowOnly,

        /// <summary>Playing loops were moved to the other clip at the same position.</summary>
        Moved,

        /// <summary>Playing players were restarted on the other clip from the start.</summary>
        Restarted,

        /// <summary>The switch was refused or failed; see the detail.</summary>
        Failed,
    }

    /// <summary>One row's part of an A/B switch.</summary>
    public sealed class AudioSwitchOutcome
    {
        /// <summary>Creates an outcome.</summary>
        /// <param name="key">The row key.</param>
        /// <param name="variant">The variant the row plays now.</param>
        /// <param name="mode">What happened.</param>
        /// <param name="players">How many players were moved or restarted.</param>
        /// <param name="positionSeconds">Where a moved loop continues, in seconds; -1 when not applicable.</param>
        /// <param name="detail">One line for people; may be empty.</param>
        public AudioSwitchOutcome(string key, AudioVariant variant, AudioSwitchMode mode, int players, double positionSeconds, string? detail)
        {
            Key = key ?? string.Empty;
            Variant = variant;
            Mode = mode;
            Players = players < 0 ? 0 : players;
            PositionSeconds = positionSeconds;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The row key.</summary>
        public string Key { get; }

        /// <summary>The variant the row plays now.</summary>
        public AudioVariant Variant { get; }

        /// <summary>What happened.</summary>
        public AudioSwitchMode Mode { get; }

        /// <summary>How many players were moved or restarted.</summary>
        public int Players { get; }

        /// <summary>Where a moved loop continues; -1 when not applicable.</summary>
        public double PositionSeconds { get; }

        /// <summary>One line for people.</summary>
        public string Detail { get; }
    }

    /// <summary>The result of an A/B switch request.</summary>
    public sealed class AudioSwitchResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="outcomes">Per-row outcomes.</param>
        /// <param name="problem">Why nothing was switched; empty when the request was valid.</param>
        public AudioSwitchResult(IEnumerable<AudioSwitchOutcome>? outcomes, string? problem)
        {
            Outcomes = outcomes is null ? new List<AudioSwitchOutcome>() : new List<AudioSwitchOutcome>(outcomes);
            Problem = problem ?? string.Empty;
        }

        /// <summary>Per-row outcomes.</summary>
        public IReadOnlyList<AudioSwitchOutcome> Outcomes { get; }

        /// <summary>Why nothing was switched; empty when the request was valid.</summary>
        public string Problem { get; }

        /// <summary>True when the request was valid (rows may still be <see cref="AudioSwitchMode.Unchanged"/>).</summary>
        public bool Ok => Problem.Length == 0;

        /// <summary>A refused request.</summary>
        /// <param name="problem">Why.</param>
        public static AudioSwitchResult Refused(string problem) => new AudioSwitchResult(null, problem);
    }

    /// <summary>One pack music track, as <c>audio music</c> and the mod facet report it.</summary>
    public sealed class MusicTrackInfo
    {
        /// <summary>Creates a report.</summary>
        /// <param name="track">The planned track.</param>
        /// <param name="loaded">True when its clip decoded.</param>
        /// <param name="listed">True when it is in the game's music list.</param>
        /// <param name="seconds">The clip length; 0 when not loaded.</param>
        /// <param name="problem">Why it is not listed; empty when fine.</param>
        /// <exception cref="ArgumentNullException"><paramref name="track"/> is null.</exception>
        public MusicTrackInfo(PackMusicTrack track, bool loaded, bool listed, double seconds, string? problem)
        {
            Track = track ?? throw new ArgumentNullException(nameof(track));
            Loaded = loaded;
            Listed = listed;
            Seconds = seconds > 0 && !double.IsInfinity(seconds) ? seconds : 0;
            Problem = problem ?? string.Empty;
        }

        /// <summary>The planned track (pack, path, title, credit, tag, UUID).</summary>
        public PackMusicTrack Track { get; }

        /// <summary>True when its clip decoded.</summary>
        public bool Loaded { get; }

        /// <summary>True when it is in the game's music list.</summary>
        public bool Listed { get; }

        /// <summary>The clip length in seconds.</summary>
        public double Seconds { get; }

        /// <summary>Why it is not listed; empty when fine.</summary>
        public string Problem { get; }
    }

    /// <summary>Where a moved loop continues on a clip of another length.</summary>
    public static class LoopPosition
    {
        /// <summary>
        /// Maps a playback position onto a clip of <paramref name="targetSeconds"/>: a loop wraps
        /// (<c>40 s</c> on a 32 s loop = 8 s), a one-shot clamps just short of its end. Bad input maps
        /// to 0, a clean restart.
        /// </summary>
        /// <param name="positionSeconds">The position on the clip being left.</param>
        /// <param name="targetSeconds">The new clip's length.</param>
        /// <param name="loop">True for a looping player.</param>
        public static double Map(double positionSeconds, double targetSeconds, bool loop)
        {
            if (double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds) || positionSeconds <= 0
                || double.IsNaN(targetSeconds) || double.IsInfinity(targetSeconds) || targetSeconds <= 0)
            {
                return 0;
            }

            if (loop)
            {
                var wrapped = positionSeconds % targetSeconds;
                return wrapped < 0 ? 0 : wrapped;
            }

            return positionSeconds < targetSeconds ? positionSeconds : 0;
        }

        /// <summary>The sample index for a position, clamped to the clip.</summary>
        /// <param name="seconds">The position.</param>
        /// <param name="frequency">The clip's sample rate.</param>
        /// <param name="samples">The clip's length in samples per channel.</param>
        public static int ToSamples(double seconds, int frequency, int samples)
        {
            if (frequency <= 0 || samples <= 0 || double.IsNaN(seconds) || seconds <= 0)
            {
                return 0;
            }

            var at = seconds * frequency;
            if (at >= samples - 1)
            {
                return samples - 1;
            }

            return (int)at;
        }
    }
}
