using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>P1-1: manifest parsing and validation, error message by error message.</summary>
    public class PacksManifestTests
    {
        private const string Minimal = "{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\"}";

        [Fact]
        public void Parses_a_minimal_manifest()
        {
            var manifest = PackManifestParser.Parse(Minimal);

            Assert.Equal("cwy.example", manifest.Id);
            Assert.Equal("Example", manifest.Name);
            Assert.Equal(SemVer.Parse("0.1.0"), manifest.Version);
            Assert.Null(manifest.GameVersion);
            Assert.Null(manifest.LoaderVersion);
            Assert.Null(manifest.Author);
            Assert.Empty(manifest.DependsOn);
        }

        [Fact]
        public void Parses_a_full_manifest()
        {
            var json =
                "{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"1.2.3-rc.1\"," +
                "\"author\":\"CWY:LS\",\"description\":\"部屋の模様替え\"," +
                "\"gameVersion\":\"1.17.2\",\"loaderVersion\":\"0.1.0\"," +
                "\"dependsOn\":[{\"id\":\"base.pack\",\"minVersion\":\"1.0.0\"},\"other.pack\"]," +
                "\"x-homepage\":\"https://example.invalid\"}";

            var manifest = PackManifestParser.Parse(json);

            Assert.Equal("1.2.3-rc.1", manifest.Version.ToString());
            Assert.Equal("CWY:LS", manifest.Author);
            Assert.Equal("部屋の模様替え", manifest.Description);
            Assert.Equal("1.17.2", manifest.GameVersion);
            Assert.Equal(SemVer.Parse("0.1.0"), manifest.LoaderVersion);
            Assert.Equal(2, manifest.DependsOn.Count);
            Assert.Equal("base.pack", manifest.DependsOn[0].Id);
            Assert.Equal(SemVer.Parse("1.0.0"), manifest.DependsOn[0].MinVersion);
            Assert.Equal("other.pack", manifest.DependsOn[1].Id);
            Assert.Null(manifest.DependsOn[1].MinVersion);
        }

        [Fact]
        public void An_explicit_null_gameVersion_means_any_build()
        {
            var manifest = PackManifestParser.Parse(
                "{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"gameVersion\":null}");

            Assert.Null(manifest.GameVersion);
        }

        [Theory]
        [InlineData("", "the manifest is empty")]
        [InlineData("   ", "the manifest is empty")]
        [InlineData("[]", "must be a JSON object, not an array")]
        [InlineData("{", "invalid JSON at 1:2")]
        [InlineData("{\"name\":\"Example\",\"version\":\"0.1.0\"}", "\"id\" is required")]
        [InlineData("{\"id\":\"cwy.example\",\"version\":\"0.1.0\"}", "\"name\" is required")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\"}", "\"version\" is required")]
        [InlineData("{\"id\":7,\"name\":\"Example\",\"version\":\"0.1.0\"}", "\"id\" must be a string, not a number")]
        [InlineData("{\"id\":\"Bad Id\",\"name\":\"Example\",\"version\":\"0.1.0\"}", "must match ^[a-z0-9]")]
        [InlineData("{\"id\":\"ab\",\"name\":\"Example\",\"version\":\"0.1.0\"}", "must match ^[a-z0-9]")]
        [InlineData("{\"id\":\"CWY.Example\",\"name\":\"Example\",\"version\":\"0.1.0\"}", "must match ^[a-z0-9]")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"  \",\"version\":\"0.1.0\"}", "\"name\" must not be blank")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"1.0\"}", "\"version\":")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"loaderVersion\":\"nope\"}", "\"loaderVersion\":")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"gameVersion\":2}", "\"gameVersion\" must be a string")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependsOn\":\"a.pack\"}", "\"dependsOn\" must be an array")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependsOn\":[7]}", "\"dependsOn[0]\" must be an object or a pack id string")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependsOn\":[{}]}", "\"dependsOn[0].id\"")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependsOn\":[{\"id\":\"a.pack\",\"minVersion\":\"x\"}]}", "\"dependsOn[0].minVersion\"")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependsOn\":[{\"id\":\"a.pack\",\"min\":\"1.0.0\"}]}", "unknown member \"min\"")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependsOn\":[\"cwy.example\"]}", "cannot depend on itself")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependsOn\":[\"a.pack\",\"a.pack\"]}", "is listed twice")]
        [InlineData("{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\",\"dependson\":[]}", "unknown member \"dependson\"")]
        public void Rejects_an_invalid_manifest(string json, string expectedFragment)
        {
            var ex = Assert.Throws<PackManifestException>(() => PackManifestParser.Parse(json));

            Assert.Contains(expectedFragment, ex.Message);
        }

        [Fact]
        public void TryParse_reports_the_failure_instead_of_throwing()
        {
            Assert.False(PackManifestParser.TryParse("{}", out var manifest, out var error));
            Assert.Null(manifest);
            Assert.Contains("\"id\" is required", error!);

            Assert.True(PackManifestParser.TryParse(Minimal, out var good, out var noError));
            Assert.Null(noError);
            Assert.Equal("cwy.example", good!.Id);
        }

        [Theory]
        [InlineData("abc", true)]
        [InlineData("a-b_c.d", true)]
        [InlineData("0ab", true)]
        [InlineData("ab", false)]
        [InlineData("-abc", false)]
        [InlineData(".abc", false)]
        [InlineData("Abc", false)]
        [InlineData("a b", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Validates_pack_ids(string? id, bool valid) =>
            Assert.Equal(valid, PackManifest.IsValidId(id));

        [Fact]
        public void A_64_character_id_is_the_longest_accepted()
        {
            Assert.True(PackManifest.IsValidId(new string('a', 64)));
            Assert.False(PackManifest.IsValidId(new string('a', 65)));
        }

        [Fact]
        public void A_dependency_is_satisfied_by_an_equal_or_newer_version()
        {
            var dependency = new PackDependency("a.pack", SemVer.Parse("1.2.0"));

            Assert.True(dependency.IsSatisfiedBy(SemVer.Parse("1.2.0")));
            Assert.True(dependency.IsSatisfiedBy(SemVer.Parse("1.3.0")));
            Assert.False(dependency.IsSatisfiedBy(SemVer.Parse("1.1.9")));
            Assert.False(dependency.IsSatisfiedBy(null));

            Assert.True(new PackDependency("a.pack").IsSatisfiedBy(SemVer.Parse("0.0.1")));
        }

        [Fact]
        public void The_manifest_prints_as_id_at_version() =>
            Assert.Equal("cwy.example@0.1.0", PackManifestParser.Parse(Minimal).ToString());
    }
}
