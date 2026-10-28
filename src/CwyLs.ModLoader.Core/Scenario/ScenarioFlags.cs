using System;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Conditions;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>One flag change, as <see cref="ScenarioFlags.Changed"/> reports it. Immutable.</summary>
    public sealed class ScenarioFlagChange
    {
        internal ScenarioFlagChange(string name, bool value, string by)
        {
            Name = name;
            Value = value;
            By = by;
        }

        /// <summary>The flag, as first written.</summary>
        public string Name { get; }

        /// <summary>True when it was set, false when cleared.</summary>
        public bool Value { get; }

        /// <summary>Who changed it: the scene's group id, <c>mod:&lt;id&gt;</c>, <c>console</c>.</summary>
        public string By { get; }

        /// <summary><c>+metSatone by cwy_demo_script_01</c>.</summary>
        public override string ToString() => (Value ? "+" : "-") + Name + " by " + By;
    }

    /// <summary>
    /// The named flags scripts and mods set (<c>cwy.flag set|clear &lt;name&gt;</c>, the Scenes facet)
    /// and the <c>flag:&lt;name&gt;</c> condition reads. A flag is either set or not. Names
    /// follow the condition argument rule (1-64 letters, digits, <c>_ . -</c>; checked by
    /// <see cref="ConditionParser.TryParseKey"/>) and compare case-insensitively. An ordinary object so
    /// it can be tested; <see cref="Shared"/> is the one the game uses. Thread-safe.
    /// </summary>
    /// <remarks>
    /// Flags live in CWY:LS Mod Loader's own data file (the game side saves <see cref="ToJson"/> to
    /// <c>BepInEx/data/script/flags.json</c>), never in the game's save, so a vanilla game and Steam
    /// Cloud never see them.
    /// </remarks>
    public sealed class ScenarioFlags
    {
        /// <summary>Most flags kept; setting one more is refused.</summary>
        public const int MaxFlags = 512;

        /// <summary>The <c>version</c> member of <see cref="ToJson"/>.</summary>
        public const int FileVersion = 1;

        private static readonly ScenarioFlags SharedInstance = new ScenarioFlags();

        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The flags the running game uses.</summary>
        public static ScenarioFlags Shared => SharedInstance;

        /// <summary>
        /// Raised after a flag changed (not when a set flag is set again), outside the lock, on the
        /// caller's thread. A throwing handler is not caught here: the game side's handlers catch
        /// and log their own failures.
        /// </summary>
        public event Action<ScenarioFlagChange>? Changed;

        /// <summary>How many flags are set.</summary>
        public int Count
        {
            get { lock (_gate) { return _names.Count; } }
        }

        /// <summary>The set flags, sorted (ordinal, case-insensitive).</summary>
        public IReadOnlyList<string> Names
        {
            get
            {
                List<string> list;
                lock (_gate)
                {
                    list = new List<string>(_names.Values);
                }

                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            }
        }

        /// <summary>True when <paramref name="name"/> is a usable flag name.</summary>
        /// <param name="name">A candidate.</param>
        /// <param name="problem">Why not; empty when usable.</param>
        public static bool IsValidName(string? name, out string problem)
        {
            var text = name?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                problem = "a flag needs a name";
                return false;
            }

            if (!ConditionParser.TryParseKey(ConditionTokens.Flag + ":" + text, out _, out _, out var error))
            {
                problem = "\"" + text + "\" is not a flag name: " + (error ?? "1 to 64 letters, digits, '_', '.' or '-'");
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>True when the flag is set (case-insensitive). An unusable name is never set.</summary>
        /// <param name="name">The flag.</param>
        public bool IsSet(string? name)
        {
            var text = name?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            lock (_gate)
            {
                return _names.ContainsKey(text!);
            }
        }

        /// <summary>Sets or clears a flag.</summary>
        /// <param name="name">The flag.</param>
        /// <param name="value">True to set, false to clear.</param>
        /// <param name="by">Who: the scene's group id, <c>mod:&lt;id&gt;</c>, <c>console</c>.</param>
        /// <returns>Accepted (also when nothing changed), or refused for a bad name or a full table.</returns>
        public ScenarioResult Set(string? name, bool value, string? by)
        {
            if (!IsValidName(name, out var problem))
            {
                return ScenarioResult.Refused(problem);
            }

            var text = name!.Trim();
            bool changed;
            lock (_gate)
            {
                if (value)
                {
                    if (_names.ContainsKey(text))
                    {
                        changed = false;
                    }
                    else if (_names.Count >= MaxFlags)
                    {
                        return ScenarioResult.Refused("there are already " + MaxFlags + " flags; clear some first");
                    }
                    else
                    {
                        _names.Add(text, text);
                        changed = true;
                    }
                }
                else
                {
                    changed = _names.Remove(text);
                }
            }

            if (!changed)
            {
                return ScenarioResult.Ok("flag " + text + " was already " + (value ? "set" : "clear"));
            }

            Changed?.Invoke(new ScenarioFlagChange(text, value, string.IsNullOrWhiteSpace(by) ? "?" : by!));
            return ScenarioResult.Ok("flag " + text + (value ? " set" : " cleared"));
        }

        /// <summary>The flags as the data file holds them: <c>{"version":1,"flags":["a","b"]}</c>.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder("{\"version\":").Append(FileVersion).Append(",\"flags\":[");
            var first = true;
            foreach (var name in Names)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                sb.Append(MiniJson.String(name));
                first = false;
            }

            return sb.Append("]}").ToString();
        }

        /// <summary>
        /// Replaces the flags with the ones in a data file (no <see cref="Changed"/> events). Unusable
        /// names are skipped and counted in the message; a document that is not the file's shape is
        /// refused and leaves the flags as they were.
        /// </summary>
        /// <param name="json">The file's text.</param>
        /// <returns>What was loaded, or why nothing was.</returns>
        public ScenarioResult Load(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return ScenarioResult.Refused("the flags file is empty");
            }

            if (!JsonReader.TryParse(json!, out var value, out var error))
            {
                return ScenarioResult.Refused("the flags file is not JSON: " + error);
            }

            if (!(value is JsonObject root) || !root.TryGetValue("flags", out var raw) || !(raw is List<object?> items))
            {
                return ScenarioResult.Refused("the flags file has no \"flags\" list");
            }

            var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var skipped = 0;
            foreach (var item in items)
            {
                if (item is string name && IsValidName(name, out _) && loaded.Count < MaxFlags)
                {
                    loaded[name.Trim()] = name.Trim();
                }
                else
                {
                    skipped++;
                }
            }

            lock (_gate)
            {
                _names.Clear();
                foreach (var pair in loaded)
                {
                    _names.Add(pair.Key, pair.Value);
                }
            }

            return ScenarioResult.Ok(loaded.Count + " flag(s) loaded" + (skipped > 0 ? ", " + skipped + " unusable entr(ies) skipped" : string.Empty));
        }

        /// <summary>Clears every flag without events (tests, a fresh profile).</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _names.Clear();
            }
        }
    }
}
