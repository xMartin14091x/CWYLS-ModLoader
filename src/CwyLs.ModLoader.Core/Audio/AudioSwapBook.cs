using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>Which clip a replaced row plays: the game's own, or the replacement (A/B).</summary>
    public enum AudioVariant
    {
        /// <summary>The replacement from a pack or a mod. The default.</summary>
        Pack = 0,

        /// <summary>The game's own clip.</summary>
        Original = 1,
    }

    /// <summary>Who supplied a replacement clip.</summary>
    public enum AudioClaimPriority
    {
        /// <summary>A content pack's file.</summary>
        Pack = 0,

        /// <summary>A mod, at runtime. Beats every pack.</summary>
        Runtime = 1,
    }

    /// <summary>
    /// The bookkeeping behind row replacement and the A/B switch, per row key: the game's original
    /// clip, the replacement claims (packs and mods), and which variant is selected. Generic over the
    /// clip type so it is testable without Unity; the adapter uses it with <c>AudioClip</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one invariant that matters: <b>a clip this book handed out is never recorded as an
    /// original</b>. Master data can be rebuilt (EditData clones rows, a new load replaces the
    /// lists), and a cloned row may still hold our clip; recording that as "the game's clip" would make
    /// restore and the A/B switch play the replacement forever.
    /// </para>
    /// <para>
    /// Clips are compared by reference. The adapter passes null for a clip Unity has destroyed.
    /// Not thread-safe: the adapter calls it on the main thread only.
    /// </para>
    /// </remarks>
    /// <typeparam name="TClip">The clip type.</typeparam>
    public sealed class AudioSwapBook<TClip>
        where TClip : class
    {
        private readonly Dictionary<string, TClip> _originals = new Dictionary<string, TClip>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Claim>> _claims = new Dictionary<string, List<Claim>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AudioVariant> _variants = new Dictionary<string, AudioVariant>(StringComparer.OrdinalIgnoreCase);
        private long _sequence;

        /// <summary>How many row keys have at least one claim.</summary>
        public int ClaimedCount => _claims.Count;

        /// <summary>Every row key with at least one claim.</summary>
        public IReadOnlyCollection<string> ClaimedKeys => _claims.Keys;

        /// <summary>
        /// Records what a row holds right now. A clip that is not one of ours becomes (or stays) the
        /// row's original; one of ours is ignored.
        /// </summary>
        /// <param name="key">The row key.</param>
        /// <param name="current">The row's clip now; null when it has none or Unity destroyed it.</param>
        public void Observe(string key, TClip? current)
        {
            if (current is null || IsOurs(current))
            {
                return;
            }

            _originals[key] = current;
        }

        /// <summary>The game's clip for a row, or null when it was never observed.</summary>
        /// <param name="key">The row key.</param>
        public TClip? OriginalOf(string key) => _originals.TryGetValue(key, out var clip) ? clip : null;

        /// <summary>Forgets an original (the adapter calls this when Unity destroyed it).</summary>
        /// <param name="key">The row key.</param>
        public void ForgetOriginal(string key) => _originals.Remove(key);

        /// <summary>True when <paramref name="clip"/> is a replacement this book holds.</summary>
        /// <param name="clip">A clip.</param>
        public bool IsOurs(TClip? clip)
        {
            if (clip is null)
            {
                return false;
            }

            foreach (var list in _claims.Values)
            {
                foreach (var claim in list)
                {
                    if (ReferenceEquals(claim.Clip, clip))
                    {
                        return true;
                    }
                }
            }

            foreach (var retired in _retired)
            {
                if (ReferenceEquals(retired, clip))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Replaces every pack claim with this set (a pack reload): rows not in it lose their pack
        /// claim; runtime claims are untouched.
        /// </summary>
        /// <param name="claims">Row key, source and clip of each pack replacement.</param>
        public void SetPackClaims(IEnumerable<KeyValuePair<string, AudioPackClaim<TClip>>>? claims)
        {
            foreach (var key in new List<string>(_claims.Keys))
            {
                var list = _claims[key];
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Priority == AudioClaimPriority.Pack)
                    {
                        Retire(list[i].Clip);
                        list.RemoveAt(i);
                    }
                }

                if (list.Count == 0)
                {
                    _claims.Remove(key);
                }
            }

            if (claims is null)
            {
                return;
            }

            foreach (var pair in claims)
            {
                if (pair.Value is null || pair.Value.Clip is null || string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                Add(pair.Key, new Claim(pair.Value.Source, AudioClaimPriority.Pack, pair.Value.Clip, ++_sequence));
            }
        }

        /// <summary>Adds a mod's replacement; it beats every pack claim. Dispose the handle to take it back.</summary>
        /// <param name="key">The row key.</param>
        /// <param name="source">Who, e.g. <c>mod:my.mod</c>.</param>
        /// <param name="clip">The clip.</param>
        /// <returns>The handle.</returns>
        /// <exception cref="ArgumentException"><paramref name="key"/> is blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="clip"/> is null.</exception>
        public IDisposable AddRuntime(string key, string source, TClip clip)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("a row key is required", nameof(key));
            }

            if (clip is null)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            var claim = new Claim(string.IsNullOrWhiteSpace(source) ? "runtime" : source, AudioClaimPriority.Runtime, clip, ++_sequence);
            Add(key, claim);
            return new ClaimHandle(this, key, claim);
        }

        /// <summary>The winning replacement for a row: the newest runtime claim, else the newest pack claim; null when none.</summary>
        /// <param name="key">The row key.</param>
        /// <param name="source">Who supplied it; empty when none.</param>
        public TClip? ReplacementOf(string key, out string source)
        {
            source = string.Empty;
            if (!_claims.TryGetValue(key, out var list) || list.Count == 0)
            {
                return null;
            }

            Claim? best = null;
            foreach (var claim in list)
            {
                if (best is null
                    || claim.Priority > best.Priority
                    || (claim.Priority == best.Priority && claim.Sequence > best.Sequence))
                {
                    best = claim;
                }
            }

            source = best!.Source;
            return best.Clip;
        }

        /// <summary>The selected variant of a row; <see cref="AudioVariant.Pack"/> unless switched.</summary>
        /// <param name="key">The row key.</param>
        public AudioVariant VariantOf(string key) => _variants.TryGetValue(key, out var variant) ? variant : AudioVariant.Pack;

        /// <summary>Selects a variant for a row.</summary>
        /// <param name="key">The row key.</param>
        /// <param name="variant">The variant.</param>
        public void Select(string key, AudioVariant variant)
        {
            if (variant == AudioVariant.Pack)
            {
                _variants.Remove(key);
            }
            else
            {
                _variants[key] = variant;
            }
        }

        /// <summary>
        /// The clip a row should hold: the replacement when one exists, the feature is active and the
        /// pack variant is selected; otherwise the original. Null means "leave the row alone" (no
        /// replacement and nothing of ours in it, or no original known to go back to).
        /// </summary>
        /// <param name="key">The row key.</param>
        /// <param name="current">What the row holds now.</param>
        /// <param name="active">False when the feature is switched off: every row goes back to its original.</param>
        public TClip? Desired(string key, TClip? current, bool active)
        {
            var replacement = ReplacementOf(key, out _);
            var original = OriginalOf(key);

            if (active && replacement != null && VariantOf(key) == AudioVariant.Pack)
            {
                // Without a known original there is nothing to switch back to later; the adapter
                // observes the row before asking, so this only happens for a row that already held
                // one of our clips when we first saw it.
                return original != null || IsOurs(current) ? replacement : null;
            }

            if (IsOurs(current))
            {
                return original;
            }

            return null;
        }

        private readonly List<TClip> _retired = new List<TClip>();

        private void Retire(TClip clip)
        {
            // A clip we handed out stays "ours" after its claim is gone: a row may still hold it until
            // the next apply puts the original back.
            foreach (var retired in _retired)
            {
                if (ReferenceEquals(retired, clip))
                {
                    return;
                }
            }

            _retired.Add(clip);
        }

        private void Add(string key, Claim claim)
        {
            if (!_claims.TryGetValue(key, out var list))
            {
                list = new List<Claim>();
                _claims.Add(key, list);
            }

            list.Add(claim);
        }

        private void Remove(string key, Claim claim)
        {
            if (!_claims.TryGetValue(key, out var list))
            {
                return;
            }

            if (list.Remove(claim))
            {
                Retire(claim.Clip);
            }

            if (list.Count == 0)
            {
                _claims.Remove(key);
            }
        }

        private sealed class Claim
        {
            internal Claim(string source, AudioClaimPriority priority, TClip clip, long sequence)
            {
                Source = source;
                Priority = priority;
                Clip = clip;
                Sequence = sequence;
            }

            internal string Source { get; }

            internal AudioClaimPriority Priority { get; }

            internal TClip Clip { get; }

            internal long Sequence { get; }
        }

        private sealed class ClaimHandle : IDisposable
        {
            private AudioSwapBook<TClip>? _book;
            private readonly string _key;
            private readonly Claim _claim;

            internal ClaimHandle(AudioSwapBook<TClip> book, string key, Claim claim)
            {
                _book = book;
                _key = key;
                _claim = claim;
            }

            public void Dispose()
            {
                var book = _book;
                _book = null;
                book?.Remove(_key, _claim);
            }
        }
    }

    /// <summary>A pack's replacement for one row: who and which clip.</summary>
    /// <typeparam name="TClip">The clip type.</typeparam>
    public sealed class AudioPackClaim<TClip>
        where TClip : class
    {
        /// <summary>Creates a claim.</summary>
        /// <param name="source">Who, e.g. <c>cwy.demo.audio/audio/ambient/LightRain.wav</c>.</param>
        /// <param name="clip">The clip.</param>
        public AudioPackClaim(string source, TClip clip)
        {
            Source = source ?? string.Empty;
            Clip = clip;
        }

        /// <summary>Who supplied it.</summary>
        public string Source { get; }

        /// <summary>The clip.</summary>
        public TClip Clip { get; }
    }
}
