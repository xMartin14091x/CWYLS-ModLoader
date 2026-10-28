using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// Reads a <c>grading/&lt;name&gt;.json</c> preset (schema <see cref="GradingPreset.CurrentSchema"/>).
    /// <para>
    /// Lenient where a typo should not cost the look, strict where the file would be misread:
    /// malformed JSON, a root that is not an object and a schema newer than this build are fatal;
    /// an unknown member, a value out of range (clamped), a Lightroom setting with no faithful
    /// mapping and a missing <c>schema</c> (read as 1) are warnings; a value of the wrong type or a
    /// broken curve is an error that costs only that member. Members whose name starts with
    /// <c>x-</c> belong to the author and are skipped in silence.
    /// </para>
    /// </summary>
    public static class GradingPresetParser
    {
        /// <summary>The schema member.</summary>
        public const string SchemaKey = "schema";

        /// <summary>The display-name member.</summary>
        public const string NameKey = "name";

        /// <summary>The description member.</summary>
        public const string DescriptionKey = "description";

        /// <summary>The tone-curve group.</summary>
        public const string ToneCurveKey = "toneCurve";

        /// <summary>The LUT group.</summary>
        public const string LutKey = "lut";

        /// <summary>The LUT file member inside <see cref="LutKey"/>.</summary>
        public const string LutFileKey = "file";

        /// <summary>The prefix of members reserved for the preset author.</summary>
        public const string ExtensionPrefix = "x-";

        /// <summary>Parses a preset document.</summary>
        /// <param name="json">The whole file. Null or blank is an error result.</param>
        /// <returns>The preset (null when unusable) and every problem found.</returns>
        public static GradingParseResult Parse(string? json)
        {
            var problems = new List<GradingProblem>();

            if (string.IsNullOrWhiteSpace(json))
            {
                problems.Add(new GradingProblem(string.Empty, "the file is empty", isError: true));
                return new GradingParseResult(null, problems);
            }

            if (!JsonReader.TryParse(json!, out var root, out var error))
            {
                problems.Add(new GradingProblem(string.Empty, "invalid JSON at " + error, isError: true));
                return new GradingParseResult(null, problems);
            }

            if (!(root is JsonObject document))
            {
                problems.Add(new GradingProblem(
                    string.Empty, "a preset must be a JSON object, not " + JsonReader.TypeName(root), isError: true));
                return new GradingParseResult(null, problems);
            }

            if (!ReadSchema(document, problems))
            {
                return new GradingParseResult(null, problems);
            }

            var name = OptionalString(document, NameKey, problems) ?? string.Empty;
            var description = OptionalString(document, DescriptionKey, problems) ?? string.Empty;

            var body = new Body();
            var blocks = new Dictionary<GradingTimeOfDay, GradingPreset>();

            foreach (var member in document)
            {
                if (member.Key == SchemaKey || member.Key == NameKey || member.Key == DescriptionKey ||
                    member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var timeOfDay = GradingTimesOfDay.FromName(member.Key);
                if (timeOfDay != GradingTimeOfDay.Unknown)
                {
                    if (!(member.Value is JsonObject blockObject))
                    {
                        problems.Add(new GradingProblem(
                            member.Key,
                            "a time-of-day block must be an object, not " + JsonReader.TypeName(member.Value),
                            isError: true));
                        continue;
                    }

                    var block = new Body();
                    foreach (var inner in blockObject)
                    {
                        ReadMember(inner.Key, inner.Value, member.Key + ".", block, problems, inBlock: true);
                    }

                    blocks[timeOfDay] = block.ToPreset(string.Empty, string.Empty);
                    continue;
                }

                ReadMember(member.Key, member.Value, string.Empty, body, problems, inBlock: false);
            }

            var preset = GradingPreset.Create(name, description, body.Values, body.Curves, body.LutFile, blocks);
            return new GradingParseResult(preset, problems);
        }

        private static bool ReadSchema(JsonObject document, List<GradingProblem> problems)
        {
            if (!document.TryGetValue(SchemaKey, out var raw))
            {
                problems.Add(new GradingProblem(
                    SchemaKey,
                    "missing; read as schema " + GradingPreset.CurrentSchema.ToString(CultureInfo.InvariantCulture) +
                    " (add \"schema\": " + GradingPreset.CurrentSchema.ToString(CultureInfo.InvariantCulture) + ")",
                    isError: false));
                return true;
            }

            if (!(raw is long schema))
            {
                problems.Add(new GradingProblem(
                    SchemaKey, "expected a whole number, not " + JsonReader.TypeName(raw), isError: true));
                return false;
            }

            if (schema < 1 || schema > GradingPreset.CurrentSchema)
            {
                problems.Add(new GradingProblem(
                    SchemaKey,
                    "schema " + schema.ToString(CultureInfo.InvariantCulture) + " is not readable by this build (it reads " +
                    GradingPreset.CurrentSchema.ToString(CultureInfo.InvariantCulture) + "); update CWY:LS Mod Loader",
                    isError: true));
                return false;
            }

            return true;
        }

        private static string? OptionalString(JsonObject document, string key, List<GradingProblem> problems)
        {
            if (!document.TryGetValue(key, out var raw) || raw is null)
            {
                return null;
            }

            if (raw is string text)
            {
                return text.Trim();
            }

            problems.Add(new GradingProblem(key, "expected a string, not " + JsonReader.TypeName(raw), isError: true));
            return null;
        }

        private static void ReadMember(
            string key, object? value, string pathPrefix, Body body, List<GradingProblem> problems, bool inBlock)
        {
            if (key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var shown = pathPrefix + key;

            if (inBlock && (key == SchemaKey || key == NameKey || key == DescriptionKey))
            {
                problems.Add(new GradingProblem(shown, "ignored inside a time-of-day block", isError: false));
                return;
            }

            if (inBlock && GradingTimesOfDay.FromName(key) != GradingTimeOfDay.Unknown)
            {
                problems.Add(new GradingProblem(shown, "time-of-day blocks cannot be nested; ignored", isError: false));
                return;
            }

            if (key == ToneCurveKey)
            {
                ReadCurves(value, shown, body, problems);
                return;
            }

            if (key == LutKey)
            {
                ReadLut(value, shown, body, problems);
                return;
            }

            ReadPath(key, value, pathPrefix, body, problems);
        }

        private static void ReadPath(string path, object? value, string pathPrefix, Body body, List<GradingProblem> problems)
        {
            var shown = pathPrefix + path;

            if (GradingControls.Unsupported.TryGetValue(path, out var reason))
            {
                problems.Add(new GradingProblem(shown, "not supported: " + reason + "; ignored", isError: false));
                return;
            }

            if (Contains(GradingControls.GroupPaths, path))
            {
                if (!(value is JsonObject group))
                {
                    problems.Add(new GradingProblem(
                        shown, "expected an object, not " + JsonReader.TypeName(value), isError: true));
                    return;
                }

                foreach (var member in group)
                {
                    if (member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ReadPath(path + "." + member.Key, member.Value, pathPrefix, body, problems);
                }

                return;
            }

            if (!GradingControls.TryGetById(path, out var control))
            {
                problems.Add(new GradingProblem(
                    shown, "unknown member; ignored (the names are listed by 'grade controls')", isError: false));
                return;
            }

            if (!TryNumber(value, out var number))
            {
                problems.Add(new GradingProblem(
                    shown, "expected a number, not " + JsonReader.TypeName(value), isError: true));
                return;
            }

            var clamped = control!.Clamp(number, out var changed);
            if (changed)
            {
                problems.Add(new GradingProblem(
                    shown,
                    number.ToString("0.###", CultureInfo.InvariantCulture) + " is outside " + control.Range +
                    "; " + (control.IsAngle ? "wrapped to " : "clamped to ") +
                    clamped.ToString("0.###", CultureInfo.InvariantCulture),
                    isError: false));
            }

            body.Values[control.Id] = clamped;
        }

        private static void ReadCurves(object? value, string shown, Body body, List<GradingProblem> problems)
        {
            if (!(value is JsonObject curves))
            {
                problems.Add(new GradingProblem(shown, "expected an object, not " + JsonReader.TypeName(value), isError: true));
                return;
            }

            foreach (var member in curves)
            {
                if (member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var path = shown + "." + member.Key;

                if (GradingControls.Unsupported.TryGetValue(ToneCurveKey + "." + member.Key, out var reason))
                {
                    problems.Add(new GradingProblem(path, "not supported: " + reason + "; ignored", isError: false));
                    continue;
                }

                if (!GradingCurves.TryParseChannel(member.Key, out var channel))
                {
                    problems.Add(new GradingProblem(
                        path, "unknown curve; expected master, red, green or blue; ignored", isError: false));
                    continue;
                }

                var points = ReadPoints(member.Value, path, problems);
                if (points != null)
                {
                    body.Curves[channel] = points;
                }
            }
        }

        private static IReadOnlyList<GradingCurvePoint>? ReadPoints(object? value, string path, List<GradingProblem> problems)
        {
            if (!(value is List<object?> list))
            {
                problems.Add(new GradingProblem(
                    path, "expected an array of [input, output] pairs on 0..255, not " + JsonReader.TypeName(value), isError: true));
                return null;
            }

            if (list.Count < 2 || list.Count > GradingCurves.MaxPoints)
            {
                problems.Add(new GradingProblem(
                    path,
                    "a curve needs 2 to " + GradingCurves.MaxPoints.ToString(CultureInfo.InvariantCulture) +
                    " points, not " + list.Count.ToString(CultureInfo.InvariantCulture) + "; the curve is ignored",
                    isError: true));
                return null;
            }

            var points = new List<GradingCurvePoint>(list.Count);
            var clampedAny = false;

            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i] is List<object?> pair) || pair.Count != 2 ||
                    !TryNumber(pair[0], out var x) || !TryNumber(pair[1], out var y))
                {
                    problems.Add(new GradingProblem(
                        path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                        "a point is [input, output], two numbers on 0..255; the curve is ignored",
                        isError: true));
                    return null;
                }

                var cx = Clamp255(x, ref clampedAny);
                var cy = Clamp255(y, ref clampedAny);
                points.Add(new GradingCurvePoint(cx, cy));
            }

            if (clampedAny)
            {
                problems.Add(new GradingProblem(path, "coordinates outside 0..255 were clamped", isError: false));
            }

            points.Sort((a, b) => a.X.CompareTo(b.X));
            for (var i = 1; i < points.Count; i++)
            {
                if (Math.Abs(points[i].X - points[i - 1].X) < 1e-9)
                {
                    problems.Add(new GradingProblem(
                        path,
                        "two points share input " + points[i].X.ToString("0.##", CultureInfo.InvariantCulture) +
                        "; the curve is ignored",
                        isError: true));
                    return null;
                }
            }

            return points;
        }

        private static void ReadLut(object? value, string shown, Body body, List<GradingProblem> problems)
        {
            if (!(value is JsonObject lut))
            {
                problems.Add(new GradingProblem(shown, "expected an object, not " + JsonReader.TypeName(value), isError: true));
                return;
            }

            foreach (var member in lut)
            {
                if (member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var path = shown + "." + member.Key;

                if (member.Key == LutFileKey)
                {
                    if (!(member.Value is string file) || file.Trim().Length == 0)
                    {
                        problems.Add(new GradingProblem(
                            path, "expected a non-empty string, not " + JsonReader.TypeName(member.Value), isError: true));
                        continue;
                    }

                    var problem = GradingFormat.LutPathProblem(file);
                    if (problem != null)
                    {
                        problems.Add(new GradingProblem(path, problem + "; the LUT is ignored", isError: true));
                        continue;
                    }

                    body.LutFile = file.Trim().Replace('\\', '/');
                    continue;
                }

                // lut.amount and anything else under lut: the control table decides.
                ReadPath(LutKey + "." + member.Key, member.Value, shown.Substring(0, shown.Length - LutKey.Length), body, problems);
            }
        }

        private static double Clamp255(double v, ref bool clamped)
        {
            if (v < 0)
            {
                clamped = true;
                return 0;
            }

            if (v > GradingCurvePoint.Max)
            {
                clamped = true;
                return GradingCurvePoint.Max;
            }

            return v;
        }

        private static bool TryNumber(object? raw, out double value)
        {
            switch (raw)
            {
                case long whole:
                    value = whole;
                    return true;
                case double real when !double.IsNaN(real) && !double.IsInfinity(real):
                    value = real;
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }

        private static bool Contains(IReadOnlyCollection<string> set, string value)
        {
            foreach (var item in set)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class Body
        {
            internal Dictionary<string, double> Values { get; } = new Dictionary<string, double>(StringComparer.Ordinal);

            internal Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>> Curves { get; } =
                new Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>();

            internal string? LutFile { get; set; }

            internal GradingPreset ToPreset(string name, string description) =>
                GradingPreset.Create(name, description, Values, Curves, LutFile, null);
        }
    }
}
