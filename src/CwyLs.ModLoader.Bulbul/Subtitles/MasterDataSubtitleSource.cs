using System;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// Publishes the packs' text tables (<c>data/subtitles/&lt;code&gt;.json</c>, merged by the
    /// master-data feature into <see cref="MasterDataFeature.Subtitles"/>) as an
    /// <see cref="ISubtitleSource"/>, so the text seam can ask them.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="MasterDataFeature.Subtitles"/> on every call rather than holding a copy, so a
    /// pack rescan that re-merges the tables is picked up without re-registering. Registered once,
    /// before <c>Satone.Say</c>'s own source, so packs are asked first.
    /// </remarks>
    internal sealed class MasterDataSubtitleSource : ISubtitleSource
    {
        /// <summary>The name the source is registered under.</summary>
        internal const string SourceName = "packs (data/subtitles)";

        private static readonly object Gate = new object();
        private static MasterDataSubtitleSource? _registered;

        /// <summary>True once the source is registered.</summary>
        internal static bool Attached => _registered != null;

        /// <summary>Registers the source with <see cref="SubtitleSources"/>. Idempotent.</summary>
        internal static void Register()
        {
            lock (Gate)
            {
                if (_registered != null)
                {
                    return;
                }

                _registered = new MasterDataSubtitleSource();
                SubtitleSources.Register(_registered, SourceName);
            }
        }

        /// <inheritdoc />
        public bool TryGet(string localizationId, string lang, out string text)
        {
            text = string.Empty;
            try
            {
                if (MasterDataFeature.Subtitles.TryGet(localizationId, lang, out var found) && !string.IsNullOrEmpty(found))
                {
                    text = found!;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                // Logged, never rethrown: this runs inside the game's own text binding.
                CwyLsModLoader.Log.LogWarning(
                    $"{SubtitleFeature.LogPrefix} the pack text table threw for '{localizationId}' [{lang}]: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
