using System;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Logging
{
    /// <summary>
    /// The one console sink. Subscribes to <see cref="TraceBus"/> before anything else logs,
    /// decides per event whether it is console-worthy, and prints the canonical line
    /// SYNCHRONOUSLY on the thread that published it - so the order of the console equals the
    /// order of the events, and the stamp on the line is the event's own time.
    /// </summary>
    /// <remarks>
    /// Before this sink existed, the console copy of a bus event was formatted and printed by the trace plugin's
    /// writer thread. Live events lagged by a millisecond or so; the pre-attach replay was stamped
    /// at enqueue time and landed 4.6 s late, so the two sinks disagreed about when the
    /// same event happened. The trace file's copy still goes through the writer thread - that is
    /// where the frame-time budget was won - but the console copy no longer does.
    /// <para>
    /// This class decides nothing about the layout: <see cref="ConsoleEventMap"/> owns that, and
    /// producers own their own tag, primary and field order. The mirror never parses free text.
    /// </para>
    /// </remarks>
    public static class ConsoleMirror
    {
        private static readonly object Gate = new object();
        private static IDisposable? _subscription;
        private static long _printed;
        private static long _skipped;
        private static long _failed;

        /// <summary>Set while this class is reporting its own failure, so it cannot recurse.</summary>
        [ThreadStatic]
        private static bool _reporting;

        /// <summary>Records printed to the console so far.</summary>
        public static long Printed { get { lock (Gate) return _printed; } }

        /// <summary>Bus events that stayed in the trace file.</summary>
        public static long Skipped { get { lock (Gate) return _skipped; } }

        /// <summary>Records that could not be printed at all. Should always be zero.</summary>
        public static long Failed { get { lock (Gate) return _failed; } }

        /// <summary>True once <see cref="Install"/> has subscribed.</summary>
        public static bool IsInstalled { get { lock (Gate) return _subscription != null; } }

        /// <summary>
        /// Subscribes to the bus. Call once, first thing in <see cref="ModLoaderPlugin"/>.Awake:
        /// everything published before this point reaches the trace file but not the console.
        /// Calling it twice is a no-op.
        /// </summary>
        public static void Install()
        {
            lock (Gate)
            {
                if (_subscription != null)
                {
                    return;
                }

                _subscription = TraceBus.Subscribe(OnBusEvent);
            }
        }

        /// <summary>Stops mirroring. Used by shutdown; safe to call when not installed.</summary>
        public static void Uninstall()
        {
            IDisposable? subscription;
            lock (Gate)
            {
                subscription = _subscription;
                _subscription = null;
            }

            try
            {
                subscription?.Dispose();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("CONSOLE mirror could not unsubscribe: " + ex.Message);
            }
        }

        /// <summary>
        /// Prints one record now, on the calling thread. The only way a console line is written:
        /// the bus subscription uses it, and so do the few lines the trace plugin owns itself
        /// (clicks, UI actions, its own status).
        /// </summary>
        /// <param name="record">The record. Null is ignored.</param>
        public static void Print(ConsoleRecord? record)
        {
            if (record is null)
            {
                return;
            }

            try
            {
                var line = ConsoleRecordFormatter.Format(record);

                switch (record.Level)
                {
                    case ConsoleLevel.Error:
                        CwyLsModLoader.Log.LogError(line);
                        break;
                    case ConsoleLevel.Warn:
                        CwyLsModLoader.Log.LogWarning(line);
                        break;
                    default:
                        CwyLsModLoader.Log.LogInfo(line);
                        break;
                }

                lock (Gate)
                {
                    _printed++;
                }
            }
            catch (Exception ex)
            {
                Report("printing a " + record.Tag + " record failed", ex);
            }
        }

        private static void OnBusEvent(BusEvent evt)
        {
            try
            {
                if (!ConsoleEventMap.TryMap(evt, out var record) || record is null)
                {
                    lock (Gate)
                    {
                        _skipped++;
                    }

                    return;
                }

                Print(record);
            }
            catch (Exception ex)
            {
                Report("mapping " + evt.Sys + "/" + evt.Name + " failed", ex);
            }
        }

        /// <summary>
        /// A sink must never throw into a producer - and must not be silent either. The report goes
        /// through the logger one more time, guarded against re-entry: if the logger itself is what
        /// failed, the second attempt is skipped and the count is the record.
        /// </summary>
        /// <param name="what">What was being done.</param>
        /// <param name="ex">The failure.</param>
        private static void Report(string what, Exception ex)
        {
            lock (Gate)
            {
                _failed++;
            }

            if (_reporting)
            {
                return;
            }

            _reporting = true;
            try
            {
                CwyLsModLoader.Log.LogWarning(
                    "CONSOLE mirror: " + what + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            catch (Exception)
            {
                // The logger is the thing that failed; Failed is now the only record of it.
            }
            finally
            {
                _reporting = false;
            }
        }
    }
}
