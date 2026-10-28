namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// Why <see cref="PackResolver"/> refused to load a pack. Every skip is reported with one of
    /// these plus a detail string; a pack is never dropped silently.
    /// <para>
    /// Values are numbered explicitly so they stay stable if a member is ever retired.
    /// </para>
    /// </summary>
    public enum PackSkipReason
    {
        /// <summary>The folder has no readable <c>manifest.json</c>.</summary>
        MissingManifest = 0,

        /// <summary>The manifest is not valid JSON, or fails the schema. Detail says which member.</summary>
        InvalidManifest = 1,

        /// <summary>Another folder already claimed this pack id. The first one wins.</summary>
        DuplicateId = 2,

        // 3 was GameVersionMismatch: retired when a different gameVersion started loading with a
        // warning, then removed because nothing produced or read it. The number stays unused so logged or stored values keep their meaning.

        /// <summary>The pack needs a newer CwyLs.ModLoader than the one running.</summary>
        CoreVersionTooOld = 4,

        /// <summary>
        /// A required pack named in <c>dependsOn</c> or <c>contentPackFor</c> is absent - either
        /// never present, or itself skipped - or <c>contentPackFor</c> names a pack that is not a
        /// code mod. Optional dependencies never produce this.
        /// </summary>
        MissingDependency = 5,

        /// <summary>A dependency is present but older than the <c>minVersion</c> asked for.</summary>
        DependencyVersionTooLow = 6,

        /// <summary>The pack is part of a <c>dependsOn</c> cycle, so no load order exists.</summary>
        DependencyCycle = 7,

        /// <summary>
        /// The running game is older than the manifest's <c>minGameVersion</c>, and the framework
        /// was not told to ignore game versions.
        /// </summary>
        GameVersionTooOld = 8,

        /// <summary>
        /// The running game is newer than the manifest's <c>maxGameVersion</c>, and the framework
        /// was not told to ignore game versions.
        /// </summary>
        GameVersionTooNew = 9,

        /// <summary>
        /// The manifest names an <c>entryDll</c> (a code mod) but that file is not in the pack
        /// folder. The whole pack is skipped - its content too - because a code mod's content is
        /// usually written for its code. Dependents go with it as <see cref="MissingDependency"/>.
        /// </summary>
        EntryDllMissing = 10,
    }
}
