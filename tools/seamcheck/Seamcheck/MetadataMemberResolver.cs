using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core;

namespace CWY.Seamcheck
{
    /// <summary>
    /// <see cref="IMemberResolver"/> over a <see cref="ManagedAssemblySet"/>: the offline twin of the
    /// game's <c>CwyLs.ModLoader.ReflectionMemberResolver</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything after "find the assembly" is a line-for-line mirror of the game-side resolver -
    /// same binding flags, same parameter matching, same wording - and signatures are rendered by the
    /// game's own <see cref="TypeNames"/> (called from CwyLs.ModLoader.dll, not copied), so a target that
    /// resolves here resolves in game and the <c>PATCH TARGET MISSING</c> text is the text the game
    /// would print. <c>ResolverParityTests</c> runs both resolvers over the same targets and fails on
    /// any difference, which is what keeps the mirror honest.
    /// </para>
    /// <para>
    /// The two differences are where the assemblies come from (a folder of files instead of the
    /// running AppDomain) and how "the type moved to another assembly" is found (every assembly in
    /// the folder instead of Harmony's AppDomain-wide <c>AccessTools.TypeByName</c>).
    /// </para>
    /// </remarks>
    internal sealed class MetadataMemberResolver : IMemberResolver
    {
        /// <summary>How many overloads are listed in the detail of a "method not found" result.</summary>
        private const int MaxListedOverloads = 6;

        private readonly ManagedAssemblySet _assemblies;
        private readonly ToolLog _log;

