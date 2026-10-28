using System;
using System.Threading;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// The handle every registering helper call returns (hotkeys, console commands, actions,
    /// script commands, condition tokens): dispose it to unregister. A refused registration comes
    /// back too, inactive, with the reason in <see cref="Problem"/> (the refusal is also logged
    /// once as a <c>MODS</c> warning). Everything still registered is taken back when the mod
    /// stops. Only the framework creates these.
    /// </summary>
    public sealed class CwyRegistration : IDisposable
    {
        private readonly Action<CwyRegistration>? _onDisposed;
        private IDisposable? _inner;

        internal CwyRegistration(string name, IDisposable inner, Action<CwyRegistration>? onDisposed)
        {
            Name = name ?? string.Empty;
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _onDisposed = onDisposed;
            Problem = string.Empty;
        }

        private CwyRegistration(string name, string problem)
        {
            Name = name ?? string.Empty;
            Problem = string.IsNullOrWhiteSpace(problem) ? "refused" : problem;
        }

        /// <summary>What was registered, as the framework knows it (e.g. <c>mod:my.mod/greet</c>, <c>my.mod.wave</c>).</summary>
        public string Name { get; }

        /// <summary>True while registered: false after <see cref="Dispose"/>, after the mod stopped, or when refused.</summary>
        public bool IsActive => Volatile.Read(ref _inner) != null;

        /// <summary>Why the registration was refused; empty when it was accepted.</summary>
        public string Problem { get; }

        /// <summary>True when the registration was refused (see <see cref="Problem"/>).</summary>
        public bool WasRefused => Problem.Length > 0;

        /// <summary>Unregisters. Disposing twice, or disposing a refused registration, does nothing.</summary>
        public void Dispose()
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            if (inner is null)
            {
                return;
            }

            _onDisposed?.Invoke(this);
            inner.Dispose();
        }

        /// <summary><c>my.mod.wave (active)</c> or <c>my.mod.wave (refused: ...)</c>.</summary>
        public override string ToString() =>
            Name + (WasRefused ? " (refused: " + Problem + ")" : IsActive ? " (active)" : " (removed)");

        /// <summary>A refused registration.</summary>
        internal static CwyRegistration Refused(string name, string problem) => new CwyRegistration(name, problem);
    }

    /// <summary>
    /// The common shape of the optional facets added for Phases D, E and S: whether the backend is
    /// installed, and one line saying what it is or why it is missing. Only the framework
    /// implements facets, so members can be added without breaking mods.
    /// </summary>
    public interface ICwyModFacet
    {
        /// <summary>True when the feature behind this facet is installed and running.</summary>
        bool IsAvailable { get; }

        /// <summary>One line for people: what is installed, or why it is not (never empty).</summary>
        string Status { get; }
    }
}
