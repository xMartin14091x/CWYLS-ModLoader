using System.IO;
using System.Text.RegularExpressions;
using CwyLs.ModLoader.Core.Mods;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>RP-5: one version for the build stamp, BepInEx and the mod API.</summary>
    public class ReleaseVersionTests
    {
        [Fact]
        public void The_api_version_matches_the_build_version()
        {
            var root = TestRepo.Root;
            Assert.NotNull(root);

            var props = File.ReadAllText(Path.Combine(root!, "Directory.Build.props"));
            var match = Regex.Match(props, "<CwyLsVersion>([^<]+)</CwyLsVersion>");
            Assert.True(match.Success, "Directory.Build.props has no CwyLsVersion");
            Assert.Equal(CwyApi.PluginVersionText, match.Groups[1].Value);
        }
    }
}
