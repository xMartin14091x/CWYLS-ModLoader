using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Subtitles
{
    /// <summary>
    /// The process-wide list of text tables for languages the game has no column for. Static for
    /// the same reason <c>SmokeRegistry</c> is: the feature that serves the text and the mods that
    /// publish tables are separate BepInEx assemblies with no reference to each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sources are asked in registration order and the first hit wins, so a mod registered later
    /// never silently overrides a pack that was already there.
    /// </para>
    /// <para>
    /// <see cref="TryGet"/> runs inside <c>LocalizationMasterWrapper.Get</c> for every localized
    /// string while a pack language is active, so it reads an immutable snapshot without a lock or
    /// an allocation; registering replaces the snapshot under the lock (copy-on-write).
    /// </para>
    /// </remarks>
    public static class SubtitleSources
    {
        private static readonly object Gate = new object();
        private static readonly List<Registration> Registered = new List<Registration>();
        private static Registration[] _snapshot = new Registration[0];

        /// <summary>How many sources are registered.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Registered.Count;
                }
            }
        }

        /// <summary>The registered source names, in registration order.</summary>
        public static IReadOnlyList<string> Names
        {
            get
            {
                lock (Gate)
                {
                    var names = new List<string>(Registered.Count);
                    foreach (var entry in Registered)
                    {
                        names.Add(entry.Name);
                    }

                    return names;
                }
            }
        }

        /// <summary>
        /// Where a source's exception is reported. Null (the default) swallows it, which is only
        /// right in tests; the adapter wires this to the BepInEx log.
        /// </summary>
        public static Action<string>? ErrorSink { get; set; }

        /// <summary>
        /// Registers a table. Registering the same instance twice replaces the first entry rather
        /// than asking it twice.
        /// </summary>
        /// <param name="source">The table.</param>
        /// <param name="name">A name for logs and the smoke report; defaults to the type name.</param>
        /// <returns>A token that unregisters the source when disposed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public static IDisposable Register(ISubtitleSource source, string? name = null)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var entry = new Registration(source, string.IsNullOrEmpty(name) ? source.GetType().Name : name!);

            lock (Gate)
            {
                for (var i = 0; i < Registered.Count; i++)
                {
                    if (ReferenceEquals(Registered[i].Source, source))
                    {
                        Registered[i] = entry;
                        Publish();
                        return new Token(source);
                    }
                }

                Registered.Add(entry);
                Publish();
            }

            return new Token(source);
        }

        /// <summary>Removes a source. Removing one that is not registered does nothing.</summary>
        /// <param name="source">The table to remove.</param>
        /// <returns>True when a source was removed.</returns>
        public static bool Unregister(ISubtitleSource? source)
        {
            if (source is null)
            {
                return false;
            }

            lock (Gate)
            {
                for (var i = 0; i < Registered.Count; i++)
                {
                    if (ReferenceEquals(Registered[i].Source, source))
                    {
                        Registered.RemoveAt(i);
                        Publish();
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Asks every registered source, in order, for one line. Never throws: a source that does
        /// throw is reported to <see cref="ErrorSink"/> and skipped.
        /// </summary>
        /// <param name="localizationId">The master-data row id.</param>
        /// <param name="lang">A normalised language code.</param>
        /// <param name="text">The line on success; empty otherwise. Never null.</param>
        /// <returns>True when a source had the line.</returns>
        public static bool TryGet(string? localizationId, string? lang, out string text)
        {
            text = string.Empty;
            if (string.IsNullOrEmpty(localizationId) || string.IsNullOrEmpty(lang))
            {
                return false;
            }

            var snapshot = System.Threading.Volatile.Read(ref _snapshot);
            foreach (var entry in snapshot)
            {
                try
                {
                    if (entry.Source.TryGet(localizationId!, lang!, out var found) && !string.IsNullOrEmpty(found))
                    {
                        text = found;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    ErrorSink?.Invoke(
                        "subtitle source '" + entry.Name + "' threw on '" + localizationId + "' ["
                        + lang + "]: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return false;
        }

        /// <summary>A view of the registry as one source, for <see cref="SubtitleResolver"/>.</summary>
        public static ISubtitleSource All { get; } = new Composite();

        /// <summary>Removes every source. Used by tests; harmless at runtime.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Registered.Clear();
                Publish();
            }
        }

        /// <summary>Replaces the lock-free snapshot <see cref="TryGet"/> reads. Callers hold <see cref="Gate"/>.</summary>
        private static void Publish() => System.Threading.Volatile.Write(ref _snapshot, Registered.ToArray());

        private readonly struct Registration
        {
            internal Registration(ISubtitleSource source, string name)
            {
                Source = source;
                Name = name;
            }

            internal ISubtitleSource Source { get; }

            internal string Name { get; }
        }

        private sealed class Composite : ISubtitleSource
        {
            public bool TryGet(string localizationId, string lang, out string text) =>
                SubtitleSources.TryGet(localizationId, lang, out text);
        }

        private sealed class Token : IDisposable
        {
            private ISubtitleSource? _source;

            internal Token(ISubtitleSource source) => _source = source;

            public void Dispose()
            {
                var source = _source;
                _source = null;
                Unregister(source);
            }
        }
    }
}
