using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>
    /// Which line a Pack Maker Play started: Play speaks through <c>Satone.Say</c>, so the game reports the
    /// runtime row it added (<c>cwy_say_*</c>), which the catalog does not list. This remembers the last few
    /// runtime rows Play queued and the line each one came from, so <c>line.spoken</c> can name the line
    /// (its <c>lineId</c> field). Safe from any thread.
    /// </summary>
    public sealed class MakerPlayedLines
    {
        /// <summary>The most plays remembered; the oldest is forgotten first.</summary>
        public const int MaxEntries = 32;

        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _lines = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();

        /// <summary>Records that Play queued <paramref name="runtimeId"/> to say <paramref name="lineId"/>.</summary>
        /// <param name="runtimeId">The runtime row id, e.g. <c>cwy_say_12_001</c>. Blank is ignored.</param>
        /// <param name="lineId">The catalog line it plays. Blank is ignored.</param>
        public void Note(string? runtimeId, string? lineId)
        {
            if (string.IsNullOrWhiteSpace(runtimeId) || string.IsNullOrWhiteSpace(lineId))
            {
                return;
            }

            lock (_gate)
            {
                if (!_lines.ContainsKey(runtimeId!))
                {
                    _order.Enqueue(runtimeId!);
                    while (_order.Count > MaxEntries)
                    {
                        _lines.Remove(_order.Dequeue());
                    }
                }

                _lines[runtimeId!] = lineId!;
            }
        }

        /// <summary>
        /// The catalog line a spoken row stands for: the line Play started for a runtime row it queued, null for
        /// any other runtime row (another mod's <c>Satone.Say</c>), and the row id itself for a game row.
        /// </summary>
        /// <param name="localizationId">The row id <c>line.spoken</c> reports, or null.</param>
        public string? LineId(string? localizationId)
        {
            if (string.IsNullOrEmpty(localizationId))
            {
                return null;
            }

            if (!MakerLineRules.IsRuntimeRow(localizationId))
            {
                return localizationId;
            }

            lock (_gate)
            {
                return _lines.TryGetValue(localizationId!, out var line) ? line : null;
            }
        }
    }
}
