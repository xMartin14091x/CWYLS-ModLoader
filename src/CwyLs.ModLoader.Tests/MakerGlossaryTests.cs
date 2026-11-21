using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerGlossaryTests
    {
        [Fact]
        public void Glossary_survives_restart_and_keeps_languages_isolated()
        {
            using var fx = new MakerProjectFixture();
            var initial = new MakerGlossaryStore(fx.Store).Read("th").Value!;
            var saved = new MakerGlossaryStore(fx.Store).Save(new MakerStudioGlossaryRequest
            {
                PackId = "sample.pack", Lang = "th", ExpectedRevision = initial.Revision, OperationId = fx.Operation(1),
                Entries = new[] { Entry("satone", "ซาโตเนะ") },
            });

            var reopened = new MakerGlossaryStore(new MakerProjectStore(fx.Root, fx.Clock)).Read("th").Value!;

            Assert.True(saved.Ok);
            Assert.Single(reopened.Entries);
            Assert.Equal("ซาโตเนะ", reopened.Entries[0].Target);
            Assert.Empty(new MakerGlossaryStore(fx.Store).Read("en").Value!.Entries);
        }

        [Fact]
        public void No_op_keeps_revision_while_stale_and_duplicate_inputs_fail()
        {
            using var fx = new MakerProjectFixture();
            var store = new MakerGlossaryStore(fx.Store);
            var initial = store.Read("th").Value!;
            var entries = new[] { Entry("satone", "ซาโตเนะ") };
            var first = store.Save(Request(fx, 1, initial.Revision, entries));
            var noop = store.Save(Request(fx, 2, first.Value!.Revision, entries));
            var stale = store.Save(Request(fx, 3, initial.Revision, new[] { Entry("other", "อื่น") }));
            var duplicate = store.Save(Request(fx, 4, noop.Value!.Revision, new[] { Entry("x", "หนึ่ง"), Entry("x", "สอง") }));

            Assert.Equal(first.Value.Revision, noop.Value!.Revision);
            Assert.Equal("conflict", stale.Error);
            Assert.Equal("bad_request", duplicate.Error);
        }

        [Fact]
        public void Entry_and_encoded_size_limits_are_enforced()
        {
            using var fx = new MakerProjectFixture();
            var store = new MakerGlossaryStore(fx.Store);
            var initial = store.Read("th").Value!;
            var tooMany = Enumerable.Range(0, 201).Select(i => Entry("id" + i, "x")).ToArray();
            Assert.Equal("bad_request", store.Save(Request(fx, 1, initial.Revision, tooMany)).Error);

            var huge = Enumerable.Range(0, 40).Select(i => new MakerStudioGlossaryEntry
            {
                Id = "id" + i, Target = new string('ก', 1900), SourceEn = new string('x', 1900), Note = new string('n', 1900),
            }).ToArray();
            Assert.Equal("too_large", store.Save(Request(fx, 2, initial.Revision, huge)).Error);
        }

        private static MakerStudioGlossaryRequest Request(
            MakerProjectFixture fx, int op, string revision, MakerStudioGlossaryEntry[] entries) => new MakerStudioGlossaryRequest
        {
            PackId = "sample.pack", Lang = "th", ExpectedRevision = revision, OperationId = fx.Operation(op), Entries = entries,
        };

        private static MakerStudioGlossaryEntry Entry(string id, string target) => new MakerStudioGlossaryEntry
        {
            Id = id, SourceJa = null, SourceEn = id, Target = target,
        };
    }
}
