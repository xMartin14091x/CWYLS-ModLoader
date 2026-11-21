using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>One pack's answer for a line or a clip, in load order.</summary>
    /// <typeparam name="T">A subtitle text or a voice file path.</typeparam>
    public readonly struct MakerLayer<T>
        where T : class
    {
        /// <summary>Creates a layer.</summary>
        /// <param name="packId">The pack.</param>
        /// <param name="value">What the pack has, or null for nothing.</param>
        public MakerLayer(string packId, T? value)
        {
            PackId = packId;
            Value = value;
        }

        /// <summary>The pack.</summary>
        public string PackId { get; }

        /// <summary>What the pack has, or null.</summary>
        public T? Value { get; }

        /// <summary>True when the pack has something.</summary>
        public bool HasValue => Value != null;
    }

    /// <summary>
    /// Which pack the game listens to for one line or clip: the same "later pack over earlier pack"
    /// rule the master-data merge (<c>SubtitleTables.Build</c>) and the language voice plan
    /// (<c>LanguageVoicePlan</c>) apply. Pure, so the live update the editor makes after a save always
    /// agrees with what the next restart would load.
    /// </summary>
    public static class MakerLayers
    {
        /// <summary>The last pack, in load order, that has a value; default (no value) when none has.</summary>
        /// <typeparam name="T">A subtitle text or a voice file path.</typeparam>
        /// <param name="inLoadOrder">Every loaded pack's answer, in load order.</param>
        public static MakerLayer<T> Effective<T>(IEnumerable<MakerLayer<T>>? inLoadOrder)
            where T : class
        {
            var winner = default(MakerLayer<T>);
            if (inLoadOrder is null)
            {
                return winner;
            }

            foreach (var layer in inLoadOrder)
            {
                if (layer.HasValue)
                {
                    winner = layer;
                }
            }

            return winner;
        }

        /// <summary>
        /// Within one pack, the file a language voice set would load for a name: the ordinally last
        /// path (<c>LanguageVoicePlan</c>: "later path over earlier"), or null when there is none.
        /// </summary>
        /// <param name="ordinalPaths">The pack's matching paths, ordinally sorted.</param>
        public static string? LastPath(IReadOnlyList<string>? ordinalPaths) =>
            ordinalPaths is null || ordinalPaths.Count == 0 ? null : ordinalPaths[ordinalPaths.Count - 1];
    }
}
