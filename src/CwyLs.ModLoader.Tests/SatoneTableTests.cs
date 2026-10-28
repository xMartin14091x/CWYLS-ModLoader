using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Satone;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-3: the generated Satone table, as embedded in CwyLs.ModLoader.Core.dll.
    /// These assert the naming contract a mod depends on - if a game update renames or renumbers
    /// something, regenerating the table has to keep these true or the mod API silently changes.
    /// </summary>
    public class SatoneTableTests
    {
        [Fact]
        public void EmbeddedTableLoads()
        {
            Assert.True(SatoneTable.Loaded, SatoneTable.LoadError ?? "no error reported");
            Assert.Null(SatoneTable.LoadError);
            Assert.Equal(SatoneCatalog.SupportedSchema, SatoneTable.Catalog.Schema);
        }

        [Fact]
        public void TableIsBigEnoughToBeTheRealOne()
        {
            // The same floors the in-game smoke check uses (p2.satone.table).
            Assert.True(SatoneTable.Facials.Count >= 20, $"only {SatoneTable.Facials.Count} facials");
            Assert.True(SatoneTable.Motions.Count >= 100, $"only {SatoneTable.Motions.Count} motions");
            Assert.True(SatoneTable.States.Count >= 30, $"only {SatoneTable.States.Count} states");
        }

        [Fact]
        public void EveryRowIsNamedAndUniquelyIdentified()
        {
            Assert.All(SatoneTable.Facials, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
            Assert.All(SatoneTable.Motions, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));

            Assert.Equal(SatoneTable.Facials.Count, SatoneTable.Facials.Select(f => f.Id).Distinct().Count());
            Assert.Equal(SatoneTable.Motions.Count, SatoneTable.Motions.Select(m => m.Id).Distinct().Count());
            Assert.Equal(
                SatoneTable.Motions.Count,
                SatoneTable.Motions.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void RowsAreInIdOrder()
        {
            AssertAscending(SatoneTable.Facials.Select(f => f.Id).ToList(), "facials");
            AssertAscending(SatoneTable.Motions.Select(m => m.Id).ToList(), "motions");
            AssertAscending(SatoneTable.States.Select(s => s.Id).ToList(), "states");
        }

        [Fact]
        public void KnownFacialsResolveByNameIdAndAlias()
        {
            Assert.True(SatoneTable.TryFacial("smile", out var byName));
            Assert.Equal(10, byName!.Id);
            Assert.Equal("anim_face_shy_smile", byName.Clip);

            Assert.True(SatoneTable.TryFacial("10", out var byId));
            Assert.Same(byName, byId);

            // FacialType.Shy_Rest = 1 (the game's own enum)
            Assert.True(SatoneTable.TryFacial("shy_rest", out var byEnumAlias));
            Assert.Equal(1, byEnumAlias!.Id);
            Assert.Equal("rest", byEnumAlias.Name);

            Assert.True(SatoneTable.TryFacial("SMILE", out var caseInsensitive));
            Assert.Equal(10, caseInsensitive!.Id);
        }

        [Fact]
        public void FacialMeshesComeFromTheClipsBlendshapes()
        {
            Assert.True(SatoneTable.TryFacial("smile", out var smile));
            Assert.Contains("Eye_smile", smile!.Meshes.Eye);
            Assert.Contains("Eyeblow_fun_L", smile.Meshes.Brow);
            Assert.Contains("Mouth_smile_ex", smile.Meshes.Mouth);
            Assert.True(smile.Meshes.Count >= 3);

            // Every family name carries the prefix the generator filtered on.
            Assert.All(smile.Meshes.Eye, shape => Assert.StartsWith("Eye_", shape, StringComparison.Ordinal));
            Assert.All(smile.Meshes.Brow, shape => Assert.StartsWith("Eyeblow_", shape, StringComparison.Ordinal));
            Assert.All(smile.Meshes.Mouth, shape => Assert.StartsWith("Mouth_", shape, StringComparison.Ordinal));
        }

        [Fact]
        public void KnownMotionsResolveByNameIdAndAlias()
        {
            // HeroineService.AnimationType.Wild001_Motion2_StretchShoulder = 51
            Assert.True(SatoneTable.TryMotion("wild001_stretch_shoulder", out var byName));
            Assert.Equal(51, byName!.Id);
            Assert.Equal("Wild001_Motion2_StretchShoulder", byName.AnimationType);
            Assert.Equal("wild", byName.Category);

            Assert.True(SatoneTable.TryMotion("51", out var byId));
            Assert.Same(byName, byId);

            Assert.True(SatoneTable.TryMotion("stretch_shoulder", out var byShortAlias));
            Assert.Same(byName, byShortAlias);

            // The game's own enum name, separators and casing included.
            Assert.True(SatoneTable.TryMotion("Wild001_Motion2_StretchShoulder", out var byEnum));
            Assert.Same(byName, byEnum);
        }

        [Fact]
        public void LooseNamesAreCollisionFree()
        {
            // The loose index (SatoneCatalog.LooseKey) only works if no two rows collapse onto the
            // same key. Assert it over the real table rather than trusting the generator.
            AssertLooseKeysUnique(
                SatoneTable.Motions.Select(m => (m.Id, Keys: new[] { m.Name }.Concat(m.Aliases))),
                "motions");
            AssertLooseKeysUnique(
                SatoneTable.Facials.Select(f => (f.Id, Keys: new[] { f.Name }.Concat(f.Aliases))),
                "facials");

            Assert.Equal("wild001motion2stretchshoulder", SatoneCatalog.LooseKey("Wild001_Motion2_StretchShoulder"));
            Assert.Equal(string.Empty, SatoneCatalog.LooseKey("___"));
        }

        [Fact]
        public void EveryGameEnumMemberNameResolves()
        {
            foreach (var motion in SatoneTable.Motions.Where(m => m.AnimationType != null))
            {
                Assert.True(
                    SatoneTable.TryMotion(motion.AnimationType, out var hit) && hit!.Id == motion.Id,
                    $"AnimationType.{motion.AnimationType} does not resolve back to id {motion.Id}");
            }

            foreach (var facial in SatoneTable.Facials.Where(f => f.FacialType != null))
            {
                Assert.True(
                    SatoneTable.TryFacial(facial.FacialType, out var hit) && hit!.Id == facial.Id,
                    $"FacialType.{facial.FacialType} does not resolve back to id {facial.Id}");
            }
        }

        [Fact]
        public void MotionStateIsTheNameIsCurrentAnimationMatches()
        {
            // HeroineService.IsCurrentAnimation -> MyAnimatorUtil -> AnimatorStateInfo.IsName,
            // so State must be the Animator state name, not the clip name.
            Assert.True(SatoneTable.TryMotion("wild001_stretch_shoulder", out var motion));
            Assert.Equal("Wild001_Motion2_StretchShoulder", motion!.State);
            Assert.Equal("anim_wild_stretchshoulder", motion.Clip);
            Assert.False(motion.Trigger);
        }

        [Fact]
        public void TriggerFlagMatchesTheGamesOwnSwitch()
        {
            // HeroineService turns these ids into SetTrigger rather than SetInteger.
            Assert.True(SatoneTable.TryMotion("work_base002_page_flip", out var pageFlip));
            Assert.True(pageFlip!.Trigger);
            Assert.True(pageFlip.GameSetsTrigger);

            // Every id the game triggers must also be a Trigger parameter on the controller.
            Assert.All(
                SatoneTable.Motions.Where(m => m.GameSetsTrigger),
                m => Assert.True(m.Trigger, m.Name + " is triggered by the game but is not a Trigger parameter"));
        }

        [Fact]
        public void UsageCountsComeFromTheGamesOwnScenarioData()
        {
            Assert.True(SatoneTable.TryFacial("none", out var none));
            Assert.True(none!.UsedByLines > 100, $"facial 0 used by only {none.UsedByLines} lines");

            Assert.True(SatoneTable.Motions.Count(m => m.UsedByLines > 0) >= 50);
            Assert.All(SatoneTable.Motions, m => Assert.True(m.UsedByLines >= 0));
        }

        [Fact]
        public void StatesMirrorActionStateType()
        {
            // HeroineAI.ActionStateType.BreakTeaTime = 23
            Assert.True(SatoneTable.TryState(23, out var tea));
            Assert.Equal("BreakTeaTime", tea!.ActionStateType);
            Assert.Equal("break_tea_time", tea.Name);

            Assert.True(SatoneTable.TryState(-1, out var none));
            Assert.Equal("None", none!.ActionStateType);

            Assert.False(SatoneTable.TryState(99999, out var missing));
            Assert.Null(missing);
        }

        [Fact]
        public void UnknownNamesReturnFalseAndNeverThrow()
        {
            Assert.False(SatoneTable.TryMotion("definitely_not_a_motion", out var motion));
            Assert.Null(motion);
            Assert.False(SatoneTable.TryFacial(null, out var facial));
            Assert.Null(facial);
            Assert.False(SatoneTable.TryFacial("   ", out _));
            Assert.False(SatoneTable.TryMotion("2147483648", out _)); // overflows int, not an id
        }

        [Fact]
        public void SuggestRanksPrefixThenSubstringThenTypo()
        {
            Assert.Contains("smile", SatoneTable.Suggest("smi"));
            Assert.Contains("smile", SatoneTable.Suggest("smilee"));  // one edit away
            Assert.Contains("wild001_stretch_shoulder", SatoneTable.Suggest("stretch_shoulder"));

            Assert.Empty(SatoneTable.Suggest(""));
            Assert.Empty(SatoneTable.Suggest("smi", 0));
            Assert.True(SatoneTable.Suggest("s", 3).Count <= 3);

            // Deterministic: the same query twice gives the same list.
            Assert.Equal(SatoneTable.Suggest("thin"), SatoneTable.Suggest("thin"));
        }

        [Fact]
        public void AliasesAreUnambiguous()
        {
            var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var motion in SatoneTable.Motions)
            {
                foreach (var alias in motion.Aliases)
                {
                    Assert.False(owners.ContainsKey(alias), $"alias '{alias}' is claimed twice");
                    owners[alias] = motion.Id;
                }
            }

            // An alias must never shadow somebody else's canonical name.
            foreach (var motion in SatoneTable.Motions)
            {
                Assert.False(
                    owners.TryGetValue(motion.Name, out var owner) && owner != motion.Id,
                    $"'{motion.Name}' is both a name and another motion's alias");
            }
        }

        [Fact]
        public void ParseRejectsAnUnsupportedSchema()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SatoneCatalog.Parse("{\"schema\":99,\"facials\":[],\"motions\":[],\"states\":[]}"));
            Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Throws<InvalidOperationException>(
                () => SatoneCatalog.Parse("{\"schema\":1,\"facials\":[]}"));
            Assert.Throws<JsonParseException>(() => SatoneCatalog.Parse("not json"));
            Assert.Throws<ArgumentNullException>(() => SatoneCatalog.Parse(null!));
        }

        [Fact]
        public void ParseReadsAHandWrittenTable()
        {
            const string json = """
                {
                 "schema": 1,
                 "facials": [
                  {"id": 3, "name": "grin", "facialType": null, "state": "s", "clip": "c",
                   "loopable": true, "meshes": {"eye": ["Eye_a"], "brow": [], "mouth": ["Mouth_b"]},
                   "usedByLines": 7, "aliases": ["c"]}
                 ],
                 "motions": [
                  {"id": 51, "name": "shrug", "animationType": "X", "category": "wild", "state": "S",
                   "clip": null, "loopable": null, "trigger": true, "gameSetsTrigger": false,
                   "usedByLines": 0, "aliases": []}
                 ],
                 "states": [{"id": 2, "name": "main_story", "actionStateType": "MainStory"}]
                }
                """;

            var catalog = SatoneCatalog.Parse(json);
            Assert.True(catalog.TryFacial("grin", out var grin));
            Assert.Equal(7, grin!.UsedByLines);
            Assert.True(grin.Loopable);
            Assert.Equal(new[] { "Eye_a" }, grin.Meshes.Eye);
            Assert.Empty(grin.Meshes.Brow);
            Assert.True(catalog.TryFacial("c", out _)); // alias

            Assert.True(catalog.TryMotion("51", out var shrug));
            Assert.Null(shrug!.Clip);
            Assert.Null(shrug.Loopable);
            Assert.True(shrug.Trigger);
            Assert.Equal("wild", shrug.Category);
            Assert.Single(catalog.States);
        }

        [Fact]
        public void SummaryNamesTheContents()
        {
            var summary = SatoneTable.Summary();
            Assert.Contains("facials", summary, StringComparison.Ordinal);
            Assert.Contains("motions", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("UNAVAILABLE", summary, StringComparison.Ordinal);
        }

        private static void AssertLooseKeysUnique(
            IEnumerable<(int Id, IEnumerable<string> Keys)> rows,
            string what)
        {
            var owners = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (id, keys) in rows)
            {
                foreach (var key in keys)
                {
                    var loose = SatoneCatalog.LooseKey(key);
                    if (owners.TryGetValue(loose, out var owner))
                    {
                        Assert.True(owner == id, $"{what}: '{loose}' is claimed by ids {owner} and {id}");
                    }

                    owners[loose] = id;
                }
            }
        }

        private static void AssertAscending(IReadOnlyList<int> ids, string what)
        {
            for (var i = 1; i < ids.Count; i++)
            {
                Assert.True(ids[i] > ids[i - 1], $"{what} are not in ascending id order at index {i}");
            }
        }
    }
}
