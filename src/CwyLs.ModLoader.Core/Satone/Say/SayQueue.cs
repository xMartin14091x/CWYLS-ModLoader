using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Satone.Say
{
    /// <summary>
    /// The waiting room for <c>Satone.Say</c> lines: first in, first out, one line speaking at a
    /// time, and a policy for what to do when the game is busy with a scenario of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Time is a parameter, never a field: every method takes <c>nowSeconds</c> on whatever clock
    /// the caller uses (<c>Time.unscaledTime</c> in the game, plain numbers in the tests). That is
    /// what makes the whole policy testable off-engine.
    /// </para>
    /// <para>
    /// <b>The policy.</b> <see cref="Next"/> is asked once a frame with "can a scenario start right
    /// now?".
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// A line is already speaking: nothing happens until its scenario ends
    /// (<see cref="Complete"/>) - unless it has been speaking for longer than
    /// <see cref="ActiveTimeoutSeconds"/>, in which case it is abandoned so one lost end event
    /// cannot wedge the queue for the rest of the session.
    /// </description></item>
    /// <item><description>
    /// Nothing is speaking and the game can start one: the head line starts.
    /// </description></item>
    /// <item><description>
    /// The game is busy and the head has waited longer than <see cref="MaxWaitSeconds"/>: with
    /// <see cref="FallbackWhenBusy"/> it is handed back for the simple voice-plus-subtitle path;
    /// without it, it keeps waiting.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed class SayQueue
    {
        /// <summary>Default number of lines that may wait.</summary>
        public const int DefaultCapacity = 8;

        /// <summary>Default seconds a line waits for the game before the fallback path is used.</summary>
        public const double DefaultMaxWaitSeconds = 10d;

        /// <summary>Default seconds a speaking line may run before the queue gives up on it.</summary>
        public const double DefaultActiveTimeoutSeconds = 120d;

        private readonly List<SayLine> _waiting = new List<SayLine>();
        private readonly object _gate = new object();

        private SayLine? _active;
        private double _activeStartedAt;

        /// <summary>Creates a queue.</summary>
        /// <param name="capacity">How many lines may wait; values below 1 are raised to 1.</param>
        /// <param name="maxWaitSeconds">
        /// How long a line waits for the game before <see cref="SayStepKind.Fallback"/>; values
        /// below 0 are raised to 0.
        /// </param>
        /// <param name="fallbackWhenBusy">
        /// False to keep waiting for the scenario path however long the game stays busy.
        /// </param>
        /// <param name="activeTimeoutSeconds">
        /// How long a speaking line may run before it is abandoned; values below 1 are raised to 1.
        /// </param>
        public SayQueue(
            int capacity = DefaultCapacity,
            double maxWaitSeconds = DefaultMaxWaitSeconds,
            bool fallbackWhenBusy = true,
            double activeTimeoutSeconds = DefaultActiveTimeoutSeconds)
        {
            Capacity = capacity < 1 ? 1 : capacity;
            MaxWaitSeconds = maxWaitSeconds < 0d ? 0d : maxWaitSeconds;
            FallbackWhenBusy = fallbackWhenBusy;
            ActiveTimeoutSeconds = activeTimeoutSeconds < 1d ? 1d : activeTimeoutSeconds;
        }

        /// <summary>How many lines may wait.</summary>
        public int Capacity { get; }

        /// <summary>How long a line waits for the game before the fallback path.</summary>
        public double MaxWaitSeconds { get; }

        /// <summary>Whether a line that waited too long is spoken the simple way.</summary>
        public bool FallbackWhenBusy { get; }

        /// <summary>How long a speaking line may run before it is abandoned.</summary>
        public double ActiveTimeoutSeconds { get; }

        /// <summary>How many lines are waiting.</summary>
        public int WaitingCount
        {
            get { lock (_gate) { return _waiting.Count; } }
        }

        /// <summary>The line being spoken, or null.</summary>
        public SayLine? Active
        {
            get { lock (_gate) { return _active; } }
        }

        /// <summary>True while a line is being spoken.</summary>
        public bool IsSpeaking => Active != null;

        /// <summary>Lines accepted since the queue was created.</summary>
        public long Accepted { get; private set; }

        /// <summary>Lines refused because the queue was full.</summary>
        public long RefusedFull { get; private set; }

        /// <summary>
        /// Puts a line at the back of the queue.
        /// </summary>
        /// <param name="line">The line. Required.</param>
        /// <returns>True when it was accepted; false when the queue is full.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public bool TryEnqueue(SayLine line)
        {
            if (line is null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            lock (_gate)
            {
                if (_waiting.Count >= Capacity)
                {
                    RefusedFull++;
                    return false;
                }

                _waiting.Add(line);
                Accepted++;
                return true;
            }
        }

        /// <summary>
        /// Decides what to do this frame. See the class remarks for the policy.
        /// </summary>
        /// <param name="nowSeconds">The current time on the caller's clock.</param>
        /// <param name="canStartNow">
        /// True when the game could start a scenario right now - nothing else is playing and
        /// Satone can be interrupted.
        /// </param>
        /// <returns>
        /// The instruction. <see cref="SayStepKind.Start"/> has already made the line active, and
        /// <see cref="SayStepKind.Fallback"/> and <see cref="SayStepKind.Abandon"/> have already
        /// removed it, so every step is handed out exactly once.
        /// </returns>
        public SayStep Next(double nowSeconds, bool canStartNow)
        {
            lock (_gate)
            {
                if (_active != null)
                {
                    if (nowSeconds - _activeStartedAt < ActiveTimeoutSeconds)
                    {
                        return SayStep.Idle;
                    }

                    var abandoned = _active;
                    _active = null;
                    return new SayStep(SayStepKind.Abandon, abandoned);
                }

                if (_waiting.Count == 0)
                {
                    return SayStep.Idle;
                }

                var head = _waiting[0];

                if (canStartNow)
                {
                    _waiting.RemoveAt(0);
                    _active = head;
                    _activeStartedAt = nowSeconds;
                    return new SayStep(SayStepKind.Start, head);
                }

                if (FallbackWhenBusy && head.WaitedSeconds(nowSeconds) >= MaxWaitSeconds)
                {
                    _waiting.RemoveAt(0);
                    return new SayStep(SayStepKind.Fallback, head);
                }

                return SayStep.Idle;
            }
        }

        /// <summary>
        /// Marks the speaking line finished, if it is the one that finished.
        /// </summary>
        /// <param name="groupId">
        /// The scenario group id the game reported ending. A group id that is not the active
        /// line's is ignored - the game ends its own scenarios too.
        /// </param>
        /// <param name="line">The line that finished, or null.</param>
        /// <returns>True when the active line was the one that ended.</returns>
        public bool Complete(string? groupId, out SayLine? line)
        {
            lock (_gate)
            {
                if (_active is null
                    || string.IsNullOrEmpty(groupId)
                    || !string.Equals(_active.GroupId, groupId, StringComparison.Ordinal))
                {
                    line = null;
                    return false;
                }

                line = _active;
                _active = null;
                return true;
            }
        }

        /// <summary>
        /// Takes a waiting line out of the queue.
        /// </summary>
        /// <param name="id">The line id.</param>
        /// <returns>
        /// True when it was waiting and is now removed. False when it is unknown, already
        /// speaking, or already finished - a line the game has started cannot be un-started here.
        /// </returns>
        public bool Cancel(int id)
        {
            lock (_gate)
            {
                for (var i = 0; i < _waiting.Count; i++)
                {
                    if (_waiting[i].Id == id)
                    {
                        _waiting.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>The waiting lines, head first. A detached copy.</summary>
        public IReadOnlyList<SayLine> Snapshot()
        {
            lock (_gate)
            {
                return _waiting.ToArray();
            }
        }

        /// <summary>
        /// Empties the queue and forgets the speaking line, for example when the room scene goes
        /// away and every id the game knew about went with it.
        /// </summary>
        /// <returns>Every line that was waiting or speaking, speaking one first.</returns>
        public IReadOnlyList<SayLine> Clear()
        {
            lock (_gate)
            {
                var dropped = new List<SayLine>(_waiting.Count + 1);
                if (_active != null)
                {
                    dropped.Add(_active);
                    _active = null;
                }

                dropped.AddRange(_waiting);
                _waiting.Clear();
                return dropped;
            }
        }
    }
}
