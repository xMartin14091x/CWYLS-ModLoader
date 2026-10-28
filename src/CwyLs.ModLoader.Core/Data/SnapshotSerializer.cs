using System;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// Turns a <see cref="GameDataSnapshot"/> into the JSON document written to
    /// <c>BepInEx/data/snapshot.json</c>. A one-line wrapper over <see cref="JsonWriter"/> on
    /// purpose: it is the named, stable entry point mods and sidecars are told about, while the
    /// walker underneath stays free to change.
    /// </summary>
    public static class SnapshotSerializer
    {
        /// <summary>Serialises a snapshot.</summary>
        /// <param name="snapshot">The snapshot; must not be null.</param>
        /// <param name="onError">
        /// Optional sink for per-member failures (see <see cref="JsonWriter.Write"/>). A member
        /// that cannot be read is omitted rather than aborting the document.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        public static string ToJson(GameDataSnapshot snapshot, Action<string>? onError = null)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return JsonWriter.Write(snapshot, onError);
        }
    }
}
