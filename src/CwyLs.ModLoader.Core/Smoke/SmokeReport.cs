using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>
    /// One smoke run, ready to serialise. Aggregation happens here, once, so the JSON file,
    /// the log line and the unit tests can never disagree about what "ok" means.
    /// </summary>
    public sealed class SmokeReport
    {
        /// <summary>
        /// <see cref="Detail"/> when nothing was registered. A run with no checks proves nothing,
        /// so it reports <c>ok:false</c> rather than a vacuous pass.
        /// </summary>
        public const string NoChecksDetail = "no checks";

        /// <summary>Creates a report from the results of one run.</summary>
        /// <param name="session">Id of the game session that produced this run.</param>
        /// <param name="ticket">Ticket label the reporter build belongs to, e.g. <c>P0-2</c>.</param>
        /// <param name="unity">Unity version reported by the engine.</param>
        /// <param name="game">Game version reported by the engine.</param>
        /// <param name="time">When the run finished.</param>
        /// <param name="checks">Results, in run order. Empty is allowed; null entries are not.</param>
        /// <exception cref="ArgumentNullException"><paramref name="checks"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="checks"/> contains a null entry.</exception>
        public SmokeReport(
            string? session,
            string? ticket,
            string? unity,
            string? game,
            DateTimeOffset time,
            IEnumerable<SmokeCheckResult> checks)
        {
            if (checks is null)
            {
                throw new ArgumentNullException(nameof(checks));
            }

            // Identity strings come from the engine and are never null in practice; an empty string
            // in the report beats throwing away the whole run over a missing label.
            Session = session ?? string.Empty;
            Ticket = ticket ?? string.Empty;
            Unity = unity ?? string.Empty;
            Game = game ?? string.Empty;
            Time = time;

            var collected = new List<SmokeCheckResult>();
            var passed = 0;
            foreach (var result in checks)
            {
                if (result is null)
                {
                    throw new ArgumentException("checks contains a null result", nameof(checks));
                }

                collected.Add(result);
                if (result.Ok)
                {
                    passed++;
                }
            }

            Checks = collected;
            Passed = passed;
            Ok = collected.Count > 0 && passed == collected.Count;
            Detail = collected.Count == 0 ? NoChecksDetail : string.Empty;
        }

        /// <summary>Id of the game session that produced this run.</summary>
        public string Session { get; }

        /// <summary>Ticket label the reporter build belongs to.</summary>
        public string Ticket { get; }

        /// <summary>Unity version reported by the engine.</summary>
        public string Unity { get; }

        /// <summary>Game version reported by the engine.</summary>
        public string Game { get; }

        /// <summary>When the run finished.</summary>
        public DateTimeOffset Time { get; }

        /// <summary>Results in run order.</summary>
        public IReadOnlyList<SmokeCheckResult> Checks { get; }

        /// <summary>How many checks passed.</summary>
        public int Passed { get; }

        /// <summary>How many checks ran.</summary>
        public int Total => Checks.Count;

        /// <summary>True only when at least one check ran and all of them passed.</summary>
        public bool Ok { get; }

        /// <summary>Why the run is not ok when there are no checks to point at; otherwise empty.</summary>
        public string Detail { get; }
    }
}
