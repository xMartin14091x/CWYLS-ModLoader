using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// A content pack's or code mod's <c>manifest.json</c>, parsed and validated. Immutable; build
    /// one with <see cref="PackManifestParser"/> rather than by hand outside tests.
    /// <para>The schema (only <c>id</c>, <c>name</c> and <c>version</c> are required):</para>
    /// <code>
    /// {
    ///   "id": "cwy.example",                  // ^[a-z0-9][a-z0-9._-]{2,63}$ - unique, stable, lowercase
    ///   "name": "CWY Example Pack",           // shown to humans
    ///   "version": "0.1.0",                   // SemVer 2.0.0
    ///   "author": "you",                      // optional
    ///   "description": "what it does",        // optional
    ///   "gameVersion": "1.17.3",              // optional; "made and tested with" - a different build loads with a warning
    ///   "minGameVersion": "1.17.0",           // optional SemVer; oldest game build it supports (inclusive), else skipped
    ///   "maxGameVersion": "1.17.99",          // optional SemVer; newest game build it supports (inclusive), else skipped
    ///   "loaderVersion": "0.9.0",     // optional; oldest CWY:LS Mod Loader (API version) that can load it
    ///   "entryDll": "MyMod.dll",              // optional; makes this a code mod
    ///   "contentPackFor": "some.codemod",     // optional; this pack is data for that code mod (a required dependency)
    ///   "dependsOn": [                        // optional
    ///     { "id": "other.pack", "minVersion": "1.2.0" },
    ///     { "id": "nice.to.have", "optional": true },
    ///     "another.pack"                      // shorthand for { "id": "another.pack" }
    ///   ]
    /// }
    /// </code>
    /// </summary>
    public sealed class PackManifest
    {
        /// <summary>The pattern every pack id must match, as a human-readable string.</summary>
        public const string IdPattern = "^[a-z0-9][a-z0-9._-]{2,63}$";

        // \A and \z rather than ^ and $: in .NET, $ also matches before a trailing newline, so
        // "cwy.example\n" would otherwise pass and then be used as a folder name.
        private static readonly Regex IdRegex =
            new Regex(@"\A[a-z0-9][a-z0-9._-]{2,63}\z", RegexOptions.CultureInvariant);

        private static readonly IReadOnlyList<PackDependency> NoDependencies =
            new ReadOnlyCollection<PackDependency>(new PackDependency[0]);

        private static readonly IReadOnlyDictionary<string, object?> NoExtensions =
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(0, StringComparer.Ordinal));

        /// <summary>Creates a manifest. Validates <paramref name="id"/> against <see cref="IdPattern"/>.</summary>
        /// <param name="id">Unique pack id.</param>
        /// <param name="name">Human-readable name.</param>
        /// <param name="version">The pack's own version.</param>
        /// <param name="author">Author, or null.</param>
        /// <param name="description">One-line description, or null.</param>
        /// <param name="gameVersion">Game build this pack was made and tested with, or null for "not stated".</param>
        /// <param name="loaderVersion">Oldest framework version that can load it, or null for "any".</param>
        /// <param name="dependsOn">Packs that must load first, or null for none.</param>
        /// <param name="minGameVersion">Oldest game build it supports (inclusive), or null for no lower bound.</param>
        /// <param name="maxGameVersion">Newest game build it supports (inclusive), or null for no upper bound.</param>
        /// <param name="entryDll">
        /// Relative path of the code mod's assembly inside the pack folder, or null for a content
        /// pack. Normalised with <see cref="EntryDllPath.TryNormalise"/>.
        /// </param>
        /// <param name="contentPackFor">
        /// The code mod this pack is data for, or null. Must be required (not optional), must not be
        /// the pack itself, and cannot be combined with <paramref name="entryDll"/>.
        /// </param>
        /// <param name="extensions">The manifest's <c>x-</c> members, or null for none.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is malformed, <paramref name="name"/> is blank,
        /// <paramref name="minGameVersion"/> is above <paramref name="maxGameVersion"/>,
        /// <paramref name="entryDll"/> is not a usable path, or <paramref name="contentPackFor"/>
        /// breaks one of its rules.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="version"/> is null.</exception>
        public PackManifest(
            string id,
            string name,
            SemVer version,
            string? author = null,
            string? description = null,
            string? gameVersion = null,
            SemVer? loaderVersion = null,
            IReadOnlyList<PackDependency>? dependsOn = null,
            SemVer? minGameVersion = null,
            SemVer? maxGameVersion = null,
            string? entryDll = null,
            PackDependency? contentPackFor = null,
            IReadOnlyDictionary<string, object?>? extensions = null)
        {
            if (!IsValidId(id))
            {
                throw new ArgumentException("\"" + id + "\" does not match " + IdPattern, nameof(id));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a pack name is required", nameof(name));
            }

            if (minGameVersion != null && maxGameVersion != null && minGameVersion > maxGameVersion)
            {
                throw new ArgumentException(
                    "minGameVersion " + minGameVersion + " is above maxGameVersion " + maxGameVersion,
                    nameof(minGameVersion));
            }

            string? normalisedEntryDll = null;
            if (entryDll != null && !EntryDllPath.TryNormalise(entryDll, out normalisedEntryDll, out var entryProblem))
            {
                throw new ArgumentException("entryDll " + entryProblem, nameof(entryDll));
            }

            if (contentPackFor != null)
            {
                if (normalisedEntryDll != null)
                {
                    throw new ArgumentException(
                        "a manifest is either a code mod (entryDll) or a content pack for one (contentPackFor), not both",
                        nameof(contentPackFor));
                }

                if (contentPackFor.IsOptional)
                {
                    throw new ArgumentException("contentPackFor is always a required dependency", nameof(contentPackFor));
                }

                if (string.Equals(contentPackFor.Id, id, StringComparison.Ordinal))
                {
                    throw new ArgumentException("a pack cannot be a content pack for itself", nameof(contentPackFor));
                }
            }

            Id = id;
            Name = name.Trim();
            Version = version ?? throw new ArgumentNullException(nameof(version));
            Author = author;
            Description = description;
            GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion!.Trim();
            LoaderVersion = loaderVersion;

            // Read-only copies (API freeze): a caller - a mod through its manifest view -
            // can neither cast them back to a list and clear it, nor change them through the
            // collection it passed in.
            DependsOn = dependsOn is null || dependsOn.Count == 0
                ? NoDependencies
                : new ReadOnlyCollection<PackDependency>(new List<PackDependency>(dependsOn));
            MinGameVersion = minGameVersion;
            MaxGameVersion = maxGameVersion;
            EntryDll = normalisedEntryDll;
            ContentPackFor = contentPackFor;
            Extensions = extensions is null || extensions.Count == 0
                ? NoExtensions
                : new ReadOnlyDictionary<string, object?>(Copy(extensions));
            AllDependencies = CombineDependencies(DependsOn, contentPackFor);
        }

        /// <summary>Unique, stable, lowercase pack id; also the folder name under <c>CWYLS Mods/</c>.</summary>
        public string Id { get; }

        /// <summary>Human-readable name, shown in the startup summary.</summary>
        public string Name { get; }

        /// <summary>The pack's own version.</summary>
        public SemVer Version { get; }

        /// <summary>Author, or null when the manifest omits it.</summary>
        public string? Author { get; }

        /// <summary>One-line description, or null when the manifest omits it.</summary>
        public string? Description { get; }

        /// <summary>
        /// The game build this pack was made and tested with, e.g. <c>1.17.3</c>, or null when the
        /// manifest does not say. Informational: a different running build still loads the pack,
        /// with one warning naming both versions (<see cref="PackGameVersionCheck"/>).
        /// </summary>
        public string? GameVersion { get; }

        /// <summary>
        /// Oldest game build this pack supports, inclusive, or null for no lower bound. An older
        /// running game skips the pack as <see cref="PackSkipReason.GameVersionTooOld"/>.
        /// </summary>
        public SemVer? MinGameVersion { get; }

        /// <summary>
        /// Newest game build this pack supports, inclusive, or null for no upper bound. A newer
        /// running game skips the pack as <see cref="PackSkipReason.GameVersionTooNew"/>.
        /// </summary>
        public SemVer? MaxGameVersion { get; }

        /// <summary>
        /// Oldest CWY:LS Mod Loader API version that can load this pack or mod, or null meaning "any".
        /// A pack that uses a feature added in 0.9.0 declares 0.9.0 and is skipped on an older
        /// framework instead of half-loading. The single minimum-framework field for content
        /// packs and code mods alike.
        /// </summary>
        public SemVer? LoaderVersion { get; }

        /// <summary>
        /// Packs that must load before this one, as written in <c>dependsOn</c>. A read-only copy;
        /// never null, empty when there are none. See <see cref="AllDependencies"/> for the list the
        /// resolver uses.
        /// </summary>
        public IReadOnlyList<PackDependency> DependsOn { get; }

        /// <summary>
        /// Relative path of the code mod's assembly inside the pack folder (forward slashes), or
        /// null for a content pack. A pack with an <c>entryDll</c> is a code mod; its content
        /// folders (textures, audio, data) load exactly like a content pack's.
        /// </summary>
        public string? EntryDll { get; }

        /// <summary>True when this manifest declares an <see cref="EntryDll"/>.</summary>
        public bool IsCodeMod => EntryDll != null;

        /// <summary>
        /// The code mod this pack is data for, or null. Always a required dependency; the code
        /// mod reads such packs through its helper's content-pack list.
        /// </summary>
        public PackDependency? ContentPackFor { get; }

        /// <summary>
        /// The manifest's <c>x-</c> members, in document order, as parsed JSON values (strings,
        /// numbers, booleans, null, <c>JsonObject</c>s and lists). The framework never reads them.
        /// A read-only copy of the dictionary (the parsed values inside are the parser's own
        /// objects); never null.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Extensions { get; }

        /// <summary>
        /// Every dependency the resolver applies: <see cref="DependsOn"/> followed by
        /// <see cref="ContentPackFor"/> when it is set. Never null.
        /// </summary>
        public IReadOnlyList<PackDependency> AllDependencies { get; }

        /// <summary>True when <paramref name="id"/> matches <see cref="IdPattern"/>.</summary>
        /// <param name="id">The candidate id.</param>
        public static bool IsValidId(string? id) => id != null && IdRegex.IsMatch(id);

        /// <summary><c>id@version</c>, the form used in the startup summary.</summary>
        public override string ToString() => Id + "@" + Version;

        /// <summary>A copy that keeps the source's insertion (document) order.</summary>
        private static Dictionary<string, object?> Copy(IReadOnlyDictionary<string, object?> source)
        {
            var copy = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
            foreach (var pair in source)
            {
                copy[pair.Key] = pair.Value;
            }

            return copy;
        }

        private static IReadOnlyList<PackDependency> CombineDependencies(
            IReadOnlyList<PackDependency> dependsOn,
            PackDependency? contentPackFor)
        {
            if (contentPackFor is null)
            {
                return dependsOn;
            }

            var all = new List<PackDependency>(dependsOn.Count + 1);
            var listed = false;
            foreach (var dependency in dependsOn)
            {
                if (string.Equals(dependency.Id, contentPackFor.Id, StringComparison.Ordinal))
                {
                    // The same pack named twice: contentPackFor makes it required, and the
                    // stricter minimum version wins.
                    listed = true;
                    var min = dependency.MinVersion is null || (contentPackFor.MinVersion != null && contentPackFor.MinVersion > dependency.MinVersion)
                        ? contentPackFor.MinVersion
                        : dependency.MinVersion;
                    all.Add(new PackDependency(dependency.Id, min));
                    continue;
                }

                all.Add(dependency);
            }

            if (!listed)
            {
                all.Add(contentPackFor);
            }

            return new ReadOnlyCollection<PackDependency>(all);
        }
    }
}
