using System.Collections.Generic;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Mods;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>The game-side backend of <see cref="ICwyModGrading"/>: forwards to <see cref="GradingFeature"/>.</summary>
    internal sealed class GradingFacetBackend : ICwyModGrading
    {
        /// <inheritdoc />
        public bool IsAvailable => GradingFeature.IsAvailable && GradingFeature.Enabled;

        /// <inheritdoc />
        public string Status => "grading " + GradingFeature.Status;

        /// <inheritdoc />
        public IReadOnlyList<string> Presets
        {
            get
            {
                var names = new List<string>();
                foreach (var entry in GradingFeature.Catalog().Entries)
                {
                    names.Add(entry.QualifiedName);
                }

                return names;
            }
        }

        /// <inheritdoc />
        public string? ActivePreset => GradingFeature.ActivePreset;

        /// <inheritdoc />
        public bool ShowingOriginal => GradingFeature.ShowingOriginal;

        /// <inheritdoc />
        public GradingResult Apply(string name) => GradingFeature.Apply(name);

        /// <inheritdoc />
        public GradingResult ApplyJson(string name, string json) => GradingFeature.ApplyJson(name, json, folder: null);

        /// <inheritdoc />
        public GradingResult Set(string control, double value) => GradingFeature.Set(control, value);

        /// <inheritdoc />
        public GradingResult Off() => GradingFeature.Off();

        /// <inheritdoc />
        public GradingResult ShowOriginal(bool original) => GradingFeature.ShowOriginal(original);
    }
}