        /// <summary>Creates a resolver over one set of game assemblies.</summary>
        /// <param name="assemblies">The game's assemblies, metadata only.</param>
        /// <param name="log">Diagnostics.</param>
        internal MetadataMemberResolver(ManagedAssemblySet assemblies, ToolLog log)
        {
            _assemblies = assemblies ?? throw new ArgumentNullException(nameof(assemblies));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        public PatchTargetResult Resolve(PatchTarget target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var assembly = _assemblies.TryLoad(target.Assembly);
            if (assembly == null)
            {
                return PatchTargetResult.Missing(
                    target, "assembly '" + target.Assembly + "' is not loaded and could not be loaded");
            }

            var type = FindType(assembly, target.TypeName);
            if (type == null)
            {
                return PatchTargetResult.Missing(target, DescribeMissingType(target));
            }

            switch (target.MemberKind)
            {
                case MemberKind.Method:
                    return ResolveMethod(target, type);
                case MemberKind.Field:
                    return ResolveField(target, type);
                case MemberKind.Property:
                    return ResolveProperty(target, type);
                default:
                    return PatchTargetResult.Missing(target, "unsupported member kind " + target.MemberKind);
            }
        }

        /// <summary>Finds a type by reflection name in one assembly, or null (logged at debug).</summary>
        /// <param name="assembly">The assembly to look in.</param>
        /// <param name="typeName">Reflection name, nested types with <c>+</c>, generics with brackets.</param>
        internal Type? FindType(Assembly assembly, string typeName)
        {
            try
            {
                return assembly.GetType(typeName, throwOnError: false);
            }
            catch (Exception ex)
            {
                _log.Debug(
                    "type '" + typeName + "' could not be read from " + assembly.GetName().Name
                    + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Builds the detail for a type that is not where the table says it is, naming the assembly
        /// it moved to when another assembly in the folder declares it.
        /// </summary>
        private string DescribeMissingType(PatchTarget target)
        {
            Type? elsewhere = null;
            foreach (var candidate in _assemblies.LoadAll())
            {
                if (string.Equals(candidate.GetName().Name, target.Assembly, StringComparison.Ordinal))
                {
                    continue;
                }

                elsewhere = FindType(candidate, target.TypeName);
                if (elsewhere != null)
                {
                    break;
                }
            }

            if (elsewhere != null)
            {
                return "type '" + target.TypeName + "' is not in " + target.Assembly
                    + " any more; found in " + elsewhere.Assembly.GetName().Name;
            }

            return "type '" + target.TypeName + "' not found in " + target.Assembly;
        }

        private static BindingFlags FlagsFor(PatchTarget target) =>
            BindingFlags.Public | BindingFlags.NonPublic
            | (target.IsStatic ? BindingFlags.Static : BindingFlags.Instance);

        private static PatchTargetResult ResolveMethod(PatchTarget target, Type type)
        {
            var named = new List<MethodInfo>();
            foreach (var candidate in type.GetMethods(FlagsFor(target)))
            {
                if (string.Equals(candidate.Name, target.MemberName, StringComparison.Ordinal))
                {
                    named.Add(candidate);
                }
            }

            if (named.Count == 0)
            {
                return PatchTargetResult.Missing(
                    target,
                    (target.IsStatic ? "static " : "instance ") + "method '" + target.MemberName
                    + "' not found on " + target.TypeName);
            }

            var matches = new List<MethodInfo>();
            foreach (var candidate in named)
            {
                if (ParametersMatch(candidate.GetParameters(), target.ParameterTypeNames))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                return PatchTargetResult.Missing(
                    target,
                    "no overload of '" + target.MemberName + "' takes ("
                    + string.Join(", ", ShortNames(target.ParameterTypeNames)) + "); declared: "
                    + DescribeOverloads(named));
            }

            if (matches.Count > 1)
            {
                return PatchTargetResult.Missing(
                    target, matches.Count + " overloads of '" + target.MemberName + "' match the same parameter list");
            }

            return CompareSignature(target, TypeNames.Signature(matches[0]));
        }

        private static PatchTargetResult ResolveField(PatchTarget target, Type type)
        {
            var field = type.GetField(target.MemberName, FlagsFor(target));
            if (field == null)
            {
                return PatchTargetResult.Missing(
                    target,
                    (target.IsStatic ? "static " : "instance ") + "field '" + target.MemberName
                    + "' not found on " + target.TypeName);
            }

            return CompareSignature(target, TypeNames.Signature(field));
        }

        private static PatchTargetResult ResolveProperty(PatchTarget target, Type type)
        {
            var property = type.GetProperty(target.MemberName, FlagsFor(target));
            if (property == null)
            {
                return PatchTargetResult.Missing(
                    target,
                    (target.IsStatic ? "static " : "instance ") + "property '" + target.MemberName
                    + "' not found on " + target.TypeName);
            }

            return CompareSignature(target, TypeNames.Signature(property));
        }

        /// <summary>
        /// A member that exists but no longer has the declared shape is reported as missing, exactly
        /// as in game: a Harmony patch against the old signature would fail later.
        /// </summary>
        private static PatchTargetResult CompareSignature(PatchTarget target, string actual)
        {
            if (target.ExpectedSignature != null
                && !string.Equals(target.ExpectedSignature, actual, StringComparison.Ordinal))
            {
                return PatchTargetResult.Missing(
                    target, "signature changed: expected '" + target.ExpectedSignature + "', found '" + actual + "'");
            }

            return PatchTargetResult.Ok(target, actual);
        }

        /// <summary>
        /// Compares a method's parameters against the names in the table: the reflection full name
        /// or the readable generic form, as the game-side resolver does.
        /// </summary>
        private static bool ParametersMatch(ParameterInfo[] parameters, IReadOnlyList<string> expected)
        {
            if (parameters.Length != expected.Count)
            {
                return false;
            }

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;
                var want = expected[i];
                if (string.Equals(parameterType.FullName, want, StringComparison.Ordinal)
                    || string.Equals(TypeNames.Readable(parameterType), want, StringComparison.Ordinal))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static string[] ShortNames(IReadOnlyList<string> names)
        {
            var shortened = new string[names.Count];
            for (var i = 0; i < names.Count; i++)
            {
                shortened[i] = PatchSignature.ShortName(names[i]);
            }

            return shortened;
        }

        private static string DescribeOverloads(List<MethodInfo> methods)
        {
            var sb = new StringBuilder();
            var shown = Math.Min(methods.Count, MaxListedOverloads);
            for (var i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    sb.Append("; ");
                }

                sb.Append(TypeNames.Signature(methods[i]));
            }

            if (methods.Count > shown)
            {
                sb.Append("; +").Append(methods.Count - shown).Append(" more");
            }

            return sb.ToString();
        }
    }
}
