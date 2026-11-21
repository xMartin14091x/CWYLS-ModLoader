using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class MakerStudioContractTests
    {
        [Fact]
        public void Approved_limits_and_consent_are_pinned()
        {
            Assert.Equal(2, MakerStudioContract.Version);
            Assert.Equal(1, MakerStudioContract.StorageSchema);
            Assert.Equal(20, MakerStudioContract.SubtitleRevisions);
            Assert.Equal(3, MakerStudioContract.TakesPerLine);
            Assert.Equal(2L * 1024 * 1024 * 1024, MakerStudioContract.ProjectBytes);
            Assert.Equal("consent-1", MakerStudioContract.ConsentVersion);
            Assert.Equal(
                "I confirm this take is my own voice, or I have the speaker's permission to use it in this pack, and the origin I picked is true. I will not present an AI voice as the original voice actor.",
                MakerStudioContract.ConsentEnglish);
            Assert.Equal(
                "ฉันยืนยันว่าเทคนี้เป็นเสียงของฉันเอง หรือได้รับอนุญาตจากเจ้าของเสียงให้ใช้ในแพ็กนี้ และแหล่งที่มาที่เลือกเป็นความจริง จะไม่นำเสียง AI ไปอ้างว่าเป็นเสียงของนักพากย์ต้นฉบับ",
                MakerStudioContract.ConsentThai);
        }

        [Fact]
        public void Feature_slots_are_independent_and_have_stable_order()
        {
            var all = new EmptyStudioBackend();
            var backends = new MakerStudioBackends(
                catalog: all,
                workflow: all,
                subtitles: all,
                dubs: all,
                projects: all,
                playback: all);

            Assert.Equal(
                new[] { "catalog", "workflow", "subtitles", "dubs", "projects", "playback" },
                backends.Features);

            var withoutDubs = new MakerStudioBackends(
                catalog: all,
                workflow: all,
                subtitles: all,
                projects: all,
                playback: all);
            Assert.Equal(new[] { "catalog", "workflow", "subtitles", "projects", "playback" }, withoutDubs.Features);
        }

        [Fact]
        public void Backend_slots_and_feature_snapshot_are_immutable_after_publication()
        {
            foreach (var name in new[] { "Status", "Catalog", "Workflow", "Subtitles", "Dubs", "Projects", "Playback" })
            {
                Assert.False(typeof(MakerStudioBackends).GetProperty(name)!.CanWrite, name + " must not be mutable");
            }

            var features = new MakerStudioBackends(catalog: new EmptyStudioBackend()).Features;
            Assert.Throws<NotSupportedException>(() => ((IList<string>)features).Add("dubs"));
        }

        [Fact]
        public void Take_delete_and_trim_patch_shapes_preserve_contract_semantics()
        {
            var method = typeof(IMakerStudioDubs).GetMethod(nameof(IMakerStudioDubs.DeleteTakeAsync));
            Assert.Equal(
                typeof(Task<MakerResult<MakerStudioTakeRemoved>>),
                method!.ReturnType);
            Assert.Null(typeof(MakerStudioTakeSaved).GetProperty("Removed"));
            Assert.NotNull(typeof(MakerStudioTakeRemoved).GetProperty(nameof(MakerStudioTakeRemoved.Removed)));

            var omitted = new MakerStudioUpdateTakeRequest();
            var clear = new MakerStudioUpdateTakeRequest
            {
                HasTrimStartSec = true,
                TrimStartSec = null,
                HasTrimEndSec = true,
                TrimEndSec = null,
            };
            Assert.False(omitted.HasTrimStartSec);
            Assert.False(omitted.HasTrimEndSec);
            Assert.True(clear.HasTrimStartSec);
            Assert.True(clear.HasTrimEndSec);
            Assert.Null(clear.TrimStartSec);
            Assert.Null(clear.TrimEndSec);
        }

        [Fact]
        public void V2_error_code_strings_are_pinned()
        {
            Assert.Equal("consent_required", MakerStudioErrorCodes.ConsentRequired);
            Assert.Equal("conflict", MakerStudioErrorCodes.Conflict);
            Assert.Equal("game_paused", MakerStudioErrorCodes.GamePaused);
            Assert.Equal("metadata_unavailable", MakerStudioErrorCodes.MetadataUnavailable);
            Assert.Equal("recovery_required", MakerStudioErrorCodes.RecoveryRequired);
            Assert.Equal("render_required", MakerStudioErrorCodes.RenderRequired);
            Assert.Equal("restart_required", MakerStudioErrorCodes.RestartRequired);
            Assert.Equal("storage_limit", MakerStudioErrorCodes.StorageLimit);
            Assert.Equal("take_in_use", MakerStudioErrorCodes.TakeInUse);
            Assert.Equal("take_limit", MakerStudioErrorCodes.TakeLimit);
        }

        [Fact]
        public void New_pack_creation_is_not_implied_by_project_storage()
        {
            var projects = new EmptyStudioBackend();
            Assert.Equal(new[] { "projects" }, new MakerStudioBackends(projects: projects).Features);
            Assert.Equal(new[] { "projects", "newPacks" },
                new MakerStudioBackends(projects: projects, newPacks: new NewPacksBackend()).Features);
        }

        private sealed class NewPacksBackend : IMakerStudioNewPacks
        {
            public Task<MakerResult<MakerStudioPackCreated>> CreatePackAsync(MakerStudioCreatePackRequest request, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioPackCreated>.Fail(MakerErrorCodes.Unavailable, "test provider"));
        }

        private sealed class EmptyStudioBackend :
            IMakerStudioCatalog,
            IMakerStudioWorkflow,
            IMakerStudioSubtitles,
            IMakerStudioDubs,
            IMakerStudioProjects,
            IMakerStudioPlayback
        {
            public Task<MakerResult<IReadOnlyList<MakerStudioPack>>> PacksAsync(CancellationToken cancel) => Missing<IReadOnlyList<MakerStudioPack>>();
            public Task<MakerResult<MakerStudioLinePage>> LinesAsync(MakerStudioLineQuery query, CancellationToken cancel) => Missing<MakerStudioLinePage>();
            public Task<MakerResult<MakerStudioLine>> LineAsync(string packId, string lang, string id, string? occurrenceId, CancellationToken cancel) => Missing<MakerStudioLine>();
            public Task<MakerResult<MakerStudioGroups>> GroupsAsync(string packId, string lang, CancellationToken cancel) => Missing<MakerStudioGroups>();
            public Task<MakerResult<MakerStudioWorkflowSaved>> UpdateWorkflowAsync(MakerStudioWorkflowRequest request, CancellationToken cancel) => Missing<MakerStudioWorkflowSaved>();
            public Task<MakerResult<MakerStudioHistoryPage>> HistoryAsync(string packId, string lang, string id, int offset, int limit, CancellationToken cancel) => Missing<MakerStudioHistoryPage>();
            public Task<MakerResult<MakerStudioSaved>> SaveSubtitleAsync(MakerStudioSubtitleRequest request, CancellationToken cancel) => Missing<MakerStudioSaved>();
            public Task<MakerResult<MakerStudioSaved>> RevertSubtitleAsync(MakerStudioRevertRequest request, CancellationToken cancel) => Missing<MakerStudioSaved>();
            public Task<MakerResult<MakerStudioTakePage>> TakesAsync(string packId, string lang, string id, CancellationToken cancel) => Missing<MakerStudioTakePage>();
            public Task<MakerResult<MakerStudioAudio>> ReadAudioAsync(string packId, string lang, string id, string takeId, string kind, CancellationToken cancel) => Missing<MakerStudioAudio>();
            public Task<MakerResult<MakerStudioTakeSaved>> CreateTakeAsync(MakerStudioCreateTakeRequest request, CancellationToken cancel) => Missing<MakerStudioTakeSaved>();
            public Task<MakerResult<MakerStudioTakeSaved>> SaveTakeAudioAsync(MakerStudioTakeAudioRequest request, byte[] wav, CancellationToken cancel) => Missing<MakerStudioTakeSaved>();
            public Task<MakerResult<MakerStudioTakeSaved>> UpdateTakeAsync(MakerStudioUpdateTakeRequest request, CancellationToken cancel) => Missing<MakerStudioTakeSaved>();
            public Task<MakerResult<MakerStudioTakeRemoved>> DeleteTakeAsync(MakerStudioDeleteTakeRequest request, CancellationToken cancel) => Missing<MakerStudioTakeRemoved>();
            public Task<MakerResult<MakerStudioTakeSaved>> SetFavouriteAsync(MakerStudioFavouriteRequest request, CancellationToken cancel) => Missing<MakerStudioTakeSaved>();
            public Task<MakerResult<MakerStudioSaved>> PublishAsync(MakerStudioPublishRequest request, CancellationToken cancel) => Missing<MakerStudioSaved>();
            public Task<MakerResult<MakerStudioSaved>> UnpublishAsync(MakerStudioUnpublishRequest request, CancellationToken cancel) => Missing<MakerStudioSaved>();
            public Task<MakerResult<MakerStudioGlossary>> GlossaryAsync(string packId, string lang, CancellationToken cancel) => Missing<MakerStudioGlossary>();
            public Task<MakerResult<MakerStudioGlossary>> SaveGlossaryAsync(MakerStudioGlossaryRequest request, CancellationToken cancel) => Missing<MakerStudioGlossary>();
            public Task<MakerResult<MakerStudioPackCreated>> CreatePackAsync(MakerStudioCreatePackRequest request, CancellationToken cancel) => Missing<MakerStudioPackCreated>();
            public Task<MakerResult<MakerStudioOperation>> OperationAsync(string packId, string operationId, CancellationToken cancel) => Missing<MakerStudioOperation>();
            public Task<MakerResult<MakerStudioSaved>> PlayAsync(MakerStudioPlayRequest request, CancellationToken cancel) => Missing<MakerStudioSaved>();

            private static Task<MakerResult<T>> Missing<T>() where T : class =>
                throw new NotSupportedException("contract-only test backend");
        }
    }
}
