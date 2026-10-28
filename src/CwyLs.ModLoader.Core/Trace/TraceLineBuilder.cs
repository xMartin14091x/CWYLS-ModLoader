using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Renders the four JSONL line shapes of the trace file: the start-up header, a call, a state
    /// snapshot and the shutdown footer. Pure string work, so the exact bytes are unit-tested.
    /// </summary>
    public sealed class TraceLineBuilder
    {
        /// <summary>The <c>sys</c> value used for classes in the root namespace.</summary>
        public const string RootSystem = "root";

        /// <summary>The <c>sys</c> value used for lines the trace writes about itself.</summary>
        public const string SelfSystem = "trace";

        /// <summary>The <c>sys</c> value used for Unity user-interface events.</summary>
        public const string UiSystem = "ui";

        /// <summary>
        /// First line of every file: what was patched and how long it took, so a trace that looks
        /// empty can be told apart from a trace whose patching silently failed.
        /// </summary>
        public string Header(DateTimeOffset when, TraceLevel level, int patched, int failed, int classes, double milliseconds) =>
            MiniJson.Object(
                MiniJson.Member("header", MiniJson.Bool(true)),
                MiniJson.Member("t", MiniJson.String(MiniJson.Iso8601(when))),
                MiniJson.Member("level", MiniJson.String(level.ToString())),
                MiniJson.Member("patched", MiniJson.Number(patched)),
                MiniJson.Member("failed", MiniJson.Number(failed)),
                MiniJson.Member("classes", MiniJson.Number(classes)),
                MiniJson.Member("ms", MiniJson.Number(Round(milliseconds, 1))));

        /// <summary>
        /// Last line of every file, written on <c>Application.quitting</c>. Carries the list of
        /// methods that were being aggregated, so a reader knows which counts are merged.
        /// </summary>
        public string Footer(
            DateTimeOffset when,
            int frame,
            double realtime,
            long lines,
            long suppressed,
            IReadOnlyList<string>? pollingMethods = null,
            long busEmitted = 0L,
            long busDropped = 0L,
            IReadOnlyList<string>? unpatchedMethods = null,
            TraceHealth? cost = null) =>
            MiniJson.Object(
                MiniJson.Member("footer", MiniJson.Bool(true)),
                MiniJson.Member("t", MiniJson.String(MiniJson.Iso8601(when))),
                MiniJson.Member("rt", MiniJson.Number(Round(realtime, 3))),
                MiniJson.Member("f", MiniJson.Number(frame)),
                MiniJson.Member("lines", MiniJson.Number(lines)),
                MiniJson.Member("suppressed", MiniJson.Number(suppressed)),
                MiniJson.Member("pollingCount", MiniJson.Number(pollingMethods?.Count ?? 0)),
                MiniJson.Member("polling", TraceJson.Strings(pollingMethods)),
                MiniJson.Member("bus", MiniJson.Object(
                    MiniJson.Member("emitted", MiniJson.Number(busEmitted)),
                    MiniJson.Member("dropped", MiniJson.Number(busDropped)))),
                MiniJson.Member("unpatchedCount", MiniJson.Number(unpatchedMethods?.Count ?? 0)),
                MiniJson.Member("unpatched", TraceJson.Strings(unpatchedMethods)),
                MiniJson.Member("hookNs", cost?.HookNs is null
                    ? "null"
                    : MiniJson.Object(
                        MiniJson.Member("avg", MiniJson.Number(Round(cost.HookNs.Average, 0))),
                        MiniJson.Member("p95", MiniJson.Number(Round(cost.HookNs.P95, 0))),
                        MiniJson.Member("samples", MiniJson.Number(cost.HookNs.Samples)))),
                MiniJson.Member("queue", cost?.Queue is null
                    ? "null"
                    : MiniJson.Object(
                        MiniJson.Member("len", MiniJson.Number(cost.Queue.Length)),
                        MiniJson.Member("dropped", MiniJson.Number(cost.Queue.Dropped)))));

        /// <summary>
        /// One traced call.
        /// </summary>
        /// <param name="when">Wall-clock time of the call.</param>
        /// <param name="frame">Unity frame counter.</param>
        /// <param name="realtime"><c>Time.realtimeSinceStartup</c>.</param>
        /// <param name="gameClock">The game's own clock, or null when it could not be read.</param>
        /// <param name="systemName">Declaring namespace; null or empty becomes <see cref="RootSystem"/>.</param>
        /// <param name="className">Simple class name.</param>
        /// <param name="methodName">Method name.</param>
        /// <param name="args">Already-formatted arguments.</param>
        /// <param name="threadId">Managed thread id the call ran on.</param>
        /// <param name="instance">Already-formatted instance, or null for a static call.</param>
        /// <param name="returnValue">Already-formatted return value; omitted when null.</param>
        /// <param name="note">Free-text note, e.g. the suppression count; omitted when null.</param>
        /// <param name="aggregateCount">
        /// When set, this line stands for a whole window of a polling method: it is written as
        /// <c>"agg":true,"count":N</c> and its arguments are the last ones seen in that window.
        /// </param>
        public string Call(
            DateTimeOffset when,
            int frame,
            double realtime,
            string? gameClock,
            string? systemName,
            string className,
            string methodName,
            IReadOnlyList<string>? args,
            int threadId,
            string? instance,
            string? returnValue = null,
            string? note = null,
            int? aggregateCount = null)
        {
            var members = new List<KeyValuePair<string, string>>(12)
            {
                MiniJson.Member("t", MiniJson.String(MiniJson.Iso8601(when))),
                MiniJson.Member("rt", MiniJson.Number(Round(realtime, 3))),
                MiniJson.Member("gt", MiniJson.String(gameClock)),
                MiniJson.Member("f", MiniJson.Number(frame)),
                MiniJson.Member("sys", MiniJson.String(string.IsNullOrEmpty(systemName) ? RootSystem : systemName)),
                MiniJson.Member("cls", MiniJson.String(className)),
                MiniJson.Member("m", MiniJson.String(methodName)),
                MiniJson.Member("args", TraceJson.Strings(args)),
                MiniJson.Member("th", MiniJson.Number(threadId)),
                MiniJson.Member("inst", MiniJson.String(instance)),
            };

            if (aggregateCount.HasValue)
            {
                members.Add(MiniJson.Member("agg", MiniJson.Bool(true)));
                members.Add(MiniJson.Member("count", MiniJson.Number(aggregateCount.Value)));
            }

            if (returnValue != null)
            {
                members.Add(MiniJson.Member("ret", MiniJson.String(returnValue)));
            }

            if (note != null)
            {
                members.Add(MiniJson.Member("note", MiniJson.String(note)));
            }

            return MiniJson.Object(members);
        }

        /// <summary>
        /// One user-interface event, from the Unity control layer rather than from a game class.
        /// Deliberately a shape of its own: what identifies a UGUI control is its hierarchy path
        /// and its label, not an argument list.
        /// </summary>
        /// <param name="controlType">Control class, e.g. <c>Button</c> or <c>TMP_InputField</c>.</param>
        /// <param name="eventName">What happened, e.g. <c>press</c>, <c>valueChanged</c>.</param>
        /// <param name="path">Hierarchy path from <see cref="UiPath"/>.</param>
        /// <param name="label">The control's visible text, when it has any.</param>
        /// <param name="value">The new value, already formatted; omitted when null.</param>
        /// <param name="threadId">Managed thread id the callback ran on.</param>
        /// <param name="source">
        /// <see cref="InteractionSource.User"/> or <see cref="InteractionSource.Code"/> for a value
        /// change UGUI cannot attribute by itself; omitted when the event is unambiguous.
        /// </param>
        /// <param name="aggregateCount">
        /// When set, this line stands for a window of a code-driven control: written as
        /// <c>"agg":true,"count":N</c>, exactly as for a polling game method.
        /// </param>
        public string Ui(
            DateTimeOffset when,
            int frame,
            double realtime,
            string? gameClock,
            string controlType,
            string eventName,
            string? path,
            string? label,
            string? value,
            int threadId,
            string? source = null,
            int? aggregateCount = null)
        {
            var members = new List<KeyValuePair<string, string>>(13)
            {
                MiniJson.Member("t", MiniJson.String(MiniJson.Iso8601(when))),
                MiniJson.Member("rt", MiniJson.Number(Round(realtime, 3))),
                MiniJson.Member("gt", MiniJson.String(gameClock)),
                MiniJson.Member("f", MiniJson.Number(frame)),
                MiniJson.Member("sys", MiniJson.String(UiSystem)),
                MiniJson.Member("cls", MiniJson.String(controlType)),
                MiniJson.Member("m", MiniJson.String(eventName)),
                MiniJson.Member("path", MiniJson.String(path)),
                MiniJson.Member("label", MiniJson.String(label)),
                MiniJson.Member("value", MiniJson.String(value)),
                MiniJson.Member("th", MiniJson.Number(threadId)),
            };

            if (source != null)
            {
                members.Add(MiniJson.Member("src", MiniJson.String(source)));
            }

            if (aggregateCount.HasValue)
            {
                members.Add(MiniJson.Member("agg", MiniJson.Bool(true)));
                members.Add(MiniJson.Member("count", MiniJson.Number(aggregateCount.Value)));
            }

            return MiniJson.Object(members);
        }

        /// <summary>Members a bus payload may not overwrite, because they identify the line itself.</summary>
        private static readonly HashSet<string> ReservedBusKeys =
            new HashSet<string>(StringComparer.Ordinal) { "t", "rt", "gt", "f", "sys", "cls", "m", "th" };

        /// <summary>
        /// One event published on <c>CwyLs.ModLoader.Core.Bus.TraceBus</c> by another plugin. The
        /// payload is flattened to top level so a reader can filter on its fields directly;
        /// a key that would collide with the line's own members is dropped rather than shadowing it.
        /// </summary>
        public string Bus(
            DateTimeOffset when,
            int frame,
            double realtime,
            string? gameClock,
            string system,
            string name,
            IReadOnlyDictionary<string, object?>? payload,
            int threadId)
        {
            var members = new List<KeyValuePair<string, string>>(8 + (payload?.Count ?? 0))
            {
                MiniJson.Member("t", MiniJson.String(MiniJson.Iso8601(when))),
                MiniJson.Member("rt", MiniJson.Number(Round(realtime, 3))),
                MiniJson.Member("gt", MiniJson.String(gameClock)),
                MiniJson.Member("f", MiniJson.Number(frame)),
                MiniJson.Member("sys", MiniJson.String(string.IsNullOrEmpty(system) ? "bus" : system)),
                MiniJson.Member("cls", MiniJson.String("bus")),
                MiniJson.Member("m", MiniJson.String(name)),
                MiniJson.Member("th", MiniJson.Number(threadId)),
            };

            if (payload != null)
            {
                foreach (var pair in payload)
                {
                    if (string.IsNullOrEmpty(pair.Key) || ReservedBusKeys.Contains(pair.Key))
                    {
                        continue;
                    }

                    members.Add(MiniJson.Member(pair.Key, TraceJson.Value(pair.Value)));
                }
            }

            return MiniJson.Object(members);
        }

        /// <summary>One periodic state snapshot.</summary>
        public string Snapshot(TraceSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var members = new List<KeyValuePair<string, string>>(13)
            {
                MiniJson.Member("snapshot", MiniJson.Bool(true)),
                MiniJson.Member("t", MiniJson.String(MiniJson.Iso8601(snapshot.Timestamp))),
                MiniJson.Member("f", MiniJson.Number(snapshot.Frame)),
                MiniJson.Member("realtime", MiniJson.String(snapshot.Realtime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture))),
                MiniJson.Member("unityTime", MiniJson.Number(Round(snapshot.UnityTime, 3))),
                MiniJson.Member("gameClock", MiniJson.String(snapshot.GameClock)),
                MiniJson.Member("heroineState", MiniJson.String(snapshot.HeroineState)),
                MiniJson.Member("pomodoro", Pomodoro(snapshot.Pomodoro)),
                MiniJson.Member("music", Music(snapshot.Music)),
                MiniJson.Member("environment", MiniJson.String(snapshot.Environment)),
                MiniJson.Member("scene", MiniJson.String(snapshot.Scene)),
                MiniJson.Member("windowsOpen", snapshot.WindowsOpen is null ? "null" : TraceJson.Strings(snapshot.WindowsOpen)),
                MiniJson.Member("focusedControl", MiniJson.String(snapshot.FocusedControl)),
                MiniJson.Member("health", Health(snapshot.Health)),
            };

            return MiniJson.Object(members);
        }

        /// <summary>Renders the health section of a snapshot, or <c>null</c> when there is none.</summary>
        public string Health(TraceHealth? health) =>
            health is null
                ? "null"
                : MiniJson.Object(
                    MiniJson.Member("fps", health.Fps is null
                        ? "null"
                        : MiniJson.Object(
                            MiniJson.Member("avg", MiniJson.Number(Round(health.Fps.Average, 1))),
                            MiniJson.Member("min", MiniJson.Number(Round(health.Fps.Min, 1))),
                            MiniJson.Member("max", MiniJson.Number(Round(health.Fps.Max, 1))))),
                    MiniJson.Member("frameMs", health.FrameMs is null
                        ? "null"
                        : MiniJson.Object(
                            MiniJson.Member("avg", MiniJson.Number(Round(health.FrameMs.Average, 2))),
                            MiniJson.Member("p95", MiniJson.Number(Round(health.FrameMs.P95, 2))),
                            MiniJson.Member("max", MiniJson.Number(Round(health.FrameMs.Max, 2))))),
                    MiniJson.Member("spikes", MiniJson.Number(health.Spikes)),
                    MiniJson.Member("gcMB", Nullable(health.GcMB, 1)),
                    MiniJson.Member("monoUsedMB", Nullable(health.MonoUsedMB, 1)),
                    MiniJson.Member("monoHeapMB", Nullable(health.MonoHeapMB, 1)),
                    MiniJson.Member("allocatedMB", Nullable(health.AllocatedMB, 1)),
                    MiniJson.Member("reservedMB", Nullable(health.ReservedMB, 1)),
                    MiniJson.Member("rssMB", Nullable(health.RssMB, 1)),
                    MiniJson.Member("threads", Nullable(health.Threads)),
                    MiniJson.Member("audio", health.Audio is null
                        ? "null"
                        : MiniJson.Object(
                            MiniJson.Member("sampleRate", Nullable(health.Audio.SampleRate)),
                            MiniJson.Member("dspBufferSize", Nullable(health.Audio.DspBufferSize)),
                            MiniJson.Member("speakerMode", MiniJson.String(health.Audio.SpeakerMode)),
                            MiniJson.Member("listenerVolume", Nullable(health.Audio.ListenerVolume, 3)),
                            MiniJson.Member("playingSources", Nullable(health.Audio.PlayingSources)))),
                    MiniJson.Member("tweens", Nullable(health.Tweens)),
                    MiniJson.Member("targetFps", Nullable(health.TargetFps)),
                    MiniJson.Member("vsync", Nullable(health.Vsync)),
                    MiniJson.Member("screen", health.Screen is null
                        ? "null"
                        : MiniJson.Object(
                            MiniJson.Member("w", Nullable(health.Screen.Width)),
                            MiniJson.Member("h", Nullable(health.Screen.Height)),
                            MiniJson.Member("fullScreen", Nullable(health.Screen.FullScreen)),
                            MiniJson.Member("dpi", Nullable(health.Screen.Dpi, 1)))),
                    MiniJson.Member("focused", Nullable(health.Focused)),
                    MiniJson.Member("fpsCapReason", MiniJson.String(health.FpsCapReason)),
                    MiniJson.Member("timeScale", Nullable(health.TimeScale, 3)),
                    MiniJson.Member("uptimeSec", MiniJson.Number(Round(health.UptimeSec, 1))),
                    MiniJson.Member("steam", Nullable(health.Steam)),
                    MiniJson.Member("warningsInInterval", MiniJson.Number(health.WarningsInInterval)),
                    MiniJson.Member("errorsInInterval", MiniJson.Number(health.ErrorsInInterval)),
                    MiniJson.Member("hookNs", health.HookNs is null
                        ? "null"
                        : MiniJson.Object(
                            MiniJson.Member("avg", MiniJson.Number(Round(health.HookNs.Average, 0))),
                            MiniJson.Member("p95", MiniJson.Number(Round(health.HookNs.P95, 0))),
                            MiniJson.Member("samples", MiniJson.Number(health.HookNs.Samples)))),
                    MiniJson.Member("queue", health.Queue is null
                        ? "null"
                        : MiniJson.Object(
                            MiniJson.Member("len", MiniJson.Number(health.Queue.Length)),
                            MiniJson.Member("dropped", MiniJson.Number(health.Queue.Dropped)))));

        private static string Pomodoro(PomodoroSnapshot? pomodoro) =>
            pomodoro is null
                ? "null"
                : MiniJson.Object(
                    MiniJson.Member("type", MiniJson.String(pomodoro.Type)),
                    MiniJson.Member("running", Nullable(pomodoro.Running)),
                    MiniJson.Member("remainingSec", Nullable(pomodoro.RemainingSeconds, 1)),
                    MiniJson.Member("loop", Nullable(pomodoro.Loop)));

        private static string Music(MusicSnapshot? music) =>
            music is null
                ? "null"
                : MiniJson.Object(
                    MiniJson.Member("current", MiniJson.String(music.Current)),
                    MiniJson.Member("index", Nullable(music.Index)),
                    MiniJson.Member("playlist", MiniJson.String(music.Playlist)),
                    MiniJson.Member("isPlaying", Nullable(music.IsPlaying)),
                    MiniJson.Member("volume", Nullable(music.Volume, 3)));

        /// <summary>
        /// The same snapshot as one compact console line, so the user can watch LogOutput.log with
        /// <c>tail -f</c> instead of reading JSON.
        /// </summary>
        public string SnapshotSummary(TraceSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var sb = new StringBuilder("TRACE snapshot f=")
                .Append(snapshot.Frame.ToString(CultureInfo.InvariantCulture))
                .Append(" rt=").Append(Round(snapshot.UnityTime, 1).ToString(CultureInfo.InvariantCulture))
                .Append(" clock=").Append(snapshot.GameClock ?? "?")
                .Append(" scene=").Append(snapshot.Scene ?? "?")
                .Append(" heroine=").Append(snapshot.HeroineState ?? "?");

            sb.Append(" pomo=");
            if (snapshot.Pomodoro is null)
            {
                sb.Append('?');
            }
            else
            {
                sb.Append(snapshot.Pomodoro.Type ?? "?")
                  .Append('/').Append(snapshot.Pomodoro.Running == true ? "run" : "stop")
                  .Append('/').Append(snapshot.Pomodoro.RemainingSeconds is null
                      ? "?"
                      : Round(snapshot.Pomodoro.RemainingSeconds.Value, 0).ToString(CultureInfo.InvariantCulture) + "s")
                  .Append("/loop").Append(snapshot.Pomodoro.Loop?.ToString(CultureInfo.InvariantCulture) ?? "?");
            }

            sb.Append(" music=");
            if (snapshot.Music is null)
            {
                sb.Append('?');
            }
            else
            {
                sb.Append('"').Append(snapshot.Music.Current ?? "-").Append('"')
                  .Append(' ').Append(snapshot.Music.IsPlaying == true ? "playing" : "paused")
                  .Append(" idx=").Append(snapshot.Music.Index?.ToString(CultureInfo.InvariantCulture) ?? "?")
                  .Append(" vol=").Append(snapshot.Music.Volume is null
                      ? "?"
                      : Round(snapshot.Music.Volume.Value, 2).ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(" env=").Append(snapshot.Environment ?? "?");
            sb.Append(" focus=").Append(string.IsNullOrEmpty(snapshot.FocusedControl) ? "-" : snapshot.FocusedControl);

            if (snapshot.Health != null)
            {
                sb.Append(" fps=").Append(Round(snapshot.Health.Fps?.Average ?? 0d, 1).ToString(CultureInfo.InvariantCulture))
                  .Append(" p95=").Append(Round(snapshot.Health.FrameMs?.P95 ?? 0d, 1).ToString(CultureInfo.InvariantCulture)).Append("ms")
                  .Append(" spikes=").Append(snapshot.Health.Spikes.ToString(CultureInfo.InvariantCulture))
                  .Append(" mono=").Append(Round(snapshot.Health.MonoUsedMB ?? 0d, 0).ToString(CultureInfo.InvariantCulture)).Append("MB")
                  .Append(" rss=").Append(Round(snapshot.Health.RssMB ?? 0d, 0).ToString(CultureInfo.InvariantCulture)).Append("MB")
                  .Append(" err=").Append(snapshot.Health.ErrorsInInterval.ToString(CultureInfo.InvariantCulture));

                if (snapshot.Health.HookNs != null)
                {
                    sb.Append(" hook=").Append(Round(snapshot.Health.HookNs.Average, 0).ToString(CultureInfo.InvariantCulture))
                      .Append('/').Append(Round(snapshot.Health.HookNs.P95, 0).ToString(CultureInfo.InvariantCulture)).Append("ns");
                }

                if (snapshot.Health.Queue != null)
                {
                    sb.Append(" q=").Append(snapshot.Health.Queue.Length.ToString(CultureInfo.InvariantCulture));
                    if (snapshot.Health.Queue.Dropped > 0)
                    {
                        sb.Append('/').Append(snapshot.Health.Queue.Dropped.ToString(CultureInfo.InvariantCulture)).Append("dropped");
                    }
                }
            }
            sb.Append(" windows=").Append(snapshot.WindowsOpen is null || snapshot.WindowsOpen.Count == 0
                ? "-"
                : string.Join(",", snapshot.WindowsOpen));

            return sb.ToString();
        }

        private static string Nullable(bool? value) => value is null ? "null" : MiniJson.Bool(value.Value);

        private static string Nullable(int? value) => value is null ? "null" : MiniJson.Number(value.Value);

        private static string Nullable(double? value, int digits) =>
            value is null ? "null" : MiniJson.Number(Round(value.Value, digits));

        private static double Round(double value, int digits) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 0d : Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }
}
