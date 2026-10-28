using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FRONTDOOR: the manifests the repo ships - the sample mod's pack folder, its source and the
    /// mod template - go through the real parser and resolver, so a broken manifest fails here and
    /// not in the game.
    /// </summary>
    public class ModsShippedManifestTests
    {
        [RepoFileFact("packs/cwy.sample.hello/CwySampleHello.dll")]
        public void The_sample_pack_resolves_as_a_code_mod_with_its_dll()
        {
            var folder = Path.Combine(RepoRoot(), "packs", "cwy.sample.hello");
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(folder.Length + 1))
                .ToArray();

            var resolution = PackResolver.Resolve(
                new[] { new Source("cwy.sample.hello", File.ReadAllText(Path.Combine(folder, "manifest.json")), files) },
                "1.17.3",
                CwyApi.Version);

            var pack = Assert.Single(resolution.Loaded);
            Assert.True(pack.Manifest.IsCodeMod);
            Assert.Equal("CwySampleHello.dll", pack.Manifest.EntryDll);
            Assert.True(CwyApi.Satisfies(pack.Manifest.LoaderVersion));
            Assert.Single(ModLoadPlanner.Plan(resolution));
            Assert.Empty(ModAssemblyRules.FindBundledShared(files, pack.Manifest.EntryDll, null));
        }

        [Fact]
        public void The_sample_pack_manifest_is_the_one_built_from_source()
        {
            var root = RepoRoot();

            Assert.Equal(
                File.ReadAllText(Path.Combine(root, "mods-src", "cwy.sample.hello", "manifest.json")),
                File.ReadAllText(Path.Combine(root, "packs", "cwy.sample.hello", "manifest.json")));
        }

        [Fact]
        public void The_template_manifest_is_a_valid_code_mod()
        {
            var manifest = PackManifestParser.Parse(
                File.ReadAllText(Path.Combine(RepoRoot(), "templates", "cwy-mod", "manifest.json")));

            Assert.True(manifest.IsCodeMod);
            Assert.Equal("MyMod.dll", manifest.EntryDll);
            Assert.Equal(CwyApi.Version, manifest.LoaderVersion);
        }

        /// <summary>
        /// The pack step of the template and of the sample leaves a DLL out by the loader's own
        /// family rule (<see cref="ModAssemblyRules.SharedFamilies"/>); the regex in each project
        /// must say the same, or a mod could ship a copy of the loader.
        /// </summary>
        [Theory]
        [InlineData("templates/cwy-mod/MyMod.csproj")]
        [InlineData("mods-src/cwy.sample.hello/cwy.sample.hello.csproj")]
        public void The_pack_step_leaves_out_exactly_the_loaders_shared_families(string project)
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), project.Replace('/', Path.DirectorySeparatorChar)));
            var match = Regex.Match(text, "<_CwySharedFamilies>(.*?)</_CwySharedFamilies>");
            Assert.True(match.Success, project + " has no _CwySharedFamilies");
            var families = new Regex(match.Groups[1].Value);

            var names = ModAssemblyRules.SharedFamilies
                .SelectMany(f => new[] { f, f + ".Core", f + "-extra", f.ToUpperInvariant(), f + "Extra", "x" + f })
                .Concat(new[] { "MyMod", "Newtonsoft.Json", "System.Collections.Immutable", "MyMod.Helpers" });
            foreach (var name in names)
            {
                Assert.True(ModAssemblyRules.IsShared(name, null) == families.IsMatch(name), project + ": " + name);
            }
        }

        /// <summary>The checkout, from this source file's path, so the tests pass under --artifacts-path too (review #17).</summary>
        private static string RepoRoot() =>
            TestRepo.Root ?? throw new InvalidOperationException("CwyLs.ModLoader.sln not found above the test source or " + AppContext.BaseDirectory);

        private sealed class Source : IPackSource
        {
            internal Source(string folder, string manifest, IReadOnlyList<string> files)
            {
                Id = folder;
                FolderName = folder;
                ManifestText = manifest;
                Files = files;
            }

            public string Id { get; }

            public string FolderName { get; }

            public string? ManifestText { get; }

            public IReadOnlyList<string> Files { get; }
        }
    }
}
