using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// The component on <see cref="TextureHost"/>'s hidden object. It gives the texture feature a
    /// main-thread frame tick: <see cref="TextureHost.Pump"/>, so an apply can be deferred past the
    /// frame the scene loaded in, and <see cref="TextureReload.Pump"/>, which is where the reload
    /// key and the file watcher's off-thread events are turned into a reload.
    /// <para>
    /// Nothing else should add this component; <see cref="TextureHost.EnsureRunner"/> owns it.
    /// </para>
    /// </summary>
    public sealed class TextureRunner : MonoBehaviour
    {
        /// <summary>Label this runner reports under.</summary>
        public const string Ticket = "P1-3";

        private void Update()
        {
            TextureHost.Pump();
            TextureReload.Pump();
        }
    }
}
