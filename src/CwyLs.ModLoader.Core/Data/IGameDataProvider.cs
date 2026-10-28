namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// Extension point that lets any mod publish its own section onto the same bus. Registered
    /// providers are captured whenever a snapshot is taken and land under
    /// <see cref="GameDataSnapshot.Extra"/>, keyed by <see cref="SectionName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Capture"/> is called on Unity's main thread while a snapshot is being built, so
    /// it may touch the engine - but it is on the frame budget: keep it to reading fields you
    /// already hold. Return plain data (primitives, strings, enums, lists, dictionaries, or simple
    /// objects with public properties); the serializer walks it reflectively and gives up below a
    /// fixed depth, so do not hand back live game objects.
    /// </para>
    /// <para>
    /// A provider that throws is logged and skipped for that snapshot: one bad mod cannot stop the
    /// rest of the bus from publishing.
    /// </para>
    /// </remarks>
    public interface IGameDataProvider
    {
        /// <summary>
        /// Stable key this provider's data appears under in <see cref="GameDataSnapshot.Extra"/>.
        /// Namespace it like a mod id (e.g. <c>mymod.mood</c>) so two mods cannot collide.
        /// Must be non-empty.
        /// </summary>
        string SectionName { get; }

        /// <summary>
        /// Produces this provider's current data, or <c>null</c> to publish nothing this time.
        /// Must not block.
        /// </summary>
        object? Capture();
    }
}
