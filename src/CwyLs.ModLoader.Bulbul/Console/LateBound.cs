using System;
using System.Collections.Generic;
using System.Reflection;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// Reaches an API by name instead of by reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two of the console's command groups cannot be compiled against their targets. The game data
    /// bus lives in <c>CwyLs.ModLoader.Bulbul.Data</c>, which references this assembly - binding to it
    /// here would be a reference cycle. The master-data, voice, Satone say and Satone motion APIs
    /// were written in parallel with the console and may not exist in a given build at all.
    /// </para>
    /// <para>
    /// So each of those commands resolves its type the first time it is used and says "not
    /// available" when it is missing, instead of failing to compile or throwing at load. Lookups
    /// are cached, misses included: a console command runs at human speed, but it should not walk
    /// every loaded assembly twice a keystroke.
    /// </para>
    /// </remarks>
    internal static class LateBound
    {
        private const BindingFlags StaticMember =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Type?> Types = new Dictionary<string, Type?>(StringComparer.Ordinal);

        /// <summary>
        /// The type with this full name from any loaded assembly, or null when nothing declares it.
        /// </summary>
        /// <param name="fullName">Namespace-qualified type name, e.g. <c>CwyLs.ModLoader.Bulbul.Data.GameData</c>.</param>
        internal static Type? Find(string fullName)
        {
            lock (Gate)
            {
                if (Types.TryGetValue(fullName, out var cached))
                {
                    return cached;
                }
            }

            var found = Search(fullName);

            lock (Gate)
            {
                Types[fullName] = found;
            }

            return found;
        }

        /// <summary>The line a command prints when its API is not in this build.</summary>
        /// <param name="fullName">The type that was looked for.</param>
        /// <param name="because">What that type provides, e.g. "Satone.Say".</param>
        internal static string NotAvailable(string fullName, string because) =>
            "not available: " + because + " - " + fullName + " is not loaded in this build";

        /// <summary>
        /// Reads a public static property or field.
        /// </summary>
        /// <param name="fullName">The declaring type's full name.</param>
        /// <param name="member">The property or field name.</param>
        /// <param name="value">The value, or null.</param>
        /// <param name="problem">Empty on success, otherwise what went wrong.</param>
        /// <returns>True when the member was found and read.</returns>
        internal static bool TryRead(string fullName, string member, out object? value, out string problem)
        {
            value = null;
            problem = string.Empty;

            var type = Find(fullName);
            if (type is null)
            {
                problem = NotAvailable(fullName, member);
                return false;
            }

            try
            {
                var property = type.GetProperty(member, StaticMember);
                if (property != null)
                {
                    value = property.GetValue(null, null);
                    return true;
                }

                var field = type.GetField(member, StaticMember);
                if (field != null)
                {
                    value = field.GetValue(null);
                    return true;
                }

                problem = fullName + " has no static '" + member + "'";
                return false;
            }
            catch (Exception ex)
            {
                // A property getter of another feature threw; that is its business, but the
                // console must say so rather than look empty.
                problem = member + " threw: " + ex.GetType().Name + ": " + Message(ex);
                CwyLsModLoader.Log.LogWarning("CONSOLE " + fullName + "." + problem);
                return false;
            }
        }

        /// <summary>
        /// Calls a public static method chosen by its exact parameter types.
        /// </summary>
        /// <param name="fullName">The declaring type's full name.</param>
        /// <param name="method">The method name.</param>
        /// <param name="parameterTypes">The parameter types, in order; an empty array for none.</param>
        /// <param name="arguments">The arguments, matching <paramref name="parameterTypes"/>.</param>
        /// <param name="result">The return value, or null for void.</param>
        /// <param name="problem">Empty on success, otherwise what went wrong.</param>
        /// <returns>True when the method was found and ran without throwing.</returns>
        internal static bool TryCall(
            string fullName,
            string method,
            Type[] parameterTypes,
            object?[] arguments,
            out object? result,
            out string problem)
        {
            result = null;
            problem = string.Empty;

            var type = Find(fullName);
            if (type is null)
            {
                problem = NotAvailable(fullName, method);
                return false;
            }

            MethodInfo? info;
            try
            {
                info = type.GetMethod(method, StaticMember, null, parameterTypes, null);
            }
            catch (Exception ex)
            {
                problem = "could not look up " + method + ": " + ex.GetType().Name + ": " + Message(ex);
                CwyLsModLoader.Log.LogWarning("CONSOLE " + fullName + "." + problem);
                return false;
            }

            if (info is null)
            {
                problem = fullName + " has no static " + method + "(" + Describe(parameterTypes) + ")";
                return false;
            }

            try
            {
                result = info.Invoke(null, arguments);
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException target && target.InnerException != null
                    ? target.InnerException
                    : ex;
                problem = method + " threw: " + inner.GetType().Name + ": " + Message(inner);
                CwyLsModLoader.Log.LogWarning("CONSOLE " + fullName + "." + problem);
                return false;
            }
        }

        /// <summary>
        /// Calls a public static method by name, filling in every parameter after the ones given
        /// with its declared default. For an API whose exact shape is not known at compile time -
        /// <c>Satone.Say(text, clip?, facial?, motion?, subtitleLang?)</c> is the case this exists
        /// for, where an exact-type lookup would miss a method that has grown optional parameters.
        /// </summary>
        /// <param name="fullName">The declaring type's full name.</param>
        /// <param name="method">The method name.</param>
        /// <param name="leadingArguments">The arguments that must match, in order.</param>
        /// <param name="result">The return value, or null for void.</param>
        /// <param name="problem">Empty on success, otherwise what went wrong.</param>
        /// <returns>True when a matching method was found and ran without throwing.</returns>
        internal static bool TryCallWithDefaults(
            string fullName,
            string method,
            object?[] leadingArguments,
            out object? result,
            out string problem)
        {
            result = null;
            problem = string.Empty;

            var type = Find(fullName);
            if (type is null)
            {
                problem = NotAvailable(fullName, method);
                return false;
            }

            MethodInfo? chosen = null;
            object?[]? arguments = null;

            try
            {
                foreach (var candidate in type.GetMethods(StaticMember))
                {
                    if (!string.Equals(candidate.Name, method, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = candidate.GetParameters();
                    if (parameters.Length < leadingArguments.Length)
                    {
                        continue;
                    }

                    var usable = true;
                    var values = new object?[parameters.Length];

                    for (var i = 0; i < parameters.Length; i++)
                    {
                        if (i < leadingArguments.Length)
                        {
                            var given = leadingArguments[i];
                            if (given != null && !parameters[i].ParameterType.IsInstanceOfType(given))
                            {
                                usable = false;
                                break;
                            }

                            values[i] = given;
                            continue;
                        }

                        if (!parameters[i].IsOptional)
                        {
                            usable = false;
                            break;
                        }

                        values[i] = parameters[i].HasDefaultValue
                            ? parameters[i].DefaultValue
                            : Fallback(parameters[i].ParameterType);
                    }

                    if (!usable)
                    {
                        continue;
                    }

                    chosen = candidate;
                    arguments = values;
                    break;
                }
            }
            catch (Exception ex)
            {
                problem = "could not look up " + method + ": " + ex.GetType().Name + ": " + Message(ex);
                CwyLsModLoader.Log.LogWarning("CONSOLE " + fullName + "." + problem);
                return false;
            }

            if (chosen is null || arguments is null)
            {
                problem = fullName + " has no usable static " + method + "(...)";
                return false;
            }

            try
            {
                result = chosen.Invoke(null, arguments);
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException target && target.InnerException != null
                    ? target.InnerException
                    : ex;
                problem = method + " threw: " + inner.GetType().Name + ": " + Message(inner);
                CwyLsModLoader.Log.LogWarning("CONSOLE " + fullName + "." + problem);
                return false;
            }
        }

        private static object? Fallback(Type parameterType) =>
            parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;

        private static Type? Search(string fullName)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? type;
                    try
                    {
                        type = assembly.GetType(fullName, false);
                    }
                    catch (Exception ex)
                    {
                        // A dynamic or half-loaded assembly can refuse the question; the others
                        // still answer it.
                        CwyLsModLoader.Log.LogDebug(
                            "CONSOLE could not search " + assembly.GetName().Name + ": " + ex.GetType().Name);
                        continue;
                    }

                    if (type != null)
                    {
                        return type;
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    "CONSOLE could not look for " + fullName + ": " + ex.GetType().Name + ": " + Message(ex));
            }

            return null;
        }

        private static string Describe(Type[] parameterTypes)
        {
            var names = new string[parameterTypes.Length];
            for (var i = 0; i < parameterTypes.Length; i++)
            {
                names[i] = parameterTypes[i].Name;
            }

            return string.Join(", ", names);
        }

        private static string Message(Exception ex) =>
            string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
    }
}
