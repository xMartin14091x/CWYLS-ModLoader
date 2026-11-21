using System;
using System.Collections.Generic;
using System.IO;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>
    /// One pack as the catalog sees it: a pack that loaded this session (with its folder), or one
    /// that was found and refused (with the reason). Built by the adapter from the pack registry.
    /// </summary>
    public sealed class MakerPackSource
    {
        private static readonly string[] NoLanguages = new string[0];

        /// <summary>Creates a source.</summary>
        /// <param name="id">The pack id (or, for a pack whose manifest could not be read, its folder name). Required.</param>
        /// <param name="name">The manifest name; blank falls back to the id.</param>
        /// <param name="version">The manifest version; null is stored as empty.</param>
        /// <param name="rootPath">The absolute pack folder, or null when it is not known.</param>
        /// <param name="isCodeMod">The manifest names an <c>entryDll</c>.</param>
        /// <param name="declaredLanguages">The codes the pack's <c>language.json</c> registered; null means none.</param>
        /// <param name="loadProblem">Why the pack did not load this session, or null when it loaded.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is blank.</exception>
        public MakerPackSource(
            string id,
            string? name,
            string? version,
            string? rootPath,
            bool isCodeMod,
            IReadOnlyList<string>? declaredLanguages = null,
            string? loadProblem = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a pack id is required", nameof(id));
            }

            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? id : name!;
            Version = version ?? string.Empty;
            RootPath = string.IsNullOrWhiteSpace(rootPath) ? null : rootPath;
            IsCodeMod = isCodeMod;
            DeclaredLanguages = declaredLanguages ?? NoLanguages;
            LoadProblem = loadProblem;
        }

        /// <summary>The pack id.</summary>
        public string Id { get; }

        /// <summary>The display name.</summary>
        public string Name { get; }

        /// <summary>The version text, or empty.</summary>
        public string Version { get; }

        /// <summary>The absolute pack folder, or null.</summary>
        public string? RootPath { get; }

        /// <summary>The pack is a code mod (its manifest names an <c>entryDll</c>).</summary>
        public bool IsCodeMod { get; }

        /// <summary>The language codes its <c>language.json</c> registered. Never null.</summary>
        public IReadOnlyList<string> DeclaredLanguages { get; }

        /// <summary>Why the pack did not load this session, or null when it loaded.</summary>
        public string? LoadProblem { get; }

        /// <summary>True when the pack loaded this session.</summary>
        public bool Loaded => LoadProblem is null;

        /// <inheritdoc />
        public override string ToString() => Id + (Loaded ? string.Empty : " (not loaded: " + LoadProblem + ")");
    }

    /// <summary>Whether Pack Maker may write to a pack, and why not when it may not.</summary>
    public readonly struct MakerPackAccess
    {
        /// <summary>Creates a decision.</summary>
        /// <param name="editable">Pack Maker may write to it.</param>
        /// <param name="reason">Why it is read-only; null when editable.</param>
        public MakerPackAccess(bool editable, string? reason)
        {
            Editable = editable;
            Reason = editable ? null : reason;
        }

        /// <summary>Pack Maker may write to the pack.</summary>
        public bool Editable { get; }

        /// <summary>One sentence saying why it is read-only, or null.</summary>
        public string? Reason { get; }

        /// <inheritdoc />
        public override string ToString() => Editable ? "editable" : "read-only: " + Reason;
    }

    /// <summary>
    /// CONTRACT.md "Editable packs": a pack is editable when it is a content pack (no
    /// <c>entryDll</c>) in <c>CWYLS Mods</c>, loaded this session, whose folder is writable, and
    /// which is not a reference pack. The first rule a pack fails is its reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reference packs</b> are the example and demo packs this repository ships in
    /// <c>packs/</c> (<c>cwy.example</c>, <c>cwy.demo.*</c>, <c>cwy.sample.*</c>):
    /// <c>deploy-packs.sh</c> replaces their folders wholesale, so an edit made in them would be lost
    /// on the next deploy.
    /// </para>
    /// <para>
    /// <b>Links (DS-10a).</b> A pack whose <c>data/subtitles</c> or <c>audio/voice</c> folder is, or
    /// goes through, a symbolic link or junction is read-only: Pack Maker never reads or writes through
    /// a link, because a link can point anywhere outside the pack. The pack folder itself is not
    /// checked (the loader loaded it from there; the editor's own path rule starts below it too).
    /// </para>
    /// </remarks>
    public static class MakerPackRules
    {
        /// <summary>Reason: the pack was found but did not load this session (the detail follows).</summary>
        public const string NotLoadedReason = "It did not load this session: ";

        /// <summary>Reason: the pack is a code mod.</summary>
        public const string CodeModReason = "It is a code mod (entryDll); Pack Maker writes to content packs only.";

        /// <summary>Reason: the pack is one of the loader's reference packs.</summary>
        public const string ReferencePackReason =
            "It is a reference pack that ships with the loader and is replaced on every deploy; copy it to a pack of your own.";

        /// <summary>Reason: the pack folder is not a folder of <c>CWYLS Mods</c>.</summary>
        public const string OutsidePacksRootReason = "It is not in the CWYLS Mods folder.";

        /// <summary>Reason: the pack folder cannot be written to.</summary>
        public const string NotWritableReason = "Its folder is read-only.";

        /// <summary>Reason: a content folder is, or goes through, a symbolic link or junction.</summary>
        /// <param name="folder">The pack-relative folder, e.g. <c>data/subtitles</c>.</param>
        public static string LinkedFolderReason(string folder) =>
            "Its " + folder + " folder is a symbolic link or junction (or goes through one); Pack Maker does not read or write through links. Make it a real folder.";

        /// <summary>The pack folders Pack Maker reads and writes, pack-relative with forward slashes.</summary>
        public static IReadOnlyList<string> ContentFolders { get; } = new[] { "data/subtitles", "audio/voice" };

        /// <summary>The reference pack ids matched exactly.</summary>
        public static IReadOnlyList<string> ReferencePackIds { get; } = new[] { "cwy.example" };

        /// <summary>The reference pack id prefixes.</summary>
        public static IReadOnlyList<string> ReferencePackPrefixes { get; } = new[] { "cwy.demo.", "cwy.sample." };

        /// <summary>True when the id is one of the loader's reference packs.</summary>
        /// <param name="id">A pack id.</param>
        public static bool IsReferencePack(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            foreach (var exact in ReferencePackIds)
            {
                if (string.Equals(id, exact, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var prefix in ReferencePackPrefixes)
            {
                if (id!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="folder"/> is a direct child of <paramref name="parent"/>, compared
        /// as full paths, ignoring case (the game's folders live on case-insensitive file systems on
        /// both platforms it ships for).
        /// </summary>
        /// <param name="folder">A pack folder.</param>
        /// <param name="parent">The <c>CWYLS Mods</c> folder.</param>
        public static bool IsDirectChildOf(string? folder, string? parent)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            try
            {
                var full = Trim(Path.GetFullPath(folder!));
                var root = Trim(Path.GetFullPath(parent!));
                var container = Path.GetDirectoryName(full);
                return container != null && string.Equals(Trim(container), root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>Decides whether Pack Maker may write to a pack.</summary>
        /// <param name="pack">The pack.</param>
        /// <param name="packsRoot">The absolute <c>CWYLS Mods</c> folder.</param>
        /// <param name="isWritable">Answers whether a folder may be written to, without writing to it.</param>
        public static MakerPackAccess Decide(MakerPackSource pack, string? packsRoot, Func<string, bool>? isWritable)
        {
            if (pack is null)
            {
                throw new ArgumentNullException(nameof(pack));
            }

            if (!pack.Loaded)
            {
                return new MakerPackAccess(false, NotLoadedReason + Sentence(pack.LoadProblem));
            }

            if (pack.IsCodeMod)
            {
                return new MakerPackAccess(false, CodeModReason);
            }

            if (IsReferencePack(pack.Id))
            {
                return new MakerPackAccess(false, ReferencePackReason);
            }

            if (pack.RootPath is null || !IsDirectChildOf(pack.RootPath, packsRoot))
            {
                return new MakerPackAccess(false, OutsidePacksRootReason);
            }

            var linked = LinkedContentFolder(pack.RootPath);
            if (linked != null)
            {
                return new MakerPackAccess(false, LinkedFolderReason(linked));
            }

            bool writable;
            try
            {
                writable = isWritable?.Invoke(pack.RootPath) ?? false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                writable = false;
            }

            return writable ? new MakerPackAccess(true, null) : new MakerPackAccess(false, NotWritableReason);
        }

        /// <summary>
        /// True when a folder exists and is not marked read-only. Decided from the file system's
        /// attributes, never by writing a probe file: Pack Maker writes only subtitle tables and dubs.
        /// </summary>
        /// <param name="folder">An absolute folder path.</param>
        public static bool IsWritableFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return false;
            }

            return (File.GetAttributes(folder) & FileAttributes.ReadOnly) == 0;
        }

        /// <summary>
        /// The first of <see cref="ContentFolders"/> that is, or goes through, a link below the pack
        /// folder (e.g. <c>data/subtitles</c> when <c>data</c> is a link), or null when none is. A folder
        /// that does not exist is no link.
        /// </summary>
        /// <param name="packRoot">The absolute pack folder.</param>
        public static string? LinkedContentFolder(string? packRoot)
        {
            if (string.IsNullOrWhiteSpace(packRoot))
            {
                return null;
            }

            foreach (var folder in ContentFolders)
            {
                if (FirstLinkBelow(packRoot!, folder) != null)
                {
                    return folder;
                }
            }

            return null;
        }

        /// <summary>
        /// The first step of a pack-relative path, below <paramref name="root"/>, that is a link (as a
        /// pack-relative path), or null when no existing step is.
        /// </summary>
        /// <param name="root">The absolute pack folder (not itself checked).</param>
        /// <param name="relative">A pack-relative path with forward slashes.</param>
        internal static string? FirstLinkBelow(string root, string relative)
        {
            var step = root;
            var walked = string.Empty;
            foreach (var segment in relative.Split('/'))
            {
                step = Path.Combine(step, segment);
                walked = walked.Length == 0 ? segment : walked + "/" + segment;
                if (IsLink(step))
                {
                    return walked;
                }
            }

            return null;
        }

        /// <summary>
        /// True when the entry at <paramref name="path"/> is a symbolic link or junction (a reparse point),
        /// dangling or not, or when that cannot be told (treated as unsafe). False when nothing is there.
        /// </summary>
        /// <param name="path">An absolute path.</param>
        public static bool IsLink(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return true;
            }
        }

        private static string Sentence(string? text)
        {
            var trimmed = string.IsNullOrWhiteSpace(text) ? "no reason was recorded" : text!.Trim();
            return trimmed.EndsWith(".", StringComparison.Ordinal) ? trimmed : trimmed + ".";
        }

        private static string Trim(string path) =>
            path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }
}
