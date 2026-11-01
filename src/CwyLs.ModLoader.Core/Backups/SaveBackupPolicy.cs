using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>One backup the policy is asked about: its folder name and the local time in that name.</summary>
    /// <param name="Name">The folder name, e.g. <c>save-20261101-140312</c>.</param>
    /// <param name="Time">The local time of the backup.</param>
    public sealed record SaveBackupStamp(string Name, DateTime Time);

    /// <summary>What the policy decided about one backup.</summary>
    /// <param name="Name">The folder name.</param>
    /// <param name="Time">The local time of the backup.</param>
    /// <param name="Keep">True when at least one rule keeps it; false means it is removed.</param>
    /// <param name="Reasons">
    /// Every rule that keeps it, in rule order: <c>last</c>, <c>daily 2026-10-31</c>, <c>weekly 2026-W44</c>,
    /// <c>monthly 2026-10</c>. Empty when it is removed.
    /// </param>
    public sealed record SaveBackupDecision(string Name, DateTime Time, bool Keep, IReadOnlyList<string> Reasons);

    /// <summary>
    /// Which launch save backups to keep, with restic <c>forget</c> semantics. Pure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backups are walked newest first. Each rule has a budget (<see cref="SaveBackupRules"/>) and a
    /// "last period" it kept. A backup is kept by <c>last</c> while that budget lasts. It is kept by
    /// <c>daily</c> / <c>weekly</c> / <c>monthly</c> when its period differs from the last period that rule
    /// kept and the rule still has budget; the rule then spends one and remembers the period. So each rule
    /// keeps the newest backup of each of its N most recent periods <em>that have a backup</em> (a week
    /// without a launch does not use up a daily slot). A backup is kept when any rule keeps it, and removed
    /// otherwise. This is the algorithm of restic's <c>ApplyPolicy</c>
    /// (<c>internal/restic/snapshot_policy.go</c>) for <c>--keep-last/-daily/-weekly/-monthly</c>.
    /// </para>
    /// <para>
    /// Periods are taken from the local time in the folder name. A day is a local calendar day. A week is an
    /// ISO 8601 week (Monday to Sunday; week 1 is the week with the year's first Thursday, so 31 Dec can be in
    /// week 1 of the next year and 1 Jan in week 52 or 53 of the previous one) - restic uses ISO weeks too.
    /// A month is a calendar month.
    /// </para>
    /// <para>
    /// Ties: backups with the same time are ordered by name, descending (ordinal). Names come from the time,
    /// so a tie only happens with duplicate input.
    /// </para>
    /// </remarks>
    public static class SaveBackupPolicy
    {
        /// <summary>The reason given by <c>KeepLast</c>.</summary>
        public const string LastReason = "last";

        /// <summary>Decides, for every backup, keep or remove, and why.</summary>
        /// <param name="backups">The backups. Order does not matter; null is treated as empty.</param>
        /// <param name="rules">The rules; null means <see cref="SaveBackupRules.Default"/>.</param>
        /// <returns>One decision per backup, newest first.</returns>
        public static IReadOnlyList<SaveBackupDecision> Apply(IEnumerable<SaveBackupStamp>? backups, SaveBackupRules? rules)
        {
            rules ??= SaveBackupRules.Default;

            var sorted = new List<SaveBackupStamp>();
            foreach (var backup in backups ?? Array.Empty<SaveBackupStamp>())
            {
                if (backup != null)
                {
                    sorted.Add(backup);
                }
            }

            sorted.Sort(NewestFirst);

            var last = rules.KeepLast;
            var daily = new Bucket(rules.KeepDaily, "daily ", DayKey);
            var weekly = new Bucket(rules.KeepWeekly, "weekly ", WeekKey);
            var monthly = new Bucket(rules.KeepMonthly, "monthly ", MonthKey);

            var decisions = new List<SaveBackupDecision>(sorted.Count);
            foreach (var backup in sorted)
            {
                var reasons = new List<string>(4);
                if (last > 0)
                {
                    last--;
                    reasons.Add(LastReason);
                }

                daily.Offer(backup.Time, reasons);
                weekly.Offer(backup.Time, reasons);
                monthly.Offer(backup.Time, reasons);

                decisions.Add(new SaveBackupDecision(backup.Name, backup.Time, reasons.Count > 0, reasons));
            }

            return decisions;
        }

        /// <summary>The day period: <c>2026-10-31</c>.</summary>
        public static string DayKey(DateTime time) => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>The ISO week period: <c>2026-W44</c> (the ISO week-numbering year, which can differ from the calendar year).</summary>
        public static string WeekKey(DateTime time)
        {
            var (year, week) = IsoWeek(time);
            return year.ToString("0000", CultureInfo.InvariantCulture) + "-W" + week.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>The month period: <c>2026-10</c>.</summary>
        public static string MonthKey(DateTime time) => time.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        /// <summary>
        /// The ISO 8601 week-numbering year and week (1-53) of a date. Written out rather than taken from
        /// <c>System.Globalization.ISOWeek</c>, which the game's Mono profile is not guaranteed to have; the tests
        /// compare it with <c>ISOWeek</c> day by day.
        /// </summary>
        public static (int Year, int Week) IsoWeek(DateTime time)
        {
            var date = time.Date;
            var isoDay = ((int)date.DayOfWeek + 6) % 7 + 1; // Monday 1 ... Sunday 7
            var thursday = date.AddDays(4 - isoDay);        // the Thursday of the same ISO week decides its year
            return (thursday.Year, (thursday.DayOfYear - 1) / 7 + 1);
        }

        private static int NewestFirst(SaveBackupStamp a, SaveBackupStamp b)
        {
            var byTime = b.Time.CompareTo(a.Time);
            return byTime != 0 ? byTime : string.CompareOrdinal(b.Name, a.Name);
        }

        /// <summary>One period rule: its remaining budget and the last period it kept.</summary>
        private sealed class Bucket
        {
            private readonly string _label;
            private readonly Func<DateTime, string> _period;
            private int _left;
            private string? _lastKept;

            internal Bucket(int count, string label, Func<DateTime, string> period)
            {
                _left = count;
                _label = label;
                _period = period;
            }

            internal void Offer(DateTime time, List<string> reasons)
            {
                if (_left <= 0)
                {
                    return;
                }

                var period = _period(time);
                if (string.Equals(period, _lastKept, StringComparison.Ordinal))
                {
                    return;
                }

                _lastKept = period;
                _left--;
                reasons.Add(_label + period);
            }
        }
    }
}
