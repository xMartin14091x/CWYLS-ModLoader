using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-1: the load-order and refusal policy. Every case runs over fake sources, so the rules are
    /// tested without a game, a filesystem or a pack on disk.
    /// </summary>
    public class PacksResolverTests
    {
        private const string GameVersion = "1.17.2";

        private static readonly SemVer CoreVersion = SemVer.Parse("0.1.0");

        [Fact]
        public void Loads_a_single_pack_and_indexes_its_files()
        {
            var source = new FakePackSource(
                "cwy.example",
                Manifest("cwy.example", "0.1.0", gameVersion: GameVersion),
                "manifest.json", "textures/Body/_MainTex.png", "audio/voice/Voice_Test_001.ogg", "README.md");

            var resolution = Resolve(source);

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Equal("cwy.example", pack.Manifest.Id);
            Assert.Single(pack.Index.Textures);
            Assert.Single(pack.Index.Voices);
            Assert.Equal("PACKS: 1 loaded (cwy.example@0.1.0); 0 skipped", resolution.Summary);
        }

        [Fact]
        public void Orders_dependencies_before_the_packs_that_need_them()
        {
            var resolution = Resolve(
                new FakePackSource("c.pack", Manifest("c.pack", "1.0.0", dependsOn: "b.pack")),
                new FakePackSource("b.pack", Manifest("b.pack", "1.0.0", dependsOn: "a.pack")),
                new FakePackSource("a.pack", Manifest("a.pack", "1.0.0")));

            Assert.Equal(new[] { "a.pack", "b.pack", "c.pack" }, Ids(resolution));
        }

        [Fact]
        public void Keeps_discovery_order_between_packs_that_do_not_constrain_each_other()
        {
            var resolution = Resolve(
                new FakePackSource("z.pack", Manifest("z.pack", "1.0.0")),
                new FakePackSource("m.pack", Manifest("m.pack", "1.0.0")),
                new FakePackSource("a.pack", Manifest("a.pack", "1.0.0")));

            Assert.Equal(new[] { "z.pack", "m.pack", "a.pack" }, Ids(resolution));
        }

        [Fact]
        public void A_folder_without_a_manifest_is_MissingManifest()
        {
            var resolution = Resolve(new FakePackSource("not-a-pack", manifestText: null));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.MissingManifest, skip.Reason);
            Assert.Equal("not-a-pack", skip.Id);
            Assert.Contains("manifest.json", skip.Detail);
        }

        [Fact]
        public void A_broken_manifest_is_InvalidManifest_with_the_parser_message()
        {
            var resolution = Resolve(new FakePackSource("broken", "{ \"id\": \"broken\", "));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.InvalidManifest, skip.Reason);
            Assert.Contains("invalid JSON at", skip.Detail);
        }

        [Fact]
        public void The_second_folder_claiming_an_id_is_DuplicateId()
        {
            var resolution = Resolve(
                new FakePackSource("first", Manifest("same.pack", "1.0.0")),
                new FakePackSource("second", Manifest("same.pack", "2.0.0")));

            var loaded = Assert.Single(resolution.Loaded);
            Assert.Equal("1.0.0", loaded.Manifest.Version.ToString());

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.DuplicateId, skip.Reason);
            Assert.Contains("'first'", skip.Detail);
        }

        [Fact]
        public void A_pack_made_for_another_game_build_loads_with_one_warning()
        {
            // PACK-VERSION: gameVersion means "made and tested with", not a gate.
            var resolution = Resolve(
                new FakePackSource("old", Manifest("old.pack", "1.0.0", gameVersion: "1.16.0")));

            Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            var warning = Assert.Single(resolution.Warnings);
            Assert.Equal("old.pack", warning.Id);
            Assert.Contains("1.16.0", warning.Message);
            Assert.Contains(GameVersion, warning.Message);
        }

        [Fact]
        public void IgnoreGameVersion_loads_a_pack_outside_its_range_anyway()
        {
            var source = new FakePackSource("new", Manifest("new.pack", "1.0.0", minGameVersion: "1.18.0"));

            var resolution = PackResolver.Resolve(
                new IPackSource[] { source }, GameVersion, CoreVersion, ignoreGameVersion: true);

            Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Contains("IgnoreGameVersion", Assert.Single(resolution.Warnings).Message);
        }

        [Fact]
        public void A_pack_without_a_gameVersion_loads_on_any_build()
        {
            var resolution = PackResolver.Resolve(
                new IPackSource[] { new FakePackSource("any", Manifest("any.pack", "1.0.0")) },
                "9.9.9",
                CoreVersion);

            Assert.Single(resolution.Loaded);
        }

        [Fact]
        public void A_pack_needing_a_newer_framework_is_CoreVersionTooOld()
        {
            var resolution = Resolve(
                new FakePackSource("future", Manifest("future.pack", "1.0.0", loaderVersion: "0.9.0")));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.CoreVersionTooOld, skip.Reason);
            Assert.Contains("0.9.0", skip.Detail);
        }

        [Fact]
        public void A_pack_whose_contentPatcherVersion_is_met_loads()
        {
            var resolution = Resolve(
                new FakePackSource("now", Manifest("now.pack", "1.0.0", loaderVersion: "0.1.0")));

            Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
        }

        [Fact]
        public void A_dependency_that_is_not_installed_is_MissingDependency()
        {
            var resolution = Resolve(
                new FakePackSource("needy", Manifest("needy.pack", "1.0.0", dependsOn: "absent.pack")));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.MissingDependency, skip.Reason);
            Assert.Contains("absent.pack", skip.Detail);
            Assert.Contains("not installed", skip.Detail);
        }

        [Fact]
        public void A_dependency_that_was_itself_skipped_takes_its_dependents_with_it()
        {
            var resolution = Resolve(
                new FakePackSource("base", Manifest("base.pack", "1.0.0", minGameVersion: "1.18.0")),
                new FakePackSource("needy", Manifest("needy.pack", "1.0.0", dependsOn: "base.pack")));

            Assert.Empty(resolution.Loaded);
            Assert.Equal(2, resolution.Skipped.Count);

            var cascade = resolution.FindSkipped("needy.pack");
            Assert.NotNull(cascade);
            Assert.Equal(PackSkipReason.MissingDependency, cascade!.Reason);
            Assert.Contains("GameVersionTooOld", cascade.Detail);
        }

        [Fact]
        public void A_dependency_older_than_minVersion_is_DependencyVersionTooLow()
        {
            var resolution = Resolve(
                new FakePackSource("base", Manifest("base.pack", "1.0.0")),
                new FakePackSource("needy", Manifest("needy.pack", "1.0.0", dependsOn: "base.pack", minVersion: "2.0.0")));

            var loaded = Assert.Single(resolution.Loaded);
            Assert.Equal("base.pack", loaded.Manifest.Id);

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.DependencyVersionTooLow, skip.Reason);
            Assert.Contains(">= 2.0.0", skip.Detail);
            Assert.Contains("found 1.0.0", skip.Detail);
        }

        [Fact]
        public void A_dependency_at_exactly_minVersion_is_accepted()
        {
            var resolution = Resolve(
                new FakePackSource("base", Manifest("base.pack", "2.0.0")),
                new FakePackSource("needy", Manifest("needy.pack", "1.0.0", dependsOn: "base.pack", minVersion: "2.0.0")));

            Assert.Equal(new[] { "base.pack", "needy.pack" }, Ids(resolution));
            Assert.Empty(resolution.Skipped);
        }

        [Fact]
        public void A_cycle_skips_every_pack_in_it_as_DependencyCycle()
        {
            var resolution = Resolve(
                new FakePackSource("a", Manifest("a.pack", "1.0.0", dependsOn: "b.pack")),
                new FakePackSource("b", Manifest("b.pack", "1.0.0", dependsOn: "a.pack")),
                new FakePackSource("free", Manifest("free.pack", "1.0.0")));

            Assert.Equal(new[] { "free.pack" }, Ids(resolution));
            Assert.Equal(2, resolution.Skipped.Count);
            Assert.All(resolution.Skipped, skip => Assert.Equal(PackSkipReason.DependencyCycle, skip.Reason));
            Assert.Contains("a.pack -> b.pack -> a.pack", resolution.FindSkipped("a.pack")!.Detail);
        }

        [Fact]
        public void The_summary_names_loaded_packs_and_skip_reasons()
        {
            var resolution = Resolve(
                new FakePackSource("ok", Manifest("ok.pack", "1.0.0", gameVersion: GameVersion)),
                new FakePackSource("bad", "not json"));

            Assert.Equal("PACKS: 1 loaded (ok.pack@1.0.0); 1 skipped (bad: InvalidManifest)", resolution.Summary);
        }

        [Fact]
        public void Nothing_found_resolves_to_nothing()
        {
            var resolution = PackResolver.Resolve(new IPackSource[0], GameVersion, CoreVersion);

            Assert.Empty(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Equal("PACKS: 0 loaded; 0 skipped", resolution.Summary);
            Assert.Null(resolution.Find("anything"));
        }

        [Fact]
        public void A_null_source_list_resolves_to_nothing() =>
            Assert.Empty(PackResolver.Resolve(null, GameVersion, CoreVersion).Loaded);

        private static PackResolution Resolve(params IPackSource[] sources) =>
            PackResolver.Resolve(sources, GameVersion, CoreVersion);

        private static string[] Ids(PackResolution resolution) =>
            resolution.Loaded.Select(pack => pack.Manifest.Id).ToArray();

        private static string Manifest(
            string id,
            string version,
            string? gameVersion = null,
            string? loaderVersion = null,
            string? dependsOn = null,
            string? minVersion = null,
            string? minGameVersion = null)
        {
            var json = "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"" + version + "\"";

            if (gameVersion != null)
            {
                json += ",\"gameVersion\":\"" + gameVersion + "\"";
            }

            if (minGameVersion != null)
            {
                json += ",\"minGameVersion\":\"" + minGameVersion + "\"";
            }

            if (loaderVersion != null)
            {
                json += ",\"loaderVersion\":\"" + loaderVersion + "\"";
            }

            if (dependsOn != null)
            {
                json += minVersion is null
                    ? ",\"dependsOn\":[\"" + dependsOn + "\"]"
                    : ",\"dependsOn\":[{\"id\":\"" + dependsOn + "\",\"minVersion\":\"" + minVersion + "\"}]";
            }

            return json + "}";
        }

        /// <summary>An in-memory pack folder: everything the resolver reads, and nothing else.</summary>
        private sealed class FakePackSource : IPackSource
        {
            internal FakePackSource(string folderName, string? manifestText, params string[] files)
            {
                Id = folderName;
                FolderName = folderName;
                ManifestText = manifestText;
                Files = files;
            }

            public string Id { get; }

            public string FolderName { get; }

            public string? ManifestText { get; }

            public IReadOnlyList<string> Files { get; }
        }
    }
}
