using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// Marker for every typed game event the framework publishes: the one compact line the console
    /// mirror prints, plus the flat payload the diagnostic stream records.
    /// </summary>
    /// <remarks>
    /// The marker is what makes <c>CWYEvents.Subscribe&lt;T&gt;</c> and the catch-all <c>Any</c>
    /// stream possible without reflection over arbitrary types: a channel exists for exactly the
    /// types that implement this.
    /// </remarks>
    public interface ICwyEvent
    {
        /// <summary>
        /// The event as one short human line, without the event name - e.g.
        /// <c>WantTalk -&gt; BreakMovie</c>. The mirror prints <c>EVENT &lt;name&gt; &lt;this&gt;</c>,
        /// so it must not contain a newline and should stay under about 120 characters.
        /// </summary>
        string Describe();

        /// <summary>
        /// The event's members as a flat payload for
        /// <see cref="CwyLs.ModLoader.Core.Bus.TraceBus"/>: keys become JSON members, values are
        /// strings, bools, numbers or null. Never null; an event with no members returns an empty
        /// dictionary. Callers may not mutate the result.
        /// </summary>
        IReadOnlyDictionary<string, object?> Fields();
    }
}
