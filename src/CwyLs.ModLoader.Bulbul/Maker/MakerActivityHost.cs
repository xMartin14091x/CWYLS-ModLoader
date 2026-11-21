using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>Persistent Unity callback host for Studio's independent focus, pause and busy facts.</summary>
    internal sealed class MakerActivityHost : MonoBehaviour
    {
        internal const string HostObjectName = "CWY.MakerActivity";
        internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

        private static readonly object Gate = new object();
        private static readonly MakerGameActivity State = new MakerGameActivity(SystemApiClock.Instance, StaleAfter);

        private static MakerActivityHost? _instance;
        private static bool _quitting;

        private float _nextBusyPoll;
        private bool _started;

        internal static MakerGameActivity Activity => State;

        /// <summary>Creates the hidden scene-surviving host. Main thread only.</summary>
        internal static MakerActivityHost? Ensure()
        {
            lock (Gate)
            {
                if (_instance != null || _quitting)
                {
                    return _instance;
                }

                try
                {
                    var host = new GameObject(HostObjectName);
                    DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;
                    _instance = host.AddComponent<MakerActivityHost>();
                    Application.quitting += OnQuitting;
                    return _instance;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        MakerEditor.LogPrefix + " activity host could not be created: " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
            }
        }

        private void Start()
        {
            State.ObserveFocus(Application.isFocused);
            PollBusy(publish: false);
            _nextBusyPoll = Time.unscaledTime + 0.25f;
            _started = true;
            PublishIf(true);
        }

        private void Update()
        {
            State.Heartbeat();
            if (Time.unscaledTime >= _nextBusyPoll)
            {
                _nextBusyPoll = Time.unscaledTime + 0.25f;
                PollBusy();
            }
        }

        private void OnApplicationFocus(bool focused)
        {
            var changed = State.ObserveFocus(focused);
            PublishIf(_started && changed);
        }

        private void OnApplicationPause(bool paused)
        {
            var changed = State.ObservePause(paused);
            PublishIf(_started && changed);
        }

        private static void PollBusy(bool publish = true)
        {
            try
            {
                var busy = MakerEditor.PlaybackBusy(out var reason);
                var changed = State.ObserveBusy(busy, reason);
                PublishIf(publish && changed);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    MakerEditor.LogPrefix + " activity poll failed: " + ex.GetType().Name + ": " + ex.Message);
                var changed = State.ObserveBusy(null, "activity_unavailable");
                PublishIf(publish && changed);
            }
        }

        private static void PublishIf(bool changed)
        {
            if (!changed)
            {
                return;
            }

            var state = State.Snapshot();
            MakerBackends.Publish("game.focus", new Dictionary<string, object?>
            {
                ["focused"] = state.Focused,
                ["paused"] = state.Paused,
                ["busy"] = state.Busy,
                ["reason"] = state.Reason,
                ["observedAt"] = state.ObservedAt?.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        private static void OnQuitting()
        {
            lock (Gate)
            {
                _quitting = true;
            }
        }
    }
}
