using System;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Where Core reports an exception it had to swallow. Core cannot reference BepInEx, so the
    /// plugin installs a sink at start-up and everything Core catches ends up in LogOutput.log.
    /// Without a sink the reports are counted and dropped, which keeps the unit tests silent.
    /// </summary>
    public static class TraceDiagnostics
    {
        private static readonly object Gate = new object();
        private static Action<string, Exception?>? _sink;

        /// <summary>Number of reports made while no sink was installed, or that the sink itself threw on.</summary>
        public static int DroppedReports { get; private set; }

        /// <summary>Installs the sink. Passing <c>null</c> removes it (used by tests).</summary>
        public static void UseSink(Action<string, Exception?>? sink)
        {
            lock (Gate)
            {
                _sink = sink;
            }
        }

        /// <summary>Reports a swallowed failure. Never throws: it is called from inside catch blocks.</summary>
        public static void Report(string message, Exception? error = null)
        {
            Action<string, Exception?>? sink;
            lock (Gate)
            {
                sink = _sink;
            }

            if (sink is null)
            {
                lock (Gate)
                {
                    DroppedReports++;
                }

                return;
            }

            try
            {
                sink(message, error);
            }
            catch (Exception)
            {
                // The logger itself failed. There is nowhere left to report to, so count it and
                // carry on: a broken log sink must never take a game method down with it.
                lock (Gate)
                {
                    DroppedReports++;
                }
            }
        }
    }
}
