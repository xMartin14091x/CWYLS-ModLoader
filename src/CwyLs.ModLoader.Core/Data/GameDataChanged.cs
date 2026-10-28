using System;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// Notification that one section of the player's data changed. Immutable and Unity-free, so a
    /// subscriber can queue it, log it or forward it off the main thread without copying.
    /// </summary>
    public sealed class GameDataChanged
    {
        /// <summary>Creates a notification.</summary>
        /// <param name="section">Which area changed.</param>
        /// <param name="reason">
        /// Short machine-readable cause, e.g. <c>save:SaveTodoList</c> or <c>event:OnEndWork</c>.
        /// <c>null</c> becomes an empty string.
        /// </param>
        /// <param name="time">When it changed; defaults to <see cref="DateTimeOffset.Now"/>.</param>
        public GameDataChanged(GameDataSection section, string? reason = null, DateTimeOffset? time = null)
        {
            Section = section;
            Reason = reason ?? string.Empty;
            Time = time ?? DateTimeOffset.Now;
        }

        /// <summary>The section that changed.</summary>
        public GameDataSection Section { get; }

        /// <summary>Short machine-readable cause. Never null; may be empty.</summary>
        public string Reason { get; }

        /// <summary>Local time the change was observed, with offset.</summary>
        public DateTimeOffset Time { get; }

        /// <summary>Diagnostic form, e.g. <c>Todos (save:SaveTodoList)</c>.</summary>
        public override string ToString() =>
            Reason.Length == 0 ? Section.ToString() : Section + " (" + Reason + ")";
    }
}
