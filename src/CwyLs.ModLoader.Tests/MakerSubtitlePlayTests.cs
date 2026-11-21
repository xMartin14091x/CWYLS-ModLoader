using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerSubtitlePlayTests
    {
        [Fact]
        public void Sub_play_needs_no_take_and_is_transient_with_canonical_language()
        {
            using var fx = new MakerProjectFixture();
            MakerStudioPlayRequest? played = null;
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                lineVoice: (_, _, _) => Task.FromResult(MakerResult<string?>.Success("voice_one")),
                play: (request, _) => { played = request; return Task.FromResult(MakerResult<MakerVoiceLiveResult>.Success(
                    new MakerVoiceLiveResult { Saved = new MakerSaved { Live = true, DurationSec = 1.25 } })); });
            var rig = new MakerRig(studio: () => new MakerStudioBackends(playback: editor));
            var revision = fx.Store.ReadLine("th", "one").Value!.Revision;
            var response = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"sub\",\"lang\":\"tha\",\"packId\":\"sample.pack\",\"expectedRevision\":\"" + revision + "\"}"));
            Assert.Equal(200, response.Status);
            Assert.Equal("sub", played!.Variant);
            Assert.Equal("th", played.Lang);
            Assert.Equal("sample.pack", played.PackId);
            Assert.Equal(false, ApiTestKit.Json(response)["saved"]);
            Assert.Equal(revision, fx.Store.ReadLine("th", "one").Value!.Revision);
            Assert.Null(fx.Store.ReadLine("th", "one").Value!.PublishedTakeId);
            // v1 must continue to reject Studio's subtitle-only variant.
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"sub\",\"lang\":\"th\"}")).Status);
        }

        [Theory]
        [InlineData("unknown", "not_found")]
        [InlineData("dlc", "not_found")]
        [InlineData("readonly", "read_only_pack")]
        [InlineData("pending", "restart_required")]
        [InlineData("paused", "game_paused")]
        [InlineData("busy", "not_ready")]
        public async Task Sub_play_keeps_refusals_before_the_host(string refusal, string expected)
        {
            using var fx = new MakerProjectFixture();
            var calls = 0;
            var editingRequested = false;
            var editor = new MakerStudioEditor((_, editing) =>
                {
                    editingRequested = editing;
                    if (refusal == "readonly" && editing) return MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.ReadOnlyPack, "read only");
                    if (refusal == "pending") return MakerResult<MakerProjectStore>.Fail(MakerStudioErrorCodes.RestartRequired, "restart first");
                    return MakerResult<MakerProjectStore>.Success(fx.Store);
                }, (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                lineScope: (_, _, _) => Task.FromResult(refusal == "dlc" || refusal == "unknown"
                    ? MakerResult<bool>.Fail(MakerErrorCodes.NotFound, "not an available story line") : MakerResult<bool>.Success(true)),
                activity: () => new MakerGameActivitySnapshot { Paused = refusal == "paused", Busy = refusal == "busy" },
                play: (_, _) => { calls++; return Task.FromResult(MakerResult<MakerVoiceLiveResult>.Success(new MakerVoiceLiveResult())); });
            var result = await editor.PlayAsync(new MakerStudioPlayRequest { Id = "one", Variant = "sub", Lang = "th", PackId = "sample.pack" }, CancellationToken.None);
            Assert.Equal(expected, result.Error);
            Assert.Equal(0, calls);
            Assert.True(editingRequested, "subtitle-only preview requires an editable selected pack");
        }

        [Fact]
        public async Task Sub_play_requires_a_selected_pack()
        {
            using var fx = new MakerProjectFixture();
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store), (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                play: (_, _) => Task.FromResult(MakerResult<MakerVoiceLiveResult>.Success(new MakerVoiceLiveResult())));
            var result = await editor.PlayAsync(new MakerStudioPlayRequest { Id = "one", Variant = "sub", Lang = "th" }, default);
            Assert.Equal(MakerErrorCodes.BadRequest, result.Error);
            Assert.Contains("packId", result.Message);
        }
    }
}
