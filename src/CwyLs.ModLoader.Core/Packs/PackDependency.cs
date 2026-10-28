using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// One entry of a manifest's <c>dependsOn</c> array (or its <c>contentPackFor</c>): the pack
    /// that must load first, optionally the oldest version of it that will do, and whether the
    /// dependency is required at all.
    /// </summary>
    public sealed class PackDependency
    {
        /// <summary>Creates a dependency.</summary>
        /// <param name="id">The required pack's id. Required.</param>
        /// <param name="minVersion">
        /// Oldest acceptable version of that pack, or null for "any version".
        /// </param>
        /// <param name="optional">
        /// True for a soft dependency: when the pack is installed it loads first,
        /// and when it is absent or was itself skipped the dependency is ignored. An installed pack
        /// older than <paramref name="minVersion"/> still refuses the dependent, as a required one
        /// would (SMAPI's rule: an optional integration against an old version is a crash waiting
        /// to happen).
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or blank.</exception>
        public PackDependency(string id, SemVer? minVersion = null, bool optional = false)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a dependency id is required", nameof(id));
            }

            Id = id.Trim();
            MinVersion = minVersion;
            IsOptional = optional;
        }

        /// <summary>The required pack's id.</summary>
        public string Id { get; }

        /// <summary>Oldest acceptable version, or null when any version satisfies the dependency.</summary>
        public SemVer? MinVersion { get; }

        /// <summary>
        /// True for a soft dependency: it orders the load when the pack is present and is ignored
        /// when it is not. False (the default) refuses the dependent when the pack is missing.
        /// </summary>
        public bool IsOptional { get; }

        /// <summary>True when <paramref name="version"/> satisfies this dependency.</summary>
        /// <param name="version">The version the required pack actually declares.</param>
        public bool IsSatisfiedBy(SemVer? version) =>
            MinVersion is null || (version != null && version >= MinVersion);

        /// <summary>The same dependency with <see cref="IsOptional"/> false.</summary>
        public PackDependency AsRequired() => IsOptional ? new PackDependency(Id, MinVersion) : this;

        /// <summary><c>id</c>, <c>id &gt;= 1.2.0</c>, with <c> (optional)</c> for a soft dependency.</summary>
        public override string ToString()
        {
            var text = MinVersion is null ? Id : Id + " >= " + MinVersion;
            return IsOptional ? text + " (optional)" : text;
        }
    }
}
