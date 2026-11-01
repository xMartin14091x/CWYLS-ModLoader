using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CwyLs.ModLoader.Core.Backups;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Launch save backups: the retention policy (restic forget semantics), rules and ISO weeks.</summary>
    public class SaveBackupPolicyTests
    {
        private static SaveBackupStamp At(string stamp) =>
            new SaveBackupStamp("save-" + stamp, DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        private static SaveBackupRules Rules(int last, int daily, int weekly, int monthly) =>
            SaveBackupRules.Clamp(last, daily, weekly, monthly);

        private static Dictionary<string, string> Reasons(IEnumerable<SaveBackupDecision> decisions) =>
            decisions.ToDictionary(d => d.Name, d => d.Keep ? string.Join(", ", d.Reasons) : "REMOVE");

        /// <summary>The 14 launches of the README's worked example, newest first. 2026-11-01 is a Sunday.</summary>
        internal static readonly string[] WorkedExample =
        {
            "20261101-214000", "20261101-091500", "20261031-200500", "20261030-193000", "20261030-081000",
            "20261028-220000", "20261025-180000", "20261021-180000", "20261014-180000", "20261002-180000",
            "20260920-180000", "20260905-100000", "20260815-100000", "20260704-100000",
        };

        /// <summary>What the README says the default rules decide for <see cref="WorkedExample"/>.</summary>
        internal static readonly Dictionary<string, string> WorkedExampleExpected = new Dictionary<string, string>
        {
            ["save-20261101-214000"] = "last, daily 2026-11-01, weekly 2026-W44, monthly 2026-11",
            ["save-20261101-091500"] = "last",
            ["save-20261031-200500"] = "last, daily 2026-10-31, monthly 2026-10",
            ["save-20261030-193000"] = "last, daily 2026-10-30",
            ["save-20261030-081000"] = "last",
            ["save-20261028-220000"] = "daily 2026-10-28",
            ["save-20261025-180000"] = "daily 2026-10-25, weekly 2026-W43",
            ["save-20261021-180000"] = "daily 2026-10-21",
            ["save-20261014-180000"] = "daily 2026-10-14, weekly 2026-W42",
            ["save-20261002-180000"] = "REMOVE",
            ["save-20260920-180000"] = "monthly 2026-09",
            ["save-20260905-100000"] = "REMOVE",
            ["save-20260815-100000"] = "REMOVE",
            ["save-20260704-100000"] = "REMOVE",
        };

        [Fact]
        public void The_README_worked_example_holds_exactly()
        {
            var decisions = SaveBackupPolicy.Apply(WorkedExample.Select(At), SaveBackupRules.Default);

            Assert.Equal(WorkedExampleExpected, Reasons(decisions));
            Assert.Equal(10, decisions.Count(d => d.Keep));
            Assert.Equal(WorkedExample.Select(s => "save-" + s), decisions.Select(d => d.Name)); // newest first
        }

        [Fact]
        public void Input_order_does_not_matter()
        {
            var expected = Reasons(SaveBackupPolicy.Apply(WorkedExample.Select(At), SaveBackupRules.Default));
            var random = new Random(7);
            for (var i = 0; i < 20; i++)
            {
                var shuffled = WorkedExample.OrderBy(_ => random.Next()).Select(At).ToList();
                var decisions = SaveBackupPolicy.Apply(shuffled, SaveBackupRules.Default);
                Assert.Equal(expected, Reasons(decisions));
                Assert.Equal(WorkedExample.Select(s => "save-" + s), decisions.Select(d => d.Name));
            }
        }

        [Fact]
        public void Nothing_in_nothing_out()
        {
            Assert.Empty(SaveBackupPolicy.Apply(null, null));
            Assert.Empty(SaveBackupPolicy.Apply(Array.Empty<SaveBackupStamp>(), SaveBackupRules.Default));
        }

        [Fact]
        public void A_single_backup_is_kept_by_every_rule()
        {
            var decision = Assert.Single(SaveBackupPolicy.Apply(new[] { At("20261231-235959") }, SaveBackupRules.Default));
            Assert.True(decision.Keep);
            Assert.Equal(new[] { "last", "daily 2026-12-31", "weekly 2026-W53", "monthly 2026-12" }, decision.Reasons);
        }

        [Fact]
        public void Keep_last_keeps_exactly_the_newest_n()
        {
            var stamps = Enumerable.Range(0, 10).Select(i => At("2026110" + (i % 9 + 1) + "-1" + i + "0000")).ToList();
            var decisions = SaveBackupPolicy.Apply(stamps, Rules(3, 0, 0, 0));

            Assert.Equal(3, decisions.Count(d => d.Keep));
            Assert.All(decisions.Take(3), d => Assert.Equal(new[] { "last" }, d.Reasons));
            Assert.All(decisions.Skip(3), d => Assert.False(d.Keep));
        }

        [Fact]
        public void Daily_keeps_the_newest_of_each_day_and_counts_only_days_that_have_backups()
        {
            var stamps = new[] { "20261101-200000", "20261101-080000", "20261020-120000", "20261020-110000", "20260601-000000", "20250101-000000" }
                .Select(At);
            var decisions = Reasons(SaveBackupPolicy.Apply(stamps, Rules(1, 3, 0, 0)));

            Assert.Equal("last, daily 2026-11-01", decisions["save-20261101-200000"]);
            Assert.Equal("REMOVE", decisions["save-20261101-080000"]);
            Assert.Equal("daily 2026-10-20", decisions["save-20261020-120000"]);
            Assert.Equal("REMOVE", decisions["save-20261020-110000"]);
            Assert.Equal("daily 2026-06-01", decisions["save-20260601-000000"]); // months of no launches do not use up a slot
            Assert.Equal("REMOVE", decisions["save-20250101-000000"]);
        }

        [Fact]
        public void A_day_is_the_local_calendar_day_midnight_splits_it()
        {
            var decisions = Reasons(SaveBackupPolicy.Apply(
                new[] { At("20261101-000000"), At("20261031-235959") }, Rules(1, 2, 0, 0)));

            Assert.Equal("last, daily 2026-11-01", decisions["save-20261101-000000"]);
            Assert.Equal("daily 2026-10-31", decisions["save-20261031-235959"]);
        }

        [Fact]
        public void Weeks_are_ISO_weeks_monday_to_sunday()
        {
            // Mon 2026-10-26 .. Sun 2026-11-01 is 2026-W44; Sun 2026-10-25 belongs to W43.
            var decisions = Reasons(SaveBackupPolicy.Apply(
                new[] { At("20261101-120000"), At("20261026-000000"), At("20261025-235959") }, Rules(1, 0, 3, 0)));

            Assert.Equal("last, weekly 2026-W44", decisions["save-20261101-120000"]);
            Assert.Equal("REMOVE", decisions["save-20261026-000000"]);
            Assert.Equal("weekly 2026-W43", decisions["save-20261025-235959"]);
        }

        [Fact]
        public void Weeks_across_new_year_use_the_ISO_week_year()
        {
            // Thu 2026-12-31 and Fri 2027-01-01 are both 2026-W53; Mon 2027-01-04 starts 2027-W01.
            // Mon 2024-12-30 is already 2025-W01.
            var decisions = Reasons(SaveBackupPolicy.Apply(
                new[] { At("20270104-100000"), At("20270101-100000"), At("20261231-100000"), At("20241230-100000") },
                Rules(1, 0, 10, 0)));

            Assert.Equal("last, weekly 2027-W01", decisions["save-20270104-100000"]);
            Assert.Equal("weekly 2026-W53", decisions["save-20270101-100000"]);
            Assert.Equal("REMOVE", decisions["save-20261231-100000"]);
            Assert.Equal("weekly 2025-W01", decisions["save-20241230-100000"]);
        }

        [Fact]
        public void Monthly_keeps_the_newest_of_each_month()
        {
            var decisions = Reasons(SaveBackupPolicy.Apply(
                new[] { "20261130-000000", "20261101-000000", "20261031-235959", "20261001-000000", "20260131-000000", "20251231-000000" }.Select(At),
                Rules(1, 0, 0, 3)));

            Assert.Equal("last, monthly 2026-11", decisions["save-20261130-000000"]);
            Assert.Equal("REMOVE", decisions["save-20261101-000000"]);
            Assert.Equal("monthly 2026-10", decisions["save-20261031-235959"]);
            Assert.Equal("REMOVE", decisions["save-20261001-000000"]);
            Assert.Equal("monthly 2026-01", decisions["save-20260131-000000"]);
            Assert.Equal("REMOVE", decisions["save-20251231-000000"]);
        }

        [Fact]
        public void A_backup_kept_by_several_rules_lists_them_all_in_rule_order()
        {
            var decision = SaveBackupPolicy.Apply(new[] { At("20261101-140312"), At("20261001-140312") }, SaveBackupRules.Default)[1];
            Assert.Equal(new[] { "last", "daily 2026-10-01", "weekly 2026-W40", "monthly 2026-10" }, decision.Reasons);
        }

        [Fact]
        public void A_period_rule_skips_older_backups_of_a_period_it_already_kept()
        {
            // restic: the bucket remembers the period it last kept, so the second backup of a day is not "daily".
            var decisions = SaveBackupPolicy.Apply(
                new[] { At("20261101-230000"), At("20261101-220000"), At("20261101-210000"), At("20261031-100000") },
                Rules(1, 2, 0, 0));

            Assert.Equal(new[] { true, false, false, true }, decisions.Select(d => d.Keep));
            Assert.Equal(new[] { "daily 2026-10-31" }, decisions[3].Reasons);
        }

        [Fact]
        public void Same_time_backups_are_ordered_by_name_descending()
        {
            var a = new SaveBackupStamp("save-a", new DateTime(2026, 11, 1, 12, 0, 0));
            var b = new SaveBackupStamp("save-b", new DateTime(2026, 11, 1, 12, 0, 0));
            var decisions = SaveBackupPolicy.Apply(new[] { a, b }, Rules(1, 0, 0, 0));

            Assert.Equal(new[] { "save-b", "save-a" }, decisions.Select(d => d.Name));
            Assert.Equal(new[] { true, false }, decisions.Select(d => d.Keep));
        }

        [Fact]
        public void The_newest_backup_is_always_kept_because_keep_last_is_at_least_one()
        {
            var decisions = SaveBackupPolicy.Apply(WorkedExample.Select(At), Rules(0, 0, 0, 0));
            Assert.True(decisions[0].Keep);
            Assert.Equal(new[] { "last" }, decisions[0].Reasons);
            Assert.All(decisions.Skip(1), d => Assert.False(d.Keep));
        }

        [Theory]
        [InlineData(5, 7, 3, 3, 5, 7, 3, 3)]
        [InlineData(0, 0, 0, 0, 1, 0, 0, 0)]
        [InlineData(-4, -1, -100, int.MinValue, 1, 0, 0, 0)]
        [InlineData(1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000)]
        [InlineData(1001, int.MaxValue, 5000, 1001, 1000, 1000, 1000, 1000)]
        public void Rules_are_clamped(int last, int daily, int weekly, int monthly, int eLast, int eDaily, int eWeekly, int eMonthly)
        {
            var rules = SaveBackupRules.Clamp(last, daily, weekly, monthly);
            Assert.Equal((eLast, eDaily, eWeekly, eMonthly), (rules.KeepLast, rules.KeepDaily, rules.KeepWeekly, rules.KeepMonthly));
        }

        [Fact]
        public void The_defaults_are_last_5_daily_7_weekly_3_monthly_3()
        {
            Assert.Equal("last 5, daily 7, weekly 3, monthly 3", SaveBackupRules.Default.ToString());
        }

        [Fact]
        public void Iso_week_matches_dotnet_ISOWeek_for_every_day_from_1990_to_2060()
        {
            for (var day = new DateTime(1990, 1, 1); day <= new DateTime(2060, 12, 31); day = day.AddDays(1))
            {
                var (year, week) = SaveBackupPolicy.IsoWeek(day.AddHours(23).AddMinutes(59));
                Assert.True(
                    year == ISOWeek.GetYear(day) && week == ISOWeek.GetWeekOfYear(day),
                    day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": got " + year + "-W" + week);
            }
        }

        [Fact]
        public void Period_keys_are_formatted_as_documented()
        {
            var t = new DateTime(2027, 1, 1, 9, 5, 3);
            Assert.Equal("2027-01-01", SaveBackupPolicy.DayKey(t));
            Assert.Equal("2026-W53", SaveBackupPolicy.WeekKey(t));
            Assert.Equal("2027-01", SaveBackupPolicy.MonthKey(t));
            Assert.Equal("2026-W02", SaveBackupPolicy.WeekKey(new DateTime(2026, 1, 5)));
        }

        /// <summary>
        /// The streaming algorithm against the set definition of each rule, on random histories: "last N" is the
        /// N newest; a period rule keeps, for each of its N most recent periods that have a backup, the newest
        /// backup of that period; a backup is kept when any rule keeps it.
        /// </summary>
        [Fact]
        public void Random_histories_match_the_set_definition_of_every_rule()
        {
            var random = new Random(20261101);
            for (var round = 0; round < 600; round++)
            {
                var start = new DateTime(2024, 11, 15).AddMinutes(random.Next(0, 60 * 24 * 400));
                var count = random.Next(0, 70);
                var spread = random.Next(1, 4) switch { 1 => 60 * 24 * 3, 2 => 60 * 24 * 60, _ => 60 * 24 * 500 };
                var stamps = new List<SaveBackupStamp>();
                var names = new HashSet<string>();
                for (var i = 0; i < count; i++)
                {
                    var time = start.AddMinutes(random.Next(0, spread)).AddSeconds(random.Next(0, 60));
                    var name = SaveBackupNames.FolderName(time);
                    if (names.Add(name))
                    {
                        stamps.Add(new SaveBackupStamp(name, new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second)));
                    }
                }

                var rules = Rules(random.Next(0, 8), random.Next(0, 10), random.Next(0, 6), random.Next(0, 6));
                var decisions = SaveBackupPolicy.Apply(stamps, rules);
                var newestFirst = stamps.OrderByDescending(s => s.Time).ToList();

                Assert.Equal(newestFirst.Select(s => s.Name), decisions.Select(d => d.Name));

                var expected = newestFirst.ToDictionary(s => s.Name, _ => new List<string>());
                foreach (var s in newestFirst.Take(rules.KeepLast))
                {
                    expected[s.Name].Add("last");
                }

                AddPeriod(expected, newestFirst, rules.KeepDaily, "daily ", SaveBackupPolicy.DayKey);
                AddPeriod(expected, newestFirst, rules.KeepWeekly, "weekly ", SaveBackupPolicy.WeekKey);
                AddPeriod(expected, newestFirst, rules.KeepMonthly, "monthly ", SaveBackupPolicy.MonthKey);

                foreach (var decision in decisions)
                {
                    Assert.Equal(expected[decision.Name], decision.Reasons);
                    Assert.Equal(expected[decision.Name].Count > 0, decision.Keep);
                }

                if (stamps.Count > 0)
                {
                    Assert.True(decisions[0].Keep);
                }
            }
        }

        private static void AddPeriod(
            Dictionary<string, List<string>> expected,
            List<SaveBackupStamp> newestFirst,
            int n,
            string label,
            Func<DateTime, string> period)
        {
            var periods = newestFirst.Select(s => period(s.Time)).Distinct().Take(n).ToList();
            foreach (var p in periods)
            {
                var newest = newestFirst.First(s => period(s.Time) == p);
                expected[newest.Name].Add(label + p);
            }
        }
    }
}
