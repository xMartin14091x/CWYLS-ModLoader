using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Trace;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>
    /// The single decision point for "does this bus event belong on the console, and what does it
    /// look like there". One owner per event kind.
    /// </summary>
    /// <remarks>
    /// Two ways an event gets a console line, in this order:
    /// <list type="number">
    /// <item>The producer named its own <see cref="ConsolePayload.TagKey"/>,
    /// <see cref="ConsolePayload.PrimaryKey"/> and field order. Every feature that can edit its own
    /// emit site does this.</item>
    /// <item>The canonical table below, for the events whose payload is a Core event record's own
    /// <c>Fields()</c> and therefore cannot carry console members (<c>event/*</c>,
    /// <c>satone/speech</c>, <c>satone/state</c>) and for the health/Unity streams.</item>
    /// </list>
    /// Anything else - ticks, <c>phase=</c> diagnostics, polling notices, aggregates - is file-only,
    /// and a producer can always force that with <c>("mirror", false)</c>.
    /// <para>
    /// The code-mod tags (<see cref="ConsoleTags.Mods"/>, <see cref="ConsoleTags.Mod"/>) and the
    /// Phases D-S tags (<see cref="ConsoleTags.Action"/>, <see cref="ConsoleTags.Api"/>,
    /// <see cref="ConsoleTags.Script"/>, <see cref="ConsoleTags.Trigger"/>, <see cref="ConsoleTags.Cond"/>,
    /// <see cref="ConsoleTags.Audio"/>, <see cref="ConsoleTags.UiSkin"/>, <see cref="ConsoleTags.EditData"/>,
    /// <see cref="ConsoleTags.Mic"/>, <see cref="ConsoleTags.Camera"/>) take the first way only: their
    /// producers name the tag, so the canonical table below has no rows for them. A
    /// <c>ConditionsChanged</c> event on the game bus stays file-only (the <c>default</c> branch).
    /// </para>
    /// </remarks>
    public static class ConsoleEventMap
    {
        /// <summary>Payload member a producer sets when a bad reading is the window's fault.</summary>
        public const string UnfocusedField = "unfocused";

        /// <summary>Builds the console record for one bus event, or returns false for a file-only event.</summary>
        /// <param name="evt">The published event.</param>
        /// <param name="record">The record to print, or null.</param>
        public static bool TryMap(BusEvent evt, out ConsoleRecord? record)
        {
            record = null;
            var payload = evt.Payload;

            if (ConsolePayload.IsMuted(payload))
            {
                return false;
            }

            // A reading the producer already blamed on the window losing focus stays in the
            // file. The game lowers its own frame rate in the background, so alt-tabbing produced a
            // stream of health lines that described the operating system, not the game.
            if (payload != null
                && payload.TryGetValue(UnfocusedField, out var unfocused)
                && unfocused is bool flag
                && flag)
            {
                return false;
            }

            var tag = ConsolePayload.Tag(payload);
            if (!string.IsNullOrEmpty(tag))
            {
                record = new ConsoleRecord(
                    evt.Time,
                    tag!,
                    WithoutTagEcho(tag!, ConsolePayload.Primary(payload)),
                    ConsolePayload.Fields(payload),
                    ConsolePayload.Level(payload));
                return true;
            }

            record = Canonical(evt.Time, evt.Sys, evt.Name, payload);
            return record != null;
        }

        /// <summary>
        /// Drops a leading "<c>TAG:</c>" from a primary that repeats its own tag, so a summary
        /// written for a plain log (<c>PACKS: 2 loaded ...</c>) does not print as
        /// <c>PACKS  PACKS: 2 loaded ...</c>. Only the exact tag followed by a colon is dropped, with
        /// the spaces after it; anything else, or a primary that would be left empty, is returned
        /// unchanged.
        /// </summary>
        /// <param name="tag">The record's tag, e.g. <c>PACKS</c>.</param>
        /// <param name="primary">The primary text.</param>
        /// <returns>The primary without the echo, or <paramref name="primary"/> unchanged.</returns>
        public static string? WithoutTagEcho(string tag, string? primary)
        {
            if (string.IsNullOrEmpty(tag)
                || primary is null
                || primary.Length <= tag.Length + 1
                || !primary.StartsWith(tag, StringComparison.Ordinal)
                || primary[tag.Length] != ':')
            {
                return primary;
            }

            var rest = primary.Substring(tag.Length + 1).TrimStart(' ');
            return rest.Length == 0 ? primary : rest;
        }

        /// <summary>
        /// The canonical record for an event whose producer cannot name its own layout, or null
        /// when the event is file-only.
        /// </summary>
        /// <param name="time">Event time.</param>
        /// <param name="sys">Bus <c>sys</c> tag.</param>
        /// <param name="name">Bus event name.</param>
        /// <param name="payload">Event payload.</param>
        public static ConsoleRecord? Canonical(
            DateTimeOffset time,
            string? sys,
            string? name,
            IReadOnlyDictionary<string, object?>? payload)
        {
            switch (sys)
            {
                case SatoneConsoleLines.System:
                    return SatoneConsoleLines.Format(time, sys, name, payload);
                case "event":
                    return GameEvent(time, name, payload);
                case "health":
                    return Health(time, name, payload);
                case "unity":
                    return Unity(time, name, payload);
                case "input":
                    return string.Equals(name, "click", StringComparison.Ordinal)
                        ? TraceConsoleLines.Click(time, ConsolePayload.Text(payload, "x"), ConsolePayload.Text(payload, "y"))
                        : null;
                default:
                    return null;
            }
        }

        private static ConsoleRecord? GameEvent(
            DateTimeOffset time,
            string? name,
            IReadOnlyDictionary<string, object?>? payload)
        {
            switch (name)
            {
                case "SceneChanged":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Scene,
                        (ConsolePayload.Text(payload, "scene") ?? "?")
                        + (Flag(payload, "loaded") ? " loaded" : " unloaded"));

                case "ScenarioStarted":
                    // A Satone.Say plays through the game's own scenario reader, so every Say
                    // raised a SCENARIO start and a SCENARIO end on top of its own SAY line. The
                    // SAY line already says via=scenario; the pair stays in the trace file.
                    if (SayIdGenerator.IsSayGroup(ConsolePayload.Text(payload, "groupId")))
                    {
                        return null;
                    }

                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Scenario,
                        "start " + (ConsolePayload.Text(payload, "groupId") ?? "?"),
                        new[]
                        {
                            new ConsoleField("type", ConsolePayload.Text(payload, "scenarioType")),
                            new ConsoleField("mode", ConsolePayload.Text(payload, "readMode")),
                        });

                case "ScenarioEnded":
                    if (SayIdGenerator.IsSayGroup(ConsolePayload.Text(payload, "groupId")))
                    {
                        return null;
                    }

                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Scenario,
                        "end " + (ConsolePayload.Text(payload, "groupId") ?? "?"));

                case "MusicChanged":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Music,
                        "\"" + (ConsolePayload.Text(payload, "title") ?? "?") + "\"",
                        new[]
                        {
                            new ConsoleField("index", ConsolePayload.Text(payload, "index")),
                            new ConsoleField("shuffle", ConsolePayload.Text(payload, "isShuffle")),
                        });

                case "PomodoroEvent":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Pomodoro,
                        ConsolePayload.Text(payload, "kind") ?? "?",
                        new[] { new ConsoleField("type", ConsolePayload.Text(payload, "pomodoroType")) });

                case "WindowChanged":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Window,
                        (ConsolePayload.Text(payload, "window") ?? "?")
                        + (Flag(payload, "active") ? " on" : " off"));

                case "RoomReady":
                    return new ConsoleRecord(time, ConsoleTags.Ready, "room ready");

                default:
                    // GameClockTick, PomodoroTick, SaveSection, LineEnded and anything a mod adds:
                    // the trace file has them, the console does not.
                    return null;
            }
        }

        private static ConsoleRecord? Health(
            DateTimeOffset time,
            string? name,
            IReadOnlyDictionary<string, object?>? payload)
        {
            switch (name)
            {
                case "spike":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Health,
                        "spike " + (ConsolePayload.Text(payload, "ms") ?? "?") + "ms",
                        new[]
                        {
                            new ConsoleField("frame", ConsolePayload.Text(payload, "frame")),
                            new ConsoleField("cause", ConsolePayload.Text(payload, "cause")),
                        });

                case "lowFps":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Health,
                        "lowFps " + (ConsolePayload.Text(payload, "fpsAvg") ?? "?"),
                        new[]
                        {
                            new ConsoleField("spikes", ConsolePayload.Text(payload, "spikes")),
                            new ConsoleField("cause", ConsolePayload.Text(payload, "cause")),
                        });

                case "lowMemory":
                    return new ConsoleRecord(time, ConsoleTags.Health, "lowMemory");

                case "focus":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Health,
                        Flag(payload, "focused") ? "focus on" : "focus off");

                case "pause":
                    return new ConsoleRecord(
                        time,
                        ConsoleTags.Health,
                        Flag(payload, "paused") ? "paused" : "resumed");

                default:
                    // health/interval: the numbers are in the file and in every snapshot.
                    return null;
            }
        }

        private static ConsoleRecord? Unity(
            DateTimeOffset time,
            string? name,
            IReadOnlyDictionary<string, object?>? payload)
        {
            if (!string.Equals(name, "Exception", StringComparison.Ordinal)
                && !string.Equals(name, "Error", StringComparison.Ordinal)
                && !string.Equals(name, "Assert", StringComparison.Ordinal))
            {
                // Unity warnings are the game's business and there are thousands of them.
                return null;
            }

            return new ConsoleRecord(
                time,
                ConsoleTags.Unity,
                name + " \"" + (ConsolePayload.Text(payload, "message") ?? "?") + "\"",
                new[] { new ConsoleField("count", ConsolePayload.Text(payload, "count")) },
                ConsoleLevel.Error);
        }

        private static bool Flag(IReadOnlyDictionary<string, object?>? payload, string key) =>
            payload != null
            && payload.TryGetValue(key, out var value)
            && value is bool flag
            && flag;
    }
}
