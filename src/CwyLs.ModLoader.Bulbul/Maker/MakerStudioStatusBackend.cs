using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>Worker-thread Studio status assembled only from cached scalar state.</summary>
    internal sealed class MakerStudioStatusBackend : IMakerStudioStatus
    {
        internal static MakerStudioStatusBackend Instance { get; } = new MakerStudioStatusBackend();

        private MakerStudioStatusBackend()
        {
        }

        public Task<MakerResult<MakerStudioStatus>> StatusAsync(CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested)
            {
                return Task.FromResult(MakerResult<MakerStudioStatus>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled."));
            }

            var game = MakerEvents.CachedStatus();
            if (game is null)
            {
                return Task.FromResult(MakerResult<MakerStudioStatus>.Fail(
                    MakerErrorCodes.Unavailable,
                    "The game has not published its first cached status yet; try again in a moment."));
            }

            var activity = MakerActivityHost.Activity.Snapshot();
            return Task.FromResult(MakerResult<MakerStudioStatus>.Success(new MakerStudioStatus
            {
                GameVersion = game.GameVersion,
                LoaderVersion = game.LoaderVersion,
                GameLanguage = game.GameLanguage,
                GameLanguageIsPack = game.GameLanguageIsPack,
                RoomReady = game.RoomReady,
                Focused = activity.Focused,
                Paused = activity.Paused,
                Busy = activity.Busy,
                Reason = activity.Reason,
                ObservedAt = activity.ObservedAt?.ToString("O", CultureInfo.InvariantCulture),
                Stale = activity.Stale,
                FocusOnPlay = MakerGameFocus.IsEnabled,
            }));
        }
    }
}
