using System;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// A scoped change to Satone: it remembers what she looked like before, and on
    /// <see cref="Dispose"/> hands the channels it covers back to the game.
    /// </summary>
    /// <example>
    /// <code>
    /// using (Satone.FaceScoped("smile"))
    /// {
    ///     // ... she smiles for as long as this block runs ...
    /// }   // -> Satone.RestoreDefaults(RestoreScope.Face)
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// <b>Why it restores the game's default rather than the captured value.</b> The snapshot is
    /// kept for evidence, not for replay. Writing a captured <c>Facial = 18</c> back would be
    /// wrong outside the context that set it (<c>ScenarioReader</c> sets
    /// <c>Open_Eye</c> only while she is asleep), so the revert runs the game's own reset idiom -
    /// the same one <c>RoomGameManager.OnEndPlayedReaction</c> runs after a click reaction.
    /// </para>
    /// <para>
    /// Engine-free: the actual restore is the delegate the adapter supplies. That delegate is
    /// expected not to throw (the adapter routes it through its main-thread host, which catches
    /// and logs), so nothing is swallowed here.
    /// </para>
    /// </remarks>
    public sealed class SatoneRevert : IDisposable
    {
        private readonly Action<RestoreScope, SatoneSnapshot>? _restore;
        private bool _disposed;

        /// <summary>Creates a revert handle.</summary>
        /// <param name="scope">The channels <see cref="Dispose"/> restores.</param>
        /// <param name="taken">What Satone looked like before the change.</param>
        /// <param name="restore">
        /// What actually restores her, called once by <see cref="Dispose"/>. Null makes this a
        /// no-op handle, which is what a refused call returns.
        /// </param>
        public SatoneRevert(RestoreScope scope, SatoneSnapshot taken, Action<RestoreScope, SatoneSnapshot>? restore)
        {
            Scope = scope;
            Taken = taken;
            _restore = restore;
        }

        /// <summary>A handle that restores nothing; returned when a call was refused.</summary>
        /// <param name="scope">The scope the caller asked for, for the log line.</param>
        public static SatoneRevert None(RestoreScope scope = RestoreScope.None) =>
            new SatoneRevert(scope, SatoneSnapshot.Unknown, null);

        /// <summary>The channels this handle restores.</summary>
        public RestoreScope Scope { get; }

        /// <summary>What Satone looked like when the handle was taken.</summary>
        public SatoneSnapshot Taken { get; }

        /// <summary>True once <see cref="Dispose"/> has run.</summary>
        public bool Disposed => _disposed;

        /// <summary>True when this handle will actually do something.</summary>
        public bool Restores => _restore != null && Scope != RestoreScope.None;

        /// <summary>
        /// Hands the covered channels back to the game. Idempotent: the second and later calls do
        /// nothing, so a <c>using</c> block inside a <c>finally</c> is safe.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Scope == RestoreScope.None)
            {
                return;
            }

            _restore?.Invoke(Scope, Taken);
        }

        /// <summary>e.g. <c>revert face (facial=0 motion=200 look=0 state=work_pc)</c>.</summary>
        public override string ToString() =>
            "revert " + Scope.Describe() + " (" + Taken + ")" + (_disposed ? " [disposed]" : string.Empty);
    }
}
