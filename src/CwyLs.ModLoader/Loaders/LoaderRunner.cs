using UnityEngine;

namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// Nothing but a coroutine owner. Unity needs a live <see cref="MonoBehaviour"/> to run one,
    /// and the loaders need one that survives scene changes; that is the whole job, so this type
    /// has no behaviour of its own. Created only by <see cref="LoaderHost.EnsureRunner"/>.
    /// </summary>
    public sealed class LoaderRunner : MonoBehaviour
    {
    }
}
