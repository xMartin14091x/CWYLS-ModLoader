using System;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>
    /// Owns one newly decoded preview resource across request cancellation, late decoder callbacks and playback.
    /// Borrowed/cache resources are never submitted here. The adapter supplies an uncancelled main-thread dispatcher.
    /// </summary>
    public sealed class MakerPreviewOwnership<T> where T : class
    {
        private readonly object _gate = new object();
        private readonly Action<Action> _dispatch;
        private readonly Action<T> _destroy;
        private T? _resource;
        private bool _requestEnded;
        private bool _playing;
        private bool _completed;

        /// <summary>Creates the owner; dispatch must arrange execution even after the request has ended.</summary>
        public MakerPreviewOwnership(Action<Action> dispatch, Action<T> destroy)
        {
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
        }

        /// <summary>Adopts a fresh decoder result, including one arriving after timeout/cancellation.</summary>
        public void Decoded(T resource)
        {
            if (resource is null) throw new ArgumentNullException(nameof(resource));
            T? release;
            lock (_gate)
            {
                if (_resource != null) throw new InvalidOperationException("Only one decoded preview is owned.");
                _resource = resource;
                release = TakeUnowned();
            }
            Release(release);
        }

        /// <summary>
        /// Runs the actual playback attempt atomically against request abandonment. Only a successful attempt
        /// transfers ownership to playback; false/throw leaves ownership with the request's finally.
        /// </summary>
        public bool TryStart(Func<bool> start)
        {
            lock (_gate)
            {
                if (_requestEnded) return false;
                if (!start()) return false;
                _playing = true;
                return true;
            }
        }

        /// <summary>Called from the request's finally, independently of its cancellation token.</summary>
        public void EndRequest()
        {
            T? release;
            lock (_gate)
            {
                _requestEnded = true;
                release = TakeUnowned();
            }
            Release(release);
        }

        /// <summary>Called when accepted playback finishes, including rejection/cancellation by the playback runner.</summary>
        public void PlaybackCompleted()
        {
            T? release;
            lock (_gate)
            {
                _completed = true;
                release = TakeUnowned();
            }
            Release(release);
        }

        private T? TakeUnowned()
        {
            if (!_completed && (!_requestEnded || _playing)) return null;
            var resource = _resource;
            _resource = null;
            return resource;
        }

        private void Release(T? resource)
        {
            if (resource != null) _dispatch(() => _destroy(resource));
        }
    }
}
