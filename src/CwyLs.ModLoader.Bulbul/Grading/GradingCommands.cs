using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Grading;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// The <c>grade</c> console command: live Lightroom-style grading in the running game.
    /// <code>
    /// grade                       status (preset, time of day, components, volume)
    /// grade list                  presets: the player's own, then each pack's
    /// grade apply &lt;name&gt;         apply and remember (warm-film, cwy.demo.scenery:warm-film, user:mine)
    /// grade off                   remove the grading, forget the preset
    /// grade set &lt;control&gt; &lt;v&gt;    change one slider (exposure 0.3, temp -20, blues.lum -30, vignette -25)
    /// grade save &lt;name&gt;          save as BepInEx/data/grading/&lt;name&gt;.json
    /// grade ab                    flip ORIGINAL (the game's look) / GRADED, instantly
    /// grade export                print the current values as JSON (also written to _export.json)
    /// grade controls [filter]     every control with its range
    /// grade reload                re-read the preset file now
    /// </code>
    /// Every change prints one console line (tag TEXTURE, primary <c>grade ...</c>).
    /// </summary>
    internal static class GradingCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "grade";

        /// <summary>The usage line.</summary>
        internal const string Usage = "grade list|apply <name>|off|set <control> <value>|save <name>|ab|export|controls [filter]|reload";

        /// <summary>Registers <c>grade</c>.</summary>
        internal static void Register() =>
            Framework.ConsoleFeature.Register(
                Name,
                "Photo grading (Lightroom-style presets) over the room: apply, tweak, compare with the original, save.",
                Usage,
                "textures",
                Run);

        /// <summary>Runs one <c>grade</c> line.</summary>
        /// <param name="args">Arguments after the name.</param>
        internal static string Run(IReadOnlyList<string> args)
        {
            var verb = args.Count == 0 ? "status" : args[0].ToLowerInvariant();

            switch (verb)
            {
                case "status":
                    return "GRADING: " + GradingFeature.Status + "\n  " + GradingFeature.VolumeState;
                case "list":
                    return List();
                case "apply":
                    return args.Count < 2 ? "grade apply <name>" : GradingFeature.Apply(Join(args, 1)).Message;
                case "off":
                    return GradingFeature.Off().Message;
                case "set":
                    return Set(args);
                case "save":
                    return args.Count < 2 ? "grade save <name>" : GradingFeature.Save(args[1]).Message;
                case "ab":
                    return GradingFeature.ToggleOriginal().Message;
                case "export":
                    return GradingFeature.Export().Message;
                case "controls":
                    return Controls(args.Count > 1 ? args[1] : null);
                case "reload":
                    return GradingFeature.Reload().Message;
                default:
                    return Usage;
            }
        }

        private static string Set(IReadOnlyList<string> args)
        {
            if (args.Count < 3)
            {
                return "grade set <control> <value>   e.g. grade set exposure 0.3, grade set split.highlightHue 40";
            }

            if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return "'" + args[2] + "' is not a number (use a dot for decimals)";
            }

            return GradingFeature.Set(args[1], value).Message;
        }

        private static string List()
        {
            var catalog = GradingFeature.Catalog();
            var active = GradingFeature.ActivePreset;
            var sb = new StringBuilder("GRADING presets (").Append(catalog.Entries.Count).Append("): ")
                .Append(active is null ? "none applied" : "applied " + active);

            foreach (var entry in catalog.Entries)
            {
                sb.Append("\n  ").Append(string.Equals(entry.QualifiedName, active, StringComparison.OrdinalIgnoreCase) ? "* " : "  ")
                  .Append(entry.QualifiedName);
            }

            if (catalog.Entries.Count == 0)
            {
                sb.Append("\n  (none: put grading/<name>.json in a pack, or 'grade set ...' then 'grade save <name>')");
            }

            return sb.ToString();
        }

        private static string Controls(string? filter)
        {
            var sb = new StringBuilder("GRADING controls (id, range, aliases):");
            foreach (var control in GradingControls.All)
            {
                if (filter != null && control.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                sb.Append("\n  ").Append(control.Id).Append("  ").Append(control.Range);
                if (control.Aliases.Count > 0)
                {
                    sb.Append("  (").Append(string.Join(", ", new List<string>(control.Aliases).ToArray())).Append(')');
                }

                sb.Append("  ").Append(control.Description);
            }

            sb.Append("\n  toneCurve.master|red|green|blue and lut.file: edit the JSON (the file is watched)");
            return sb.ToString();
        }

        private static string Join(IReadOnlyList<string> args, int from)
        {
            var parts = new List<string>();
            for (var i = from; i < args.Count; i++)
            {
                parts.Add(args[i]);
            }

            return string.Join(" ", parts.ToArray());
        }
    }
}
