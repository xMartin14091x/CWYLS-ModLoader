using System;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// One subscriber that threw. Handed to the dispatcher's error sink so the framework can name
    /// the guilty mod in the log; the exception never reaches the game.
    /// </summary>
    public sealed class EventDispatchFailure
    {
        /// <summary>Creates a failure record.</summary>
        /// <param name="eventName">Name of the channel that was publishing, e.g. <c>SatoneStateChanged</c>.</param>
        /// <param name="subscriber">Where the handler is declared, e.g. <c>MyMod.OnState</c>.</param>
        /// <param name="error">The exception the handler threw.</param>
        /// <exception cref="ArgumentException"><paramref name="eventName"/> or <paramref name="subscriber"/> is null or blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
        public EventDispatchFailure(string eventName, string subscriber, Exception error)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("must not be null or blank", nameof(eventName));
            }

            if (string.IsNullOrWhiteSpace(subscriber))
            {
                throw new ArgumentException("must not be null or blank", nameof(subscriber));
            }

            EventName = eventName;
            Subscriber = subscriber;
            Error = error ?? throw new ArgumentNullException(nameof(error));
        }

        /// <summary>Name of the channel that was publishing.</summary>
        public string EventName { get; }

        /// <summary>Type and method of the handler that threw, as far as reflection can tell.</summary>
        public string Subscriber { get; }

        /// <summary>The exception the handler threw.</summary>
        public Exception Error { get; }

        /// <summary>
        /// One log line: <c>EVENT SatoneStateChanged: subscriber MyMod.OnState threw
        /// InvalidOperationException: ...</c>.
        /// </summary>
        public override string ToString() =>
            "EVENT " + EventName + ": subscriber " + Subscriber + " threw "
            + Error.GetType().Name + ": " + Error.Message;
    }
}
