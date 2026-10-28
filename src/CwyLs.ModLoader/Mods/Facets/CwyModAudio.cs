using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using UnityEngine;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Audio: the game's replaceable sound rows - ambient loops, ambient one-shots, system
    /// and timer sounds, story music - and the pack music tracks. Packs replace rows with files in
    /// <c>audio/ambient/</c>, <c>audio/ambient_se/</c>, <c>audio/se/system/</c>, <c>audio/se/timer/</c>,
    /// <c>audio/direction_music/</c> and add tracks with <c>audio/music/</c> (+ <c>_tracks.json</c>);
    /// a code mod is a pack too, so its own folders work the same way. This facet adds runtime
    /// replacements and the A/B switch. Main thread. Only the framework implements this interface.
    /// </summary>
    /// <remarks>
    /// Paid content is never replaced: story music that plays a track of the game's soundtrack
    /// (sold as Steam DLC 3718890) is refused, as the console and <see cref="AudioRowInfo.Refusal"/> say.
    /// Voice clips stay in the Satone facet.
    /// </remarks>
    public interface ICwyModAudio : ICwyModFacet
    {
        /// <summary>Every game sound row with its replacement, selection and what is playing. A detached copy; empty before the game's data is loaded.</summary>
        IReadOnlyList<AudioRowInfo> Rows();

        /// <summary>The pack music tracks, loaded or not, listed or not. A detached copy.</summary>
        IReadOnlyList<MusicTrackInfo> MusicTracks();

        /// <summary>
        /// Replaces a row's clip from code; beats every pack. The clip is renamed to the game clip's
        /// name (the game's audio managers look players up by name). Dispose the handle to take it back.
        /// </summary>
        /// <param name="row">A row key (<c>ambient/LightRain</c>), or a row name once the game's data is loaded (<c>LightRain</c>).</param>
        /// <param name="clip">The clip; it must stay alive while registered.</param>
        /// <returns>The handle; refused (with the reason) for an unknown row, a paid row, or when audio is off.</returns>
        CwyRegistration ReplaceClip(string row, AudioClip clip);

        /// <summary>
        /// The A/B switch: plays the game's clip or the replacement for the named rows; a playing loop
        /// moves to the other clip at the same position.
        /// </summary>
        /// <param name="rows">A row, a table (<c>ambient</c>) or <c>all</c>.</param>
        /// <param name="variant">Which clip; null toggles.</param>
        /// <returns>Per-row outcomes, or the problem.</returns>
        AudioSwitchResult Select(string rows, AudioVariant? variant);

        /// <summary>Re-reads the packs' audio files and applies them again (changed files are reloaded).</summary>
        void Reload();
    }

    /// <summary>
    /// The adapter's side of the Audio facet: <see cref="ICwyModAudio"/> plus the owner-attributed
    /// registration the per-mod proxy needs. Framework assemblies only.
    /// </summary>
    internal interface IAudioFacetBackend : ICwyModAudio
    {
        /// <summary>Adds a runtime replacement for <paramref name="owner"/>; the result says why when refused.</summary>
        /// <param name="owner">Who, e.g. <c>mod:my.mod</c>.</param>
        /// <param name="row">The row spec.</param>
        /// <param name="clip">The clip.</param>
        AudioClaimResult Claim(string owner, string row, AudioClip clip);
    }

    /// <summary>The result of <see cref="IAudioFacetBackend.Claim"/>.</summary>
    internal sealed class AudioClaimResult
    {
        private AudioClaimResult(string key, IDisposable? handle, string problem)
        {
            Key = key;
            Handle = handle;
            Problem = problem;
        }

        /// <summary>The resolved row key, or the spec when it did not resolve.</summary>
        internal string Key { get; }

        /// <summary>The registration; null when refused.</summary>
        internal IDisposable? Handle { get; }

        /// <summary>Why it was refused; empty when accepted.</summary>
        internal string Problem { get; }

        /// <summary>An accepted claim.</summary>
        internal static AudioClaimResult Accepted(string key, IDisposable handle) => new AudioClaimResult(key, handle, string.Empty);

        /// <summary>A refused claim.</summary>
        internal static AudioClaimResult Refused(string key, string problem) =>
            new AudioClaimResult(key, null, string.IsNullOrWhiteSpace(problem) ? "refused" : problem);
    }

    /// <summary>The per-mod Audio facet.</summary>
    internal sealed class ModAudio : ModFacetProxy<ICwyModAudio>, ICwyModAudio
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable = "audio replacement is not available: no audio backend is installed";

        internal ModAudio(ModHelper helper)
            : base(helper, "audio", NotAvailable)
        {
        }

        public IReadOnlyList<AudioRowInfo> Rows() => BackendFor("Rows")?.Rows() ?? Array.Empty<AudioRowInfo>();

        public IReadOnlyList<MusicTrackInfo> MusicTracks() => BackendFor("MusicTracks")?.MusicTracks() ?? Array.Empty<MusicTrackInfo>();

        public CwyRegistration ReplaceClip(string row, AudioClip clip)
        {
            var name = "audio:" + (row ?? string.Empty);
            if (clip == null)
            {
                return Refuse(name, "no clip was given");
            }

            if (!Helper.IsActive)
            {
                return CwyRegistration.Refused(name, "the mod " + Helper.ModId + " is not running");
            }

            if (!(BackendFor("ReplaceClip") is IAudioFacetBackend backend))
            {
                return CwyRegistration.Refused(name, Unavailable);
            }

            AudioClaimResult result;
            try
            {
                result = backend.Claim(Threading.MainThread.ModOwner(Helper.ModId), row ?? string.Empty, clip);
            }
            catch (Exception ex)
            {
                return Refuse(name, "the audio backend failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return result.Handle is null
                ? Refuse("audio:" + result.Key, result.Problem)
                : Helper.Track("audio:" + result.Key, result.Handle);
        }

        public AudioSwitchResult Select(string rows, AudioVariant? variant) =>
            BackendFor("Select")?.Select(rows, variant) ?? AudioSwitchResult.Refused(Unavailable);

        public void Reload() => BackendFor("Reload")?.Reload();

        private CwyRegistration Refuse(string name, string problem)
        {
            Helper.WarnOnce(name, "audio replacement '" + name + "' refused: " + problem);
            return CwyRegistration.Refused(name, problem);
        }
    }
}
