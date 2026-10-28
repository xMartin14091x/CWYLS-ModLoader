namespace CwyLs.ModLoader.Mods
{
    /// <summary>Where microphone audio comes from.</summary>
    public enum CwyMicRoute
    {
        /// <summary>No capture: <c>[Mic] Enabled=false</c> (the default), or the microphone plugin is not installed.</summary>
        Off = 0,

        /// <summary>Captured in the game by <c>CwyLs.ModLoader.Mic.dll</c> (Windows).</summary>
        InGame = 1,

        /// <summary>
        /// Captured by an external helper, a separate process with its own
        /// microphone permission, which posts utterances to the game API (macOS: the game's
        /// Info.plist has no <c>NSMicrophoneUsageDescription</c>, so in-game capture would terminate it).
        /// </summary>
        ExternalHelper = 2,
    }

    /// <summary>
    /// Mic: push-to-talk utterances. Everything that touches a capture device lives in the separate
    /// <c>CwyLs.ModLoader.Mic.dll</c>, which is never loaded while <c>[Mic] Enabled=false</c>; this facet
    /// and CwyLs.ModLoader never reference <c>UnityEngine.Microphone</c>. A slot until a microphone
    /// backend is installed with <c>ModFacetBackends.Use&lt;ICwyModMic&gt;</c> and members are added here; only the
    /// framework implements this interface.
    /// </summary>
    public interface ICwyModMic : ICwyModFacet
    {
        /// <summary>True when the player switched capture on (<c>[Mic] Enabled=true</c>); false without the backend.</summary>
        bool IsEnabled { get; }

        /// <summary>Where audio comes from; <see cref="CwyMicRoute.Off"/> without the backend.</summary>
        CwyMicRoute Route { get; }
    }

    /// <summary>The per-mod Mic facet.</summary>
    internal sealed class ModMic : ModFacetProxy<ICwyModMic>, ICwyModMic
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable =
            "microphone capture is off: CwyLs.ModLoader.Mic is not loaded ([Mic] Enabled=false, the default); " +
            "on macOS capture runs in an external helper";

        internal ModMic(ModHelper helper)
            : base(helper, "mic", NotAvailable)
        {
        }

        public bool IsEnabled => Backend?.IsEnabled ?? false;

        public CwyMicRoute Route => Backend?.Route ?? CwyMicRoute.Off;
    }
}
