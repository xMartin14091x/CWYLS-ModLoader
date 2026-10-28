using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CWY.Seamcheck
{
    /// <summary>
    /// A game's <c>Managed/</c> folder opened through <see cref="MetadataLoadContext"/>: types and
    /// members can be inspected, nothing can be executed, and nothing is loaded into the tool's own
    /// runtime. One set per platform checked; dispose it to release the files.
    /// </summary>
    internal sealed class ManagedAssemblySet : IDisposable
    {
        /// <summary>Core library name of a Unity Mono player (its Managed/ ships mscorlib.dll).</summary>
        internal const string UnityCoreAssembly = "mscorlib";

        private readonly MetadataLoadContext _context;
        private readonly Dictionary<string, string> _pathsByName;
        private readonly ToolLog _log;
        private List<Assembly>? _all;

        /// <summary>Opens an explicit list of assembly files.</summary>
        /// <param name="assemblyPaths">Every file the context may resolve from; one per simple name.</param>
        /// <param name="coreAssemblyName">The core library among them, e.g. <c>mscorlib</c>.</param>
        /// <param name="description">How the set is named in messages, normally its folder.</param>
        /// <param name="log">Diagnostics.</param>
        /// <exception cref="SeamcheckException">The core library is not among the files.</exception>
        internal ManagedAssemblySet(IReadOnlyList<string> assemblyPaths, string coreAssemblyName, string description, ToolLog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            Description = description ?? throw new ArgumentNullException(nameof(description));

            _pathsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in assemblyPaths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!_pathsByName.ContainsKey(name))
                {
                    _pathsByName.Add(name, path);
                }
            }

            if (!_pathsByName.ContainsKey(coreAssemblyName))
            {
                throw new SeamcheckException(
                    "core library '" + coreAssemblyName + "' is not in " + description
                    + " - is this a game's Managed folder?");
            }

            _context = new MetadataLoadContext(new PathAssemblyResolver(_pathsByName.Values), coreAssemblyName);
        }

        /// <summary>Where the assemblies come from, as shown in the report.</summary>
        internal string Description { get; }

        /// <summary>How many assembly files the set can resolve.</summary>
        internal int Count => _pathsByName.Count;

        /// <summary>Opens every <c>*.dll</c> in a Unity player's <c>Managed/</c> folder.</summary>
        /// <param name="directory">The folder.</param>
        /// <param name="log">Diagnostics.</param>
        /// <exception cref="SeamcheckException">The folder does not exist or is not a Managed folder.</exception>
        internal static ManagedAssemblySet FromManagedDirectory(string directory, ToolLog log)
        {
            if (!Directory.Exists(directory))
            {
                throw new SeamcheckException("managed folder not found: " + directory);
            }

            var paths = Directory.GetFiles(directory, "*.dll")
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

            return new ManagedAssemblySet(paths, UnityCoreAssembly, directory, log);
        }

        /// <summary>The file a simple assembly name resolves to, or null.</summary>
        /// <param name="simpleName">E.g. <c>Assembly-CSharp</c>.</param>
        internal string? PathOf(string simpleName) =>
            _pathsByName.TryGetValue(simpleName, out var path) ? path : null;

        /// <summary>
        /// Loads an assembly by simple name, or returns null (logged at debug) when the folder does
        /// not have it or it cannot be read - the metadata counterpart of the game-side
        /// <c>Assembly.Load</c> fallback.
        /// </summary>
        /// <param name="simpleName">E.g. <c>AudioManager</c>.</param>
        internal Assembly? TryLoad(string simpleName)
        {
            try
            {
                return _context.LoadFromAssemblyName(simpleName);
            }
            catch (Exception ex)
            {
                _log.Debug("assembly '" + simpleName + "' could not be loaded from " + Description
                    + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Every assembly in the set, loaded on first use. Files that do not load are skipped and
        /// logged at debug; the set is only used to enrich "type moved" messages.
        /// </summary>
        internal IReadOnlyList<Assembly> LoadAll()
        {
            if (_all != null)
            {
                return _all;
            }

            var all = new List<Assembly>();
            foreach (var name in _pathsByName.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                var assembly = TryLoad(name);
                if (assembly != null)
                {
                    all.Add(assembly);
                }
            }

            _all = all;
            return all;
        }

        /// <inheritdoc />
        public void Dispose() => _context.Dispose();
    }
}
