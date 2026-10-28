using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Satone;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// F1: the engine-free half of "put Satone back" - the snapshot, the scope flags, the revert
    /// handle, the result line, and the decision about which AI states may be re-dispatched.
    /// </summary>
    /// <remarks>
    /// The game half (calling <c>InitHeroineFacialAfterDelay</c> / <c>LookInitSlowly</c> /
    /// <c>DebugChangeState</c>) is covered by the in-game smoke check <c>p2.satone.restore</c>;
    /// what is testable off-engine is the policy, and that is what these assert.
    /// </remarks>
    public class SatoneRestoreTests
    {
        private static SatoneSnapshot Snapshot(
            int? facial = 0, int? motion = 200, float? look = 0f, int stateId = 17, string state = "work_pc") =>
            new SatoneSnapshot(true, facial, motion, look, stateId, state, 1d);

        // ---- scope ------------------------------------------------------------------------

        [Fact]
        public void AllIsTheThreeChannels()
        {
            Assert.True(RestoreScope.All.Has(RestoreScope.Face));
            Assert.True(RestoreScope.All.Has(RestoreScope.Motion));
            Assert.True(RestoreScope.All.Has(RestoreScope.Gaze));
            Assert.Equal(3, RestoreScope.All.Count());
            Assert.Equal("all", RestoreScope.All.Describe());
        }

        [Fact]
        public void ScopeCombinationsReadBackExactly()
        {
            var faceAndGaze = RestoreScope.Face | RestoreScope.Gaze;

            Assert.True(faceAndGaze.Has(RestoreScope.Face));
            Assert.True(faceAndGaze.Has(RestoreScope.Gaze));
            Assert.False(faceAndGaze.Has(RestoreScope.Motion));
            Assert.Equal(2, faceAndGaze.Count());
            Assert.Equal("face+gaze", faceAndGaze.Describe());

            Assert.Equal("none", RestoreScope.None.Describe());
            Assert.Equal(0, RestoreScope.None.Count());
            Assert.False(RestoreScope.None.Has(RestoreScope.None));
            Assert.Equal("motion", RestoreScope.Motion.Describe());
        }

        // ---- snapshot ---------------------------------------------------------------------

        [Fact]
        public void SnapshotKeepsWhatItWasGiven()
        {
            var snapshot = Snapshot(facial: 10, motion: 61, look: 1f, stateId: 20, state: "break_movie");

            Assert.True(snapshot.Valid);
            Assert.Equal(10, snapshot.FacialId);
            Assert.Equal(61, snapshot.MotionId);
            Assert.Equal(1f, snapshot.LookScale);
            Assert.Equal(20, snapshot.StateId);
            Assert.Equal("break_movie", snapshot.StateName);
            Assert.True(snapshot.HasFacial && snapshot.HasMotion && snapshot.HasLook);
            Assert.Contains("facial=10", snapshot.ToString());
            Assert.Contains("state=break_movie", snapshot.ToString());
        }

        [Fact]
        public void UnknownSnapshotAdmitsItKnowsNothing()
        {
            var unknown = SatoneSnapshot.Unknown;

            Assert.False(unknown.Valid);
            Assert.False(unknown.HasFacial);
            Assert.False(unknown.HasMotion);
            Assert.False(unknown.HasLook);
            Assert.Equal(-1, unknown.StateId);
            Assert.Equal("unknown", unknown.ToString());
            Assert.Equal(5d, unknown.At(5d).TakenSeconds);
        }

        [Fact]
        public void DiffNamesExactlyTheChannelsThatMoved()
        {
            var before = Snapshot();
            var after = Snapshot(facial: 10, motion: 61, look: 1f);

            Assert.Equal(RestoreScope.All, after.Diff(before));
            Assert.Equal(RestoreScope.Face, Snapshot(facial: 10).Diff(before));
            Assert.Equal(RestoreScope.Motion, Snapshot(motion: 61).Diff(before));
            Assert.Equal(RestoreScope.Gaze, Snapshot(look: 1f).Diff(before));
            Assert.Equal(RestoreScope.None, Snapshot().Diff(before));
            Assert.True(Snapshot().Matches(before));
            Assert.False(after.Matches(before));
        }

        [Fact]
        public void DiffIgnoresChannelsNeitherSideCouldRead()
        {
            // The facial and the look come from an Animator read-back that may be unavailable;
            // "unknown" must never be reported as "changed".
            var before = Snapshot(facial: null, look: null);
            var after = Snapshot(facial: 10, look: 1f);

            Assert.Equal(RestoreScope.None, after.Diff(before));
            Assert.Equal(RestoreScope.None, before.Diff(after));
            Assert.True(after.Matches(before));
        }

        [Fact]
        public void LookToleranceAbsorbsTheTween()
        {
            var before = Snapshot(look: 0f);

            // The gaze is tweened, so a value a hair off zero is still "back".
            Assert.Equal(RestoreScope.None, Snapshot(look: 0.04f).Diff(before));
            Assert.Equal(RestoreScope.None, Snapshot(look: -0.04f).Diff(before));
            Assert.Equal(RestoreScope.Gaze, Snapshot(look: 0.2f).Diff(before));
            Assert.Equal(RestoreScope.Gaze, Snapshot(look: 0.04f).Diff(before, lookTolerance: 0f));
        }

        // ---- the skip list ----------------------------------------------------------------

        [Fact]
        public void WorkAndBreakStatesAreRedispatchable()
        {
            foreach (var state in SatoneRestorePolicy.Redispatchable)
            {
                Assert.True(SatoneRestorePolicy.ShouldRedispatch(state, out var reason), state);
                Assert.Equal(string.Empty, reason);
                Assert.False(SatoneRestorePolicy.IsDirected(state), state);
            }

            // Exactly HeroineAI.GetCurrentStateActionType's Work + Break members (HeroineAI)
            // minus the three LeaveChair* states.
            Assert.Equal(9, SatoneRestorePolicy.Redispatchable.Count);
            Assert.Contains("work_pc", SatoneRestorePolicy.Redispatchable);
            Assert.Contains("break_sleep", SatoneRestorePolicy.Redispatchable);
        }

        [Theory]
        [InlineData("click_heroine")]
        [InlineData("main_story")]
        [InlineData("game_start_direction")]
        [InlineData("game_end_talk")]
        [InlineData("want_talk")]
        [InlineData("leave_chair_preparing")]
        [InlineData("leave_chair_go_sofa")]
        [InlineData("leave_chair_go_far")]
        [InlineData("event_new_year_countdown")]
        [InlineData("none")]
        public void DirectedStatesAreNeverRedispatchedAndSayWhy(string state)
        {
            Assert.False(SatoneRestorePolicy.ShouldRedispatch(state, out var reason), state);
            Assert.True(SatoneRestorePolicy.IsDirected(state), state);
            Assert.NotEqual(string.Empty, reason);
        }

        [Fact]
        public void OneShotStatesAreRefusedWithTheGenericReason()
        {
            // A wild idle or a desk-to-desk transition would replay its clip, not restore a loop.
            Assert.False(SatoneRestorePolicy.ShouldRedispatch("wild_guts", out var wild));
            Assert.Equal(SatoneRestorePolicy.NotSelfHealingReason, wild);
            Assert.False(SatoneRestorePolicy.IsDirected("wild_guts"));

            Assert.False(SatoneRestorePolicy.ShouldRedispatch("from_pc_to_book", out var move));
            Assert.Equal(SatoneRestorePolicy.NotSelfHealingReason, move);
        }

        [Fact]
        public void AnUnnamedStateIsNeverRedispatched()
        {
            Assert.False(SatoneRestorePolicy.ShouldRedispatch(null, out var nothing));
            Assert.Equal(SatoneRestorePolicy.UnknownStateReason, nothing);

            Assert.False(SatoneRestorePolicy.ShouldRedispatch("   ", out var blank));
            Assert.Equal(SatoneRestorePolicy.UnknownStateReason, blank);

            // What SatoneMotionFeature produces when the table has no name for the id.
            Assert.False(SatoneRestorePolicy.ShouldRedispatch("unknown_17", out var unknown));
            Assert.Equal(SatoneRestorePolicy.UnknownStateReason, unknown);
            Assert.False(SatoneRestorePolicy.IsDirected("unknown_17"));
        }

        [Fact]
        public void BothSpellingsOfAStateNameAreOneKey()
        {
            // The table says work_pc; the game's enum member is WorkPC.
            Assert.True(SatoneRestorePolicy.ShouldRedispatch("WorkPC", out _));
            Assert.True(SatoneRestorePolicy.ShouldRedispatch("BreakReadBook", out _));
            Assert.False(SatoneRestorePolicy.ShouldRedispatch("ClickHeroine", out var reason));
            Assert.NotEqual(string.Empty, reason);

            Assert.Equal("workpc", SatoneRestorePolicy.Normalize("work_pc"));
            Assert.Equal("workpc", SatoneRestorePolicy.Normalize("WorkPC"));
            Assert.Equal("leavechairgofar", SatoneRestorePolicy.Normalize("LeaveChairGoFar"));
            Assert.Equal(string.Empty, SatoneRestorePolicy.Normalize(null));
        }

        [Fact]
        public void EveryTableStateGetsADecisionAndAReason()
        {
            // No state the game can be in may fall through without an explanation.
            foreach (var state in SatoneTable.States)
            {
                var allowed = SatoneRestorePolicy.ShouldRedispatch(state.Name, out var reason);
                Assert.True(allowed || reason.Length > 0, state.Name);
                Assert.True(allowed == SatoneRestorePolicy.Redispatchable.Contains(state.Name), state.Name);
            }

            // Every name in either list is one the table actually knows.
            var known = SatoneTable.States.Select(s => s.Name).ToList();
            Assert.All(SatoneRestorePolicy.Redispatchable, name => Assert.Contains(name, known));
            Assert.All(SatoneRestorePolicy.Directed, pair => Assert.Contains(pair.Key, known));
        }

        // ---- the revert handle ------------------------------------------------------------

        [Fact]
        public void RevertRestoresItsScopeOnceAndOnlyOnce()
        {
            var calls = new List<RestoreScope>();
            var taken = Snapshot(facial: 10);
            var revert = new SatoneRevert(RestoreScope.Face, taken, (scope, _) => calls.Add(scope));

            Assert.True(revert.Restores);
            Assert.False(revert.Disposed);
            Assert.Equal(taken.FacialId, revert.Taken.FacialId);

            revert.Dispose();
            revert.Dispose();

            Assert.True(revert.Disposed);
            Assert.Single(calls);
            Assert.Equal(RestoreScope.Face, calls[0]);
            Assert.Contains("[disposed]", revert.ToString());
        }

        [Fact]
        public void ARefusedCallGetsAHandleThatDoesNothing()
        {
            var none = SatoneRevert.None(RestoreScope.Motion);

            Assert.False(none.Restores);
            none.Dispose();
            Assert.True(none.Disposed);

            var calls = 0;
            var empty = new SatoneRevert(RestoreScope.None, SatoneSnapshot.Unknown, (_, __) => calls++);
            empty.Dispose();
            Assert.Equal(0, calls);
        }

        [Fact]
        public void RevertPassesTheSnapshotItTook()
        {
            SatoneSnapshot seen = SatoneSnapshot.Unknown;
            var taken = Snapshot(facial: 3, motion: 61, look: 1f);
            using (new SatoneRevert(RestoreScope.All, taken, (_, snapshot) => seen = snapshot))
            {
            }

            Assert.Equal(3, seen.FacialId);
            Assert.Equal(61, seen.MotionId);
            Assert.Equal(1f, seen.LookScale);
        }

        // ---- the result line ---------------------------------------------------------------

        [Fact]
        public void ResultPrintsOneLinePerChannel()
        {
            var result = new RestoreResult(
                RestoreScope.All, true, 0.77f, true, "work_pc", null, true, null, queued: false);

            Assert.Equal("face=in 0.77s motion=work_pc gaze=easing", result.Describe());
            Assert.Equal(RestoreScope.All, result.Applied);
            Assert.True(result.Complete);
        }

        [Fact]
        public void ResultCarriesTheReasonAChannelWasSkipped()
        {
            var result = new RestoreResult(
                RestoreScope.All, true, 0.77f, false, "click_heroine", "it is directed", true, null, queued: false);

            Assert.Contains("motion=skipped(click_heroine: it is directed)", result.Describe());
            Assert.Equal(RestoreScope.Face | RestoreScope.Gaze, result.Applied);
            Assert.False(result.Complete);
        }

        [Fact]
        public void ResultOnlyPrintsTheChannelsThatWereAskedFor()
        {
            var result = new RestoreResult(
                RestoreScope.Face, true, 0.77f, false, null, null, false, null, queued: false);

            Assert.Equal("face=in 0.77s motion=- gaze=-", result.Describe());
            Assert.Equal(RestoreScope.Face, result.Applied);
            Assert.True(result.Complete);
        }

        [Fact]
        public void RefusedAndQueuedResultsSaySoInsteadOfClaimingChannels()
        {
            var refused = RestoreResult.Refused(RestoreScope.All, "RoomScene is not loaded");
            Assert.Contains("nothing: RoomScene is not loaded", refused.Describe());
            Assert.Equal(RestoreScope.None, refused.Applied);
            Assert.False(refused.Complete);

            var queued = RestoreResult.QueuedFor(RestoreScope.Motion);
            Assert.True(queued.Queued);
            Assert.Contains("queued for the next frame", queued.Describe());
            Assert.Equal(RestoreScope.None, queued.Applied);
            Assert.False(queued.Complete);
        }
    }
}
