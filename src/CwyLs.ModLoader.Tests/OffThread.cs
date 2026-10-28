using System;
using System.Threading;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Runs test work on a dedicated thread, never a pool thread. Queue tests capture the test's own
    /// (pool) thread as the "main thread"; <c>Task.Run</c> can hand work back to that same thread, where
    /// the queue runs it inline by design — the cause of a flaky test on 2026-10-02.
    /// </summary>
    internal static class OffThread
    {
        public static Task Run(Action work) =>
            Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        public static Task<T> Run<T>(Func<T> work) =>
            Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
