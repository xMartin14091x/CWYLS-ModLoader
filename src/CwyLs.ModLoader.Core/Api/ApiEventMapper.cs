using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// Turns the framework's typed game events into the API's stable event types (the list is the
    /// contract in <c>Core/Api/README.md</c>). Every member is chosen here explicitly: a new field
    /// on a framework event never reaches outside programs by accident. Per-second ticks
    /// (<see cref="PomodoroTick"/>, <see cref="GameClockTick"/>) are not events here; they update
    /// the <c>live</c> part of <c>/v1/state</c> instead.
    /// </summary>
    /// <remarks>
    /// Privacy (user rule): no event carries the text of the player's notes, diary, todos, habits
    /// or calendar. The data-bus change event names only the section and the game's own reason
    /// (a method or signal name); action events carry no arguments and no result message.
    /// </remarks>
    public static class ApiEventMapper
    {
        /// <summary>Satone's action state changed: <c>{prev, next}</c>.</summary>
        public const string SatoneState = "satone.state";

        /// <summary>Satone spoke a line: <c>{voice, text, textJa, language, localizationId, source, scenarioType, scenarioGroupId, isStory, durationSec}</c>.</summary>
        public const string LineSpoken = "line.spoken";

        /// <summary>A line finished: <c>{voice, durationSec}</c>.</summary>
        public const string LineEnded = "line.ended";

        /// <summary>A scenario started: <c>{scenarioType, groupId, readMode}</c>.</summary>
        public const string ScenarioStarted = "scenario.started";

        /// <summary>A scenario ended: <c>{groupId}</c>.</summary>
        public const string ScenarioEnded = "scenario.ended";

        /// <summary>A pomodoro transition: <c>{kind, pomodoroType}</c>.</summary>
        public const string PomodoroChanged = "pomodoro.changed";

        /// <summary>A window view switched: <c>{window, active}</c>.</summary>
        public const string WindowChanged = "window.changed";

        /// <summary>A track started: <c>{title, index, shuffle}</c>.</summary>
        public const string MusicChanged = "music.changed";

        /// <summary>The game saved a section: <c>{section}</c>.</summary>
        public const string Saved = "save";

        /// <summary>A Unity scene loaded or unloaded: <c>{scene, loaded}</c>.</summary>
        public const string SceneChanged = "scene.changed";

        /// <summary>The room is up: <c>{}</c>.</summary>
        public const string RoomReady = "room.ready";

        /// <summary>A framework event this version does not map: <c>{name}</c> only.</summary>
        public const string Other = "game.other";

        /// <summary>The player-data bus saw a change: <c>{section, reason}</c>.</summary>
        public const string DataChanged = "data.changed";

        /// <summary>An action started through the API finished: <c>{handle, action, tier, status}</c>.</summary>
        public const string ActionCompleted = "action.completed";

        /// <summary>Every event type this version can publish, in documentation order.</summary>
        public static IReadOnlyList<string> Types { get; } = new[]
        {
            SatoneState, LineSpoken, LineEnded, ScenarioStarted, ScenarioEnded, PomodoroChanged, WindowChanged,
            MusicChanged, Saved, SceneChanged, RoomReady, DataChanged, ActionCompleted, Other,
        };

        /// <summary>Maps one framework event.</summary>
        /// <param name="name">The channel name (<c>AnyEvent.Name</c>), used only for <see cref="Other"/>.</param>
        /// <param name="args">The event.</param>
        /// <param name="type">The API event type.</param>
        /// <param name="data">The API event data.</param>
        /// <returns>False for events that are not published (ticks); true otherwise.</returns>
        public static bool TryMap(string? name, ICwyEvent? args, out string type, out JsonObject data)
        {
            switch (args)
            {
                case Events.SatoneStateChanged s:
                    type = SatoneState;
                    data = new JsonObject { ["prev"] = s.Previous, ["next"] = s.Next };
                    return true;

                case Events.LineSpoken l:
                    type = LineSpoken;
                    data = new JsonObject
                    {
                        ["voice"] = l.VoiceName,
                        ["text"] = l.Text,
                        ["textJa"] = l.TextJa,
                        ["language"] = l.Language,
                        ["localizationId"] = l.LocalizationId,
                        ["source"] = ApiJson.Camel(l.Source.ToString()),
                        ["scenarioType"] = l.ScenarioType,
                        ["scenarioGroupId"] = l.ScenarioGroupId,
                        ["isStory"] = l.IsStory,
                        ["durationSec"] = l.DurationSec,
                    };
                    return true;

                case Events.LineEnded e:
                    type = LineEnded;
                    data = new JsonObject { ["voice"] = e.VoiceName, ["durationSec"] = e.DurationSec };
                    return true;

                case Events.ScenarioStarted st:
                    type = ScenarioStarted;
                    data = new JsonObject { ["scenarioType"] = st.ScenarioType, ["groupId"] = st.GroupId, ["readMode"] = st.ReadMode };
                    return true;

                case Events.ScenarioEnded en:
                    type = ScenarioEnded;
                    data = new JsonObject { ["groupId"] = en.GroupId };
                    return true;

                case PomodoroEvent p:
                    type = PomodoroChanged;
                    data = new JsonObject { ["kind"] = ApiJson.Camel(p.Kind.ToString()), ["pomodoroType"] = p.PomodoroType };
                    return true;

                case PomodoroTick _:
                case GameClockTick _:
                    type = string.Empty;
                    data = new JsonObject();
                    return false;

                case Events.WindowChanged w:
                    type = WindowChanged;
                    data = new JsonObject { ["window"] = w.Window, ["active"] = w.Active };
                    return true;

                case Events.MusicChanged m:
                    type = MusicChanged;
                    data = new JsonObject { ["title"] = m.Title, ["index"] = m.Index, ["shuffle"] = m.IsShuffle };
                    return true;

                case SaveSection save:
                    type = Saved;
                    data = new JsonObject { ["section"] = save.Section };
                    return true;

                case Events.SceneChanged sc:
                    type = SceneChanged;
                    data = new JsonObject { ["scene"] = sc.Scene, ["loaded"] = sc.Loaded };
                    return true;

                case Events.RoomReady _:
                    type = RoomReady;
                    data = new JsonObject();
                    return true;

                case null:
                    type = string.Empty;
                    data = new JsonObject();
                    return false;

                default:
                    type = Other;
                    data = new JsonObject { ["name"] = string.IsNullOrEmpty(name) ? args.GetType().Name : name };
                    return true;
            }
        }

        /// <summary>The data-bus change event: section (camel case) and the game's reason.</summary>
        /// <param name="change">The change.</param>
        public static JsonObject FromDataChanged(GameDataChanged change)
        {
            if (change is null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            return new JsonObject { ["section"] = ApiJson.Camel(change.Section.ToString()), ["reason"] = change.Reason };
        }

        /// <summary>The action-finished event: no arguments and no message (they may hold the player's text).</summary>
        /// <param name="handle">The handle id.</param>
        /// <param name="action">The action name.</param>
        /// <param name="tier">Its tier.</param>
        /// <param name="status">How it ended.</param>
        public static JsonObject FromActionCompleted(string handle, string action, ActionTier tier, ActionStatus status) =>
            new JsonObject
            {
                ["handle"] = handle,
                ["action"] = action,
                ["tier"] = ApiActionCatalog.TierName(tier),
                ["status"] = ApiActionCatalog.StatusName(status),
            };
    }
}
