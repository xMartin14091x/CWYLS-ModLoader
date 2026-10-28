using System;
using System.Collections.Generic;
using System.Threading;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// One typed event channel: subscribers in, one <see cref="Publish"/> out, and a hard promise
    /// that a subscriber's exception never reaches the publisher.
    /// </summary>
    /// <typeparam name="T">The event argument type carried by this channel.</typeparam>
    /// <remarks>
    /// <para>
    /// The publisher is usually a Harmony postfix running inside the game's own call stack, so a
    /// mod that throws would otherwise abort whatever the game was doing - a save, a line of
    /// dialogue. Every handler is therefore invoked in its own <c>try</c>/<c>catch</c>, the failure
    /// is reported to the error sink supplied at construction, and the remaining handlers still run.
    /// </para>
    /// <para>
    /// Handlers are held in an immutable array swapped under a lock, so <see cref="Publish"/> never
    /// takes the lock and a handler that subscribes or unsubscribes from inside a callback cannot
    /// corrupt the iteration in progress - that change is seen by the next publish.
    /// </para>
    /// </remarks>
    public sealed class EventDispatcher<T>
    {
        private static readonly Action<T>[] None = new Action<T>[0];

        private readonly object _gate = new object();
        private readonly Action<EventDispatchFailure>? _errorSink;

        private Action<T>[] _handlers = None;
        private long _publishCount;
        private long _failureCount;

        /// <summary>Creates a channel.</summary>
        /// <param name="name">
        /// Channel name used in log lines and in <see cref="EventDispatchFailure.EventName"/>;
        /// normally the event type's name.
        /// </param>
        /// <param name="errorSink">
        /// Where subscriber failures are reported. Optional: when null, a failing subscriber is
        /// still isolated and counted, it just goes unreported. The sink itself must not throw;
        /// if it does, the exception is swallowed - there is nowhere left to report it.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        public EventDispatcher(string name, Action<EventDispatchFailure>? errorSink = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("must not be null or blank", nameof(name));
            }

            Name = name;
            _errorSink = errorSink;
        }

        /// <summary>Channel name, normally the event type's name.</summary>
        public string Name { get; }

        /// <summary>How many handlers are currently subscribed.</summary>
        public int SubscriberCount => Volatile.Read(ref _handlers).Length;

        /// <summary>How many times <see cref="Publish"/> has been called on this channel.</summary>
        public long PublishCount => Interlocked.Read(ref _publishCount);

        /// <summary>How many handler invocations have thrown on this channel.</summary>
        public long FailureCount => Interlocked.Read(ref _failureCount);

        /// <summary>
        /// Subscribes <paramref name="handler"/> and returns the token that unsubscribes it.
        /// Disposing the token more than once is harmless. Subscribing the same delegate twice
        /// means it is called twice; each token removes one registration.
        /// </summary>
        /// <param name="handler">The callback. Must not throw - see the class remarks.</param>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public IDisposable Subscribe(Action<T> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (_gate)
            {
                var next = new Action<T>[_handlers.Length + 1];
                Array.Copy(_handlers, next, _handlers.Length);
                next[_handlers.Length] = handler;
                Volatile.Write(ref _handlers, next);
            }

            return new Subscription(this, handler);
        }

        /// <summary>
        /// Removes one registration of <paramref name="handler"/>.
        /// </summary>
        /// <param name="handler">The callback to remove.</param>
        /// <returns>True if a registration was removed; false if it was not subscribed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public bool Unsubscribe(Action<T> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (_gate)
            {
                var index = Array.IndexOf(_handlers, handler);
                if (index < 0)
                {
                    return false;
                }

                if (_handlers.Length == 1)
                {
                    Volatile.Write(ref _handlers, None);
                    return true;
                }

                var next = new Action<T>[_handlers.Length - 1];
                Array.Copy(_handlers, 0, next, 0, index);
                Array.Copy(_handlers, index + 1, next, index, _handlers.Length - index - 1);
                Volatile.Write(ref _handlers, next);
                return true;
            }
        }

        /// <summary>
        /// Invokes every subscriber with <paramref name="args"/>, each inside its own
        /// <c>try</c>/<c>catch</c>. Never throws, whatever the subscribers do.
        /// </summary>
        /// <param name="args">The event arguments.</param>
        public void Publish(T args)
        {
            Interlocked.Increment(ref _publishCount);

            var handlers = Volatile.Read(ref _handlers);
            for (var i = 0; i < handlers.Length; i++)
            {
                var handler = handlers[i];
                try
                {
                    handler(args);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failureCount);
                    Report(new EventDispatchFailure(Name, Describe(handler), ex));
                }
            }
        }

        /// <summary>Removes every subscriber. Used by tests and by shutdown; harmless mid-session.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                Volatile.Write(ref _handlers, None);
            }
        }

        /// <summary>
        /// Names a handler for a log line: <c>DeclaringType.Method</c>, or the delegate's target
        /// type when reflection cannot see a declaring type (some dynamic methods).
        /// </summary>
        /// <param name="handler">The delegate to describe; null becomes <c>&lt;null&gt;</c>.</param>
        public static string Describe(Delegate? handler)
        {
            if (handler is null)
            {
                return "<null>";
            }

            try
            {
                var method = handler.Method;
                var declaring = method.DeclaringType;
                return declaring is null ? method.Name : declaring.Name + "." + method.Name;
            }
            catch (Exception)
            {
                // Naming a subscriber is a nicety; failing to name it must never hide the failure
                // it was being named for, so this deliberately degrades instead of propagating.
                return handler.Target?.GetType().Name ?? "<unknown>";
            }
        }

        private void Report(EventDispatchFailure failure)
        {
            var sink = _errorSink;
            if (sink is null)
            {
                return;
            }

            try
            {
                sink(failure);
            }
            catch (Exception)
            {
                // The sink is the last place a failure could be reported. If it throws there is
                // nowhere left to say so, and rethrowing would push a mod's bug into the game.
            }
        }

        /// <summary>The token returned by <see cref="Subscribe"/>; unsubscribes exactly once.</summary>
        private sealed class Subscription : IDisposable
        {
            private readonly EventDispatcher<T> _owner;
            private Action<T>? _handler;

            internal Subscription(EventDispatcher<T> owner, Action<T> handler)
            {
                _owner = owner;
                _handler = handler;
            }

            public void Dispose()
            {
                var handler = Interlocked.Exchange(ref _handler, null);
                if (handler != null)
                {
                    _owner.Unsubscribe(handler);
                }
            }
        }
    }
}
