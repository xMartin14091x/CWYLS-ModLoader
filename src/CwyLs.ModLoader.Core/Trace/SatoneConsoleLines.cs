using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// The two console records that matter most to a person watching the log: what Satone said, and
    /// what she is doing.
    /// <para>
    /// These two events come from <c>CWYEvents</c>, whose payload is the event record's own
    /// <c>Fields()</c> - so unlike every other producer they cannot name their tag and field order
    /// at the emit site. They are built here instead, and <see cref="ConsoleEventMap"/> routes to
    /// them.
    /// </para>
    /// <para>
    /// The generic bus mirror used to show the first six payload members cut at 60 characters,
    /// which is right for a health reading and wrong for a sentence: the one line the user actually
    /// reads arrived truncated in the middle of the dialogue. The fixed layout below never drops a
    /// field, and caps only the sentence, at <see cref="TextLimit"/> characters per language.
    /// </para>
    /// </summary>
    public static class SatoneConsoleLines
    {
        /// <summary>Bus <c>sys</c> tag these events are published under.</summary>
        public const string System = "satone";

        /// <summary>Bus event name of a spoken line.</summary>
        public const string SpeechEvent = "speech";

        /// <summary>Bus event name of a state change.</summary>
        public const string StateEvent = "state";

        /// <summary>Longest sentence written per language before it is cut.</summary>
        public const int TextLimit = 160;

        /// <summary>Prefix the game puts on every voice clip name; dropped from the console line.</summary>
        public const string VoicePrefix = "Voice_";

        /// <summary>
        /// <c>SPEECH  "&lt;text&gt;" 「&lt;ja&gt;」  src= dur= voice= id= attr=</c>.
        /// The Japanese is omitted when it is absent or identical to the shown text.
        /// </summary>
        /// <param name="time">Event time.</param>
        /// <param name="payload">The <c>satone/speech</c> payload (LineSpoken fields). Null is allowed.</param>
        public static ConsoleRecord Speech(DateTimeOffset time, IReadOnlyDictionary<string, object?>? payload)
        {
            var text = Clip(ConsolePayload.Text(payload, "text"));
            var japanese = Clip(ConsolePayload.Text(payload, "textJa"));

            string? primary = null;
            if (text != null)
            {
                primary = "\"" + text + "\"";
            }

            if (japanese != null && !string.Equals(japanese, text, StringComparison.Ordinal))
            {
                primary = primary is null ? "「" + japanese + "」" : primary + " 「" + japanese + "」";
            }

            return new ConsoleRecord(
                time,
                ConsoleTags.Speech,
                primary,
                new[]
                {
                    new ConsoleField("src", ConsolePayload.Text(payload, "source")),
                    new ConsoleField("dur", ConsolePayload.Seconds(payload, "durationSec")),
                    new ConsoleField("voice", Voice(payload)),
                    new ConsoleField("id", ConsolePayload.Text(payload, "localizationId")),
                    new ConsoleField("attr", ConsolePayload.Text(payload, "attribution")),
                });
        }

        /// <summary><c>STATE  &lt;prev&gt; -&gt; &lt;next&gt;</c>.</summary>
        /// <param name="time">Event time.</param>
        /// <param name="payload">The <c>satone/state</c> payload (SatoneStateChanged fields). Null is allowed.</param>
        public static ConsoleRecord State(DateTimeOffset time, IReadOnlyDictionary<string, object?>? payload) =>
            new ConsoleRecord(
                time,
                ConsoleTags.State,
                (ConsolePayload.Text(payload, "prev") ?? ConsoleRecord.Missing)
                + " -> "
                + (ConsolePayload.Text(payload, "next") ?? ConsoleRecord.Missing));

        /// <summary>True when <paramref name="name"/> is one of the two events formatted here.</summary>
        /// <param name="sys">Bus <c>sys</c> tag.</param>
        /// <param name="name">Bus event name.</param>
        public static bool Handles(string? sys, string? name) =>
            string.Equals(sys, System, StringComparison.Ordinal)
            && (string.Equals(name, SpeechEvent, StringComparison.Ordinal)
                || string.Equals(name, StateEvent, StringComparison.Ordinal));

        /// <summary>The record for one of the two events; null when <see cref="Handles"/> is false.</summary>
        /// <param name="time">Event time.</param>
        /// <param name="sys">Bus <c>sys</c> tag.</param>
        /// <param name="name">Bus event name.</param>
        /// <param name="payload">The event payload.</param>
        public static ConsoleRecord? Format(
            DateTimeOffset time,
            string? sys,
            string? name,
            IReadOnlyDictionary<string, object?>? payload)
        {
            if (!string.Equals(sys, System, StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(name, SpeechEvent, StringComparison.Ordinal))
            {
                return Speech(time, payload);
            }

            return string.Equals(name, StateEvent, StringComparison.Ordinal) ? State(time, payload) : null;
        }

        private static string? Voice(IReadOnlyDictionary<string, object?>? payload)
        {
            var voice = ConsolePayload.Text(payload, "voice");
            if (voice is null)
            {
                return null;
            }

            return voice.StartsWith(VoicePrefix, StringComparison.Ordinal)
                ? voice.Substring(VoicePrefix.Length)
                : voice;
        }

        private static string? Clip(string? text)
        {
            if (text is null)
            {
                return null;
            }

            var single = text.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= TextLimit ? single : single.Substring(0, TextLimit) + "…";
        }
    }
}
