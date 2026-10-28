using System;
using System.Reflection;
using CwyLs.ModLoader.Core.Trace;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// How the trace turns a reflected type and method into the value-only
    /// <see cref="MethodCandidate"/> that <see cref="TraceFilter"/> decides on. Unity-free and
    /// Harmony-free on purpose: <c>tools/seamcheck</c> compiles this file in and runs it over the
    /// game assemblies through a metadata-only load, so its preflight sees exactly the methods the
    /// in-game trace would patch.
    /// </summary>
    internal static class TraceCandidates
    {
        /// <summary>Game assemblies walked for methods to patch, by simple name.</summary>
        internal static readonly string[] TracedAssemblies = { "Assembly-CSharp", "AudioManager" };

        /// <summary>Binding flags the trace lists a type's methods with.</summary>
        internal const BindingFlags DeclaredMethods =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Whether the trace looks at a type at all: concrete classes only (structs, interfaces and
        /// enums are skipped), and never an open generic.
        /// </summary>
        /// <param name="type">The type.</param>
        internal static bool IsTraceableType(Type type) => type.IsClass && !type.ContainsGenericParameters;

        /// <summary>Builds the value-only description the filter works from.</summary>
        /// <param name="method">The method.</param>
        /// <param name="type">Its declaring type.</param>
        /// <param name="isInteractionClass">From <see cref="IsInteractionType"/>.</param>
        internal static MethodCandidate Describe(MethodInfo method, Type type, bool isInteractionClass) =>
            new MethodCandidate(
                type.Namespace,
                type.Name,
                method.Name,
                method.IsPublic,
                method.IsStatic,
                method.IsAbstract,
                method.ContainsGenericParameters || method.IsGenericMethodDefinition || type.ContainsGenericParameters,
                HasBody(method),
                type.IsInterface,
                isInteractionClass);

        /// <summary>
        /// Whether this type handles user input. Core cannot answer this because it would have to
        /// see UnityEngine.EventSystems, so the names come from Core and the reflection happens here.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="methods">Its declared methods, as listed with <see cref="DeclaredMethods"/>.</param>
        internal static bool IsInteractionType(Type type, MethodInfo[] methods)
        {
            try
            {
                foreach (var contract in type.GetInterfaces())
                {
                    if (TraceFilter.IsInteractionInterfaceName(contract.Name))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // A type whose interfaces will not load still gets the name-based rules below.
                TraceDiagnostics.Report($"TRACE: could not list the interfaces of {type.FullName}", ex);
            }

            foreach (var method in methods)
            {
                if (TraceFilter.IsInteractionMethodName(method.Name))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether the method has IL Harmony could patch (not abstract, extern or InternalCall).</summary>
        /// <param name="method">The method.</param>
        internal static bool HasBody(MethodInfo method)
        {
            if (method.IsAbstract)
            {
                return false;
            }

            if ((method.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Native)) != 0)
            {
                return false;
            }

            try
            {
                return method.GetMethodBody() != null;
            }
            catch (Exception ex)
            {
                // Some Mono method handles refuse to produce a body. Treat that as "not patchable".
                TraceDiagnostics.Report($"TRACE: could not read the body of {method.DeclaringType?.FullName}.{method.Name}", ex);
                return false;
            }
        }

        /// <summary>Signatures Harmony cannot build a patch for (pointer return or parameter types).</summary>
        /// <param name="method">The method.</param>
        internal static bool HasUnpatchableSignature(MethodInfo method)
        {
            if (method.ReturnType.IsPointer)
            {
                return true;
            }

            foreach (var parameter in method.GetParameters())
            {
                var type = parameter.ParameterType;
                if (type.IsPointer || (type.IsByRef && type.GetElementType()?.IsPointer == true))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
