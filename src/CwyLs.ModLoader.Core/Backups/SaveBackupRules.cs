using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>
    /// How many launch save backups to keep: the four <c>[Backups] Keep*</c> settings, already clamped.
    /// Read by <see cref="SaveBackupPolicy"/>. Immutable.
    /// </summary>
    public sealed class SaveBackupRules
    {
        /// <summary>Default of <c>KeepLast</c>.</summary>
        public const int DefaultKeepLast = 5;

        /// <summary>Default of <c>KeepDaily</c>.</summary>
        public const int DefaultKeepDaily = 7;

        /// <summary>Default of <c>KeepWeekly</c>.</summary>
        public const int DefaultKeepWeekly = 3;

        /// <summary>Default of <c>KeepMonthly</c>.</summary>
        public const int DefaultKeepMonthly = 3;

        /// <summary>The most any one rule may ask for. Larger values are clamped to it.</summary>
        public const int MaxKeep = 1000;

        /// <summary>The defaults: last 5, daily 7, weekly 3, monthly 3.</summary>
        public static readonly SaveBackupRules Default =
            Clamp(DefaultKeepLast, DefaultKeepDaily, DefaultKeepWeekly, DefaultKeepMonthly);

        private SaveBackupRules(int keepLast, int keepDaily, int keepWeekly, int keepMonthly)
        {
            KeepLast = keepLast;
            KeepDaily = keepDaily;
            KeepWeekly = keepWeekly;
            KeepMonthly = keepMonthly;
        }

        /// <summary>The newest this-many backups are kept. 1 to <see cref="MaxKeep"/>.</summary>
        public int KeepLast { get; }

        /// <summary>The newest backup of each of the last this-many days that have one. 0 to <see cref="MaxKeep"/>.</summary>
        public int KeepDaily { get; }

        /// <summary>The newest backup of each of the last this-many ISO weeks that have one. 0 to <see cref="MaxKeep"/>.</summary>
        public int KeepWeekly { get; }

        /// <summary>The newest backup of each of the last this-many months that have one. 0 to <see cref="MaxKeep"/>.</summary>
        public int KeepMonthly { get; }

        /// <summary>
        /// The rules from raw settings. <paramref name="keepLast"/> is at least 1 (the newest backup is always
        /// kept while the feature is on); the others are at least 0; every rule is at most <see cref="MaxKeep"/>.
        /// </summary>
        public static SaveBackupRules Clamp(int keepLast, int keepDaily, int keepWeekly, int keepMonthly) =>
            new SaveBackupRules(
                Math.Min(MaxKeep, Math.Max(1, keepLast)),
                Math.Min(MaxKeep, Math.Max(0, keepDaily)),
                Math.Min(MaxKeep, Math.Max(0, keepWeekly)),
                Math.Min(MaxKeep, Math.Max(0, keepMonthly)));

        /// <summary><c>last 5, daily 7, weekly 3, monthly 3</c>.</summary>
        public override string ToString() =>
            "last " + KeepLast.ToString(CultureInfo.InvariantCulture)
            + ", daily " + KeepDaily.ToString(CultureInfo.InvariantCulture)
            + ", weekly " + KeepWeekly.ToString(CultureInfo.InvariantCulture)
            + ", monthly " + KeepMonthly.ToString(CultureInfo.InvariantCulture);
    }
}
