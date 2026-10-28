using System;
using System.Text;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// The colour family a console line belongs to. The families are the ones the terminal viewer
    /// uses (<c>tools/mac/watch-log.py</c>, table <c>TAGS</c>), so a line reads the same in the
    /// game and in the terminal; only the colours differ, because in the game they come from the
    /// game's own UI palette.
    /// </summary>
    public enum ConsoleColorRole
    {
        /// <summary>A command's own result text.</summary>
        Output,

        /// <summary>The line the user typed, echoed back.</summary>
        Echo,

        /// <summary>Satone: SPEECH, SAY, STATE, SUBTITLE, FACE, MOTION, LOOK, RESTORE.</summary>
        Satone,

        /// <summary>The game world: MUSIC, POMODORO, SCENARIO, SCENE, WINDOW, LANGUAGE.</summary>
        World,

        /// <summary>The player and good news: READY, BUTTON, TYPED, CLICK, a passing SMOKE.</summary>
        Player,

        /// <summary>Content the framework moved: DATA, VOICE, TEXTURE, MASTERDATA.</summary>
        Content,

        /// <summary>The framework talking about itself: CONSOLE, SETUP, TRACE, KEYS, PACKS, NOTE and anything unknown.</summary>
        Framework,

        /// <summary>Worth a look: HEALTH, WARN, a Warning line.</summary>
        Warning,

        /// <summary>Something failed: UNITY, ERROR, a bad HEALTH reading, a failed SMOKE, an Error line.</summary>
        Error
    }

    /// <summary>An sRGB colour as three bytes. Unity-free, so the palette can be unit-tested.</summary>
    public readonly struct ConsoleRgb : IEquatable<ConsoleRgb>
    {
        /// <summary>Creates a colour.</summary>
        /// <param name="r">Red, 0-255.</param>
        /// <param name="g">Green, 0-255.</param>
        /// <param name="b">Blue, 0-255.</param>
        public ConsoleRgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        /// <summary>Red, 0-255.</summary>
        public byte R { get; }

        /// <summary>Green, 0-255.</summary>
        public byte G { get; }

        /// <summary>Blue, 0-255.</summary>
        public byte B { get; }

        /// <summary>The colour as TextMeshPro rich text wants it: <c>#RRGGBB</c>.</summary>
        public string ToHex() => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");

        /// <inheritdoc />
        public bool Equals(ConsoleRgb other) => R == other.R && G == other.G && B == other.B;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ConsoleRgb other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (R << 16) | (G << 8) | B;

        /// <inheritdoc />
        public override string ToString() => ToHex();
    }

    /// <summary>
    /// How a console line is coloured in the game-style console: which family its tag belongs to,
    /// which colour that family gets, and the TextMeshPro rich text for the whole line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The palette is taken from the game's own UI atlas (<c>UIAtlas</c>, sampled 2026-09-28):
    /// the cyan <c>#00D5EF</c> and magenta <c>#EA00B2</c> of every hover outline
    /// (<c>t_ui_addbox_active</c>), the lavender <c>#9999BF</c> of the separator bar
    /// (<c>t_ui_back_bar</c>) and the muted <c>#6E6E93</c> of the close icon and of unselected
    /// tab labels. The accents are lifted a little where the raw value is too dark to read as
    /// 18-point text on the game's 90 % black window.
    /// </para>
    /// <para>
    /// A mirrored line looks like <c>TAG  primary  k=v k=v</c> (the stamp is dropped by the log
    /// mirror). It is drawn the way the terminal viewer draws it: the tag bold in its family
    /// colour, the primary column near-white, the fields dim.
    /// </para>
    /// </remarks>
    public static class ConsoleLineColors
    {
        /// <summary>The game's hover-outline cyan.</summary>
        public static readonly ConsoleRgb GameCyan = new ConsoleRgb(0x00, 0xD5, 0xEF);

        /// <summary>The game's hover-outline magenta.</summary>
        public static readonly ConsoleRgb GameMagenta = new ConsoleRgb(0xEA, 0x00, 0xB2);

        /// <summary>The game's separator lavender.</summary>
        public static readonly ConsoleRgb GameLavender = new ConsoleRgb(0x99, 0x99, 0xBF);

        /// <summary>The game's muted icon and unselected-tab colour.</summary>
        public static readonly ConsoleRgb GameMuted = new ConsoleRgb(0x6E, 0x6E, 0x93);

        /// <summary>The primary column of a tagged line.</summary>
        public static readonly ConsoleRgb PrimaryText = new ConsoleRgb(0xF2, 0xF2, 0xF8);

        /// <summary>The <c>k=v</c> column of a tagged line.</summary>
        public static readonly ConsoleRgb FieldText = new ConsoleRgb(0x9A, 0x9A, 0xBC);

        private const string NoParseOpen = "<noparse>";
        private const string NoParseClose = "</noparse>";

        /// <summary>The colour of a family.</summary>
        /// <param name="role">The family.</param>
        public static ConsoleRgb Colour(ConsoleColorRole role)
        {
            switch (role)
            {
                case ConsoleColorRole.Echo: return GameCyan;
                case ConsoleColorRole.Satone: return new ConsoleRgb(0xFF, 0x7A, 0xD6);
                case ConsoleColorRole.World: return new ConsoleRgb(0x5C, 0xE1, 0xF4);
                case ConsoleColorRole.Player: return new ConsoleRgb(0xC4, 0xC4, 0xF0);
                case ConsoleColorRole.Content: return new ConsoleRgb(0x9E, 0xAE, 0xFF);
                case ConsoleColorRole.Framework: return new ConsoleRgb(0x8C, 0x8C, 0xB0);
                case ConsoleColorRole.Warning: return new ConsoleRgb(0xFF, 0xC8, 0x6B);
                case ConsoleColorRole.Error: return new ConsoleRgb(0xFF, 0x5F, 0x6B);
                default: return new ConsoleRgb(0xE6, 0xE6, 0xF2);
            }
        }

        /// <summary>
        /// The family of a tag, with the viewer's two content-dependent rules: a HEALTH line whose
        /// primary starts with <c>spike</c>, <c>lowFps</c> or <c>lowMemory</c> and a SMOKE line
        /// that starts with <c>FAIL</c> or reads <c>ok=false</c> are errors.
        /// </summary>
        /// <param name="tag">A one-word console tag; anything unknown is <see cref="ConsoleColorRole.Framework"/>.</param>
        /// <param name="primary">The primary column, for the two content rules. May be null.</param>
        public static ConsoleColorRole RoleOfTag(string? tag, string? primary)
        {
            switch (tag)
            {
                case ConsoleTags.Speech:
                case ConsoleTags.Say:
                case ConsoleTags.State:
                case ConsoleTags.Subtitle:
                case ConsoleTags.Face:
                case ConsoleTags.Motion:
                case ConsoleTags.Look:
                case ConsoleTags.Restore:
                    return ConsoleColorRole.Satone;

                case ConsoleTags.Music:
                case ConsoleTags.Pomodoro:
                case ConsoleTags.Scenario:
                case ConsoleTags.Scene:
                case ConsoleTags.Window:
                case ConsoleTags.Language:
                case ConsoleTags.Script:
                case ConsoleTags.Trigger:
                case ConsoleTags.Camera:
                    return ConsoleColorRole.World;

                case ConsoleTags.Ready:
                case ConsoleTags.Button:
                case ConsoleTags.Typed:
                case ConsoleTags.Click:
                case ConsoleTags.Action:
                case ConsoleTags.Api:
                case ConsoleTags.Mic:
                    return ConsoleColorRole.Player;

                case ConsoleTags.Smoke:
                    return primary != null
                           && (primary.StartsWith("FAIL", StringComparison.Ordinal)
                               || string.Equals(primary, "ok=false", StringComparison.Ordinal))
                        ? ConsoleColorRole.Error
                        : ConsoleColorRole.Player;

                case ConsoleTags.Health:
                    return IsBadHealth(primary) ? ConsoleColorRole.Error : ConsoleColorRole.Warning;

                case ConsoleTags.Warn:
                    return ConsoleColorRole.Warning;

                case ConsoleTags.Unity:
                case ConsoleTags.Error:
                    return ConsoleColorRole.Error;

                case ConsoleTags.Data:
                case ConsoleTags.Voice:
                case ConsoleTags.Texture:
                case ConsoleTags.MasterData:
                case ConsoleTags.Mod:
                case ConsoleTags.Audio:
                case ConsoleTags.UiSkin:
                case ConsoleTags.EditData:
                    return ConsoleColorRole.Content;

                // The loader's own lines; a mod that failed (ModConsoleLines.Failure) is red.
                case ConsoleTags.Mods:
                    return primary != null && primary.Contains(" failed in ")
                        ? ConsoleColorRole.Error
                        : ConsoleColorRole.Content;

                default:
                    return ConsoleColorRole.Framework;
            }
        }

        /// <summary>
        /// The family a whole scrollback line belongs to. The line's kind wins where it is decisive
        /// (an echo, a command result, a Warning or Error from the log); an Info line is coloured by
        /// its tag.
        /// </summary>
        /// <param name="kind">What the scrollback says the line is.</param>
        /// <param name="text">The line's text.</param>
        public static ConsoleColorRole RoleOf(ConsoleLineKind kind, string? text)
        {
            switch (kind)
            {
                case ConsoleLineKind.Echo:
                    return ConsoleColorRole.Echo;
                case ConsoleLineKind.Output:
                    return ConsoleColorRole.Output;
                case ConsoleLineKind.Error:
                    return ConsoleColorRole.Error;
                case ConsoleLineKind.Warning:
                    // A WARN or ERROR record keeps its own reading; a tagged warning of another
                    // family (a HEALTH spike logged at Warning, say) is still a warning.
                    return TrySplit(text, out var warnTag, out var warnPrimary, out _)
                           && RoleOfTag(warnTag, warnPrimary) == ConsoleColorRole.Error
                        ? ConsoleColorRole.Error
                        : ConsoleColorRole.Warning;
                default:
                    return TrySplit(text, out var tag, out var primary, out _)
                        ? RoleOfTag(tag, primary)
                        : ConsoleColorRole.Framework;
            }
        }

        /// <summary>
        /// Splits a mirrored line into its columns: <c>TAG  primary  fields</c>. A leading
        /// <c>HH:mm:ss.fff  </c> stamp is skipped, so a raw console-file line splits too.
        /// </summary>
        /// <param name="text">The line.</param>
        /// <param name="tag">The tag, or empty.</param>
        /// <param name="primary">The primary column, or empty.</param>
        /// <param name="fields">The <c>k=v</c> column, or empty when the line has none.</param>
        /// <returns>False when the line does not start with a console tag.</returns>
        public static bool TrySplit(string? text, out string tag, out string primary, out string fields)
        {
            tag = string.Empty;
            primary = string.Empty;
            fields = string.Empty;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var body = SkipStamp(text!);
            var tagEnd = body.IndexOf(ConsoleRecordFormatter.ColumnSeparator, StringComparison.Ordinal);
            var candidate = tagEnd < 0 ? body : body.Substring(0, tagEnd);
            if (!IsTag(candidate))
            {
                return false;
            }

            tag = candidate;
            if (tagEnd < 0)
            {
                return true;
            }

            var rest = body.Substring(tagEnd + ConsoleRecordFormatter.ColumnSeparator.Length);
            var split = rest.IndexOf(ConsoleRecordFormatter.ColumnSeparator, StringComparison.Ordinal);
            if (split < 0)
            {
                primary = rest;
                return true;
            }

            primary = rest.Substring(0, split);
            fields = rest.Substring(split + ConsoleRecordFormatter.ColumnSeparator.Length);
            return true;
        }

        /// <summary>
        /// The line as TextMeshPro rich text. Everything that came from the log or a command is
        /// inside <c>&lt;noparse&gt;</c>, so a <c>&lt;name&gt;</c> in a usage line prints as
        /// written instead of being read as markup.
        /// </summary>
        /// <param name="kind">What the scrollback says the line is.</param>
        /// <param name="text">The line's text.</param>
        public static string ToRichText(ConsoleLineKind kind, string? text)
        {
            var value = text ?? string.Empty;
            if (value.Length == 0)
            {
                return string.Empty;
            }

            var role = RoleOf(kind, value);
            if (kind == ConsoleLineKind.Info || kind == ConsoleLineKind.Warning || kind == ConsoleLineKind.Error)
            {
                if (TrySplit(value, out var tag, out var primary, out var fields))
                {
                    var sb = new StringBuilder(value.Length + 96);
                    sb.Append("<color=").Append(Colour(role).ToHex()).Append("><b>").Append(tag).Append("</b></color>");

                    if (primary.Length > 0 || fields.Length > 0)
                    {
                        sb.Append("  <color=").Append(PrimaryText.ToHex()).Append('>')
                            .Append(Escape(primary)).Append("</color>");
                    }

                    if (fields.Length > 0)
                    {
                        sb.Append("  <color=").Append(FieldText.ToHex()).Append('>')
                            .Append(Escape(fields)).Append("</color>");
                    }

                    return sb.ToString();
                }
            }

            return "<color=" + Colour(role).ToHex() + ">" + Escape(value) + "</color>";
        }

        /// <summary>
        /// Wraps text so TextMeshPro draws it literally. A <c>&lt;/noparse&gt;</c> inside the text
        /// is broken with a zero-width space, so it cannot end the literal run early.
        /// </summary>
        /// <param name="text">Any text; null is empty.</param>
        public static string Escape(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var safe = text!.IndexOf(NoParseClose, StringComparison.OrdinalIgnoreCase) < 0
                ? text
                : ReplaceIgnoreCase(text, NoParseClose, "</​noparse>");

            return NoParseOpen + safe + NoParseClose;
        }

        private static bool IsBadHealth(string? primary)
        {
            if (string.IsNullOrEmpty(primary))
            {
                return false;
            }

            var head = primary!;
            var space = head.IndexOf(' ');
            if (space >= 0)
            {
                head = head.Substring(0, space);
            }

            return string.Equals(head, "spike", StringComparison.Ordinal)
                || string.Equals(head, "lowFps", StringComparison.Ordinal)
                || string.Equals(head, "lowMemory", StringComparison.Ordinal);
        }

        private static string SkipStamp(string text)
        {
            // HH:mm:ss.fff followed by the column separator.
            const int stamp = 12;
            if (text.Length > stamp + 2
                && text[2] == ':' && text[5] == ':' && text[8] == '.'
                && text[stamp] == ' ' && text[stamp + 1] == ' '
                && char.IsDigit(text[0]) && char.IsDigit(text[11]))
            {
                return text.Substring(stamp + 2);
            }

            return text;
        }

        /// <summary>The same rule as the console grammar: 2-12 upper-case letters or digits.</summary>
        private static bool IsTag(string candidate)
        {
            if (candidate.Length < 2 || candidate.Length > 12)
            {
                return false;
            }

            foreach (var ch in candidate)
            {
                if ((ch < 'A' || ch > 'Z') && (ch < '0' || ch > '9'))
                {
                    return false;
                }
            }

            return candidate[0] >= 'A' && candidate[0] <= 'Z';
        }

        private static string ReplaceIgnoreCase(string text, string find, string with)
        {
            var sb = new StringBuilder(text.Length + 8);
            var at = 0;
            while (true)
            {
                var hit = text.IndexOf(find, at, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    sb.Append(text, at, text.Length - at);
                    return sb.ToString();
                }

                sb.Append(text, at, hit - at).Append(with);
                at = hit + find.Length;
            }
        }
    }
}
