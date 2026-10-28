using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>One ambient sound layer and its level.</summary>
    public sealed class AmbientSoundView
    {
        /// <summary>The layer, by name (e.g. <c>RadioNoise</c>, <c>Rain</c>).</summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>Its volume, 0..1.</summary>
        public float Volume { get; init; }

        /// <summary>True when the layer is muted.</summary>
        public bool IsMute { get; init; }
    }

    /// <summary>
    /// The room's environment from <c>EnviromentData</c> (the game's own spelling): which window
    /// views are switched on and how the ambient layers are mixed.
    /// </summary>
    public sealed class EnvironmentView
    {
        /// <summary>Window views the player currently has enabled, by name.</summary>
        public IReadOnlyList<string> ActiveWindowViews { get; init; } = Array.Empty<string>();

        /// <summary>Ambient layers that are audible (unmuted and above zero).</summary>
        public IReadOnlyList<AmbientSoundView> ActiveAmbientSounds { get; init; } =
            Array.Empty<AmbientSoundView>();
    }
}
