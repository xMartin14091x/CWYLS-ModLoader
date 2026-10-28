namespace CwyLs.ModLoader.Core
{
    /// <summary>
    /// Looks a <see cref="PatchTarget"/> up in whatever the host provides. The plugin side uses
    /// reflection over the loaded game assemblies; tests supply a fake, which is the only reason
    /// this is an interface.
    /// </summary>
    public interface IMemberResolver
    {
        /// <summary>Resolves one target. Implementations must not throw for an absent member.</summary>
        /// <param name="target">The target to look up.</param>
        /// <returns>A found or missing result, never null.</returns>
        PatchTargetResult Resolve(PatchTarget target);
    }
}
