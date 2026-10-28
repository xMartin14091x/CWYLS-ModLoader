using System.Collections.Generic;
using CwyLs.ModLoader.Core.Grading;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Grading: Lightroom-style photo grading over the room camera. Presets are
    /// <c>grading/&lt;name&gt;.json</c> files in packs (format: <c>src/CwyLs.ModLoader.Bulbul/Grading/README.md</c>)
    /// or the player's own in <c>BepInEx/data/grading/</c>. The backend is installed by the game
    /// adapter with <c>ModFacetBackends.Use&lt;ICwyModGrading&gt;</c>; only the framework implements this
    /// interface. Every call is main-thread only and returns a <see cref="GradingResult"/> (a refusal
    /// off the main thread, while grading is off, or without the backend - never an exception).
    /// </summary>
    public interface ICwyModGrading : ICwyModFacet
    {
        /// <summary>Every preset name the game can apply (qualified: <c>pack:stem</c>, <c>user:stem</c>).</summary>
        IReadOnlyList<string> Presets { get; }

        /// <summary>The applied preset's name, or null when none is applied.</summary>
        string? ActivePreset { get; }

        /// <summary>True while the game's own look is shown for comparison.</summary>
        bool ShowingOriginal { get; }

        /// <summary>Applies a preset by name and remembers it across restarts.</summary>
        /// <param name="name">A plain or qualified preset name.</param>
        GradingResult Apply(string name);

        /// <summary>Applies a preset document the mod supplies (not remembered across restarts).</summary>
        /// <param name="name">Name to show, e.g. <c>my.mod:dusk</c>.</param>
        /// <param name="json">The preset JSON.</param>
        GradingResult ApplyJson(string name, string json);

        /// <summary>Sets one control (id or alias) on the applied preset, clamped to its range.</summary>
        /// <param name="control">E.g. <c>exposure</c>, <c>splitToning.highlightHue</c>.</param>
        /// <param name="value">The value.</param>
        GradingResult Set(string control, double value);

        /// <summary>Removes the grading and forgets the remembered preset.</summary>
        GradingResult Off();

        /// <summary>Shows the game's own look (true) or the graded one (false), instantly.</summary>
        /// <param name="original">True for the game's own look.</param>
        GradingResult ShowOriginal(bool original);
    }

    /// <summary>The per-mod Grading facet.</summary>
    internal sealed class ModGrading : ModFacetProxy<ICwyModGrading>, ICwyModGrading
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable = "grading is not available: no grading backend is installed";

        private static readonly string[] NoPresets = new string[0];

        internal ModGrading(ModHelper helper)
            : base(helper, "grading", NotAvailable)
        {
        }

        public IReadOnlyList<string> Presets => Backend?.Presets ?? NoPresets;

        public string? ActivePreset => Backend?.ActivePreset;

        public bool ShowingOriginal => Backend?.ShowingOriginal ?? false;

        public GradingResult Apply(string name) =>
            BackendFor("Apply")?.Apply(name) ?? GradingResult.Failure(NotAvailable);

        public GradingResult ApplyJson(string name, string json) =>
            BackendFor("ApplyJson")?.ApplyJson(name, json) ?? GradingResult.Failure(NotAvailable);

        public GradingResult Set(string control, double value) =>
            BackendFor("Set")?.Set(control, value) ?? GradingResult.Failure(NotAvailable);

        public GradingResult Off() =>
            BackendFor("Off")?.Off() ?? GradingResult.Failure(NotAvailable);

        public GradingResult ShowOriginal(bool original) =>
            BackendFor("ShowOriginal")?.ShowOriginal(original) ?? GradingResult.Failure(NotAvailable);
    }
}
