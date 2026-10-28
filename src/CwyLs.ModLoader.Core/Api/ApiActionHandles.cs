using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// One action started through the API: <c>POST /v1/actions/&lt;name&gt;</c> answers with its
    /// id at once, <c>GET /v1/actions/&lt;handle&gt;</c> reads it. The id is random and unguessable,
    /// but holding it is not authorisation: every read still needs the access key. Thread-safe.
    /// </summary>
    public sealed class ApiActionHandle
    {
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        private ActionResult? _result;
        private DateTimeOffset? _completedAt;

        internal ApiActionHandle(string id, string action, ActionTier tier, string client, DateTimeOffset createdAt, double createdMono)
        {
            Id = id;
            Action = action;
            Tier = tier;
            Client = client;
            CreatedAt = createdAt;
            CreatedMono = createdMono;
            Cancellation = new CancellationTokenSource();
        }

        /// <summary>The handle id, <c>act-</c> and 24 hex characters.</summary>
        public string Id { get; }

        /// <summary>The action name.</summary>
        public string Action { get; }

        /// <summary>The action's tier.</summary>
        public ActionTier Tier { get; }

        /// <summary>The calling program's name.</summary>
        public string Client { get; }

        /// <summary>When it was started.</summary>
        public DateTimeOffset CreatedAt { get; }

        /// <summary>Monotonic seconds when it was started.</summary>
        public double CreatedMono { get; }

        /// <summary>Cancelled when the API stops or the hard limit passes: the API stops waiting for the runner.</summary>
        public CancellationTokenSource Cancellation { get; }

        /// <summary>True once the result is in.</summary>
        public bool IsDone => _done.IsSet;

        /// <summary>The result, or null while pending.</summary>
        public ActionResult? Result
        {
            get { lock (_gate) { return _result; } }
        }

        /// <summary>When it finished, or null.</summary>
        public DateTimeOffset? CompletedAt
        {
            get { lock (_gate) { return _completedAt; } }
        }

        internal double CompletedMono { get; private set; }

        /// <summary>Blocks until the result is in or the time is up.</summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>True when done.</returns>
        public bool Wait(TimeSpan timeout) => timeout <= TimeSpan.Zero ? _done.IsSet : _done.Wait(timeout);

        /// <summary>The handle as JSON: <c>{handle, action, tier, client, state, createdAt, completedAt, result}</c>.</summary>
        /// <param name="replayed">True when answering a repeated idempotency key.</param>
        public string ToJson(bool replayed = false)
        {
            ActionResult? result;
            DateTimeOffset? completed;
            lock (_gate)
            {
                result = _result;
                completed = _completedAt;
            }

            var o = new JsonObject
            {
                ["handle"] = Id,
                ["action"] = Action,
                ["tier"] = ApiActionCatalog.TierName(Tier),
                ["client"] = Client,
                ["state"] = result is null ? "pending" : "done",
                ["replayed"] = replayed,
                ["createdAt"] = ApiJson.Time(CreatedAt),
                ["completedAt"] = completed is null ? null : ApiJson.Time(completed.Value),
                ["result"] = result is null
                    ? null
                    : new JsonObject
                    {
                        ["status"] = ApiActionCatalog.StatusName(result.Status),
                        ["ok"] = result.IsOk,
                        ["path"] = ApiActionCatalog.PathName(result.Path),
                        ["message"] = result.Message,
                        ["data"] = result.Data,
                    },
            };
            return ApiJson.Write(o);
        }

        internal bool TryComplete(ActionResult result, DateTimeOffset now, double mono)
        {
            lock (_gate)
            {
                if (_result != null)
                {
                    return false;
                }

                _result = result;
                _completedAt = now;
                CompletedMono = mono;
            }

            _done.Set();
            return true;
        }
    }

    /// <summary>
    /// The handles of API-started actions: at most <see cref="MaxInFlight"/> pending, finished
    /// ones kept for <see cref="Lifetime"/> and at most <see cref="Capacity"/> in all (oldest
    /// finished dropped first). Thread-safe.
    /// </summary>
    public sealed class ApiActionHandles
    {
        /// <summary>The prefix of every handle id.</summary>
        public const string Prefix = "act-";

        private readonly object _gate = new object();
        private readonly Dictionary<string, ApiActionHandle> _byId = new Dictionary<string, ApiActionHandle>(StringComparer.Ordinal);
        private readonly IApiClock _clock;

        /// <summary>Creates an empty table.</summary>
        /// <param name="clock">The clock.</param>
        /// <param name="capacity">Handles kept in all.</param>
        /// <param name="lifetime">How long a finished handle stays readable.</param>
        /// <param name="maxInFlight">Pending actions allowed at once.</param>
        public ApiActionHandles(IApiClock clock, int capacity, TimeSpan lifetime, int maxInFlight)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Capacity = Math.Max(1, capacity);
            Lifetime = lifetime;
            MaxInFlight = Math.Max(1, maxInFlight);
        }

        /// <summary>Handles kept in all.</summary>
        public int Capacity { get; }

        /// <summary>How long a finished handle stays readable.</summary>
        public TimeSpan Lifetime { get; }

        /// <summary>Pending actions allowed at once.</summary>
        public int MaxInFlight { get; }

        /// <summary>Pending actions now.</summary>
        public int InFlight
        {
            get
            {
                lock (_gate)
                {
                    var n = 0;
                    foreach (var h in _byId.Values)
                    {
                        if (!h.IsDone)
                        {
                            n++;
                        }
                    }

                    return n;
                }
            }
        }

        /// <summary>Handles held now.</summary>
        public int Count
        {
            get { lock (_gate) { return _byId.Count; } }
        }

        /// <summary>True when <paramref name="text"/> has the shape of a handle id.</summary>
        /// <param name="text">A candidate.</param>
        public static bool IsHandleId(string? text)
        {
            if (text is null || text.Length != Prefix.Length + 24 || !text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            for (var i = Prefix.Length; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Creates a pending handle, or says why not (too many in flight).</summary>
        /// <param name="action">The action name.</param>
        /// <param name="tier">Its tier.</param>
        /// <param name="client">The caller's name.</param>
        /// <param name="handle">The handle, or null.</param>
        /// <returns>True when created.</returns>
        public bool TryCreate(string action, ActionTier tier, string client, out ApiActionHandle? handle)
        {
            lock (_gate)
            {
                PruneLocked();
                var pending = 0;
                foreach (var h in _byId.Values)
                {
                    if (!h.IsDone)
                    {
                        pending++;
                    }
                }

                if (pending >= MaxInFlight || _byId.Count >= Capacity)
                {
                    handle = null;
                    return false;
                }

                string id;
                do
                {
                    id = NewId();
                }
                while (_byId.ContainsKey(id));

                handle = new ApiActionHandle(id, action, tier, client, _clock.Now, _clock.MonotonicSeconds);
                _byId.Add(id, handle);
                return true;
            }
        }

        /// <summary>Finds a handle.</summary>
        /// <param name="id">The id.</param>
        /// <param name="handle">The handle, or null when unknown or expired.</param>
        public bool TryGet(string? id, out ApiActionHandle? handle)
        {
            handle = null;
            if (!IsHandleId(id))
            {
                return false;
            }

            lock (_gate)
            {
                PruneLocked();
                return _byId.TryGetValue(id!, out handle);
            }
        }

        /// <summary>Stores the result (first one wins) and wakes readers waiting on it.</summary>
        /// <param name="handle">The handle.</param>
        /// <param name="result">The result.</param>
        /// <returns>True when this call stored it.</returns>
        public bool Complete(ApiActionHandle handle, ActionResult result)
        {
            if (handle is null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            return handle.TryComplete(result ?? ActionResult.Failed("no result"), _clock.Now, _clock.MonotonicSeconds);
        }

        /// <summary>Stops waiting for every pending action (the API is stopping).</summary>
        public void CancelAll()
        {
            List<ApiActionHandle> pending;
            lock (_gate)
            {
                pending = new List<ApiActionHandle>(_byId.Values);
            }

            foreach (var h in pending)
            {
                if (!h.IsDone)
                {
                    h.Cancellation.Cancel();
                }
            }
        }

        private void PruneLocked()
        {
            var now = _clock.MonotonicSeconds;
            List<string>? expired = null;
            foreach (var pair in _byId)
            {
                if (pair.Value.IsDone && now - pair.Value.CompletedMono > Lifetime.TotalSeconds)
                {
                    (expired ??= new List<string>()).Add(pair.Key);
                }
            }

            if (expired != null)
            {
                foreach (var key in expired)
                {
                    _byId.Remove(key);
                }
            }

            if (_byId.Count < Capacity)
            {
                return;
            }

            // Still full: drop the oldest finished ones until there is room for one more.
            var finished = new List<ApiActionHandle>();
            foreach (var h in _byId.Values)
            {
                if (h.IsDone)
                {
                    finished.Add(h);
                }
            }

            finished.Sort((a, b) => a.CompletedMono.CompareTo(b.CompletedMono));
            for (var i = 0; i < finished.Count && _byId.Count >= Capacity; i++)
            {
                _byId.Remove(finished[i].Id);
            }
        }

        private static string NewId()
        {
            var bytes = new byte[12];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var sb = new StringBuilder(Prefix, Prefix.Length + 24);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }

    /// <summary>What an idempotency lookup found.</summary>
    public enum ApiIdempotencyOutcome
    {
        /// <summary>A new key: a new handle was created.</summary>
        Created = 0,

        /// <summary>The same key with the same request: the earlier handle.</summary>
        Replayed = 1,

        /// <summary>The same key with a different request: refused (409).</summary>
        Conflict = 2,

        /// <summary>A new key, but no handle could be created (too many in flight).</summary>
        Full = 3,
    }

    /// <summary>
    /// Idempotency keys of <c>POST /v1/actions/&lt;name&gt;</c> (header <c>Idempotency-Key</c>): a
    /// retried request with the same key and the same action and arguments gets the first
    /// handle back instead of running twice. Keys live <see cref="Lifetime"/>; at most
    /// <see cref="Capacity"/> are kept. Thread-safe.
    /// </summary>
    public sealed class ApiIdempotencyCache
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly IApiClock _clock;

        /// <summary>Creates an empty cache.</summary>
        /// <param name="clock">The clock.</param>
        /// <param name="capacity">Keys kept.</param>
        /// <param name="lifetime">How long a key is remembered.</param>
        public ApiIdempotencyCache(IApiClock clock, int capacity, TimeSpan lifetime)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Capacity = Math.Max(1, capacity);
            Lifetime = lifetime;
        }

        /// <summary>Keys kept.</summary>
        public int Capacity { get; }

        /// <summary>How long a key is remembered.</summary>
        public TimeSpan Lifetime { get; }

        /// <summary>A key is 1-64 characters of letters, digits and <c>. _ : -</c>.</summary>
        /// <param name="key">A candidate.</param>
        public static bool IsValidKey(string? key)
        {
            if (string.IsNullOrEmpty(key) || key!.Length > 64)
            {
                return false;
            }

            foreach (var c in key)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == ':' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns the handle for a key, creating it with <paramref name="create"/> when the key is
        /// new. Atomic: two concurrent requests with one key get one handle.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="fingerprint">The request's fingerprint (<see cref="ApiActionCatalog.Fingerprint"/>).</param>
        /// <param name="create">Creates the handle; returns null when it cannot (nothing is remembered then).</param>
        /// <param name="handle">The new or earlier handle, or null.</param>
        public ApiIdempotencyOutcome GetOrCreate(
            string key, string fingerprint, Func<ApiActionHandle?> create, out ApiActionHandle? handle)
        {
            if (create is null)
            {
                throw new ArgumentNullException(nameof(create));
            }

            lock (_gate)
            {
                Prune();
                if (_byKey.TryGetValue(key, out var entry))
                {
                    if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        handle = null;
                        return ApiIdempotencyOutcome.Conflict;
                    }

                    handle = entry.Handle;
                    return ApiIdempotencyOutcome.Replayed;
                }

                handle = create();
                if (handle is null)
                {
                    return ApiIdempotencyOutcome.Full;
                }

                if (_byKey.Count >= Capacity)
                {
                    string? oldest = null;
                    var oldestAt = double.MaxValue;
                    foreach (var pair in _byKey)
                    {
                        if (pair.Value.At < oldestAt)
                        {
                            oldestAt = pair.Value.At;
                            oldest = pair.Key;
                        }
                    }

                    if (oldest != null)
                    {
                        _byKey.Remove(oldest);
                    }
                }

                _byKey[key] = new Entry(fingerprint, handle, _clock.MonotonicSeconds);
                return ApiIdempotencyOutcome.Created;
            }
        }

        private void Prune()
        {
            var now = _clock.MonotonicSeconds;
            List<string>? expired = null;
            foreach (var pair in _byKey)
            {
                if (now - pair.Value.At > Lifetime.TotalSeconds)
                {
                    (expired ??= new List<string>()).Add(pair.Key);
                }
            }

            if (expired != null)
            {
                foreach (var key in expired)
                {
                    _byKey.Remove(key);
                }
            }
        }

        private sealed class Entry
        {
            internal Entry(string fingerprint, ApiActionHandle handle, double at)
            {
                Fingerprint = fingerprint;
                Handle = handle;
                At = at;
            }

            internal string Fingerprint { get; }

            internal ApiActionHandle Handle { get; }

            internal double At { get; }
        }
    }
}
