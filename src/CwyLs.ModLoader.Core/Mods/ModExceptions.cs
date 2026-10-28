using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// Turns the exceptions the runtime wraps around a mod's own error back into that error, and
    /// reads the assembly names out of type-load failures. Pure; unit-tested.
    /// </summary>
    public static class ModExceptions
    {
        // Mono: "... in assembly 'Other.Mod, Version=1.0.0.0, ...'" / "Could not load file or assembly 'Other.Mod, ...'".
        private static readonly Regex AssemblyInMessage =
            new Regex(@"assembly\s*[:']\s*'?([A-Za-z0-9_.\-]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// The exception the author wants to see: reflection wraps constructor and invoke failures in
        /// <see cref="TargetInvocationException"/>, and a throwing static constructor surfaces as
        /// <see cref="TypeInitializationException"/>. Both are peeled, repeatedly.
        /// </summary>
        /// <param name="error">What was caught.</param>
        /// <returns>The innermost mod exception; <paramref name="error"/> itself when nothing wraps it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
        public static Exception Unwrap(Exception error)
        {
            if (error is null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            var current = error;
            while ((current is TargetInvocationException || current is TypeInitializationException) && current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current;
        }

        /// <summary>
        /// The simple names of the assemblies a set of loader exceptions could not find: the
        /// <c>FileName</c> of a <c>FileNotFoundException</c>/<c>FileLoadException</c>, else a name
        /// quoted in the message. Distinct, in first-seen order.
        /// </summary>
        /// <param name="loaderExceptions">The <c>ReflectionTypeLoadException.LoaderExceptions</c>; null entries are skipped.</param>
        /// <returns>The names. Never null.</returns>
        public static IReadOnlyList<string> MissingAssemblies(IEnumerable<Exception?>? loaderExceptions)
        {
            var names = new List<string>();
            if (loaderExceptions is null)
            {
                return names;
            }

            foreach (var error in loaderExceptions)
            {
                if (error is null)
                {
                    continue;
                }

                string? name = null;
                if (error is System.IO.FileNotFoundException notFound && !string.IsNullOrEmpty(notFound.FileName))
                {
                    name = ModAssemblyResolver.SimpleName(notFound.FileName);
                }
                else if (error is System.IO.FileLoadException loadFailed && !string.IsNullOrEmpty(loadFailed.FileName))
                {
                    name = ModAssemblyResolver.SimpleName(loadFailed.FileName);
                }
                else
                {
                    var match = AssemblyInMessage.Match(error.Message ?? string.Empty);
                    if (match.Success)
                    {
                        name = match.Groups[1].Value.TrimEnd('.');
                    }
                }

                if (!string.IsNullOrEmpty(name) && !ContainsIgnoreCase(names, name!))
                {
                    names.Add(name!);
                }
            }

            return names;
        }

        /// <summary>
        /// The explanation at the end of the loader's "N type(s) could not load" warning.
        /// It names the likely cause instead of always blaming a CWY:LS Mod Loader version:
        /// an optional dependency that is not running, a missing private DLL, or a framework mismatch.
        /// </summary>
        /// <param name="missingAssemblies">From <see cref="MissingAssemblies"/>.</param>
        /// <param name="optionalDependenciesNotRunning">Ids of this mod's optional dependencies that are not installed or did not load.</param>
        /// <returns>One sentence fragment, starting lowercase, no trailing period.</returns>
        public static string TypeLoadHint(IReadOnlyList<string>? missingAssemblies, IReadOnlyList<string>? optionalDependenciesNotRunning)
        {
            var missing = missingAssemblies ?? Array.Empty<string>();
            var optional = optionalDependenciesNotRunning ?? Array.Empty<string>();

            foreach (var name in missing)
            {
                if (name.StartsWith("CwyLs.ModLoader", StringComparison.OrdinalIgnoreCase))
                {
                    return "it references " + name + " as it was in another CWY:LS Mod Loader version; rebuild it against this one";
                }
            }

            if (missing.Count > 0 && optional.Count > 0)
            {
                return "they need " + string.Join(", ", missing) + ", which is not loaded - the optional dependency " +
                       string.Join(", ", optional) + " is not running. Keep types that use an optional dependency " +
                       "out of the entry class and call them only after helper.ModRegistry.IsLoaded(...)";
            }

            if (missing.Count > 0)
            {
                return "they need " + string.Join(", ", missing) + ", which is not loaded and not in the mod folder; " +
                       "ship it next to the entry DLL or declare the mod that provides it in dependsOn";
            }

            return optional.Count > 0
                ? "an optional dependency (" + string.Join(", ", optional) + ") is not running, or it was built against another CWY:LS Mod Loader version"
                : "was it built against another CWY:LS Mod Loader version?";
        }

        private static bool ContainsIgnoreCase(List<string> names, string name)
        {
            foreach (var existing in names)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
