using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Turns a call argument, an instance or a return value into one short, readable string.
    /// A trace whose arguments all read <c>GameAudioInfo</c> is useless, so non-scalars are
    /// printed as <c>Type{Name=..., Title=...}</c> using a small fixed set of identity members.
    /// </summary>
    public sealed class ArgFormatter
    {
        /// <summary>Marks a cut string. One character, so it barely costs anything in the file.</summary>
        public const string Ellipsis = "…";

        /// <summary>Identity members looked up on an unknown object, in the order they are printed.</summary>
        private static readonly string[] IdentityMembers =
        {
            "ID", "Id", "Name", "Title", "Key", "ScenarioGroupID", "LocalizationID",
        };

        /// <summary>At most this many identity members per object, so one argument stays one line.</summary>
        private const int MaxIdentityMembers = 4;

        private static readonly object CacheGate = new object();
        private static readonly Dictionary<Type, MemberInfo[]> IdentityCache = new Dictionary<Type, MemberInfo[]>();

        private readonly int _maxStringLength;
        private readonly int _argDepth;

        /// <summary>Creates a formatter.</summary>
        /// <param name="maxStringLength">Longest string emitted before it is cut. Values below 1 are clamped to 1.</param>
        /// <param name="argDepth">How far to descend into an object. 1 prints the type plus its identity members.</param>
        public ArgFormatter(int maxStringLength = TraceConfig.DefaultMaxStringLength, int argDepth = 1)
        {
            _maxStringLength = maxStringLength < 1 ? 1 : maxStringLength;
            _argDepth = argDepth < 0 ? 0 : argDepth;
        }

        /// <summary>
        /// Optional hook the plugin installs so Unity objects print as name plus type without Core
        /// ever referencing UnityEngine. Returning <c>null</c> means "not mine, carry on".
        /// </summary>
        public Func<object, string?>? ObjectHook { get; set; }

        /// <summary>Formats one value.</summary>
        public string Format(object? value) => Format(value, 0);

        /// <summary>Formats a whole argument list; a <c>null</c> array yields an empty list.</summary>
        public IReadOnlyList<string> FormatAll(object?[]? values)
        {
            if (values is null || values.Length == 0)
            {
                return System.Array.Empty<string>();
            }

            var formatted = new string[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                formatted[i] = Format(values[i], 0);
            }

            return formatted;
        }

        private string Format(object? value, int depth)
        {
            if (value is null)
            {
                return "null";
            }

            if (value is string text)
            {
                return Truncate(text);
            }

            var type = value.GetType();

            if (type.IsEnum)
            {
                return Truncate(value.ToString() ?? type.Name);
            }

            if (IsScalar(type))
            {
                return FormatScalar(value);
            }

            if (value is Array array)
            {
                // Type.Name of an array is already "String[]", so name the element type instead.
                var elementType = type.GetElementType();
                var elementName = elementType is null ? FriendlyTypeName(type) : FriendlyTypeName(elementType);
                return elementName + "[" + array.Length.ToString(CultureInfo.InvariantCulture) + "]";
            }

            if (value is ICollection collection)
            {
                return FriendlyTypeName(type) + "[" + collection.Count.ToString(CultureInfo.InvariantCulture) + "]";
            }

            var hooked = RunHook(value);
            if (hooked != null)
            {
                return Truncate(hooked);
            }

            if (depth >= _argDepth)
            {
                return FriendlyTypeName(type);
            }

            return FormatIdentity(value, type, depth);
        }

        private string? RunHook(object value)
        {
            var hook = ObjectHook;
            if (hook is null)
            {
                return null;
            }

            try
            {
                return hook(value);
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: argument hook threw; falling back to the plain type name", ex);
                return null;
            }
        }

        private string FormatIdentity(object value, Type type, int depth)
        {
            var members = IdentityMembersFor(type);
            if (members.Length == 0)
            {
                return FriendlyTypeName(type);
            }

            var sb = new StringBuilder(FriendlyTypeName(type)).Append('{');
            var written = 0;
            foreach (var member in members)
            {
                if (written > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(member.Name).Append('=').Append(Format(ReadMember(member, value), depth + 1));
                written++;
            }

            return sb.Append('}').ToString();
        }

        private static object? ReadMember(MemberInfo member, object target)
        {
            try
            {
                return member is PropertyInfo property
                    ? property.GetValue(target, null)
                    : ((FieldInfo)member).GetValue(target);
            }
            catch (Exception ex)
            {
                // A game getter can throw on a half-built object. That is the game's business,
                // never the trace's, so record why and print a marker instead.
                TraceDiagnostics.Report(
                    $"TRACE: reading {member.DeclaringType?.Name}.{member.Name} for the trace threw", ex);
                return "<error>";
            }
        }

        /// <summary>Resolves (and caches) the identity members of a type, in <see cref="IdentityMembers"/> order.</summary>
        private static MemberInfo[] IdentityMembersFor(Type type)
        {
            lock (CacheGate)
            {
                if (IdentityCache.TryGetValue(type, out var cached))
                {
                    return cached;
                }
            }

            var found = new List<MemberInfo>(MaxIdentityMembers);
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                foreach (var name in IdentityMembers)
                {
                    if (found.Count >= MaxIdentityMembers)
                    {
                        break;
                    }

                    var property = type.GetProperty(name, flags);
                    if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                    {
                        found.Add(property);
                        continue;
                    }

                    var field = type.GetField(name, flags);
                    if (field != null)
                    {
                        found.Add(field);
                    }
                }
            }
            catch (Exception ex)
            {
                // Ambiguous match, a type that fails to load its members, an obfuscated type:
                // treat it as having no identity rather than letting the game call fail.
                TraceDiagnostics.Report($"TRACE: could not inspect {type.FullName} for identity members", ex);
                found.Clear();
            }

            var members = found.ToArray();
            lock (CacheGate)
            {
                IdentityCache[type] = members;
            }

            return members;
        }

        private static bool IsScalar(Type type) =>
            type.IsPrimitive
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid);

        private string FormatScalar(object value)
        {
            // Lower case, so a bool in the trace reads the same as a bool in the JSON around it.
            if (value is bool flag)
            {
                return flag ? "true" : "false";
            }

            var text = value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();

            return Truncate(text ?? "null");
        }

        /// <summary>Type name without its namespace and without the CLR's generic arity suffix.</summary>
        public static string FriendlyTypeName(Type type)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        private string Truncate(string text) =>
            text.Length <= _maxStringLength ? text : text.Substring(0, _maxStringLength) + Ellipsis;

        /// <summary>Clears the identity-member cache. Only used by tests.</summary>
        public static void ResetCache()
        {
            lock (CacheGate)
            {
                IdentityCache.Clear();
            }
        }
    }
}
