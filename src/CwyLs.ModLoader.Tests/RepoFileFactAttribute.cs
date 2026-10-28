using System.IO;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// A <see cref="FactAttribute"/> that is skipped, with the reason shown, when a file or folder it
    /// reads is not there (build output that has not been built yet, or a local file that is not part
    /// of the repository), so such a test says why it did not run instead of failing.
    /// </summary>
    public sealed class RepoFileFactAttribute : FactAttribute
    {
        /// <param name="paths">Paths relative to the repository root, '/'-separated.</param>
        public RepoFileFactAttribute(params string[] paths)
        {
            var missing = RepoFiles.FirstMissing(paths);
            if (missing != null)
            {
                Skip = "needs " + missing + " (not built yet, or not in this checkout)";
            }
        }
    }

    /// <summary>Repository paths a test may depend on.</summary>
    internal static class RepoFiles
    {
        /// <summary>The first of <paramref name="paths"/> that does not exist, or null when all do.</summary>
        internal static string? FirstMissing(params string[] paths)
        {
            var root = TestRepo.Root;
            if (root is null)
            {
                return "the repository root";
            }

            foreach (var path in paths)
            {
                var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>True when a pack folder under <c>packs/</c> is present.</summary>
        internal static bool HasPack(string id) => FirstMissing("packs/" + id) is null;
    }
}
