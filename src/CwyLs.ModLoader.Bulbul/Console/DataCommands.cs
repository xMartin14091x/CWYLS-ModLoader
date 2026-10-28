using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Data;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// The <c>data</c> command: the player's own todos, notes, habits and pomodoro settings, read
    /// through the game data bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bus lives in <c>CwyLs.ModLoader.Bulbul.Data</c>, which references this assembly, so it is
    /// reached by name through <see cref="LateBound"/> rather than by a reference that would be a
    /// cycle. The values themselves are ordinary Core view types, so only the entry point is
    /// reflection.
    /// </para>
    /// <para>
    /// Note text is truncated unless <c>--full</c> is passed: notes are the most private thing in
    /// this game's save, and a console left open on a stream should not spell one out by accident.
    /// The data bus stays read-only here - there is no write command.
    /// </para>
    /// </remarks>
    internal static class DataCommands
    {
        /// <summary>Full name of the data bus facade.</summary>
        internal const string GameDataType = "CwyLs.ModLoader.Bulbul.Data.GameData";

        /// <summary>How much of a note is shown without <c>--full</c>.</summary>
        internal const int NoteTextLimit = 80;

        /// <summary>The flag that prints a note in full.</summary>
        internal const string FullFlag = "--full";

        /// <summary>Runs the command.</summary>
        /// <param name="args">Arguments after the command name.</param>
        internal static string Run(IReadOnlyList<string> args)
        {
            var full = false;
            var what = string.Empty;

            foreach (var arg in args)
            {
                if (string.Equals(arg, FullFlag, StringComparison.OrdinalIgnoreCase))
                {
                    full = true;
                }
                else if (what.Length == 0)
                {
                    what = arg;
                }
            }

            if (what.Length == 0)
            {
                what = "snapshot";
            }

            switch (what.ToLowerInvariant())
            {
                case "snapshot": return Snapshot();
                case "todos": return Todos();
                case "notes": return Notes(full);
                case "habits": return Habits();
                case "pomodoro": return Pomodoro();
                default: return "data snapshot|todos|notes|habits|pomodoro [--full]";
            }
        }

        /// <summary>One line for the State tab: whether the data bus can be read at all.</summary>
        internal static string ReadyLine()
        {
            if (!LateBound.TryRead(GameDataType, "IsReady", out var value, out var problem))
            {
                return problem;
            }

            return value is bool ready && ready
                ? "ready (" + Path.Combine(ModLoaderPaths.DataRoot, "snapshot.json") + ")"
                : "not ready yet (the save is not loaded)";
        }

        private static string Snapshot()
        {
            if (!LateBound.TryCall(GameDataType, "Snapshot", Type.EmptyTypes, Array.Empty<object?>(),
                    out var value, out var problem))
            {
                return problem;
            }

            if (value is not GameDataSnapshot snapshot)
            {
                return "the data bus returned no snapshot";
            }

            var items = 0;
            foreach (var list in snapshot.Todos)
            {
                items += list.Items.Count;
            }

            var sb = new StringBuilder("DATA snapshot v")
                .Append(snapshot.SchemaVersion)
                .Append(snapshot.IsReady ? " (ready)" : " (not ready)")
                .Append("  ").Append(snapshot.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

            sb.Append("\n  todos    ").Append(snapshot.Todos.Count).Append(" list(s), ")
                .Append(items).Append(" item(s)");
            sb.Append("\n  notes    ").Append(snapshot.Notes.Count).Append(" page(s)");
            sb.Append("\n  habits   ").Append(snapshot.Habits.Count);
            sb.Append("\n  sections ")
                .Append(Present("player", snapshot.Player)).Append(' ')
                .Append(Present("profile", snapshot.Profile)).Append(' ')
                .Append(Present("pomodoro", snapshot.Pomodoro)).Append(' ')
                .Append(Present("calendar", snapshot.Calendar)).Append(' ')
                .Append(Present("settings", snapshot.Settings)).Append(' ')
                .Append(Present("music", snapshot.Music)).Append(' ')
                .Append(Present("environment", snapshot.Environment)).Append(' ')
                .Append(Present("story", snapshot.Story)).Append(' ')
                .Append(Present("achievements", snapshot.Achievements));

            if (snapshot.Extra.Count > 0)
            {
                sb.Append("\n  extra    ").Append(snapshot.Extra.Count).Append(" provider section(s)");
            }

            sb.Append("\n  file     ").Append(Path.Combine(ModLoaderPaths.DataRoot, "snapshot.json"));
            return sb.ToString();
        }

        private static string Todos()
        {
            if (!TryList<TodoListView>("Todos", out var lists, out var problem))
            {
                return problem;
            }

            if (lists!.Count == 0)
            {
                return "no todo lists (is the save loaded?)";
            }

            var sb = new StringBuilder("TODOS: ").Append(lists.Count).Append(" list(s)");
            foreach (var list in lists)
            {
                sb.Append("\n  [").Append(list.Title).Append("]  ").Append(list.Items.Count).Append(" item(s)");
                foreach (var item in list.Items)
                {
                    sb.Append("\n    ").Append(Mark(item.State)).Append(' ').Append(item.Text);
                    if (item.CompletedAt.HasValue)
                    {
                        sb.Append("  (done ")
                            .Append(item.CompletedAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                            .Append(')');
                    }
                }
            }

            return sb.ToString();
        }

        private static string Notes(bool full)
        {
            if (!TryList<NotePageView>("Notes", out var pages, out var problem))
            {
                return problem;
            }

            if (pages!.Count == 0)
            {
                return "no note pages (is the save loaded?)";
            }

            var sb = new StringBuilder("NOTES: ").Append(pages.Count).Append(" page(s)");
            if (!full)
            {
                sb.Append("  (text cut at ").Append(NoteTextLimit).Append(" chars; --full shows all)");
            }

            foreach (var page in pages)
            {
                sb.Append("\n  #").Append(page.Order).Append("  ").Append(page.Title);

                if (!page.TextAvailable)
                {
                    sb.Append("\n      (note text is switched off in the data bus config)");
                    continue;
                }

                var text = (page.Text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ');
                if (text.Length == 0)
                {
                    sb.Append("\n      (empty)");
                    continue;
                }

                sb.Append("\n      ").Append(
                    full || text.Length <= NoteTextLimit
                        ? text
                        : text.Substring(0, NoteTextLimit) + "...");
            }

            return sb.ToString();
        }

        private static string Habits()
        {
            if (!TryList<HabitView>("Habits", out var habits, out var problem))
            {
                return problem;
            }

            if (habits!.Count == 0)
            {
                return "no habits (is the save loaded?)";
            }

            var sb = new StringBuilder("HABITS: ").Append(habits.Count);
            foreach (var habit in habits)
            {
                var done = 0;
                foreach (var day in habit.RecentDays)
                {
                    if (day.Completed)
                    {
                        done++;
                    }
                }

                sb.Append("\n  ").Append(habit.Title)
                    .Append("  ").Append(done).Append('/').Append(habit.RecentDays.Count)
                    .Append(" recent day(s)")
                    .Append("  days: ").Append(Days(habit.ScheduledDays));
            }

            return sb.ToString();
        }

        private static string Pomodoro()
        {
            if (!LateBound.TryRead(GameDataType, "Pomodoro", out var value, out var problem))
            {
                return problem;
            }

            if (value is not PomodoroView pomodoro)
            {
                return "no pomodoro data (is the save loaded?)";
            }

            var sb = new StringBuilder("POMODORO")
                .Append("\n  mode   ").Append(pomodoro.Mode)
                .Append("\n  timer  ").Append(pomodoro.WorkMinutes).Append(" min work / ")
                .Append(pomodoro.BreakMinutes).Append(" min break x ").Append(pomodoro.LoopCount)
                .Append("\n  worked ").Append(pomodoro.LastUpdatedWorkSeconds.ToString("0", CultureInfo.InvariantCulture))
                .Append(" s");

            if (pomodoro.LastUpdatedWorkAt.HasValue)
            {
                sb.Append(" (last ")
                    .Append(pomodoro.LastUpdatedWorkAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                    .Append(')');
            }

            foreach (var preset in pomodoro.Presets)
            {
                sb.Append("\n  preset ").Append(preset.Name).Append("  ")
                    .Append(preset.WorkMinutes).Append('/').Append(preset.BreakMinutes)
                    .Append(" x ").Append(preset.LoopCount);
            }

            return sb.ToString();
        }

        private static bool TryList<T>(string member, out IReadOnlyList<T>? list, out string problem)
        {
            list = null;

            if (!LateBound.TryRead(GameDataType, member, out var value, out problem))
            {
                return false;
            }

            if (value is IReadOnlyList<T> typed)
            {
                list = typed;
                return true;
            }

            problem = "the data bus returned " + (value?.GetType().Name ?? "nothing")
                + " for " + member + ", not a list of " + typeof(T).Name;
            return false;
        }

        private static string Present(string name, object? section) =>
            section is null ? "-" + name : "+" + name;

        private static string Mark(TodoViewState state) =>
            state == TodoViewState.Complete ? "[x]" : "[ ]";

        private static string Days(IReadOnlyList<DayOfWeek> days)
        {
            if (days.Count == 0)
            {
                return "none";
            }

            var names = new string[days.Count];
            for (var i = 0; i < days.Count; i++)
            {
                names[i] = days[i].ToString().Substring(0, 2);
            }

            return string.Join(",", names);
        }
    }
}
