using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Packs;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>Game-facing bindings for the shared production Studio editor.</summary>
    internal static class MakerStudioEditor
    {
        internal static Core.Api.Maker.Studio.MakerStudioEditor Instance { get; } =
            new Core.Api.Maker.Studio.MakerStudioEditor(Open, (pack, lang, id, removed, write) =>
            {
                var saved = MakerEditor.ApplyCommittedSubtitle(pack, lang, id, removed, write, out var effectivePack);
                return new MakerSubtitleLiveResult { Saved = saved, EffectivePackId = effectivePack };
            }, lineVoice: ReadLineVoice, applyVoice: MakerEditor.ApplyCommittedVoice,
            activity: () => MakerActivityHost.Activity.Snapshot(),
            play: PlayAsync, lineScope: CheckStoryLine);

        private static async Task<MakerResult<bool>> CheckStoryLine(string lang, string id, CancellationToken cancel)
        {
            var line = await MakerCatalog.Instance.LineAsync(id, lang, cancel).ConfigureAwait(false);
            return line.Ok ? MakerResult<bool>.Success(true)
                : MakerResult<bool>.Fail(line.Error!, line.Message ?? "no such story line", line.Details);
        }

        private static Task<MakerResult<MakerVoiceLiveResult>> PlayAsync(MakerStudioPlayRequest request, CancellationToken cancel) =>
            MakerEditor.Instance is null
                ? Task.FromResult(MakerResult<MakerVoiceLiveResult>.Fail(MakerErrorCodes.Unavailable, "the editor is not initialised"))
                : MakerEditor.Instance.PlayStudioAsync(request, cancel);

        private static async Task<MakerResult<string?>> ReadLineVoice(string lang, string id, CancellationToken cancel)
        {
            var line = await MakerCatalog.Instance.LineAsync(id, lang, cancel).ConfigureAwait(false);
            return line.Ok
                ? MakerResult<string?>.Success(line.Value!.Voice)
                : MakerResult<string?>.Fail(line.Error!, line.Message ?? "no such line", line.Details);
        }

        /// <summary>Opens a loaded pack for Studio (also the exporter's opener): pending 409, unknown 404, read-only 403.</summary>
        internal static MakerResult<MakerProjectStore> Open(string packId, bool editing)
        {
            // A pack Studio created and the game has not loaded yet: 409 restart_required, not a 404.
            var pending = MakerStudioPacks.PendingRefusal(packId);
            if (pending != null) return pending;
            if (editing)
            {
                var verdict = MakerEditor.Editability(packId);
                if (!verdict.Known) return MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.NotFound, verdict.Reason ?? "no such pack");
                if (!verdict.Editable) return MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.ReadOnlyPack, verdict.Reason ?? "read-only pack");
            }
            var pack = PackManager.Find(packId);
            if (pack is null)
            {
                var skipped = PackManager.FindSkipped(packId);
                return skipped != null
                    ? MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.ReadOnlyPack, skipped.Reason + " (" + skipped.Detail + ")")
                    : MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.NotFound, "no such pack");
            }
            return MakerResult<MakerProjectStore>.Success(new MakerProjectStore(pack.RootPath, recoveryWritable: MakerEditor.Editability(packId).Editable));
        }

        internal static void CaptureV1Subtitle(string pack, string lang, string id, string? before, string? after) =>
            Instance.CaptureV1Subtitle(pack, lang, id, before, after);
    }
}
