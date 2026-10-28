using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CwyLs.ModLoader.Core;
using HarmonyLib;

namespace CwyLs.ModLoader
{
    /// <summary>
    /// Resolves patch targets by reflection over the assemblies loaded into the game process.
    /// Everything is looked up by name, so this class - and the whole core - stays free of any
    /// reference to Assembly-CSharp: a target may name a type in an assembly we do not link against.
    /// </summary>
    public sealed class ReflectionMemberResolver : IMemberResolver
    {
        /// <summary>How many overloads are listed in the detail of a "method not found" result.</summary>
        private const int MaxListedOverloads = 6;

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
        public PatchTargetResult Resolve(PatchTarget target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var assembly = FindAssembly(target.Assembly);
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

        /// <summary>
        /// Finds a loaded assembly by simple name, falling back to <see cref="Assembly.Load(AssemblyName)"/>
        /// because Mono loads a game assembly only once something touches it - AudioManager.dll is
        /// typically still untouched when plugins Awake, and reporting that as a missing seam would
        /// be a lie.
        /// </summary>
        private static Assembly? FindAssembly(string simpleName)
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.Ordinal))
                {
                    return loaded;
                }
            }

            try
            {
                return Assembly.Load(new AssemblyName(simpleName));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    "assembly '" + simpleName + "' could not be loaded: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static Type? FindType(Assembly assembly, string typeName)
        {
            try
            {
                return assembly.GetType(typeName, throwOnError: false);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    "type '" + typeName + "' could not be read from " + assembly.GetName().Name
                    + ": " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Builds the detail for a type that is not where the table says it is. A type that moved
        /// to another assembly is much more useful to report than a bare "not found", so Harmony's
        /// whole-domain lookup is used purely to enrich the message.
        /// </summary>
        private static string DescribeMissingType(PatchTarget target)
        {
            Type? elsewhere = null;
            try
            {
                elsewhere = AccessTools.TypeByName(target.TypeName);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    "domain-wide lookup of '" + target.TypeName + "' failed: " + ex.GetType().Name + ": " + ex.Message);
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
        /// A member that exists but no longer has the declared shape is reported as missing:
        /// a Harmony patch against the old signature would fail later, further from the cause.
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
        /// Compares a method's parameters against the names in the table. A name matches either the
        /// reflection full name (<c>Bulbul.HeroineService+AnimationType</c>) or the readable generic
        /// form, so a table entry never has to spell an assembly-qualified generic argument list.
        /// Comparing names rather than resolved <see cref="Type"/>s also means a parameter type from
        /// an assembly that is not loaded yet cannot produce a false miss.
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
