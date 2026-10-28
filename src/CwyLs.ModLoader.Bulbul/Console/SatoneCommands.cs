using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Console;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// The console commands for the APIs built in parallel with P2-5: Satone's expression and
    /// motion (P2-3), her speech (P2-2) and the master-data loader (P2-1). The <c>subtitle</c>
    /// command went with the overlay panel it drove (B-WORDING retired it; C5 removed its code).
    /// </summary>
    /// <remarks>
    /// Every one of them is reached by type name through <see cref="LateBound"/>. Those tickets
    /// were written at the same time as this one, so a compile-time call would have meant this
    /// console could not be built until all of them had landed; a name lookup means a missing API
    /// is a "not available" line the user can read, not a build failure.
    /// </remarks>
    internal static class SatoneCommands
    {
        /// <summary>The expression/motion facade (P2-3), which P2-2 extends with <c>Say</c>.</summary>
        internal const string SatoneType = "CwyLs.ModLoader.Bulbul.Satone.Motion.Satone";

        /// <summary>The master-data loader (P2-1).</summary>
        internal const string MasterDataType = "CwyLs.ModLoader.Bulbul.MasterData.MasterDataFeature";

        /// <summary>What <c>satone say</c> prints while P2-2 has not landed.</summary>
        internal const string SayUnavailable =
            "not available until P2-2 (Satone.Say); 'voice satone <name>' plays a registered clip in the meantime";

        /// <summary><c>satone state|face|motion|look|say</c>.</summary>
        /// <param name="args">Arguments after the command name.</param>
        internal static string Satone(IReadOnlyList<string> args)
        {
            var what = args.Count > 0 ? args[0] : "state";

            switch (what.ToLowerInvariant())
            {
                case "state": return State();
                case "face": return Face(args);
                case "motion": return Motion(args);
                case "look": return Look(args);
                case "say": return Say(args);
                default: return "satone state|face <name>|motion <name>|look <scale> <sec>|say <text>";
            }
        }

        /// <summary><c>masterdata status</c>.</summary>
        /// <param name="args">Arguments after the command name.</param>
        internal static string MasterData(IReadOnlyList<string> args)
        {
            if (args.Count > 0 && !string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase))
            {
                return "masterdata status";
            }

            if (LateBound.Find(MasterDataType) is null)
            {
                return LateBound.NotAvailable(MasterDataType, "P2-1 (master-data patching)");
            }

            var sb = new StringBuilder("MASTERDATA");
            sb.Append("\n  injected ").Append(Read(MasterDataType, "IsInjected"));
            sb.Append("\n  patch    ").Append(Read(MasterDataType, "PatchApplied"))
                .Append("  ").Append(Read(MasterDataType, "PatchDetail"));
            sb.Append("\n  summary  ").Append(Read(MasterDataType, "LastSummary"));
            sb.Append("\n  trigger  ").Append(Read(MasterDataType, "LastTrigger"));
            return sb.ToString();
        }

        private static string State()
        {
            if (LateBound.Find(SatoneType) is null)
            {
                return LateBound.NotAvailable(SatoneType, "P2-3 (the expression and motion API)");
            }

            var sb = new StringBuilder("SATONE");
            sb.Append("\n  state    ").Append(Read(SatoneType, "State"))
                .Append(" (").Append(Read(SatoneType, "StateId")).Append(')');
            sb.Append("\n  motion   ").Append(Read(SatoneType, "CurrentMotion"))
                .Append(" (").Append(Read(SatoneType, "CurrentMotionId")).Append(')');
            sb.Append("\n  animation ").Append(Read(SatoneType, "IsAnimationEnded")).Append(" ended");
            sb.Append("\n  interact ").Append(Read(SatoneType, "CanInteract"));
            return sb.ToString();
        }

        private static string Face(IReadOnlyList<string> args)
        {
            if (args.Count < 2)
            {
                return "satone face <name>";
            }

            return Call("Face", new[] { typeof(string) }, new object?[] { args[1] },
                "face '" + args[1] + "'");
        }

        private static string Motion(IReadOnlyList<string> args)
        {
            if (args.Count < 2)
            {
                return "satone motion <name>";
            }

            return Call("Motion", new[] { typeof(string), typeof(bool) },
                new object?[] { args[1], false }, "motion '" + args[1] + "'");
        }

        private static string Look(IReadOnlyList<string> args)
        {
            if (args.Count < 3)
            {
                return "satone look <scale> <seconds>";
            }

            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                || !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return "look takes two numbers: satone look <scale> <seconds>";
            }

            return Call("LookAt", new[] { typeof(float), typeof(float) },
                new object?[] { scale, seconds },
                "look " + args[1] + " for " + args[2] + "s");
        }

        private static string Say(IReadOnlyList<string> args)
        {
            var text = CommandLineParser.Join(args, 1);
            if (text.Length == 0)
            {
                return "satone say <text>";
            }

            if (!LateBound.TryCallWithDefaults(
                    SatoneType, "Say", new object?[] { text }, out var result, out var problem))
            {
                // Either the type is missing entirely or it has no Say yet - both mean P2-2.
                return SayUnavailable + " [" + problem + "]";
            }

            if (result is bool said)
            {
                return said
                    ? "Satone is saying \"" + text + "\""
                    : "Satone.Say refused the line; see the log";
            }

            // P2-2 returns a handle rather than a flag; its own ToString carries the outcome, so
            // the console shows whatever that API decided to say about the line.
            var outcome = result is null ? string.Empty : "  " + result;
            return "Satone is saying \"" + text + "\"" + outcome;
        }

        private static string Call(string method, Type[] parameterTypes, object?[] arguments, string what)
        {
            if (LateBound.Find(SatoneType) is null)
            {
                return LateBound.NotAvailable(SatoneType, "P2-3 (the expression and motion API)");
            }

            if (!LateBound.TryCall(SatoneType, method, parameterTypes, arguments, out var result, out var problem))
            {
                return problem;
            }

            return result is bool ok && !ok
                ? what + " was refused (is Satone in the scene, and is the name in the table?)"
                : what;
        }

        private static string Read(string type, string member) =>
            LateBound.TryRead(type, member, out var value, out var problem)
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)"
                : problem;
    }
}
