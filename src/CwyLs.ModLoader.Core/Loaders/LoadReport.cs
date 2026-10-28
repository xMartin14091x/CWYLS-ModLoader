using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// Everything one run of the loaders did, file by file. Aggregation lives here, once, so the
    /// log line, the smoke detail and the unit tests cannot disagree about how many files loaded.
    /// <para>
    /// Every member takes the same lock. The loaders run on Unity's main thread, but the async
    /// audio path finishes in a coroutine callback several frames after the call that started it,
    /// and a pack load that is still in flight while a report is being summarised must not throw.
    /// </para>
    /// </summary>
    public sealed class LoadReport
    {
        /// <summary><see cref="Summarize"/> when nothing was attempted.</summary>
        public const string NothingLoadedSummary = "no files loaded";

        private readonly object _gate = new object();
        private readonly List<LoadEntry> _entries = new List<LoadEntry>();

        /// <summary>Entries in the order they were added.</summary>
        /// <remarks>A detached copy: adding to the report never mutates a list a caller is reading.</remarks>
        public IReadOnlyList<LoadEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return new List<LoadEntry>(_entries);
                }
            }
        }

        /// <summary>How many files were attempted.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>
        /// True when at least one file was attempted and every one of them loaded. An empty report
        /// is not ok: it proves nothing, exactly like an empty smoke run.
        /// </summary>
        public bool Ok
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count > 0 && CountFailed() == 0;
                }
            }
        }

        /// <summary>Entries that did not load, in order.</summary>
        public IReadOnlyList<LoadEntry> Failures
        {
            get
            {
                lock (_gate)
                {
                    var failures = new List<LoadEntry>();
                    foreach (var entry in _entries)
                    {
                        if (!entry.Ok)
                        {
                            failures.Add(entry);
                        }
                    }

                    return failures;
                }
            }
        }

        /// <summary>Records one attempt.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
        public void Add(LoadEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        /// <summary>How many entries of this kind were recorded, loaded or not.</summary>
        public int CountOf(AssetKind kind)
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var entry in _entries)
                {
                    if (entry.Kind == kind)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// One line fit for LogOutput.log or a smoke detail, e.g.
        /// <c>4 file(s): 3 ok, 1 failed; 2 texture, 2 audio; 9216 bytes; 21 ms</c>.
        /// </summary>
        /// <remarks>Invariant culture, so the string is the same on every machine and in every test.</remarks>
        public string Summarize()
        {
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    return NothingLoadedSummary;
                }

                var failed = CountFailed();
                var textures = 0;
                var audio = 0;
                long bytes = 0;
                long ms = 0;

                foreach (var entry in _entries)
                {
                    if (entry.Kind == AssetKind.Texture)
                    {
                        textures++;
                    }
                    else if (entry.Kind == AssetKind.Audio)
                    {
                        audio++;
                    }

                    bytes += entry.Bytes;
                    ms += entry.Ms;
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} file(s): {1} ok, {2} failed; {3} texture, {4} audio; {5} bytes; {6} ms",
                    _entries.Count,
                    _entries.Count - failed,
                    failed,
                    textures,
                    audio,
                    bytes,
                    ms);
            }
        }

        /// <summary>Forgets every entry. Used between runs and by tests.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        /// <summary>Caller must hold <see cref="_gate"/>.</summary>
        private int CountFailed()
        {
            var failed = 0;
            foreach (var entry in _entries)
            {
                if (!entry.Ok)
                {
                    failed++;
                }
            }

            return failed;
        }
    }
}
