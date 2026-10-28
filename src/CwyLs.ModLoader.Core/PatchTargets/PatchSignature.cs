using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core
{
    /// <summary>
    /// Builds the canonical signature strings used by <see cref="PatchTarget.ExpectedSignature"/>
    /// and by resolver results, e.g. <c>UniTask Load()</c> or <c>static int Count</c>.
    ///
    /// The canonical form deliberately drops namespaces and uses C# keyword aliases, so a target
    /// table reads the way the game's members are written in C#. Both sides of a comparison go
    /// through this class, so "expected" and "actual" are always normalised identically.
    /// </summary>
    public static class PatchSignature
    {
        /// <summary>Rendered in place of a type name that could not be determined.</summary>
        public const string UnknownType = "?";

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "System.Void", "void" },
            { "System.Object", "object" },
            { "System.String", "string" },
            { "System.Boolean", "bool" },
            { "System.Byte", "byte" },
            { "System.SByte", "sbyte" },
            { "System.Char", "char" },
            { "System.Int16", "short" },
            { "System.UInt16", "ushort" },
            { "System.Int32", "int" },
            { "System.UInt32", "uint" },
            { "System.Int64", "long" },
            { "System.UInt64", "ulong" },
            { "System.Single", "float" },
            { "System.Double", "double" },
            { "System.Decimal", "decimal" }
        };

        /// <summary>
        /// Shortens a type name for display: <c>System.Int32</c> becomes <c>int</c>,
        /// <c>Bulbul.HeroineService+AnimationType</c> becomes <c>HeroineService.AnimationType</c>,
        /// and every identifier inside a generic argument list is shortened the same way, so
        /// <c>R3.Observable&lt;R3.Unit&gt;</c> becomes <c>Observable&lt;Unit&gt;</c>.
        ///
        /// <para>The input must be a <em>reflection</em> type name, where nesting is written with
        /// <c>+</c>. A dotted prefix is always read as a namespace, so this is not idempotent over
        /// its own output for a nested type - <c>HeroineService.AnimationType</c> would shorten
        /// again to <c>AnimationType</c>. Nothing calls it twice: signatures are built once from
        /// reflection and then compared as plain strings.</para>
        /// </summary>
        /// <param name="typeName">A reflection type name, possibly with generic arguments in angle brackets.</param>
        public static string ShortName(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return UnknownType;
            }

            var result = new StringBuilder(typeName!.Length);
            var token = new StringBuilder();
            foreach (var c in typeName)
            {
                if (c == '<' || c == '>' || c == ',' || c == ' ')
                {
                    AppendShortToken(result, token);
                    result.Append(c);
                }
                else
                {
                    token.Append(c);
                }
            }

            AppendShortToken(result, token);
            return result.ToString();
        }

        /// <summary>Canonical form of a method: <c>[static ]&lt;return&gt; &lt;name&gt;(&lt;params&gt;)</c>.</summary>
        /// <param name="returnTypeName">Return type name; shortened by <see cref="ShortName"/>.</param>
        /// <param name="memberName">Method name.</param>
        /// <param name="parameterTypeNames">Parameter type names in declaration order; may be empty.</param>
        /// <param name="isStatic">Whether the method is static.</param>
        public static string ForMethod(
            string? returnTypeName,
            string memberName,
            IEnumerable<string>? parameterTypeNames,
            bool isStatic)
        {
            var sb = new StringBuilder();
            if (isStatic)
            {
                sb.Append("static ");
            }

            sb.Append(ShortName(returnTypeName)).Append(' ').Append(memberName).Append('(');

            if (parameterTypeNames != null)
            {
                var first = true;
                foreach (var parameter in parameterTypeNames)
                {
                    if (!first)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(ShortName(parameter));
                    first = false;
                }
            }

            return sb.Append(')').ToString();
        }

        /// <summary>Canonical form of a field: <c>[static ]&lt;type&gt; &lt;name&gt;</c>.</summary>
        /// <param name="fieldTypeName">Field type name; shortened by <see cref="ShortName"/>.</param>
        /// <param name="memberName">Field name.</param>
        /// <param name="isStatic">Whether the field is static (enum constants are).</param>
        public static string ForField(string? fieldTypeName, string memberName, bool isStatic)
        {
            return (isStatic ? "static " : string.Empty) + ShortName(fieldTypeName) + " " + memberName;
        }

        /// <summary>
        /// Canonical form of a property: <c>[static ]&lt;type&gt; &lt;name&gt; { get; set; }</c>,
        /// with only the accessors that actually exist.
        /// </summary>
        /// <param name="propertyTypeName">Property type name; shortened by <see cref="ShortName"/>.</param>
        /// <param name="memberName">Property name.</param>
        /// <param name="canRead">Whether the property has a getter.</param>
        /// <param name="canWrite">Whether the property has a setter.</param>
        /// <param name="isStatic">Whether the property's accessors are static.</param>
        public static string ForProperty(
            string? propertyTypeName,
            string memberName,
            bool canRead,
            bool canWrite,
            bool isStatic)
        {
            var accessors = new StringBuilder("{");
            if (canRead)
            {
                accessors.Append(" get;");
            }

            if (canWrite)
            {
                accessors.Append(" set;");
            }

            accessors.Append(" }");

            return (isStatic ? "static " : string.Empty)
                + ShortName(propertyTypeName) + " " + memberName + " " + accessors;
        }

        private static void AppendShortToken(StringBuilder result, StringBuilder token)
        {
            if (token.Length == 0)
            {
                return;
            }

            result.Append(ShortenToken(token.ToString()));
            token.Length = 0;
        }

        /// <summary>
        /// Shortens one identifier run. Trailing array/by-ref/pointer decoration is preserved,
        /// the arity tick of a generic type definition is dropped, and a nested type keeps its
        /// declaring type ("Outer.Inner") because that is how the game source reads.
        /// </summary>
        private static string ShortenToken(string token)
        {
            var end = token.Length;
            while (end > 0 && (token[end - 1] == '[' || token[end - 1] == ']' || token[end - 1] == '&' || token[end - 1] == '*'))
            {
                end--;
            }

            var core = token.Substring(0, end);
            var suffix = token.Substring(end);

            if (core.Length == 0)
            {
                return token;
            }

            if (Aliases.TryGetValue(core, out var alias))
            {
                return alias + suffix;
            }

            var parts = core.Split('+');
            var sb = new StringBuilder();
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

                var tick = part.IndexOf('`');
                if (tick >= 0)
                {
                    part = part.Substring(0, tick);
                }

                if (i == 0)
                {
                    var dot = part.LastIndexOf('.');
                    if (dot >= 0)
                    {
                        part = part.Substring(dot + 1);
                    }
                }
                else
                {
                    sb.Append('.');
                }

                sb.Append(part);
            }

            return sb.Append(suffix).ToString();
        }
    }
}
