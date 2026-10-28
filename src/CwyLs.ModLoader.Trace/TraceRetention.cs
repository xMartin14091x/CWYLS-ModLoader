using System;
using System.Threading;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Logging;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// Runs one <see cref="TraceRetentionRunner"/> pass per launch on its own below-normal
    /// background thread, <see cref="StartDelayMs"/> after the trace starts, so boot IO and the
    /// Unity main thread are never touched by it.
    /// </summary>
    /// <remarks>
    /// Output: one <c>TRACE  retention: …</c> console line (a warning when a file failed or the cap
    /// could not be met), and a file-only <c>trace/retentionFile</c> line per file. Quitting the
    /// game cancels the pass between buffers; an interrupted archive is removed and its original
    /// kept, and the next launch picks up where this one stopped.
    /// </remarks>
    internal static class TraceRetention
    {
        /// <summary>Delay after the trace starts before the pass begins.</summary>
        internal const int StartDelayMs = 30_000;

        /// <summary>Bus event name of the summary line.</summary>
        internal const string SummaryEvent = "retention";

        /// <summary>Bus event name of the per-file lines.</summary>
        internal const string StepEvent = "retentionFile";

        private static readonly CancellationTokenSource Quit = new CancellationTokenSource();
        private static Thread? _thread;

        /// <summary>Schedules the pass. Called once from <c>TracePlugin</c> after the writer is open.</summary>
        /// <param name="config">The bound trace configuration.</param>
        /// <param name="directory">The trace folder.</param>
        /// <param name="currentStamp">The live session's stamp; its files are never touched.</param>
        internal static void Start(TraceConfig config, string directory, string currentStamp)
        {
            if (_thread != null)
            {
                return;
            }

            var policy = TraceRetentionPolicy.FromConfig(config);
            if (!policy.CompressClosedParts && policy.MaxTotalBytes <= 0)
            {
                ConsoleMirror.Print(TraceConsoleLines.Status(
                    TraceRuntime.Now, "retention off (CompressClosedParts=false, RetainMaxTotalMB=0)"));
                return;
            }

            try
            {
                _thread = new Thread(() => Run(directory, currentStamp, policy))
                {
                    Name = "CWY.Trace.Retention",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                };

                _thread.Start();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TRACE: retention could not start its thread and will not run this launch: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary><c>Application.quitting</c> handler: stops the pass at the next buffer or file boundary.</summary>
        internal static void Stop()
        {
            try
            {
                Quit.Cancel();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: retention could not be told to stop: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Run(string directory, string currentStamp, TraceRetentionPolicy policy)
        {
            try
            {
                if (Quit.Token.WaitHandle.WaitOne(StartDelayMs))
                {
                    return;
                }

                var report = new TraceRetentionRunner(directory, currentStamp, policy, EmitStep).Run(Quit.Token);
                EmitSummary(report, policy);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: retention stopped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void EmitStep(TraceRetentionStep step) =>
            ConsoleEvents.File(
                TraceLineBuilder.SelfSystem,
                StepEvent,
                ("action", step.Action),
                ("file", step.File),
                ("bytesBefore", step.BytesBefore),
                ("bytesAfter", step.BytesAfter),
                ("detail", step.Detail));

        private static void EmitSummary(TraceRetentionReport report, TraceRetentionPolicy policy)
        {
            var fields = new (string Key, object? Value)[]
            {
                ("folder", TraceRetentionReport.WholeMegabytes(report.FolderBytes) + "MB"),
                ("cap", policy.MaxTotalBytes > 0
                    ? TraceRetentionReport.WholeMegabytes(policy.MaxTotalBytes) + "MB"
                    : "off"),
                ("failed", report.Failures),
                ("partsCompressed", report.PartsCompressed),
                ("bytesBefore", report.CompressedFromBytes),
                ("bytesAfter", report.CompressedToBytes),
                ("tempsRemoved", report.TempsRemoved),
                ("sessionsDeleted", report.SessionsDeleted),
                ("filesDeleted", report.FilesDeleted),
                ("bytesDeleted", report.DeletedBytes),
                ("capReached", report.CapReached),
                ("cancelled", report.Cancelled),
            };

            const string shown = "folder,cap,failed";
            if (report.NeedsAttention)
            {
                ConsoleEvents.EmitAt(
                    TraceLineBuilder.SelfSystem, SummaryEvent, ConsoleTags.Trace, ConsoleLevel.Warn,
                    report.Summary(), shown, fields);
            }
            else
            {
                ConsoleEvents.Emit(
                    TraceLineBuilder.SelfSystem, SummaryEvent, ConsoleTags.Trace, report.Summary(), shown, fields);
            }
        }
    }
}
