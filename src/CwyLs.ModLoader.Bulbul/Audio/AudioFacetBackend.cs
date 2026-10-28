using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Mods;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>
    /// The game side of the mod Audio facet (<see cref="ICwyModAudio"/>), installed with
    /// <c>ModFacetBackends.Use</c> from <see cref="AudioFeature.Initialize"/>. A thin pass-through to
    /// <see cref="AudioFeature"/>; the per-mod proxy wraps registrations in <c>CwyRegistration</c>s.
    /// </summary>
    internal sealed class AudioFacetBackend : IAudioFacetBackend
    {
        private AudioFacetBackend()
        {
        }

        /// <summary>The one backend.</summary>
        internal static AudioFacetBackend Instance { get; } = new AudioFacetBackend();

        public bool IsAvailable => AudioFeature.Enabled;

        public string Status => AudioFeature.Status();

        public IReadOnlyList<AudioRowInfo> Rows() => AudioFeature.Rows();

        public IReadOnlyList<MusicTrackInfo> MusicTracks() => AudioFeature.MusicTracks();

        public CwyRegistration ReplaceClip(string row, AudioClip clip) =>
            CwyRegistration.Refused("audio:" + row, "call this through a mod's helper.Audio");

        public AudioSwitchResult Select(string rows, AudioVariant? variant)
        {
            try
            {
                return AudioFeature.Select(rows, variant);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("AUDIO a mod's Select('" + rows + "') failed: " + ex.GetType().Name + ": " + ex.Message);
                return AudioSwitchResult.Refused(ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void Reload() => AudioFeature.Reload("mod");

        public AudioClaimResult Claim(string owner, string row, AudioClip clip) => AudioFeature.Claim(owner, row, clip);
    }
}
