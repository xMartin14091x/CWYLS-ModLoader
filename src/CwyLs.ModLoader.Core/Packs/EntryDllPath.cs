using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// The rules for a manifest's <c>entryDll</c>: a relative path inside the pack folder that
    /// names the code mod's assembly. Pure string logic, shared by the manifest
    /// parser, <see cref="PackManifest"/> and <see cref="PackResolver"/>, so all three agree on
    /// what a valid path is and what it matches.
    /// </summary>
    public static class EntryDllPath
    {
        /// <summary>The file extension an entry assembly must have.</summary>
        public const string Extension = ".dll";

        /// <summary>
        /// Normalises and validates a raw <c>entryDll</c> value.
        /// </summary>
        /// <param name="raw">The value as written in the manifest.</param>
        /// <param name="normalised">
        /// The path with forward slashes and no leading <c>./</c>, e.g. <c>MyMod.dll</c> or
        /// <c>bin/MyMod.dll</c>; null when invalid.
        /// </param>
        /// <param name="problem">Why it is invalid, written for the pack author; null when valid.</param>
        /// <returns>True when the value is a usable relative path to a <c>.dll</c> inside the pack.</returns>
        public static bool TryNormalise(string? raw, out string? normalised, out string? problem)
        {
            normalised = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                problem = "must name the mod's .dll, e.g. \"MyMod.dll\"";
                return false;
            }

            var text = raw!.Trim();

            // A rooted path would silently lose its root in PackContentIndex.Normalise and then
            // match a file of the same name inside the pack; refuse it by name instead.
            if (text.StartsWith("/", StringComparison.Ordinal)
                || text.StartsWith("\\", StringComparison.Ordinal)
                || text.IndexOf(':') >= 0)
            {
                problem = "\"" + text + "\" must be a path relative to the pack folder, not an absolute path";
                return false;
            }

            var path = PackContentIndex.Normalise(text);
            if (path is null)
            {
                problem = "\"" + text + "\" is not a path inside the pack folder";
                return false;
            }

            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    problem = "\"" + text + "\" must not contain empty, '.' or '..' segments";
                    return false;
                }
            }

            var fileName = FileName(path);
            if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                || fileName.Length == Extension.Length)
            {
                problem = "\"" + text + "\" must name a .dll file";
                return false;
            }

            normalised = path;
            problem = null;
            return true;
        }

        /// <summary>
        /// True when a pack's file listing contains the entry assembly. Case-insensitive, because
        /// both macOS and Windows file systems are by default, and a manifest that says
        /// <c>mymod.dll</c> for <c>MyMod.dll</c> loads there.
        /// </summary>
        /// <param name="files">The pack's files, relative to its root, any separator style.</param>
        /// <param name="entryDll">A normalised <c>entryDll</c>.</param>
        public static bool IsListed(IEnumerable<string>? files, string? entryDll)
        {
            if (files is null || string.IsNullOrEmpty(entryDll))
            {
                return false;
            }

            foreach (var raw in files)
            {
                var path = PackContentIndex.Normalise(raw);
                if (path != null && string.Equals(path, entryDll, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The assembly's simple name as the file names it: <c>bin/MyMod.dll</c> gives
        /// <c>MyMod</c>. The real name lives in the assembly's metadata; this is the name the
        /// runtime probes for.
        /// </summary>
        /// <param name="path">A relative path to a <c>.dll</c>.</param>
        public static string SimpleName(string path)
        {
            var fileName = FileName(path ?? string.Empty);
            return fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - Extension.Length)
                : fileName;
        }

        private static string FileName(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }
    }
}
