using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// The runtime half of the <see cref="CwyDeprecatedAttribute"/> convention: a deprecated
    /// member calls <see cref="Notify(MemberInfo)"/>, which finds the calling mod and writes one
    /// line per mod per member per session - trace file only for
    /// <see cref="DeprecationStage.Notice"/>, a <c>MODS</c> console line for
    /// <see cref="DeprecationStage.Info"/>, a <c>MODS</c> warning for
    /// <see cref="DeprecationStage.PendingRemoval"/> (SMAPI's levels).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attribute is found where the author put it - on the property of a
    /// <c>get_X</c>, the event of an <c>add_X</c>, the interface member a class method implements
    /// (<see cref="DeprecatedMembers"/>) - and the stack is walked once per member and calling mod
    /// assembly: later calls read one fixed frame and return. Mark a deprecated member
    /// <c>[MethodImpl(MethodImplOptions.NoInlining)]</c> so that frame is its caller.
    /// </para>
    /// </remarks>
    public static class CwyDeprecations
    {
        /// <summary>Stack depth, seen from a <c>Notify</c> overload, of the deprecated member's caller.</summary>
        private const int CallerDepth = 2;

        private static readonly DeprecationLedger Ledger = new DeprecationLedger();
        private static readonly object Gate = new object();
        private static readonly HashSet<KeyValuePair<string, Assembly>> Settled = new HashSet<KeyValuePair<string, Assembly>>();

        /// <summary>How many distinct (mod, member) notices have been written this session.</summary>
        public static int Count => Ledger.Count;

        /// <summary>Reports a call to a deprecated member, with its values given explicitly. Never throws.</summary>
        /// <param name="member">The member, as authors know it, e.g. <c>ICwyModSatone.SayLegacy</c>.</param>
        /// <param name="since">The API version that deprecated it.</param>
        /// <param name="stage">How loudly to announce it.</param>
        /// <param name="replacement">What to use instead, or null.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Notify(string member, string since, DeprecationStage stage, string? replacement = null)
        {
            try
            {
                Report(member ?? string.Empty, since, stage, replacement, ImmediateCaller());
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("MODS could not report a deprecated call to " + member + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Reports a call to the deprecated member <paramref name="member"/>, reading its
        /// <see cref="CwyDeprecatedAttribute"/> - on the member, its property or event, the interface
        /// member it implements, or its type. Does nothing when there is none. Never throws.
        /// </summary>
        /// <param name="member">The deprecated member, normally <c>MethodBase.GetCurrentMethod()</c>.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Notify(MemberInfo member)
        {
            try
            {
                var info = DeprecatedMembers.Find(member);
                if (info is null)
                {
                    return;
                }

                Report(info.Name, info.Attribute.Since, info.Attribute.Stage, info.Attribute.Replacement, ImmediateCaller());
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    "MODS could not report a deprecated call to " + member?.Name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The assembly of the method that called the deprecated member: frame
        /// <see cref="CallerDepth"/> above the <c>Notify</c> overload that calls this (which is itself
        /// one more frame, hence the +1). Null when the frame cannot be read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Assembly? ImmediateCaller() =>
            new StackFrame(CallerDepth + 1, false).GetMethod()?.DeclaringType?.Assembly;

        private static void Report(string member, string since, DeprecationStage stage, string? replacement, Assembly? caller)
        {
            string? modId;
            if (caller != null && ModRuntime.TryFindByAssembly(caller, out var direct))
            {
                // The common case: a mod calls the member itself. Once per (member, assembly),
                // with no stack walk at all afterwards.
                lock (Gate)
                {
                    if (!Settled.Add(new KeyValuePair<string, Assembly>(member, caller)))
                    {
                        return;
                    }
                }

                modId = direct;
            }
            else
            {
                // A framework wrapper or a plugin called it: the mod, if any, is further up.
                modId = CallingMod();
            }

            if (!Ledger.ShouldReport(modId, member))
            {
                return;
            }

            var line = DeprecationLedger.Describe(modId, member, since, stage, replacement);
            var fields = new (string Key, object? Value)[]
            {
                ("mod", modId ?? DeprecationLedger.UnknownCaller),
                ("member", member),
                ("since", since),
                ("stage", stage.ToString()),
                ("replacement", replacement),
            };

            switch (stage)
            {
                case DeprecationStage.Notice:
                    ConsoleEvents.File(ModConsoleLines.LoaderSystem, "deprecated", Append(fields, ("line", line)));
                    break;
                case DeprecationStage.Info:
                    ConsoleEvents.Emit(ModConsoleLines.LoaderSystem, "deprecated", ModConsoleLines.ModsTag, line, null, fields);
                    break;
                default:
                    ConsoleEvents.EmitAt(
                        ModConsoleLines.LoaderSystem, "deprecated", ModConsoleLines.ModsTag, ConsoleLevel.Warn, line, null, fields);
                    break;
            }
        }

        /// <summary>The first loaded mod assembly on the calling stack, or null. Skips its own frame.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string? CallingMod()
        {
            var frames = new StackTrace(1, false).GetFrames();
            if (frames is null)
            {
                return null;
            }

            foreach (var frame in frames)
            {
                var assembly = frame?.GetMethod()?.DeclaringType?.Assembly;
                if (ModRuntime.TryFindByAssembly(assembly, out var modId))
                {
                    return modId;
                }
            }

            return null;
        }

        private static (string Key, object? Value)[] Append((string Key, object? Value)[] fields, (string Key, object? Value) extra)
        {
            var all = new (string Key, object? Value)[fields.Length + 1];
            Array.Copy(fields, all, fields.Length);
            all[fields.Length] = extra;
            return all;
        }
    }
}
