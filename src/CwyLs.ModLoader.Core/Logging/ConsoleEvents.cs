using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Bus;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>
    /// How a producer publishes an event that has a console line: once, on
    /// <see cref="TraceBus"/>, naming its own tag, primary column and field order.
    /// </summary>
    /// <remarks>
    /// Before these helpers, every feature did the same thing twice - <c>Log.LogInfo(pretty text)</c> for the
    /// console and <c>TraceBus.EmitFields(the same event)</c> for the file - which is why one
    /// spoken sentence cost 23 console lines. These helpers are the one way to publish now: the
    /// trace file gets the full payload, and the console gets exactly the columns named here.
    /// </remarks>
    public static class ConsoleEvents
    {
        /// <summary>Bus <c>sys</c> tag of a feature's startup summary.</summary>
        public const string SetupSystem = "setup";

        /// <summary>
        /// Publishes an event that belongs on the console.
        /// </summary>
        /// <param name="sys">Bus subsystem, e.g. <c>satone</c>, <c>textures</c>.</param>
        /// <param name="name">Bus event name, e.g. <c>reload</c>.</param>
        /// <param name="tag">The one-word console tag; see <see cref="ConsoleTags"/>.</param>
        /// <param name="primary">The primary column. Null prints as <c>-</c>.</param>
        /// <param name="consoleFields">
        /// Ordered payload members to show, e.g. <c>"trigger,applied,failed"</c>. An item may
        /// rename its column: <c>"dur:durationSec"</c>. Null or empty shows no fields.
        /// </param>
        /// <param name="fields">The full payload; everything here reaches the trace file.</param>
        public static void Emit(
            string sys,
            string name,
            string tag,
            string? primary,
            string? consoleFields,
            params (string Key, object? Value)[] fields)
        {
            var payload = new Dictionary<string, object?>(fields.Length + 3)
            {
                [ConsolePayload.TagKey] = tag,
                [ConsolePayload.PrimaryKey] = primary,
            };

            if (!string.IsNullOrEmpty(consoleFields))
            {
                payload[ConsolePayload.FieldsKey] = consoleFields;
            }

            foreach (var (key, value) in fields)
            {
                payload[key] = value;
            }

            TraceBus.Emit(sys, name, payload);
        }

        /// <summary>
        /// Publishes an event at <see cref="ConsoleLevel.Warn"/> or <see cref="ConsoleLevel.Error"/>
        /// - same contract as <see cref="Emit"/>, louder channel.
        /// </summary>
        /// <param name="sys">Bus subsystem.</param>
        /// <param name="name">Bus event name.</param>
        /// <param name="tag">The one-word console tag.</param>
        /// <param name="level">Which channel the line belongs on.</param>
        /// <param name="primary">The primary column.</param>
        /// <param name="consoleFields">Ordered payload members to show.</param>
        /// <param name="fields">The full payload.</param>
        public static void EmitAt(
            string sys,
            string name,
            string tag,
            ConsoleLevel level,
            string? primary,
            string? consoleFields,
            params (string Key, object? Value)[] fields)
        {
            var all = new (string Key, object? Value)[fields.Length + 1];
            all[0] = (ConsolePayload.LevelKey, level.ToString().ToLowerInvariant());
            for (var i = 0; i < fields.Length; i++)
            {
                all[i + 1] = fields[i];
            }

            Emit(sys, name, tag, primary, consoleFields, all);
        }

        /// <summary>
        /// Publishes an event that stays in the trace file: phases, observations, polling notices,
        /// ticks and aggregates.
        /// </summary>
        /// <param name="sys">Bus subsystem.</param>
        /// <param name="name">Bus event name.</param>
        /// <param name="fields">The payload.</param>
        public static void File(string sys, string name, params (string Key, object? Value)[] fields)
        {
            var payload = new Dictionary<string, object?>(fields.Length + 1)
            {
                [ConsolePayload.MirrorKey] = false,
            };

            foreach (var (key, value) in fields)
            {
                payload[key] = value;
            }

            TraceBus.Emit(sys, name, payload);
        }

        /// <summary>
        /// One startup summary per feature: <c>SETUP  &lt;summary&gt;  src=&lt;feature&gt; …</c>.
        /// Every field given is shown, in the order given.
        /// </summary>
        /// <param name="feature">Short feature name, e.g. <c>subtitles</c>. Becomes <c>src=</c>.</param>
        /// <param name="summary">What the feature is ready to do, in one phrase.</param>
        /// <param name="fields">Extra columns, shown in this order.</param>
        public static void Setup(string feature, string summary, params (string Key, object? Value)[] fields)
        {
            var all = new (string Key, object? Value)[fields.Length + 1];
            all[0] = ("src", feature);
            for (var i = 0; i < fields.Length; i++)
            {
                all[i + 1] = fields[i];
            }

            Emit(SetupSystem, feature, ConsoleTags.Setup, summary, Keys(all), all);
        }

        /// <summary>The ordered field spec for a set of payload members: their keys, in order.</summary>
        /// <param name="fields">The payload members to show.</param>
        public static string Keys(params (string Key, object? Value)[] fields)
        {
            var sb = new StringBuilder();
            foreach (var (key, _) in fields)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(key);
            }

            return sb.ToString();
        }
    }
}
