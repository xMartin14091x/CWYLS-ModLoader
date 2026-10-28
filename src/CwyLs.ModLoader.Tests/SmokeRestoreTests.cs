using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Smoke;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// F4 / R4 section 5.2. A loud check read Satone's facial, motion and look values only to
    /// describe the delta and never wrote them back, so one <c>Ctrl+Shift+S</c> left her smiling
    /// at a motion nobody chose. These pin the contract that fixes it: the undo runs in a
    /// <c>finally</c>, it runs even when the body throws, and an undo that itself throws is
    /// reported instead of taking the run down.
    /// </summary>
    [Collection(SmokeRegistryCollection.Name)]
    public class SmokeRestoreTests
    {
        [Fact]
        public void A_check_without_a_restore_still_runs()
        {
            var check = new SmokeCheck("p1.quiet", "P1", () => SmokeCheckResult.Pass("p1.quiet", "fine"));

            var failures = new List<string>();
            var result = check.ExecuteAndRestore((c, ex) => failures.Add(c.Id));

            Assert.Null(check.Restore);
            Assert.True(result.Ok);
            Assert.Equal("fine", result.Detail);
            Assert.Empty(failures);
        }

        [Fact]
        public void The_restore_runs_after_the_body()
        {
            var order = new List<string>();
            var check = new SmokeCheck(
                "p2.satone.face",
                "P2",
                () =>
                {
                    order.Add("run");
                    return SmokeCheckResult.Pass("p2.satone.face", "Facial 7 -> 10");
                },
                loud: true,
                () => order.Add("restore"));

            var result = check.ExecuteAndRestore((c, ex) => order.Add("failed"));

            Assert.Equal(new[] { "run", "restore" }, order);
            Assert.True(result.Ok);
        }

        /// <summary>
        /// The whole reason it is a <c>finally</c>: a check that blows up halfway is exactly the
        /// one that leaves the character in a state nobody asked for.
        /// </summary>
        [Fact]
        public void A_throwing_check_still_restores()
        {
            var restored = 0;
            var check = new SmokeCheck(
                "p2.satone.motion",
                "P2",
                () => throw new InvalidOperationException("boom"),
                loud: true,
                () => restored++);

            var failures = new List<string>();
            var result = check.ExecuteAndRestore((c, ex) => failures.Add(c.Id));

            Assert.Equal(1, restored);
            Assert.False(result.Ok);
            Assert.Equal("InvalidOperationException: boom", result.Detail);
            Assert.Empty(failures);
        }

        [Fact]
        public void A_check_that_returns_nothing_still_restores()
        {
            var restored = 0;
            var check = new SmokeCheck(
                "p2.satone.look", "P2", () => null!, loud: true, () => restored++);

            var result = check.ExecuteAndRestore((c, ex) => { });

            Assert.Equal(1, restored);
            Assert.False(result.Ok);
            Assert.Equal("check returned no result", result.Detail);
        }

        /// <summary>
        /// A failing undo is a warning line (<c>SMOKE restore failed &lt;id&gt;: …</c>), never an
        /// exception escaping into the runner's loop over the other 48 checks.
        /// </summary>
        [Fact]
        public void A_throwing_restore_is_reported_and_the_result_survives()
        {
            var check = new SmokeCheck(
                "p1.textures.restore",
                "P1",
                () => SmokeCheckResult.Pass("p1.textures.restore", "restored 3 slot(s)"),
                loud: true,
                () => throw new NullReferenceException("renderer is gone"));

            var failures = new List<(string Id, Exception Error)>();
            var result = check.ExecuteAndRestore((c, ex) => failures.Add((c.Id, ex)));

            Assert.True(result.Ok);
            Assert.Equal("restored 3 slot(s)", result.Detail);

            var failure = Assert.Single(failures);
            Assert.Equal("p1.textures.restore", failure.Id);
            Assert.IsType<NullReferenceException>(failure.Error);
            Assert.Equal("renderer is gone", failure.Error.Message);
        }

        [Fact]
        public void Both_the_body_and_the_restore_can_fail_without_throwing()
        {
            var check = new SmokeCheck(
                "p2.say.scenario",
                "P2",
                () => throw new InvalidOperationException("no scenario"),
                loud: true,
                () => throw new InvalidOperationException("cannot cancel"));

            var failures = new List<Exception>();
            var result = check.ExecuteAndRestore((c, ex) => failures.Add(ex));

            Assert.False(result.Ok);
            Assert.Equal("InvalidOperationException: no scenario", result.Detail);
            Assert.Single(failures);
        }

        [Fact]
        public void A_caller_with_no_way_to_report_a_failed_restore_is_a_programming_error()
        {
            // No silent catch: the undo's failure has to reach a log line somewhere.
            var check = new SmokeCheck(
                "p1.x", "P1", () => SmokeCheckResult.Pass("p1.x"), loud: true, () => { });

            Assert.Throws<ArgumentNullException>(() => check.ExecuteAndRestore(null!));
        }

        [Fact]
        public void Marking_a_check_loud_keeps_its_restore()
        {
            var restored = 0;
            var check = new SmokeCheck(
                "p1.x", "P1", () => SmokeCheckResult.Pass("p1.x"), loud: false, () => restored++);

            var loud = check.AsLoud();

            Assert.True(loud.Loud);
            Assert.NotNull(loud.Restore);
            loud.ExecuteAndRestore((c, ex) => { });
            Assert.Equal(1, restored);
        }

        [Fact]
        public void The_registry_can_attach_the_restore_at_registration()
        {
            SmokeRegistry.Clear();
            try
            {
                var restored = 0;
                SmokeRegistry.Register(
                    new SmokeCheck("p2.satone.face", "P2", () => SmokeCheckResult.Pass("p2.satone.face")),
                    loud: true,
                    () => restored++);

                var stored = Assert.Single(SmokeRegistry.Snapshot());
                Assert.True(stored.Loud);

                stored.ExecuteAndRestore((c, ex) => { });
                Assert.Equal(1, restored);
            }
            finally
            {
                SmokeRegistry.Clear();
            }
        }

        [Fact]
        public void Registering_without_a_restore_keeps_the_one_the_check_was_built_with()
        {
            SmokeRegistry.Clear();
            try
            {
                var restored = 0;
                SmokeRegistry.Register(
                    new SmokeCheck(
                        "p1.textures.restore", "P1",
                        () => SmokeCheckResult.Pass("p1.textures.restore"),
                        loud: true,
                        () => restored++),
                    loud: true);

                Assert.Single(SmokeRegistry.Snapshot()).ExecuteAndRestore((c, ex) => { });
                Assert.Equal(1, restored);
            }
            finally
            {
                SmokeRegistry.Clear();
            }
        }

        [Fact]
        public void WithRestore_copies_rather_than_mutating()
        {
            var check = new SmokeCheck("p1.x", "P1", () => SmokeCheckResult.Pass("p1.x"));
            Action restore = () => { };

            var withRestore = check.WithRestore(restore);

            Assert.Null(check.Restore);
            Assert.Same(restore, withRestore.Restore);
            Assert.Equal(check.Id, withRestore.Id);
            Assert.Equal(check.Phase, withRestore.Phase);
            Assert.Same(check.Run, withRestore.Run);
            Assert.Same(withRestore, withRestore.WithRestore(restore));
        }
    }
}
