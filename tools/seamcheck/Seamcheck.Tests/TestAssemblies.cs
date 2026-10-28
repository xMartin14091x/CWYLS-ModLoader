using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CwyLs.ModLoader.Core;

namespace CWY.Seamcheck.Tests
{
    /// <summary>Shared helpers: this assembly as a metadata-only set, and paths of the real game folders.</summary>
    internal static class TestAssemblies
    {
        /// <summary>Simple name of this test assembly, as patch targets name it.</summary>
        internal const string Self = "Seamcheck.Tests";

        /// <summary>This test assembly's file.</summary>
        internal static string SelfPath => typeof(TestAssemblies).Assembly.Location;

        /// <summary>
        /// A metadata-only set over the running .NET's framework folder, this assembly and
        /// CwyLs.ModLoader.Core, with System.Private.CoreLib as the core library - the same types the
        /// runtime resolver sees in this process.
        /// </summary>
        internal static ManagedAssemblySet OpenSelf()
        {
            var paths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
            paths.Add(SelfPath);
            paths.Add(typeof(PatchTarget).Assembly.Location);
            return new ManagedAssemblySet(paths, "System.Private.CoreLib", "test assemblies", ToolLog.Null);
        }

        /// <summary>The macOS game's Managed/ folder on this machine.</summary>
        internal static string MacManaged =>
            SeamcheckOptions.DefaultManaged("mac", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null);

        /// <summary>The Windows reference copy, found by walking up from the test binary to the repo root.</summary>
        internal static string WinManaged
        {
            get
            {
                var root = FindRepoRoot();
                return root == null ? "(repo root not found)" : Path.Combine(root, SeamcheckOptions.WindowsReferenceFolder);
            }
        }

        /// <summary>
        /// The repository root (the folder with CwyLs.ModLoader.sln): the path the build stamped into this
        /// assembly, else a walk up from the working directory.
        /// </summary>
        internal static string? FindRepoRoot()
        {
            var stamped = typeof(TestAssemblies).Assembly
                .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                .OfType<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
            if (stamped != null && File.Exists(Path.Combine(stamped, "CwyLs.ModLoader.sln")))
            {
                return stamped;
            }

            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CwyLs.ModLoader.sln")))
                {
                    return dir.FullName;
                }
            }

            return null;
        }

        /// <summary>The Managed/ folder for a platform key.</summary>
        /// <param name="key"><c>mac</c> or <c>win</c>.</param>
        internal static string Managed(string key) => key == "win" ? WinManaged : MacManaged;

        /// <summary>The CWY plugin assemblies the build copied next to the tests.</summary>
        internal static IReadOnlyList<string> PluginAssemblies() =>
            RegistrationScanner.DiscoverPluginAssemblies(
                AppContext.BaseDirectory,
                new[] { "seamcheck", Self },
                ToolLog.Null)
            .ToList();
    }

    /// <summary>A fact that is reported as skipped, with the reason, when a game's Managed/ folder is not on this machine.</summary>
    public sealed class ManagedFolderFactAttribute : Xunit.FactAttribute
    {
        /// <summary>Creates the attribute.</summary>
        /// <param name="platform"><c>mac</c> or <c>win</c>.</param>
        public ManagedFolderFactAttribute(string platform)
        {
            var path = TestAssemblies.Managed(platform);
            if (!Directory.Exists(path))
            {
                Skip = "the " + platform + " Managed folder is not on this machine: " + path;
            }
        }
    }
}
