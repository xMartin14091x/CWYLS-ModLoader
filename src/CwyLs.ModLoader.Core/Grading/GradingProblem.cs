using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// Something about a preset file worth telling its author: a value that was clamped, a member
    /// that is not known, a Lightroom setting that has no faithful mapping. Errors cost the member
    /// (or, for a broken document, the whole file); warnings cost nothing.
    /// </summary>
    public sealed class GradingProblem
    {
        /// <summary>Creates a problem.</summary>
        /// <param name="path">JSON path of the member, e.g. <c>basic.exposure</c>; empty for the whole document.</param>
        /// <param name="message">What is wrong, in the words a preset author needs.</param>
        /// <param name="isError">True when the member (or file) was not used.</param>
        public GradingProblem(string path, string message, bool isError)
        {
            Path = path ?? string.Empty;
            Message = message ?? string.Empty;
            IsError = isError;
        }

        /// <summary>JSON path of the member; empty for the whole document.</summary>
        public string Path { get; }

        /// <summary>What is wrong.</summary>
        public string Message { get; }

        /// <summary>True when the member (or file) was not used; false for a warning.</summary>
        public bool IsError { get; }

        /// <inheritdoc />
        public override string ToString() =>
            (IsError ? "error " : "warning ") + (Path.Length == 0 ? string.Empty : Path + ": ") + Message;
    }

    /// <summary>What <see cref="GradingPresetParser.Parse"/> produced.</summary>
    public sealed class GradingParseResult
    {
        private static readonly GradingProblem[] NoProblems = new GradingProblem[0];

        /// <summary>Creates a result.</summary>
        /// <param name="preset">The preset, or null when the document could not be read at all.</param>
        /// <param name="problems">Everything worth telling the author. Null means none.</param>
        public GradingParseResult(GradingPreset? preset, IReadOnlyList<GradingProblem>? problems)
        {
            Preset = preset;
            Problems = problems ?? NoProblems;
        }

        /// <summary>The preset; null when the document was unusable (see <see cref="Problems"/>).</summary>
        public GradingPreset? Preset { get; }

        /// <summary>Errors and warnings, in document order. Never null.</summary>
        public IReadOnlyList<GradingProblem> Problems { get; }

        /// <summary>True when a preset came out.</summary>
        public bool Ok => Preset != null;

        /// <summary>How many problems are errors.</summary>
        public int ErrorCount
        {
            get
            {
                var count = 0;
                foreach (var problem in Problems)
                {
                    if (problem.IsError)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>How many problems are warnings.</summary>
        public int WarningCount => Problems.Count - ErrorCount;
    }

    /// <summary>The outcome of a grading command, for the console and the mod facet.</summary>
    public sealed class GradingResult
    {
        private GradingResult(bool ok, string message)
        {
            Ok = ok;
            Message = message ?? string.Empty;
        }

        /// <summary>True when the command did what was asked.</summary>
        public bool Ok { get; }

        /// <summary>One line for people: what happened, or why not. Never null.</summary>
        public string Message { get; }

        /// <summary>A success.</summary>
        /// <param name="message">What happened.</param>
        public static GradingResult Success(string message) => new GradingResult(true, message);

        /// <summary>A refusal or failure.</summary>
        /// <param name="message">Why.</param>
        public static GradingResult Failure(string message) => new GradingResult(false, message);

        /// <inheritdoc />
        public override string ToString() => Message;
    }

    /// <summary>File-format constants of grading presets.</summary>
    public static class GradingFormat
    {
        /// <summary>The pack folder holding presets; direct children ending in <c>.json</c>.</summary>
        public const string PackFolder = "grading/";

        /// <summary>The preset file extension.</summary>
        public const string Extension = ".json";

        /// <summary>Extension of a 3D LUT in Adobe/Resolve's text format.</summary>
        public const string CubeExtension = ".cube";

        /// <summary>Extension of a strip LUT image (width = size x size, height = size).</summary>
        public const string StripExtension = ".png";

        /// <summary>Longest preset name <c>grade save</c> accepts.</summary>
        public const int MaxNameLength = 48;

        /// <summary>
        /// Why a name cannot be used for <c>grade save</c>, or null when it can: 1..48 characters
        /// of letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, starting with a letter or digit,
        /// and not a Windows device name.
        /// </summary>
        /// <param name="name">The name, without extension.</param>
        public static string? NameProblem(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "a preset name is required";
            }

            if (name!.Length > MaxNameLength)
            {
                return "a preset name is at most " + MaxNameLength + " characters";
            }

            if (!char.IsLetterOrDigit(name[0]) || name[0] > 127)
            {
                return "a preset name starts with a letter or digit";
            }

            foreach (var c in name)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                         c == '.' || c == '_' || c == '-';
                if (!ok)
                {
                    return "a preset name may only use letters, digits, '.', '_' and '-' ('" + c + "' is not allowed)";
                }
            }

            return Packs.SafeFileNames.ProblemWith(name);
        }

        /// <summary>
        /// Why a LUT path from a preset cannot be used, or null when it can: it must be relative,
        /// stay inside the preset's folder (no <c>..</c>), and end in <c>.cube</c> or <c>.png</c>.
        /// </summary>
        /// <param name="relative">The path as written in the preset.</param>
        public static string? LutPathProblem(string? relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                return "a LUT file path is required";
            }

            var path = relative!.Trim().Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0)
            {
                return "\"" + relative + "\" must be relative to the preset's folder";
            }

            foreach (var segment in path.Split('/'))
            {
                if (segment == ".." || segment.Length == 0)
                {
                    return "\"" + relative + "\" must stay inside the preset's folder";
                }

                var unsafeSegment = Packs.SafeFileNames.ProblemWith(segment);
                if (unsafeSegment != null && segment != ".")
                {
                    return "\"" + relative + "\": " + unsafeSegment;
                }
            }

            if (!path.EndsWith(CubeExtension, StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(StripExtension, StringComparison.OrdinalIgnoreCase))
            {
                return "\"" + relative + "\" must be a " + CubeExtension + " or a strip " + StripExtension + " LUT";
            }

            return null;
        }
    }
}
