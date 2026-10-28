using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// E-ACT-CORE: the ACTION audit line - redaction of the player's text, which records reach the
    /// console, and the line itself through the real bus, routing table and formatter.
    /// </summary>
    [Collection("TraceBus")]
    public class ActionsAuditTests
    {
        private static readonly ActionDescriptor AddNote = new ActionDescriptor(
            "note.add", ActionTier.Write, "Adds a note page.",
            new[]
            {
                new ActionParameter("title", ActionValueKind.String, true, "The page title."),
                new ActionParameter("pinned", ActionValueKind.Boolean, false, "Pin it."),
                new ActionParameter("folder", ActionValueKind.Integer, false, "Folder id."),
            });

        private static readonly ActionDescriptor Play = new ActionDescriptor(
            "music.play", ActionTier.Act, "Plays a track.",
            new[]
            {
                new ActionParameter("title", ActionValueKind.String, true, "The title."),
                new ActionParameter("comment", ActionValueKind.String, false, "Said aloud.", isPrivate: true),
            });

        [Fact]
        public void Write_tier_text_private_values_and_unknown_names_show_only_their_length()
        {
            Assert.Equal(
                "title=<private:14> pinned=true folder=4",
                ActionAudit.RedactArgs(AddNote, ActionArgs.Of(("title", "Dear diary...."), ("pinned", true), ("folder", 4))));
            Assert.Equal(
                "title=Rain comment=<private:5> extra=<private:3>",
                ActionAudit.RedactArgs(Play, ActionArgs.Of(("title", "Rain"), ("comment", "hello"), ("extra", "abc"))));
            Assert.Equal("text=<private:6> n=<private:2>", ActionAudit.RedactArgs(null, ActionArgs.Of(("text", "secret"), ("n", 42))));
            Assert.Equal("title=null", ActionAudit.RedactArgs(Play, ActionArgs.Of(("title", null))));
            Assert.Equal(string.Empty, ActionAudit.RedactArgs(Play, null));
        }

        [Fact]
        public void Long_values_and_details_are_cut()
        {
            var longTitle = new string('a', 60);
            Assert.Equal("title=" + new string('a', ActionAudit.MaxValueLength) + "…", ActionAudit.RedactArgs(Play, ActionArgs.Of(("title", longTitle))));
            Assert.Equal(ActionAudit.MaxDetailLength + 1, ActionAudit.RedactDetail(ActionTier.Act, new string('x', 300), true).Length);
        }

        [Fact]
        public void A_write_handlers_message_is_hidden_but_the_runners_own_reasons_are_kept()
        {
            Assert.Equal("<private:20>", ActionAudit.RedactDetail(ActionTier.Write, "added 'Dear diary..'", fromHandler: true));
            Assert.Equal("writes are off", ActionAudit.RedactDetail(ActionTier.Write, "writes are off", fromHandler: false));
            Assert.Equal("playing Rain", ActionAudit.RedactDetail(ActionTier.Act, "playing Rain", fromHandler: true));
            Assert.Equal(string.Empty, ActionAudit.RedactDetail(null, null, false));
        }

        [Fact]
        public void Only_a_read_that_worked_stays_off_the_console()
        {
            Assert.False(ActionAudit.ShowsOnConsole(Record(ActionTier.Read, ActionStatus.Ok)));
            Assert.True(ActionAudit.ShowsOnConsole(Record(ActionTier.Read, ActionStatus.Denied)));
            Assert.True(ActionAudit.ShowsOnConsole(Record(ActionTier.Read, ActionStatus.Ok, late: true)));
            Assert.True(ActionAudit.ShowsOnConsole(Record(ActionTier.Act, ActionStatus.Ok)));
            Assert.True(ActionAudit.ShowsOnConsole(Record(null, ActionStatus.NotFound)));

            Assert.Equal(ConsoleLevel.Info, ActionAudit.Level(Record(ActionTier.Act, ActionStatus.Denied)));
            Assert.Equal(ConsoleLevel.Warn, ActionAudit.Level(Record(ActionTier.Act, ActionStatus.Failed)));
            Assert.Equal(ConsoleLevel.Warn, ActionAudit.Level(Record(ActionTier.Act, ActionStatus.TimedOut)));
            Assert.Equal(ConsoleLevel.Warn, ActionAudit.Level(Record(ActionTier.Act, ActionStatus.Ok, late: true)));
        }

        [Fact]
        public void The_console_line_names_action_outcome_path_caller_and_arguments()
        {
            var line = Line(Record(ActionTier.Act, ActionStatus.Ok, ActionPath.Semantic, args: "title=Rain", detail: "playing Rain", ms: 12.34));

            Assert.Equal("ACTION  music.play ok  via=semantic by=console:console args=title=Rain ms=12.3 detail=\"playing Rain\"", line);
        }

        [Fact]
        public void A_refusal_is_a_console_line_and_a_late_failure_a_warning()
        {
            var denied = Line(Record(ActionTier.Write, ActionStatus.Denied, ActionPath.None, ActionCaller.Api, "cwy-mcp",
                args: "title=<private:8>", detail: "writes are off ([Data] AllowWrites=false)"));
            Assert.Equal(
                "ACTION  music.play denied  via=- by=api:cwy-mcp args=title=<private:8> ms=0 detail=\"writes are off ([Data] AllowWrites=false)\"",
                denied);

            var late = Map(Record(ActionTier.Act, ActionStatus.Failed, late: true));
            Assert.Equal(ConsoleLevel.Warn, late!.Level);
            Assert.Equal("music.play failed (late)", late.Primary);
        }

        [Fact]
        public void A_read_that_worked_is_a_trace_record_only()
        {
            Assert.Null(Map(Record(ActionTier.Read, ActionStatus.Ok, ActionPath.Semantic)));
        }

        [Fact]
        public void Publishing_nothing_is_harmless()
        {
            TraceBus.Reset();
            var count = 0;
            using (TraceBus.Subscribe(_ => count++))
            {
                ActionAudit.Publish(null);
            }

            TraceBus.Reset();
            Assert.Equal(0, count);
        }

        private static ActionAuditRecord Record(
            ActionTier? tier,
            ActionStatus status,
            ActionPath path = ActionPath.None,
            ActionCaller caller = ActionCaller.Console,
            string callerId = "console",
            string args = "",
            string detail = "",
            double ms = 0,
            bool late = false) =>
            new ActionAuditRecord(
                "music.play", tier, new ActionRequest("music.play", null, caller, callerId),
                status, path, ms, confirmed: false, late: late, args: args, detail: detail);

        private static ConsoleRecord? Map(ActionAuditRecord record)
        {
            ConsoleRecord? seen = null;
            var count = 0;
            TraceBus.Reset();
            using (TraceBus.Subscribe(evt =>
                   {
                       count++;
                       Assert.Equal(ActionAudit.TraceSys, evt.Sys);
                       Assert.Equal(ActionAudit.RunEvent, evt.Name);
                       if (ConsoleEventMap.TryMap(evt, out var mapped))
                       {
                           seen = mapped;
                       }
                   }))
            {
                ActionAudit.Publish(record);
            }

            TraceBus.Reset();
            Assert.Equal(1, count);
            return seen;
        }

        private static string Line(ActionAuditRecord record)
        {
            var mapped = Map(record);
            Assert.NotNull(mapped);
            var text = ConsoleRecordFormatter.Format(mapped);
            return text.Substring(text.IndexOf("ACTION", StringComparison.Ordinal));
        }
    }
}
