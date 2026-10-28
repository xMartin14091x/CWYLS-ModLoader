using System;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// The console records the trace plugin owns itself: the player's clicks, the Unity UI layer,
    /// and the plugin's own status lines.
    /// </summary>
    /// <remarks>
    /// These are not bus events - the trace plugin writes their file copy through its own line
    /// builder - so they are built here and handed to the one console sink directly. Keeping the
    /// layout in Core is what lets the golden tests cover them.
    /// </remarks>
    public static class TraceConsoleLines
    {
        /// <summary><c>CLICK  (612, 380)</c>.</summary>
        /// <param name="time">Event time.</param>
        /// <param name="x">Pointer x, already rounded to a whole pixel.</param>
        /// <param name="y">Pointer y.</param>
        public static ConsoleRecord Click(DateTimeOffset time, string? x, string? y) =>
            new ConsoleRecord(
                time,
                ConsoleTags.Click,
                "(" + (x ?? "?") + ", " + (y ?? "?") + ")");

        /// <summary>
        /// A Unity UI action: <c>BUTTON  "Start"  path=Canvas/Home/StartButton</c>, or
        /// <c>TYPED  "buy milk"  path=… control=TMP_InputField</c> for a text field.
        /// </summary>
        /// <param name="time">Event time.</param>
        /// <param name="controlType">Unity control type, e.g. <c>Button</c>.</param>
        /// <param name="eventName">What happened, e.g. <c>press</c>.</param>
        /// <param name="path">Hierarchy path of the control.</param>
        /// <param name="label">The control's visible label, when it has one.</param>
        /// <param name="value">The new value, for a control that carries one.</param>
        public static ConsoleRecord Ui(
            DateTimeOffset time,
            string? controlType,
            string? eventName,
            string? path,
            string? label,
            string? value)
        {
            var typed = controlType != null
                && controlType.IndexOf("InputField", StringComparison.Ordinal) >= 0;

            var tag = typed
                ? ConsoleTags.Typed
                : string.Equals(controlType, "Button", StringComparison.Ordinal)
                    ? ConsoleTags.Button
                    : ConsoleTags.Unknown;

            var subject = typed ? value ?? label : label ?? value;
            var primary = subject is null
                ? (eventName ?? controlType ?? "?")
                : "\"" + subject + "\"";

            return new ConsoleRecord(
                time,
                tag,
                primary,
                new[]
                {
                    new ConsoleField("path", path),
                    new ConsoleField("control", controlType),
                    new ConsoleField("event", eventName),
                });
        }

        /// <summary><c>TRACE  &lt;status&gt;</c> - the trace plugin's own startup/shutdown notices.</summary>
        /// <param name="time">Event time.</param>
        /// <param name="status">The status sentence.</param>
        /// <param name="level">Which channel it belongs on.</param>
        public static ConsoleRecord Status(
            DateTimeOffset time,
            string? status,
            ConsoleLevel level = ConsoleLevel.Info) =>
            new ConsoleRecord(time, ConsoleTags.Trace, status, null, level);
    }
}
