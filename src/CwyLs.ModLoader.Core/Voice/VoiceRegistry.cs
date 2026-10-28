using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// Name to clip, with the rules that decide who wins when two registrations claim one name.
    /// </summary>
    /// <remarks>
    /// <para>The rules, in full:</para>
    /// <list type="bullet">
    /// <item>A free name is taken by whoever asks first.</item>
    /// <item>Pack over pack: the later registration wins, which makes a pack loaded after another
    /// override it - the pack list is already in dependency order.</item>
    /// <item>Runtime over pack: a clip a mod registered outranks a file on disk, so a rescan of the
    /// packs never silently swallows a line a mod just generated. A caller that would
    /// rather defer to a pack passes <c>keepPack</c>.</item>
    /// <item>Runtime over runtime: the later registration wins; two mods claiming one name is
    /// last-one-in, and the log line says who displaced whom.</item>
    /// </list>
    /// <para>
    /// Names are matched with <see cref="StringComparer.Ordinal"/> - case-sensitively - because the
    /// game's own cache is an ordinary <c>Dictionary&lt;string, AudioClip&gt;</c>
    /// (KanKikuchi's <c>AudioManager</c>) and a name that differs only in case is a different clip there too.
    /// </para>
    /// <para>Every member takes the same lock: packs preload on the main thread while a mod may
    /// register from a callback, and both touch this table.</para>
    /// </remarks>
    public sealed class VoiceRegistry
    {
        /// <summary>The <see cref="VoiceEntry.Source"/> a registration with no pack behind it uses.</summary>
        public const string RuntimeSource = "runtime";

        private static readonly VoiceEntry[] NoEntries = new VoiceEntry[0];
        private static readonly string[] NoNames = new string[0];

        private readonly object _gate = new object();

        private readonly Dictionary<string, VoiceEntry> _entries =
            new Dictionary<string, VoiceEntry>(StringComparer.Ordinal);

        /// <summary>How many names are registered.</summary>
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

        /// <summary>The registered names, ordinally sorted. A detached copy.</summary>
        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_gate)
                {
                    if (_entries.Count == 0)
                    {
                        return NoNames;
                    }

                    var names = new List<string>(_entries.Keys);
                    names.Sort(StringComparer.Ordinal);
                    return names;
                }
            }
        }

        /// <summary>
        /// Offers an entry, applying the override rules.
        /// </summary>
        /// <param name="entry">The entry to register.</param>
        /// <param name="keepPack">
        /// For a <see cref="VoicePriority.Runtime"/> entry: leave the name alone when a pack
        /// already provides it. Ignored for a pack entry, which never outranks a runtime one.
        /// </param>
        /// <returns>What happened, including the entry that was displaced or kept.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
        public VoiceRegistration Register(VoiceEntry entry, bool keepPack = false)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (!VoiceNameRules.IsValid(entry.Name))
            {
                return VoiceRegistration.Invalid(
                    entry, "'" + entry.Name + "' is not a usable voice name: " + VoiceNameRules.CharacterRuleReason);
            }

            lock (_gate)
            {
                if (!_entries.TryGetValue(entry.Name, out var existing))
                {
                    _entries[entry.Name] = entry;
                    return VoiceRegistration.Added(entry);
                }

                if (entry.Priority == VoicePriority.Pack && existing.Priority == VoicePriority.Runtime)
                {
                    return VoiceRegistration.KeptExisting(
                        entry,
                        existing,
                        "kept the runtime clip from " + existing.Source + "; a pack does not override a mod");
                }

                if (entry.Priority == VoicePriority.Runtime
                    && keepPack
                    && existing.Priority == VoicePriority.Pack)
                {
                    return VoiceRegistration.KeptExisting(
                        entry, existing, "kept the pack clip from " + existing.Source + " (keepPack)");
                }

                _entries[entry.Name] = entry;
                return VoiceRegistration.Replaced(entry, existing);
            }
        }

        /// <summary>The entry registered under a name, or null when there is none.</summary>
        /// <param name="name">The name to look up; case-sensitive.</param>
        public VoiceEntry? Resolve(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            lock (_gate)
            {
                return _entries.TryGetValue(name!, out var entry) ? entry : null;
            }
        }

        /// <summary>Removes a name.</summary>
        /// <param name="name">The name to remove; case-sensitive.</param>
        /// <returns>The entry that was removed, or null when the name was not registered.</returns>
        public VoiceEntry? Unregister(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            lock (_gate)
            {
                if (!_entries.TryGetValue(name!, out var entry))
                {
                    return null;
                }

                _entries.Remove(name!);
                return entry;
            }
        }

        /// <summary>Every entry, ordinally sorted by name. A detached copy.</summary>
        public IReadOnlyList<VoiceEntry> Snapshot()
        {
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    return NoEntries;
                }

                var entries = new List<VoiceEntry>(_entries.Values);
                entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                return entries;
            }
        }

        /// <summary>How many entries have this priority.</summary>
        /// <param name="priority">The priority to count.</param>
        public int CountOf(VoicePriority priority)
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var entry in _entries.Values)
                {
                    if (entry.Priority == priority)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Forgets every entry. Used between test cases and by a full reload.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }
    }
}
