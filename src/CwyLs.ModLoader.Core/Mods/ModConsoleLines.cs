using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// Console tags and the startup summary line of the code-mod loader. Pure, so the wording is
    /// unit-tested.
    /// </summary>
    /// <remarks>
    /// Two tags, both one word: <see cref="ModsTag"/> for what the
    /// framework says about mods (loaded, skipped, failed, deprecated calls) and
    /// <see cref="ModTag"/> for what a mod says itself through its helper's log. The tags live in
    /// <see cref="ConsoleTags"/> (<see cref="ConsoleTags.Mods"/>, <see cref="ConsoleTags.Mod"/>);
    /// the two constants here are aliases so existing call sites stay valid.
    /// </remarks>
    public static class ModConsoleLines
    {
        /// <summary>The loader's tag: <c>MODS</c> (alias of <see cref="ConsoleTags.Mods"/>).</summary>
        public const string ModsTag = ConsoleTags.Mods;

        /// <summary>A mod's own log lines: <c>MOD</c> (alias of <see cref="ConsoleTags.Mod"/>).</summary>
        public const string ModTag = ConsoleTags.Mod;

        /// <summary>Bus <c>sys</c> of the loader's events.</summary>
        public const string LoaderSystem = "mods";

        /// <summary>Bus <c>sys</c> of a mod's own log lines; the bus event name is the mod id.</summary>
        public const string ModSystem = "mod";

        /// <summary>
        /// The primary column of the one summary line after loading:
        /// <c>1 loaded (cwy.sample.hello@0.1.0); 1 skipped (x.mod: NoEntryType); 0 failed</c>.
        /// Lists are omitted when empty, like the pack summary; the tag column already says MODS,
        /// so the text does not repeat it.
        /// </summary>
        /// <param name="statuses">Every code mod's outcome, in load order.</param>
        public static string Summary(IReadOnlyList<ModStatus>? statuses)
        {
            var loaded = new List<ModStatus>();
            var skipped = new List<ModStatus>();
            var failed = new List<ModStatus>();

            if (statuses != null)
            {
                foreach (var status in statuses)
                {
                    if (status is null)
                    {
                        continue;
                    }

                    switch (status.State)
                    {
                        case ModState.Loaded:
                        case ModState.ShutDown:
                            loaded.Add(status);
                            break;
                        case ModState.Skipped:
                            skipped.Add(status);
                            break;
                        case ModState.Failed:
                            failed.Add(status);
                            break;
                    }
                }
            }

            var sb = new StringBuilder();
            Append(sb, loaded, "loaded", withReason: false);
            sb.Append("; ");
            Append(sb, skipped, "skipped", withReason: true);
            sb.Append("; ");
            Append(sb, failed, "failed", withReason: true);
            return sb.ToString();
        }

        /// <summary>
        /// The primary column of a failure line: <c>cwy.sample.hello failed in Entry: NullReferenceException: …</c>.
        /// </summary>
        /// <param name="modId">The mod.</param>
        /// <param name="stage">Where.</param>
        /// <param name="label">Which handler or command, or null.</param>
        /// <param name="errorType">The exception's type name.</param>
        /// <param name="message">The exception's message.</param>
        /// <param name="suppressed">Repeats swallowed since the last line for this site.</param>
        public static string Failure(string modId, ModStage stage, string? label, string errorType, string? message, int suppressed)
        {
            var sb = new StringBuilder(modId).Append(" failed in ").Append(stage);
            if (!string.IsNullOrWhiteSpace(label))
            {
                sb.Append(" '").Append(label).Append('\'');
            }

            sb.Append(": ").Append(errorType);
            if (!string.IsNullOrWhiteSpace(message))
            {
                sb.Append(": ").Append(OneLine(message!));
            }

            if (suppressed > 0)
            {
                sb.Append(" (+").Append(suppressed.ToString(CultureInfo.InvariantCulture)).Append(" earlier)");
            }

            return sb.ToString();
        }

        /// <summary>Collapses line breaks so an exception message cannot break the one-line grammar.</summary>
        /// <param name="text">Any text.</param>
        public static string OneLine(string text) =>
            (text ?? string.Empty).Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

        private static void Append(StringBuilder sb, List<ModStatus> items, string word, bool withReason)
        {
            sb.Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(word);
            if (items.Count == 0)
            {
                return;
            }

            sb.Append(" (");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var item = items[i];
                if (withReason)
                {
                    sb.Append(item.Id).Append(": ").Append(item.ReasonText);
                }
                else
                {
                    sb.Append(item.Version is null ? item.Id : item.Id + "@" + item.Version);
                }
            }

            sb.Append(')');
        }
    }
}
