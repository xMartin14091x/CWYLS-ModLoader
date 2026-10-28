using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>RP-2: the loader runs on the full game only, never on the demo.</summary>
    public class DemoLockTests
    {
        private static DemoLockFacts FullGame() => new DemoLockFacts
        {
            IsDemoConstant = false,
            SteamIdConstant = 3548580u,
            SteamAppIdEnvironment = "3548580",
            ProductName = "Chill With You",
            GameFolderName = "Chill with You Lo-Fi Story",
        };

        [Fact]
        public void The_full_game_runs()
        {
            var verdict = DemoLockPolicy.Evaluate(FullGame());
            Assert.False(verdict.Locked);
            Assert.Empty(verdict.Reasons);
            Assert.Equal(string.Empty, verdict.PlayerMessage);
            Assert.StartsWith("demo lock: full game confirmed.", verdict.LogLine);
        }

        [Fact]
        public void The_demo_flag_locks()
        {
            var facts = FullGame();
            facts.IsDemoConstant = true;
            var verdict = DemoLockPolicy.Evaluate(facts);
            Assert.True(verdict.Locked);
            Assert.Contains(verdict.Reasons, r => r.Contains("IsDemo"));
            Assert.Contains("full game", verdict.PlayerMessage);
            Assert.StartsWith("DEMO LOCK:", verdict.LogLine);
        }

        [Fact]
        public void A_build_for_another_app_locks()
        {
            var facts = FullGame();
            facts.SteamIdConstant = 3600000u;
            Assert.True(DemoLockPolicy.Evaluate(facts).Locked);
        }

        [Theory]
        [InlineData("3600000", true)]
        [InlineData(" 3548580 ", false)]
        [InlineData("0", false)]
        [InlineData("", false)]
        [InlineData("not-a-number", false)]
        [InlineData("-1", false)]
        public void Steam_launching_another_app_locks_and_unknown_ids_do_not(string appId, bool locked)
        {
            var facts = FullGame();
            facts.SteamAppIdEnvironment = appId;
            Assert.Equal(locked, DemoLockPolicy.Evaluate(facts).Locked);
        }

        [Fact]
        public void A_steam_appid_file_for_another_app_locks()
        {
            var facts = FullGame();
            facts.SteamAppIdFile = "3600000";
            Assert.True(DemoLockPolicy.Evaluate(facts).Locked);
        }

        [Theory]
        [InlineData("Chill with You Lo-Fi Story Demo")]
        [InlineData("Chill With You Lo-Fi Story DEMO")]
        [InlineData("Demo_Data")]
        public void A_demo_folder_locks(string folder)
        {
            var facts = FullGame();
            facts.GameFolderName = folder;
            Assert.True(DemoLockPolicy.Evaluate(facts).Locked);
        }

        [Fact]
        public void A_demo_product_name_locks()
        {
            var facts = FullGame();
            facts.ProductName = "Chill With You Demo";
            Assert.True(DemoLockPolicy.Evaluate(facts).Locked);
        }

        [Theory]
        [InlineData("Demonstration", false)]
        [InlineData("demos", false)]
        [InlineData("Lo-Fi Demo", true)]
        [InlineData("demo", true)]
        [InlineData(null, false)]
        public void Demo_must_be_a_word_of_its_own(string? text, bool expected) =>
            Assert.Equal(expected, DemoLockPolicy.MentionsDemo(text));

        [Fact]
        public void The_test_switch_only_ever_locks()
        {
            var facts = FullGame();
            facts.ForcedForTest = true;
            var verdict = DemoLockPolicy.Evaluate(facts);
            Assert.True(verdict.Locked);
            Assert.Contains(verdict.Reasons, r => r.Contains(DemoLockPolicy.TestSwitch));
        }

        [Fact]
        public void Facts_that_could_not_be_read_never_lock_on_their_own()
        {
            Assert.False(DemoLockPolicy.Evaluate(new DemoLockFacts()).Locked);
            Assert.False(DemoLockPolicy.Evaluate(null).Locked);
        }

        [Fact]
        public void Every_reason_is_reported_and_the_facts_are_logged()
        {
            var facts = FullGame();
            facts.IsDemoConstant = true;
            facts.SteamAppIdEnvironment = "3600000";
            facts.GameFolderName = "Chill with You Lo-Fi Story Demo";
            var verdict = DemoLockPolicy.Evaluate(facts);
            Assert.Equal(3, verdict.Reasons.Count);
            Assert.Contains("SteamAppId env=3600000", verdict.LogLine);
            Assert.Contains("folder=Chill with You Lo-Fi Story Demo", verdict.LogLine);
        }

        /// <summary>
        /// A guardrail against an accidental refactor, not against a determined fork: every plugin's
        /// Awake must start with the lock, before it patches or loads anything.
        /// </summary>
        [Theory]
        [InlineData("CwyLs.ModLoader/ModLoaderPlugin.cs", "if (DemoLock.Engage())")]
        [InlineData("CwyLs.ModLoader.Bulbul/BulbulPlugin.cs", "if (DemoLock.IsLocked)")]
        [InlineData("CwyLs.ModLoader.Bulbul.Data/GameDataPlugin.cs", "if (DemoLock.IsLocked)")]
        [InlineData("CwyLs.ModLoader.Trace/TracePlugin.cs", "if (DemoLock.IsLocked)")]
        [InlineData("CwyLs.ModLoader.Smoke/SmokePlugin.cs", "if (DemoLock.IsLocked)")]
        [InlineData("CwyLs.ModLoader.Api/ApiPlugin.cs", "if (DemoLock.IsLocked)")]
        public void Every_plugin_checks_the_lock_before_anything_else(string file, string guard)
        {
            var root = TestRepo.Root;
            Assert.NotNull(root);

            var lines = File.ReadAllLines(Path.Combine(root!, "src", file));
            var awake = System.Array.FindIndex(lines, l => l.Trim() == "private void Awake()");
            Assert.True(awake >= 0, file + " has no Awake");

            var body = lines.Skip(awake + 1)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l != "{" && !l.StartsWith("//"))
                .ToList();

            // ModLoaderPlugin binds its log source first so the verdict has somewhere to go.
            var first = body[0] == "CwyLsModLoader.UseLog(Logger);" ? body[1] : body[0];
            Assert.Equal(guard, first);
        }
    }
}
