using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// The runtime state of every language set: which clips are waiting, loading, loaded or failed,
    /// the load order, and the lookup the voice prefix asks on every line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity-free so the selection and scheduling rules are unit-tested: a clip is held as
    /// <see cref="object"/> (a <c>UnityEngine.AudioClip</c> at runtime) exactly like
    /// <see cref="VoiceEntry.Clip"/>.
    /// </para>
    /// <para>
    /// Load order, per language: names asked for "now" first (<see cref="Prioritize(string, string)"/>:
    /// a line that just fell back, or the lines of a scenario that is about to play), most recent
    /// request first; then, only when the caller allows background work, the rest in plan order.
    /// A failed clip is not retried automatically, so a broken file cannot become a load loop.
    /// </para>
    /// <para>Every member takes the same lock; in practice everything runs on Unity's main thread.</para>
    /// </remarks>
    public sealed class LanguageVoiceTable
    {
        private static readonly VoiceLoadStep[] NoSteps = new VoiceLoadStep[0];

        private readonly object _gate = new object();

        private Dictionary<string, Dictionary<string, Slot>> _slots =
            new Dictionary<string, Dictionary<string, Slot>>(StringComparer.Ordinal);

        private Dictionary<string, List<Slot>> _order = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
        private Dictionary<string, List<Slot>> _priority = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
        private Dictionary<string, int> _cursor = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Creates the table for a plan; every clip starts <see cref="LanguageVoiceState.Pending"/>.</summary>
        /// <param name="plan">The plan; null means <see cref="LanguageVoicePlan.Empty"/>.</param>
        public LanguageVoiceTable(LanguageVoicePlan? plan)
        {
            Plan = LanguageVoicePlan.Empty;
            Rebuild(plan);
        }

        /// <summary>The plan the table was last built from.</summary>
        public LanguageVoicePlan Plan { get; private set; }

        /// <summary>The language codes that have a set, ordinally sorted.</summary>
        public IReadOnlyList<string> Codes => Plan.Codes;

        /// <summary>True when a pack ships a set for this language code (any accepted spelling).</summary>
        /// <param name="code">A language code.</param>
        public bool Has(string? code) => Plan.Has(code);

        /// <summary>
        /// What the set of the language in force says about a voice name. Pure: changes nothing.
        /// </summary>
        /// <param name="activeCode">The language in force, in any accepted spelling; null or empty means none.</param>
        /// <param name="name">The voice name the game asked for (case-sensitive).</param>
        public LanguageVoiceLookup Lookup(string? activeCode, string? name)
        {
            var code = SubtitleLanguages.Normalize(activeCode);
            var safeName = name ?? string.Empty;

            lock (_gate)
            {
                if (code.Length == 0 || !_slots.TryGetValue(code, out var byName))
                {
                    return new LanguageVoiceLookup(LanguageVoiceOutcome.NoSet, code, safeName, null, null, null);
                }

                if (safeName.Length == 0 || !byName.TryGetValue(safeName, out var slot))
                {
                    return new LanguageVoiceLookup(LanguageVoiceOutcome.NotInSet, code, safeName, null, null, null);
                }

                switch (slot.State)
                {
                    case LanguageVoiceState.Loaded:
                        return new LanguageVoiceLookup(LanguageVoiceOutcome.Loaded, code, safeName, slot.Step, slot.Clip, null);
                    case LanguageVoiceState.Failed:
                        return new LanguageVoiceLookup(LanguageVoiceOutcome.Failed, code, safeName, slot.Step, null, slot.Failure);
                    default:
                        return new LanguageVoiceLookup(LanguageVoiceOutcome.NotLoaded, code, safeName, slot.Step, null, null);
                }
            }
        }

        /// <summary>The state of one clip, or null when the language's set does not cover the name.</summary>
        /// <param name="code">A language code.</param>
        /// <param name="name">The voice name.</param>
        public LanguageVoiceState? StateOf(string? code, string? name)
        {
            lock (_gate)
            {
                var slot = Find(SubtitleLanguages.Normalize(code), name);
                return slot?.State;
            }
        }

        /// <summary>
        /// Moves a waiting clip to the front of its language's load order.
        /// </summary>
        /// <param name="code">The language code.</param>
        /// <param name="name">The voice name.</param>
        /// <returns>True when the clip is waiting and is now first; false when it is loading, loaded, failed or unknown.</returns>
        public bool Prioritize(string? code, string? name) => Prioritize(code, new[] { name }) == 1;

        /// <summary>
        /// Moves waiting clips to the front of their language's load order as one block, keeping the
        /// order given (a scenario's lines in reading order).
        /// </summary>
        /// <param name="code">The language code.</param>
        /// <param name="names">The voice names; unknown, blank and non-waiting names are ignored.</param>
        /// <returns>How many waiting clips were moved.</returns>
        public int Prioritize(string? code, IEnumerable<string?>? names)
        {
            if (names is null)
            {
                return 0;
            }

            var canonical = SubtitleLanguages.Normalize(code);

            lock (_gate)
            {
                if (!_priority.TryGetValue(canonical, out var queue))
                {
                    return 0;
                }

                var block = new List<Slot>();
                foreach (var name in names)
                {
                    var slot = Find(canonical, name);
                    if (slot is null || slot.State != LanguageVoiceState.Pending || block.Contains(slot))
                    {
                        continue;
                    }

                    block.Add(slot);
                }

                if (block.Count == 0)
                {
                    return 0;
                }

                foreach (var slot in block)
                {
                    queue.Remove(slot);
                }

                queue.InsertRange(0, block);
                return block.Count;
            }
        }

        /// <summary>
        /// Takes up to <paramref name="max"/> waiting clips of one language to load next and marks them
        /// <see cref="LanguageVoiceState.Loading"/>: prioritised clips first, then (only when
        /// <paramref name="includeBackground"/>) the rest in plan order.
        /// </summary>
        /// <param name="code">The language whose clips to take.</param>
        /// <param name="max">The most to take; zero or less takes none.</param>
        /// <param name="includeBackground">False takes prioritised clips only.</param>
        /// <returns>The steps to load, in order. Never null.</returns>
        public IReadOnlyList<VoiceLoadStep> TakeNext(string? code, int max, bool includeBackground)
        {
            if (max <= 0)
            {
                return NoSteps;
            }

            var canonical = SubtitleLanguages.Normalize(code);

            lock (_gate)
            {
                if (!_priority.TryGetValue(canonical, out var queue))
                {
                    return NoSteps;
                }

                var taken = new List<VoiceLoadStep>(max);

                while (taken.Count < max && queue.Count > 0)
                {
                    var slot = queue[0];
                    queue.RemoveAt(0);
                    if (slot.State == LanguageVoiceState.Pending)
                    {
                        slot.State = LanguageVoiceState.Loading;
                        taken.Add(slot.Step);
                    }
                }

                if (includeBackground)
                {
                    var order = _order[canonical];
                    var cursor = _cursor[canonical];
                    while (taken.Count < max && cursor < order.Count)
                    {
                        var slot = order[cursor++];
                        if (slot.State == LanguageVoiceState.Pending)
                        {
                            slot.State = LanguageVoiceState.Loading;
                            taken.Add(slot.Step);
                        }
                    }

                    _cursor[canonical] = cursor;
                }

                return taken;
            }
        }

        /// <summary>True when <see cref="TakeNext"/> would return something for this language.</summary>
        /// <param name="code">The language code.</param>
        /// <param name="includeBackground">As for <see cref="TakeNext"/>.</param>
        public bool HasWaiting(string? code, bool includeBackground)
        {
            var canonical = SubtitleLanguages.Normalize(code);

            lock (_gate)
            {
                if (!_priority.TryGetValue(canonical, out var queue))
                {
                    return false;
                }

                foreach (var slot in queue)
                {
                    if (slot.State == LanguageVoiceState.Pending)
                    {
                        return true;
                    }
                }

                if (!includeBackground)
                {
                    return false;
                }

                var order = _order[canonical];
                for (var i = _cursor[canonical]; i < order.Count; i++)
                {
                    if (order[i].State == LanguageVoiceState.Pending)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Records a finished load.</summary>
        /// <param name="code">The language code the step belongs to.</param>
        /// <param name="step">The step that was taken by <see cref="TakeNext"/>.</param>
        /// <param name="clip">The loaded clip; null is recorded as a failure.</param>
        /// <param name="reason">Why the result was not accepted, or null.</param>
        /// <returns>
        /// True when the clip is now held. False when the step is no longer current - a rescan
        /// replaced or removed its file - or was not loading; the caller then owns the clip.
        /// </returns>
        public bool Complete(string? code, VoiceLoadStep? step, object? clip, out string? reason)
        {
            if (clip is null)
            {
                Fail(code, step, "the loader returned no clip");
                reason = "the loader returned no clip";
                return false;
            }

            lock (_gate)
            {
                var slot = Current(SubtitleLanguages.Normalize(code), step, out reason);
                if (slot is null)
                {
                    return false;
                }

                slot.State = LanguageVoiceState.Loaded;
                slot.Clip = clip;
                slot.Failure = null;
                reason = null;
                return true;
            }
        }

        /// <summary>Records a failed load; the game's clip keeps playing for that name.</summary>
        /// <param name="code">The language code the step belongs to.</param>
        /// <param name="step">The step that was taken by <see cref="TakeNext"/>.</param>
        /// <param name="reason">Why it failed.</param>
        /// <returns>True when the failure was recorded against a current, loading step.</returns>
        public bool Fail(string? code, VoiceLoadStep? step, string? reason)
        {
            lock (_gate)
            {
                var slot = Current(SubtitleLanguages.Normalize(code), step, out _);
                if (slot is null)
                {
                    return false;
                }

                slot.State = LanguageVoiceState.Failed;
                slot.Clip = null;
                slot.Failure = string.IsNullOrWhiteSpace(reason) ? "unknown failure" : reason;
                return true;
            }
        }

        /// <summary>
        /// Puts a loaded clip back to waiting - for a clip the engine destroyed under us - so the next
        /// request loads it again.
        /// </summary>
        /// <param name="code">The language code.</param>
        /// <param name="name">The voice name.</param>
        /// <returns>True when a loaded clip was reset.</returns>
        public bool Reset(string? code, string? name)
        {
            lock (_gate)
            {
                var slot = Find(SubtitleLanguages.Normalize(code), name);
                if (slot is null || slot.State != LanguageVoiceState.Loaded)
                {
                    return false;
                }

                slot.State = LanguageVoiceState.Pending;
                slot.Clip = null;
                return true;
            }
        }

        /// <summary>How many clips of one language are in each state.</summary>
        /// <param name="code">A language code.</param>
        public LanguageVoiceCounts CountsFor(string? code)
        {
            var canonical = SubtitleLanguages.Normalize(code);

            lock (_gate)
            {
                if (!_order.TryGetValue(canonical, out var order))
                {
                    return new LanguageVoiceCounts(canonical, 0, 0, 0, 0, 0);
                }

                int pending = 0, loading = 0, loaded = 0, failed = 0;
                foreach (var slot in order)
                {
                    switch (slot.State)
                    {
                        case LanguageVoiceState.Pending: pending++; break;
                        case LanguageVoiceState.Loading: loading++; break;
                        case LanguageVoiceState.Loaded: loaded++; break;
                        default: failed++; break;
                    }
                }

                return new LanguageVoiceCounts(canonical, order.Count, pending, loading, loaded, failed);
            }
        }

        /// <summary>Up to <paramref name="max"/> loaded clips of one language, in plan order.</summary>
        /// <param name="code">A language code.</param>
        /// <param name="max">The most to return.</param>
        /// <returns>Voice name and clip pairs. Never null.</returns>
        public IReadOnlyList<KeyValuePair<string, object>> LoadedClips(string? code, int max)
        {
            var canonical = SubtitleLanguages.Normalize(code);
            var result = new List<KeyValuePair<string, object>>();

            lock (_gate)
            {
                if (max <= 0 || !_order.TryGetValue(canonical, out var order))
                {
                    return result;
                }

                foreach (var slot in order)
                {
                    if (slot.State == LanguageVoiceState.Loaded && slot.Clip != null)
                    {
                        result.Add(new KeyValuePair<string, object>(slot.Step.Name, slot.Clip));
                        if (result.Count >= max)
                        {
                            break;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>The failures of one language, in plan order: voice name and reason.</summary>
        /// <param name="code">A language code.</param>
        /// <param name="max">The most to return.</param>
        public IReadOnlyList<KeyValuePair<string, string>> Failures(string? code, int max)
        {
            var canonical = SubtitleLanguages.Normalize(code);
            var result = new List<KeyValuePair<string, string>>();

            lock (_gate)
            {
                if (max <= 0 || !_order.TryGetValue(canonical, out var order))
                {
                    return result;
                }

                foreach (var slot in order)
                {
                    if (slot.State == LanguageVoiceState.Failed)
                    {
                        result.Add(new KeyValuePair<string, string>(slot.Step.Name, slot.Failure ?? string.Empty));
                        if (result.Count >= max)
                        {
                            break;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Replaces the plan (a pack rescan). A clip whose file is unchanged - same pack, same path -
        /// keeps its state and its loaded clip; every other clip starts waiting; a clip the new plan
        /// no longer has is dropped from the table (never destroyed here: it may be playing).
        /// </summary>
        /// <param name="plan">The new plan; null means <see cref="LanguageVoicePlan.Empty"/>.</param>
        /// <returns>How many clips were kept, added and dropped.</returns>
        public LanguageVoiceRebuild Rebuild(LanguageVoicePlan? plan)
        {
            plan ??= LanguageVoicePlan.Empty;

            lock (_gate)
            {
                var slots = new Dictionary<string, Dictionary<string, Slot>>(StringComparer.Ordinal);
                var order = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
                var priority = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
                var cursor = new Dictionary<string, int>(StringComparer.Ordinal);
                int kept = 0, added = 0, before = 0;

                foreach (var byName in _slots.Values)
                {
                    before += byName.Count;
                }

                foreach (var set in plan.Sets)
                {
                    var byName = new Dictionary<string, Slot>(StringComparer.Ordinal);
                    var list = new List<Slot>(set.Count);

                    foreach (var step in set.Steps)
                    {
                        var previous = Find(set.Code, step.Name);
                        Slot slot;
                        if (previous != null && SameFile(previous.Step, step))
                        {
                            slot = previous;
                            kept++;
                        }
                        else
                        {
                            slot = new Slot(step);
                            added++;
                        }

                        byName[step.Name] = slot;
                        list.Add(slot);
                    }

                    slots[set.Code] = byName;
                    order[set.Code] = list;
                    priority[set.Code] = new List<Slot>();
                    cursor[set.Code] = 0;
                }

                _slots = slots;
                _order = order;
                _priority = priority;
                _cursor = cursor;
                Plan = plan;

                return new LanguageVoiceRebuild(kept, added, before - kept);
            }
        }

        private static bool SameFile(VoiceLoadStep a, VoiceLoadStep b) =>
            string.Equals(a.PackId, b.PackId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.RelativePath, b.RelativePath, StringComparison.Ordinal)
            && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && a.Loader == b.Loader;

        /// <summary>Caller holds the lock.</summary>
        private Slot? Find(string code, string? name)
        {
            if (string.IsNullOrEmpty(name) || !_slots.TryGetValue(code, out var byName))
            {
                return null;
            }

            return byName.TryGetValue(name!, out var slot) ? slot : null;
        }

        /// <summary>The loading slot for a step that is still current. Caller holds the lock.</summary>
        private Slot? Current(string code, VoiceLoadStep? step, out string? reason)
        {
            if (step is null)
            {
                reason = "no step";
                return null;
            }

            var slot = Find(code, step.Name);
            if (slot is null || !SameFile(slot.Step, step))
            {
                reason = "the set no longer has " + step.PackId + "/" + step.RelativePath + " (packs rescanned)";
                return null;
            }

            if (slot.State != LanguageVoiceState.Loading)
            {
                reason = "the clip was not loading (state " + slot.State.ToString() + ")";
                return null;
            }

            reason = null;
            return slot;
        }

        private sealed class Slot
        {
            internal Slot(VoiceLoadStep step)
            {
                Step = step;
            }

            internal VoiceLoadStep Step { get; }

            internal LanguageVoiceState State { get; set; }

            internal object? Clip { get; set; }

            internal string? Failure { get; set; }
        }
    }

    /// <summary>How many clips of one language are in each state.</summary>
    public readonly struct LanguageVoiceCounts
    {
        /// <summary>Creates a count.</summary>
        /// <param name="code">The language code.</param>
        /// <param name="total">Every clip in the set.</param>
        /// <param name="pending">Waiting.</param>
        /// <param name="loading">In flight.</param>
        /// <param name="loaded">Held.</param>
        /// <param name="failed">Failed.</param>
        public LanguageVoiceCounts(string code, int total, int pending, int loading, int loaded, int failed)
        {
            Code = code ?? string.Empty;
            Total = total;
            Pending = pending;
            Loading = loading;
            Loaded = loaded;
            Failed = failed;
        }

        /// <summary>The language code.</summary>
        public string Code { get; }

        /// <summary>Every clip in the set.</summary>
        public int Total { get; }

        /// <summary>Waiting.</summary>
        public int Pending { get; }

        /// <summary>In flight.</summary>
        public int Loading { get; }

        /// <summary>Held.</summary>
        public int Loaded { get; }

        /// <summary>Failed.</summary>
        public int Failed { get; }

        /// <summary>True when nothing is waiting or loading: every clip is loaded or failed.</summary>
        public bool Settled => Total > 0 && Pending == 0 && Loading == 0;

        /// <inheritdoc />
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1}/{2} loaded, {3} loading, {4} waiting, {5} failed",
            Code,
            Loaded,
            Total,
            Loading,
            Pending,
            Failed);
    }

    /// <summary>What <see cref="LanguageVoiceTable.Rebuild"/> did.</summary>
    public readonly struct LanguageVoiceRebuild
    {
        /// <summary>Creates a result.</summary>
        /// <param name="kept">Clips whose file was unchanged and kept their state.</param>
        /// <param name="added">Clips that start waiting.</param>
        /// <param name="dropped">Clips the new plan no longer has.</param>
        public LanguageVoiceRebuild(int kept, int added, int dropped)
        {
            Kept = kept;
            Added = added;
            Dropped = dropped;
        }

        /// <summary>Clips whose file was unchanged and kept their state.</summary>
        public int Kept { get; }

        /// <summary>Clips that start waiting.</summary>
        public int Added { get; }

        /// <summary>Clips the new plan no longer has.</summary>
        public int Dropped { get; }
    }
}
