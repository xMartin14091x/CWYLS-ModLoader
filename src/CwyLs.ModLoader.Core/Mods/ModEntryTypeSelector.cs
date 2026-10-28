using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// What the loader knows about one type in a mod assembly, reduced to the facts the entry
    /// rule needs. The runtime builds these from <c>System.Type</c>; tests build them by hand.
    /// </summary>
    public sealed class ModTypeInfo
    {
        /// <summary>Creates a description.</summary>
        /// <param name="fullName">The type's full name.</param>
        /// <param name="isModClass">True when it derives from the framework's <c>CwyMod</c>.</param>
        /// <param name="isAbstract">True when it is abstract.</param>
        /// <param name="isGenericDefinition">True when it is an open generic type.</param>
        /// <param name="hasParameterlessConstructor">True when it can be created without arguments.</param>
        public ModTypeInfo(string fullName, bool isModClass, bool isAbstract, bool isGenericDefinition, bool hasParameterlessConstructor)
        {
            FullName = fullName ?? string.Empty;
            IsModClass = isModClass;
            IsAbstract = isAbstract;
            IsGenericDefinition = isGenericDefinition;
            HasParameterlessConstructor = hasParameterlessConstructor;
        }

        /// <summary>The type's full name.</summary>
        public string FullName { get; }

        /// <summary>True when it derives from <c>CwyMod</c>.</summary>
        public bool IsModClass { get; }

        /// <summary>True when it is abstract.</summary>
        public bool IsAbstract { get; }

        /// <summary>True when it is an open generic type.</summary>
        public bool IsGenericDefinition { get; }

        /// <summary>True when it can be created without arguments.</summary>
        public bool HasParameterlessConstructor { get; }

        /// <summary>True when the type could be a mod's entry: a concrete, closed <c>CwyMod</c> subclass.</summary>
        public bool IsEntryCandidate => IsModClass && !IsAbstract && !IsGenericDefinition;
    }

    /// <summary>The entry rule's verdict for one assembly.</summary>
    public sealed class ModEntrySelection
    {
        private ModEntrySelection(int index, ModSkipReason? reason, string detail)
        {
            Index = index;
            Reason = reason;
            Detail = detail;
        }

        /// <summary>Index of the chosen type in the list given, or -1.</summary>
        public int Index { get; }

        /// <summary>Why no type was chosen; null on success.</summary>
        public ModSkipReason? Reason { get; }

        /// <summary>What to tell the author. Never null.</summary>
        public string Detail { get; }

        /// <summary>True when exactly one usable entry type was found.</summary>
        public bool Found => Reason is null;

        internal static ModEntrySelection Chosen(int index, string detail) => new ModEntrySelection(index, null, detail);

        internal static ModEntrySelection Refused(ModSkipReason reason, string detail) => new ModEntrySelection(-1, reason, detail);
    }

    /// <summary>
    /// SMAPI's entry rule: an entry DLL contains exactly one concrete class deriving from
    /// <c>CwyMod</c>, and the loader creates it with its parameterless constructor.
    /// </summary>
    public static class ModEntryTypeSelector
    {
        /// <summary>Picks the entry type.</summary>
        /// <param name="types">Every type the assembly exposes (loadable ones).</param>
        /// <param name="assemblyName">The DLL's name, for the messages.</param>
        public static ModEntrySelection Select(IReadOnlyList<ModTypeInfo>? types, string assemblyName)
        {
            var name = string.IsNullOrWhiteSpace(assemblyName) ? "the entry DLL" : assemblyName;
            var candidates = new List<int>();
            var near = new List<string>();

            if (types != null)
            {
                for (var i = 0; i < types.Count; i++)
                {
                    var type = types[i];
                    if (type is null || !type.IsModClass)
                    {
                        continue;
                    }

                    if (type.IsEntryCandidate)
                    {
                        candidates.Add(i);
                    }
                    else
                    {
                        near.Add(type.FullName + (type.IsAbstract ? " (abstract)" : " (open generic)"));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                var detail = name + " has no concrete class deriving from CwyMod";
                if (near.Count > 0)
                {
                    detail += "; found only " + string.Join(", ", near);
                }

                return ModEntrySelection.Refused(ModSkipReason.NoEntryType, detail);
            }

            if (candidates.Count > 1)
            {
                var sb = new StringBuilder(name).Append(" has ").Append(candidates.Count)
                    .Append(" classes deriving from CwyMod (");
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(types![candidates[i]].FullName);
                }

                sb.Append("); a mod has exactly one entry class");
                return ModEntrySelection.Refused(ModSkipReason.MultipleEntryTypes, sb.ToString());
            }

            var chosen = types![candidates[0]];
            if (!chosen.HasParameterlessConstructor)
            {
                return ModEntrySelection.Refused(
                    ModSkipReason.NoEntryType,
                    chosen.FullName + " in " + name + " needs a parameterless constructor");
            }

            return ModEntrySelection.Chosen(candidates[0], chosen.FullName);
        }
    }
}
