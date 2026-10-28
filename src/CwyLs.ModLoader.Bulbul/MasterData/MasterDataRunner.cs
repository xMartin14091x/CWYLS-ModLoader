using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// The component on <see cref="MasterDataHost"/>'s hidden object. It does one thing: give
    /// <see cref="MasterDataHost.Pump"/> a main-thread frame tick, so the feature can watch for the
    /// moment the game's master data finishes loading.
    /// <para>
    /// Nothing else should add this component; <see cref="MasterDataHost.EnsureRunner"/> owns it.
    /// </para>
    /// </summary>
    public sealed class MasterDataRunner : MonoBehaviour
    {
        /// <summary>Label this runner reports under.</summary>
        public const string Ticket = "P2-1";

        private void Update() => MasterDataHost.Pump();
    }
}
