using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FRONTDOOR: the mod API version (0.x until the 1.0 freeze), how a manifest's
    /// <c>loaderVersion</c> is checked against it, and the deprecation convention that
    /// writes one line per mod per deprecated member.
    /// </summary>
    public class ModsVersioningTests
    {
        [Fact]
        public void The_api_is_0_9_1_and_freezes_at_1_0_0()
        {
            Assert.Equal("0.9.1", CwyApi.VersionText);
            Assert.Equal(SemVer.Parse("0.9.1"), CwyApi.Version);
            Assert.Equal(SemVer.Parse("1.0.0"), CwyApi.FreezeVersion);
            Assert.False(CwyApi.IsFrozen);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("0.1.0", true)]
        [InlineData("0.9.0", true)]
        [InlineData("0.9.0-beta.1", true)]
        [InlineData("0.9.1", true)]
        [InlineData("0.9.2", false)]
        [InlineData("1.0.0", false)]
        public void A_minimum_is_satisfied_by_this_api_version(string? minimum, bool satisfied) =>
            Assert.Equal(satisfied, CwyApi.Satisfies(minimum is null ? null : SemVer.Parse(minimum)));

        [Fact]
        public void The_existing_example_pack_minimum_still_loads_on_the_new_api()
        {
            // packs/cwy.example declares loaderVersion 0.1.0, written before the bump.
            Assert.True(CwyApi.Satisfies(SemVer.Parse("0.1.0")));
        }

        [Fact]
        public void Each_mod_is_told_once_per_member()
        {
            var ledger = new DeprecationLedger();

            Assert.True(ledger.ShouldReport("a.mod", "Old.Member"));
            Assert.False(ledger.ShouldReport("a.mod", "Old.Member"));
            Assert.True(ledger.ShouldReport("b.mod", "Old.Member"));
            Assert.True(ledger.ShouldReport("a.mod", "Other.Member"));
            Assert.Equal(3, ledger.Count);
        }

        [Fact]
        public void An_unknown_caller_is_one_caller()
        {
            var ledger = new DeprecationLedger();

            Assert.True(ledger.ShouldReport(null, "Old.Member"));
            Assert.False(ledger.ShouldReport("  ", "Old.Member"));
            Assert.False(ledger.ShouldReport(DeprecationLedger.UnknownCaller, "Old.Member"));
        }

        [Fact]
        public void The_ledger_is_safe_under_concurrent_first_calls()
        {
            var ledger = new DeprecationLedger();
            var firsts = 0;

            Parallel.For(0, 200, _ =>
            {
                if (ledger.ShouldReport("a.mod", "Old.Member"))
                {
                    System.Threading.Interlocked.Increment(ref firsts);
                }
            });

            Assert.Equal(1, firsts);
        }

        [Theory]
        [InlineData(DeprecationStage.Notice, null, "a.mod uses Old.Member, deprecated since 0.9.0")]
        [InlineData(DeprecationStage.Info, "New.Member", "a.mod uses Old.Member, deprecated since 0.9.0; use New.Member instead")]
        [InlineData(DeprecationStage.PendingRemoval, "New.Member", "a.mod uses Old.Member, deprecated since 0.9.0 and due for removal; use New.Member instead")]
        public void The_line_says_who_what_since_and_instead(DeprecationStage stage, string? replacement, string expected) =>
            Assert.Equal(expected, DeprecationLedger.Describe("a.mod", "Old.Member", "0.9.0", stage, replacement));

        [Fact]
        public void The_line_for_an_unknown_caller_and_version_still_reads()
        {
            Assert.Equal(
                "(unknown) uses Old.Member, deprecated since ?",
                DeprecationLedger.Describe(null, "Old.Member", "", DeprecationStage.Notice, " "));
        }

        [Fact]
        public void The_attribute_carries_since_stage_and_replacement()
        {
            var method = typeof(Sample).GetMethod(nameof(Sample.Old))!;
            var attribute = method.GetCustomAttribute<CwyDeprecatedAttribute>()!;

            Assert.Equal("0.9.0", attribute.Since);
            Assert.Equal(DeprecationStage.PendingRemoval, attribute.Stage);
            Assert.Equal("Sample.New", attribute.Replacement);
            Assert.Equal(string.Empty, new CwyDeprecatedAttribute(null!, DeprecationStage.Info).Since);
        }

        [Fact]
        public void The_stages_keep_their_numbers()
        {
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues(typeof(DeprecationStage)).Cast<int>().ToArray());
        }

        private static class Sample
        {
            [CwyDeprecated("0.9.0", DeprecationStage.PendingRemoval, Replacement = "Sample.New")]
            public static void Old()
            {
            }
        }
    }
}
