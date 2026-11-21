using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// The maker event stream behind <c>GET /v1/maker/events</c>: an <see cref="ApiEventRing"/> of
    /// <see cref="MakerDefaults.EventCapacity"/> events (same sequence numbers, epoch, drop counting and
    /// long-poll as <c>/v1/events</c>) that the game adapters publish into through
    /// <see cref="MakerBackends.Publish"/>. Data members are copied as scalars only (strings, whole and
    /// decimal numbers, booleans, null); anything else is written as null, so an adapter can never put
    /// an object graph or game data on the wire by accident. Thread-safe; <see cref="Publish"/> never throws.
    /// </summary>
    public sealed class MakerEventRing : IMakerEventSink
    {
        private readonly Action<string>? _diagnostics;
        private long _refused;

        /// <summary>Creates an empty stream.</summary>
        /// <param name="capacity">Events kept; at least 1.</param>
        /// <param name="now">Wall clock for event times; null means <see cref="DateTimeOffset.Now"/>.</param>
        /// <param name="diagnostics">Where a refused or failed publish is reported (once per 100), or null.</param>
        public MakerEventRing(int capacity = MakerDefaults.EventCapacity, Func<DateTimeOffset>? now = null, Action<string>? diagnostics = null)
        {
            Ring = new ApiEventRing(capacity, now);
            _diagnostics = diagnostics;
        }

        /// <summary>The ring the events route reads.</summary>
        public ApiEventRing Ring { get; }

        /// <summary>Publishes that were refused (no type) or failed.</summary>
        public long Refused => System.Threading.Interlocked.Read(ref _refused);

        /// <inheritdoc />
        public void Publish(string type, IReadOnlyDictionary<string, object?> data)
        {
            try
            {
                if (string.IsNullOrEmpty(type) || type.Length > 64)
                {
                    Refuse("an event without a type (or with one over 64 characters) was dropped");
                    return;
                }

                var copy = new JsonObject();
                if (data != null)
                {
                    foreach (var pair in data)
                    {
                        if (!string.IsNullOrEmpty(pair.Key))
                        {
                            copy[pair.Key] = Scalar(pair.Value);
                        }
                    }
                }

                Ring.Append(type, copy);
            }
            catch (Exception ex)
            {
                Refuse("publishing " + type + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Stops recording and wakes every waiting reader.</summary>
        public void Close() => Ring.Close();

        private static object? Scalar(object? value) => value switch
        {
            null => null,
            string s => s,
            bool b => b,
            sbyte or byte or short or ushort or int or uint or long => value,
            ulong u => u,
            float f => (double)f,
            double d => d,
            decimal m => (double)m,
            _ => null,
        };

        private void Refuse(string message)
        {
            var n = System.Threading.Interlocked.Increment(ref _refused);
            if (n == 1 || n % 100 == 0)
            {
                try
                {
                    _diagnostics?.Invoke("maker events: " + message + " (" + n + " so far)");
                }
                catch
                {
                    // A diagnostics sink that throws must not make Publish throw.
                }
            }
        }
    }
}
