using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// The version of the CWY:LS Mod Loader mod API: the one number a manifest's
    /// <c>loaderVersion</c> is checked against, for content packs and code mods alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SemVer. Until <see cref="FreezeVersion"/> (CWY:LS Mod Loader 1.0) the
    /// API is 0.x: a minor version may change or remove members, and
    /// every such change first ships a <see cref="CwyDeprecatedAttribute"/> notice. From 1.0 on a
    /// breaking change needs a new major version; deprecated members stay until then.
    /// </para>
    /// <para>
    /// The BepInEx plugin version of CwyLs.ModLoader is this same string, so the startup line, the
    /// <c>version</c> command and the manifest check can never disagree.
    /// </para>
    /// </remarks>
    public static class CwyApi
    {
        /// <summary>
        /// The API version as a compile-time constant, for attribute arguments such as the
        /// framework's own <c>[BepInPlugin]</c>. A mod must not use it: a constant is copied into
        /// the mod when it is compiled, so it would keep reporting the version the mod was built
        /// against. Read <see cref="Version"/> (or <see cref="VersionText"/>) at run time instead.
        /// </summary>
        public const string PluginVersionText = "0.9.0";

        /// <summary>
        /// The running API version as text. <c>static readonly</c>, not a constant, so a mod reads the
        /// version of the framework it runs on, not the one it was compiled against.
        /// </summary>
        public static readonly string VersionText = PluginVersionText;

        /// <summary>The version at which the API freezes (SemVer 1.0.0).</summary>
        public const string FreezeVersionText = "1.0.0";

        /// <summary>The API version, parsed.</summary>
        public static SemVer Version { get; } = SemVer.Parse(VersionText);

        /// <summary>The version at which the API freezes, parsed.</summary>
        public static SemVer FreezeVersion { get; } = SemVer.Parse(FreezeVersionText);

        /// <summary>
        /// True once the API has reached <see cref="FreezeVersion"/>: after that, only a major
        /// version may break a mod.
        /// </summary>
        public static bool IsFrozen => Version >= FreezeVersion;

        /// <summary>
        /// True when a manifest asking for <paramref name="minimum"/> can load on this API.
        /// Null (no <c>loaderVersion</c>) is always satisfied.
        /// </summary>
        /// <param name="minimum">The manifest's <c>loaderVersion</c>.</param>
        public static bool Satisfies(SemVer? minimum) => minimum is null || Version >= minimum;
    }
}
