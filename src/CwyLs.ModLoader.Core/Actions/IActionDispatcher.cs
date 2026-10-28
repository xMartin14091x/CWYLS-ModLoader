using System;
using System.Threading;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// The main-thread hop the runner uses: the shared dispatcher in the game
    /// (<c>CwyLs.ModLoader.Threading.MainThread.InvokeAsync</c>), a <c>MainThreadQueue</c> in tests.
    /// </summary>
    public interface IActionDispatcher
    {
        /// <summary>
        /// Runs <paramref name="work"/> on the main thread and completes with its result. Always
        /// queued (awaiting it on the main thread cannot deadlock).
        /// </summary>
        /// <param name="owner">The dispatcher owner: <c>mod:&lt;id&gt;</c> for a mod caller, the runner's own owner otherwise.</param>
        /// <param name="label">What the work is, for the dispatcher's logs.</param>
        /// <param name="work">The handler call.</param>
        /// <param name="timeout">The caller's wall-clock limit; work not started by then is skipped.</param>
        /// <param name="cancellationToken">Stops the wait and, if not yet started, the work.</param>
        /// <returns>
        /// The task: the work's result; faulted with <see cref="TimeoutException"/> at the limit,
        /// with the dispatcher's refusal (<c>MainThreadRejectedException</c>), or with the work's own
        /// exception; cancelled when the token fires.
        /// </returns>
        Task<ActionResult> InvokeAsync(
            string owner,
            string label,
            Func<ActionResult> work,
            TimeSpan timeout,
            CancellationToken cancellationToken);
    }
}
