using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Packs
{
    /// <summary>
    /// An <see cref="IPackSource"/> backed by one folder under <c>CWYLS Mods/</c>. Reading is done
    /// once, in the constructor, so a pack that changes on disk mid-scan cannot produce a manifest
    /// and a file listing that disagree.
    /// <para>
    /// Nothing here throws: a pack folder is untrusted input, and an unreadable one must be
    /// reported as a skipped pack, not as an exception that stops every other pack from loading.
    /// Every failure is logged and turned into a missing manifest or an empty listing.
    /// </para>
    /// </summary>
    public sealed class DirectoryPackSource : IPackSource
    {
        private static readonly string[] NoFiles = new string[0];

        private DirectoryPackSource(string id, string folderName, string rootPath, string? manifestText, IReadOnlyList<string> files)
        {
            Id = id;
            FolderName = folderName;
            RootPath = rootPath;
            ManifestText = manifestText;
            Files = files;
        }

        /// <inheritdoc />
        public string Id { get; }

        /// <inheritdoc />
        public string FolderName { get; }

        /// <summary>Absolute path of the pack folder.</summary>
        public string RootPath { get; }

        /// <inheritdoc />
        public string? ManifestText { get; }

        /// <inheritdoc />
        public IReadOnlyList<string> Files { get; }

        /// <summary>Reads one pack folder.</summary>
        /// <param name="rootPath">Absolute path of the folder, e.g. <c>&lt;CWYLS Mods&gt;/cwy.example</c>.</param>
        /// <returns>
        /// A source describing the folder. A folder with no readable <c>manifest.json</c> still
        /// produces a source, with a null <see cref="ManifestText"/>, so the resolver can report
        /// it as <see cref="PackSkipReason.MissingManifest"/> rather than silently ignoring it.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="rootPath"/> is null or blank.</exception>
        public static DirectoryPackSource Read(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("a pack folder path is required", nameof(rootPath));
            }

            // A trailing separator would make the relative paths below start with one, so it goes
            // before anything else uses the path.
            var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Length == 0)
            {
                root = rootPath;
            }

            var folderName = new DirectoryInfo(root).Name;
            var manifestText = ReadManifest(root, folderName);
            var files = manifestText is null ? NoFiles : ListFiles(root, folderName);

            return new DirectoryPackSource(folderName, folderName, root, manifestText, files);
        }

        private static string? ReadManifest(string rootPath, string folderName)
        {
            var path = Path.Combine(rootPath, PackContentIndex.ManifestFileName);
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                // Permissions, a locked file, a broken symlink: all become "no manifest", which the
                // resolver reports as a skip. The reason is logged here because only this layer
                // knows it.
                CwyLsModLoader.Log.LogWarning(
                    $"pack '{folderName}': cannot read {PackContentIndex.ManifestFileName}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static IReadOnlyList<string> ListFiles(string rootPath, string folderName)
        {
            try
            {
                var prefixLength = rootPath.Length + 1;
                var files = new List<string>();

                foreach (var path in Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(path);

                    // Dot-files are bookkeeping, not content: .gitkeep holds an empty placeholder
                    // folder in the repo and .DS_Store is macOS noise. Neither is a pack asset.
                    if (name.Length > 0 && name[0] == '.')
                    {
                        continue;
                    }

                    if (path.Length > prefixLength)
                    {
                        files.Add(path.Substring(prefixLength));
                    }
                }

                return files;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"pack '{folderName}': cannot list files: {ex.GetType().Name}: {ex.Message}");
                return NoFiles;
            }
        }
    }
}
