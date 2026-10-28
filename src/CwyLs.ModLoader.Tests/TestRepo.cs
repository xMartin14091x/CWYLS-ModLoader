using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Finds the repository (the folder holding <c>CwyLs.ModLoader.sln</c>) for tests that read checked-in files:
    /// packs, the golden console corpus, the log viewer, templates. The compiler records this source
    /// file's path, so the answer does not depend on where the test binary was built
    /// (<c>--artifacts-path</c>, a worker's overlay tree); walking up from the binary is the fallback.
    /// </summary>
    internal static class TestRepo
    {
        private static readonly Lazy<string?> Found = new Lazy<string?>(Find);

        /// <summary>The repository root, or null when neither search finds <c>CwyLs.ModLoader.sln</c>.</summary>
        public static string? Root => Found.Value;

        private static string? Find() =>
            WalkUp(Path.GetDirectoryName(SourcePath())) ?? WalkUp(AppContext.BaseDirectory);

        private static string SourcePath([CallerFilePath] string path = "") => path;

        private static string? WalkUp(string? start)
        {
            for (var directory = start; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                if (File.Exists(Path.Combine(directory, "CwyLs.ModLoader.sln")))
                {
                    return directory;
                }
            }

            return null;
        }
    }
}
