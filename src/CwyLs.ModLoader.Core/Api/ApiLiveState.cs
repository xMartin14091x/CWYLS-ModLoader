using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// The <c>live</c> part of <c>/v1/state</c>: what is happening right now, taken from the
    /// latest game event of each kind (Satone's state, the line she is speaking, the scenario, the
    /// pomodoro clock, the track, the window views, the scene). The save-backed sections come from
    /// the data-bus snapshot instead (<see cref="ApiStateProjection"/>). Thread-safe.
    /// </summary>
    public sealed class ApiLiveState
    {
        private readonly object _gate = new object();
        private readonly SortedSet<string> _windows = new SortedSet<string>(StringComparer.Ordinal);

        private bool _roomReady;
        private string? _scene;
        private string? _satoneState;
        private DateTimeOffset? _satoneSince;
        private bool _speaking;
        private string? _voice;
        private string? _localizationId;
        private DateTimeOffset? _lineAt;
        private bool _scenarioRunning;
        private string? _scenarioGroup;
        private string? _scenarioType;
        private string? _pomodoroType;
        private string? _pomodoroLast;
        private bool _pomodoroPaused;
        private double? _elapsed;
        private double? _remaining;
        private DateTimeOffset? _tickAt;
        private string? _musicTitle;
        private int? _musicIndex;
        private bool? _shuffle;
        private DateTime? _clock;

        /// <summary>Sets Satone's state before the first event arrives (read once when the API starts).</summary>
        /// <param name="state">The state name, or null when unknown.</param>
        /// <param name="now">The time.</param>
        public void Seed(string? state, DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_satoneState is null && !string.IsNullOrEmpty(state))
                {
                    _satoneState = state;
                    _satoneSince = now;
                }
            }
        }

        /// <summary>Folds one framework event in.</summary>
        /// <param name="e">The event.</param>
        /// <param name="now">When it arrived.</param>
        /// <returns>True when something in the live state changed.</returns>
        public bool Observe(ICwyEvent? e, DateTimeOffset now)
        {
            lock (_gate)
            {
                switch (e)
                {
                    case RoomReady _:
                        _roomReady = true;
                        return true;
                    case SceneChanged s:
                        if (s.Loaded)
                        {
                            _scene = s.Scene;
                        }

                        return true;
                    case SatoneStateChanged st:
                        _satoneState = st.Next;
                        _satoneSince = now;
                        return true;
                    case LineSpoken l:
                        _speaking = true;
                        _voice = l.VoiceName;
                        _localizationId = l.LocalizationId;
                        _lineAt = now;
                        return true;
                    case LineEnded _:
                        _speaking = false;
                        return true;
                    case ScenarioStarted sc:
                        _scenarioRunning = true;
                        _scenarioGroup = sc.GroupId;
                        _scenarioType = sc.ScenarioType;
                        return true;
                    case ScenarioEnded _:
                        _scenarioRunning = false;
                        return true;
                    case PomodoroEvent p:
                        _pomodoroType = p.PomodoroType;
                        _pomodoroLast = ApiJson.Camel(p.Kind.ToString());
                        if (p.Kind == PomodoroEventKind.Paused)
                        {
                            _pomodoroPaused = true;
                        }
                        else if (p.Kind == PomodoroEventKind.Unpaused || p.Kind == PomodoroEventKind.Started
                                 || p.Kind == PomodoroEventKind.WorkStarted || p.Kind == PomodoroEventKind.BreakStarted)
                        {
                            _pomodoroPaused = false;
                        }

                        return true;
                    case PomodoroTick t:
                        _pomodoroType = t.PomodoroType;
                        _elapsed = t.ElapsedSec;
                        _remaining = t.RemainingSec;
                        _tickAt = now;
                        return true;
                    case WindowChanged w:
                        var changed = w.Active ? _windows.Add(w.Window) : _windows.Remove(w.Window);
                        return changed;
                    case MusicChanged m:
                        _musicTitle = m.Title;
                        _musicIndex = m.Index;
                        _shuffle = m.IsShuffle;
                        return true;
                    case GameClockTick c:
                        _clock = c.Time;
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>The <c>live</c> object.</summary>
        /// <param name="language">The display language code, or null when unknown.</param>
        public JsonObject ToJson(string? language)
        {
            lock (_gate)
            {
                var windows = new List<object?>(_windows.Count);
                foreach (var w in _windows)
                {
                    windows.Add(w);
                }

                return new JsonObject
                {
                    ["roomReady"] = _roomReady,
                    ["scene"] = _scene,
                    ["language"] = string.IsNullOrEmpty(language) ? null : language,
                    ["clock"] = ApiJson.Time(_clock),
                    ["satone"] = new JsonObject
                    {
                        ["state"] = _satoneState,
                        ["since"] = _satoneSince is null ? null : ApiJson.Time(_satoneSince.Value),
                        ["speaking"] = _speaking,
                        ["voice"] = _voice,
                        ["localizationId"] = _localizationId,
                        ["lineAt"] = _lineAt is null ? null : ApiJson.Time(_lineAt.Value),
                    },
                    ["scenario"] = new JsonObject
                    {
                        ["running"] = _scenarioRunning,
                        ["groupId"] = _scenarioGroup,
                        ["scenarioType"] = _scenarioType,
                    },
                    ["pomodoro"] = new JsonObject
                    {
                        ["type"] = _pomodoroType,
                        ["lastEvent"] = _pomodoroLast,
                        ["paused"] = _pomodoroPaused,
                        ["elapsedSec"] = _elapsed,
                        ["remainingSec"] = _remaining,
                        ["tickAt"] = _tickAt is null ? null : ApiJson.Time(_tickAt.Value),
                    },
                    ["music"] = new JsonObject
                    {
                        ["title"] = _musicTitle,
                        ["index"] = _musicIndex,
                        ["shuffle"] = _shuffle,
                    },
                    ["windows"] = windows,
                };
            }
        }
    }
}
