using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerStudioM4RouterTests
    {
        [Theory]
        [InlineData("{\"status\":5,\"starred\":true}")]
        [InlineData("{\"status\":null,\"starred\":true}")]
        [InlineData("{\"starred\":\"false\"}")]
        public void Present_wrong_workflow_types_are_not_silently_ignored(string fields)
        {
            var backend = new FakeM4();
            var rig = Rig(backend);
            var body = "{\"expectedRevision\":\"r1\",\"operationId\":\"00000000000000000000000000000001\","
                + fields.Substring(1);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PATCH", Line("workflow"), rig.Token, body)).Status);
            Assert.Empty(backend.Calls);
        }

        [Theory]
        [InlineData("has space")]
        [InlineData("../path")]
        [InlineData("tab\\tvalue")]
        public void Malformed_revision_tokens_are_refused(string revision)
        {
            var backend = new FakeM4(); var rig = Rig(backend);
            var body = "{\"expectedRevision\":\"" + revision + "\",\"operationId\":\"00000000000000000000000000000001\",\"starred\":true}";
            Assert.Equal(400, rig.Send(MakerHttp.Json("PATCH", Line("workflow"), rig.Token, body)).Status);
            Assert.Empty(backend.Calls);
        }

        [Fact]
        public void Pack_creation_is_unavailable_until_its_capability_is_installed()
        {
            var rig = Rig(new FakeM4());
            Assert.Equal(503, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, "{}")).Status);
        }
        [Fact]
        public void Workflow_subtitle_history_revert_glossary_and_operation_reach_their_feature_backends()
        {
            var backend = new FakeM4();
            var rig = Rig(backend);
            var op = "00000000000000000000000000000001";
            var rev = "initial";

            Assert.Equal(200, rig.Send(MakerHttp.Json("PATCH", Line("workflow"), rig.Token,
                "{\"expectedRevision\":\"initial\",\"operationId\":\"" + op + "\",\"status\":\"draft\",\"starred\":true}")).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Json("PUT", Line("subtitle"), rig.Token,
                "{\"expectedRevision\":\"initial\",\"operationId\":\"" + op + "\",\"text\":null}")).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Get(Line("history") + "?offset=2&limit=5", rig.Token)).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Json("POST", Line("history/00000000000000000000000000000002/revert"), rig.Token,
                "{\"expectedRevision\":\"initial\",\"operationId\":\"" + op + "\"}")).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/glossary/th", rig.Token)).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Json("PUT", "/v1/maker/studio/packs/sample.pack/glossary/th", rig.Token,
                "{\"expectedRevision\":\"" + rev + "\",\"operationId\":\"" + op + "\",\"entries\":[{\"id\":\"satone\",\"sourceJa\":null,\"sourceEn\":\"Satone\",\"target\":\"ซาโตเนะ\",\"note\":null}]}" )).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/operations/" + op, rig.Token)).Status);

            Assert.Equal(new[]
            {
                "workflow draft True", "subtitle <null>", "history 2 5",
                "revert 00000000000000000000000000000002", "glossary get", "glossary put 1", "operation " + op,
            }, backend.Calls);
        }

        [Fact]
        public void M4_routes_validate_segments_queries_and_strict_body_members_before_backend()
        {
            var backend = new FakeM4();
            var rig = Rig(backend);
            var op = "00000000000000000000000000000001";

            Assert.Equal(400, rig.Send(MakerHttp.Json("PATCH", Line("workflow"), rig.Token,
                "{\"expectedRevision\":\"x\",\"operationId\":\"" + op + "\",\"extra\":1}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", Line("subtitle"), rig.Token,
                "{\"expectedRevision\":\"x\",\"operationId\":\"BAD\",\"text\":\"x\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get(Line("history") + "?limit=21", rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/operations/BAD", rig.Token)).Status);
            Assert.Empty(backend.Calls);
        }

        [Fact]
        public void M4_wire_shapes_are_explicit_and_capabilities_name_only_installed_features()
        {
            var backend = new FakeM4();
            var rig = Rig(backend);
            var capabilities = ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token)));
            Assert.Equal(new object?[] { "workflow", "subtitles", "projects" }, ((List<object?>)capabilities["features"]!).ToArray());

            var history = ApiTestKit.Json(rig.Send(MakerHttp.Get(Line("history"), rig.Token)));
            Assert.Equal(new[] { "revision", "total", "offset", "items", "historyGap" }, history.Keys);
            var glossary = ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/glossary/th", rig.Token)));
            Assert.Equal(new[] { "revision", "entries" }, glossary.Keys);
        }

        private static MakerRig Rig(FakeM4 backend) => new MakerRig(studio: () => new MakerStudioBackends(
            workflow: backend, subtitles: backend, projects: backend));

        private static string Line(string tail) => "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/" + tail;

        private sealed class FakeM4 : IMakerStudioWorkflow, IMakerStudioSubtitles, IMakerStudioProjects
        {
            internal List<string> Calls { get; } = new List<string>();

            public Task<MakerResult<MakerStudioWorkflowSaved>> UpdateWorkflowAsync(MakerStudioWorkflowRequest request, CancellationToken cancel)
            {
                Calls.Add("workflow " + request.Status + " " + request.Starred);
                return Ok(new MakerStudioWorkflowSaved { Revision = "next", Workflow = new MakerStudioWorkflow { Status = request.Status!, Starred = request.Starred == true } });
            }

            public Task<MakerResult<MakerStudioSaved>> SaveSubtitleAsync(MakerStudioSubtitleRequest request, CancellationToken cancel)
            {
                Calls.Add("subtitle " + (request.Text ?? "<null>"));
                return Ok(new MakerStudioSaved { Revision = "next", OperationId = request.OperationId, Saved = true, Live = true, Path = "data/subtitles/th.json", Note = "Saved." });
            }

            public Task<MakerResult<MakerStudioSaved>> RevertSubtitleAsync(MakerStudioRevertRequest request, CancellationToken cancel)
            {
                Calls.Add("revert " + request.RevisionId);
                return Ok(new MakerStudioSaved { Revision = "next", OperationId = request.OperationId, Saved = true, Live = true, Note = "Reverted." });
            }

            public Task<MakerResult<MakerStudioHistoryPage>> HistoryAsync(string packId, string lang, string id, int offset, int limit, CancellationToken cancel)
            {
                Calls.Add("history " + offset + " " + limit);
                return Ok(new MakerStudioHistoryPage { Revision = "next", Total = 0, Offset = offset, Items = new MakerStudioHistoryItem[0] });
            }

            public Task<MakerResult<MakerStudioGlossary>> GlossaryAsync(string packId, string lang, CancellationToken cancel)
            {
                Calls.Add("glossary get");
                return Ok(new MakerStudioGlossary { Revision = "g1", Entries = new MakerStudioGlossaryEntry[0] });
            }

            public Task<MakerResult<MakerStudioGlossary>> SaveGlossaryAsync(MakerStudioGlossaryRequest request, CancellationToken cancel)
            {
                Calls.Add("glossary put " + request.Entries.Count);
                return Ok(new MakerStudioGlossary { Revision = "g2", Entries = request.Entries });
            }

            public Task<MakerResult<MakerStudioPackCreated>> CreatePackAsync(MakerStudioCreatePackRequest request, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioPackCreated>.Fail(MakerErrorCodes.Unavailable, "not in M4"));

            public Task<MakerResult<MakerStudioOperation>> OperationAsync(string packId, string operationId, CancellationToken cancel)
            {
                Calls.Add("operation " + operationId);
                return Ok(new MakerStudioOperation { State = "committed", Revision = "next", Saved = true });
            }

            private static Task<MakerResult<T>> Ok<T>(T value) where T : class => Task.FromResult(MakerResult<T>.Success(value));
        }
    }
}
