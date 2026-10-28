using System;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Actions
{
    /// <summary>
    /// The runner's main-thread hop: the shared dispatcher, never a host of its own.
    /// <c>mod:&lt;id&gt;</c> owners run in the budgeted mod lane, everything else in the framework lane.
    /// </summary>
    internal sealed class MainThreadActionDispatcher : IActionDispatcher
    {
        /// <inheritdoc />
        public Task<ActionResult> InvokeAsync(
            string owner,
            string label,
            Func<ActionResult> work,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            MainThread.InvokeAsync(label, work, timeout, cancellationToken, owner);
    }
}
