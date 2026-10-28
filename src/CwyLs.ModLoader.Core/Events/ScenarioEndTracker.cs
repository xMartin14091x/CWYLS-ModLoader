using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>Why a scenario end was not published. Values are the <c>reason</c> field of the trace diagnostic.</summary>
    public static class ScenarioEndReason
    {
        /// <summary>The scenario was started and has already ended once; this is a repeat.</summary>
        public const string AlreadyClosed = "alreadyClosed";

        /// <summary>No start was seen for this group id (or the reader reported no group id at all).</summary>
        public const string NeverOpened = "neverOpened";
    }

    /// <summary>The tracker's answer for one end: publish it, or suppress it and say why.</summary>
    public readonly struct ScenarioEndDecision
    {
        /// <summary>Creates a decision.</summary>
        /// <param name="publish">True when the end should reach the event bus.</param>
        /// <param name="groupId">The group id the end reported; null when it had none.</param>
        /// <param name="reason">One of the <see cref="ScenarioEndReason"/> values; null when <paramref name="publish"/> is true.</param>
        public ScenarioEndDecision(bool publish, string? groupId, string? reason)
        {
            Publish = publish;
            GroupId = groupId;
            Reason = reason;
        }

        /// <summary>True when this end closes an open scenario and should be published.</summary>
        public bool Publish { get; }

        /// <summary>The group id the end reported, with an empty id normalised to null.</summary>
        public string? GroupId { get; }

        /// <summary>One of the <see cref="ScenarioEndReason"/> values when suppressed; null when published.</summary>
        public string? Reason { get; }
    }

    /// <summary>
    /// Makes <c>ScenarioEnded</c> one-per-start: a scenario opens on start and closes on its FIRST
    /// end; any further end for a closed or never-opened group id is suppressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: the game's <c>ScenarioReader.EndStory()</c> has no "already ended" guard
    /// and <c>Release()</c> does not clear the playing group id (game 1.17.3), so every extra call reports the last scenario again. The game
    /// makes such calls on at least two ordinary paths: the game-start greeting ends itself through
    /// the reader, then <c>HeroineStateGameStartDirection.EndSpeak</c> - which waits for
    /// <c>!IsPlayingScenario()</c> - calls <c>EndStory()</c> again one frame later;
    /// and the exit button (<c>UIManagerForPC</c>) calls it unconditionally, long after the last scenario ended.
    /// </para>
    /// <para>
    /// Keyed by group id, so interleaved scenarios (a mod's line under a game scene) close
    /// independently. Starting an id that is already open does not open it twice: the game has
    /// one reader, so a restart replaces the run rather than nesting it, and only one end follows.
    /// Both sets are bounded by the number of distinct scenario group ids in the master data.
    /// Thread-safe; every member takes one lock.
    /// </para>
    /// </remarks>
    public sealed class ScenarioEndTracker
    {
        private readonly object _gate = new object();
        private readonly HashSet<string> _open = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _closed = new HashSet<string>(StringComparer.Ordinal);

        private long _published;
        private long _suppressed;

        /// <summary>Ends that closed an open scenario, this session.</summary>
        public long Published
        {
            get
            {
                lock (_gate)
                {
                    return _published;
                }
            }
        }

        /// <summary>Ends that were suppressed as repeats or strays, this session.</summary>
        public long Suppressed
        {
            get
            {
                lock (_gate)
                {
                    return _suppressed;
                }
            }
        }

        /// <summary>How many scenarios are open right now.</summary>
        public int OpenCount
        {
            get
            {
                lock (_gate)
                {
                    return _open.Count;
                }
            }
        }

        /// <summary>Whether <paramref name="groupId"/> is open (started and not yet ended).</summary>
        /// <param name="groupId">Scenario group id; null and empty are the same id.</param>
        public bool IsOpen(string? groupId)
        {
            lock (_gate)
            {
                return _open.Contains(Key(groupId));
            }
        }

        /// <summary>Records that a scenario started. Opening an id that is already open is a no-op.</summary>
        /// <param name="groupId">Scenario group id; null and empty are the same id.</param>
        public void Start(string? groupId)
        {
            var key = Key(groupId);
            lock (_gate)
            {
                _closed.Remove(key);
                _open.Add(key);
            }
        }

        /// <summary>
        /// Decides whether an end is the first one for an open scenario. A published decision closes
        /// the scenario, so the next end for the same id is suppressed until it starts again.
        /// </summary>
        /// <param name="groupId">The group id the end reported; null and empty are the same id.</param>
        /// <returns>The decision; <see cref="ScenarioEndDecision.Reason"/> says why when suppressed.</returns>
        public ScenarioEndDecision End(string? groupId)
        {
            var key = Key(groupId);
            var reported = key.Length == 0 ? null : key;

            lock (_gate)
            {
                if (_open.Remove(key))
                {
                    _closed.Add(key);
                    _published++;
                    return new ScenarioEndDecision(true, reported, null);
                }

                _suppressed++;
                var reason = _closed.Contains(key) ? ScenarioEndReason.AlreadyClosed : ScenarioEndReason.NeverOpened;
                return new ScenarioEndDecision(false, reported, reason);
            }
        }

        /// <summary>
        /// Forgets every open and closed id - called when the room scene unloads, because the room's
        /// <c>ScenarioReader</c> is a room-scoped singleton (registered by <c>RoomRegister</c>) and cannot end a
        /// scenario once it is gone. The session counters are kept.
        /// </summary>
        public void Forget()
        {
            lock (_gate)
            {
                _open.Clear();
                _closed.Clear();
            }
        }

        private static string Key(string? groupId) => groupId ?? string.Empty;
    }
}
