using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Core.Trace;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>A deprecated member as its author declared it: where the attribute sits and what it says.</summary>
    public sealed class DeprecatedMemberInfo
    {
        internal DeprecatedMemberInfo(string name, MemberInfo declaredOn, CwyDeprecatedAttribute attribute)
        {
            Name = name;
            DeclaredOn = declaredOn;
            Attribute = attribute;
        }

        /// <summary>The member as authors know it, e.g. <c>ICwyModSatone.SayLegacy</c> or <c>Facet.OldProp</c>.</summary>
        public string Name { get; }

        /// <summary>The member (or type) that carries the attribute: a property, event, interface member, method or class.</summary>
        public MemberInfo DeclaredOn { get; }

        /// <summary>The attribute.</summary>
        public CwyDeprecatedAttribute Attribute { get; }
    }

    /// <summary>
    /// Finds the <see cref="CwyDeprecatedAttribute"/> that governs a running method.
    /// <c>MethodBase.GetCurrentMethod()</c> inside a deprecated property is <c>get_X</c>, inside
    /// an event <c>add_X</c>, inside an interface implementation the class's method - none of which
    /// carries the attribute the author put on the property, the event or the interface member. The
    /// search order: the member itself; an accessor's property or event; the interface members it
    /// implements (and their property or event); then the declaring type and the interfaces.
    /// Results are cached per member; thread-safe.
    /// </summary>
    public static class DeprecatedMembers
    {
        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly object Gate = new object();
        private static readonly Dictionary<MemberInfo, DeprecatedMemberInfo?> Cache = new Dictionary<MemberInfo, DeprecatedMemberInfo?>();

        /// <summary>The deprecation that governs <paramref name="member"/>, or null when there is none.</summary>
        /// <param name="member">A member, typically <c>MethodBase.GetCurrentMethod()</c> inside the deprecated one.</param>
        /// <returns>The attribute and the name to report; null when nothing is deprecated.</returns>
        public static DeprecatedMemberInfo? Find(MemberInfo? member)
        {
            if (member is null)
            {
                return null;
            }

            lock (Gate)
            {
                if (Cache.TryGetValue(member, out var cached))
                {
                    return cached;
                }
            }

            var found = Search(member);
            lock (Gate)
            {
                Cache[member] = found;
            }

            return found;
        }

        private static DeprecatedMemberInfo? Search(MemberInfo member)
        {
            var direct = OnMemberOrOwner(member);
            if (direct != null)
            {
                return direct;
            }

            if (member is MethodInfo method)
            {
                foreach (var interfaceMethod in InterfaceMethods(method))
                {
                    var viaInterface = OnMemberOrOwner(interfaceMethod);
                    if (viaInterface != null)
                    {
                        return viaInterface;
                    }
                }

                foreach (var interfaceMethod in InterfaceMethods(method))
                {
                    var onInterface = On(interfaceMethod.DeclaringType, interfaceMethod);
                    if (onInterface != null)
                    {
                        return onInterface;
                    }
                }
            }

            return On(member.DeclaringType, member);
        }

        /// <summary>The attribute on the member itself, or on the property or event an accessor belongs to.</summary>
        private static DeprecatedMemberInfo? OnMemberOrOwner(MemberInfo member)
        {
            var own = On(member, member);
            if (own != null)
            {
                return own;
            }

            if (member is MethodInfo accessor && accessor.IsSpecialName && accessor.DeclaringType != null)
            {
                var owner = AccessorOwner(accessor);
                if (owner != null)
                {
                    return On(owner, owner);
                }
            }

            return null;
        }

        /// <summary>The attribute on <paramref name="carrier"/>, reported under <paramref name="named"/>'s name.</summary>
        private static DeprecatedMemberInfo? On(MemberInfo? carrier, MemberInfo named)
        {
            if (carrier is null)
            {
                return null;
            }

            var attribute = carrier.GetCustomAttribute<CwyDeprecatedAttribute>(inherit: false);
            if (attribute is null)
            {
                return null;
            }

            return new DeprecatedMemberInfo(NameOf(carrier is Type ? named : carrier), carrier, attribute);
        }

        private static string NameOf(MemberInfo member)
        {
            var name = member.Name;
            var dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                // An explicit interface implementation is named "Namespace.IFacet.OldCall".
                name = name.Substring(dot + 1);
            }

            if (member is MethodInfo accessor && accessor.IsSpecialName && accessor.DeclaringType != null && AccessorOwner(accessor) is MemberInfo owner)
            {
                name = owner.Name;
            }

            return member.DeclaringType is null ? name : member.DeclaringType.Name + "." + name;
        }

        /// <summary>The property or event whose accessor <paramref name="accessor"/> is, or null.</summary>
        private static MemberInfo? AccessorOwner(MethodInfo accessor)
        {
            var type = accessor.DeclaringType!;
            foreach (var property in type.GetProperties(Declared))
            {
                if (Same(property.GetGetMethod(true), accessor) || Same(property.GetSetMethod(true), accessor))
                {
                    return property;
                }
            }

            foreach (var @event in type.GetEvents(Declared))
            {
                if (Same(@event.GetAddMethod(true), accessor) || Same(@event.GetRemoveMethod(true), accessor) ||
                    Same(@event.GetRaiseMethod(true), accessor))
                {
                    return @event;
                }
            }

            return null;
        }

        /// <summary>The interface members <paramref name="method"/> implements.</summary>
        private static List<MethodInfo> InterfaceMethods(MethodInfo method)
        {
            var found = new List<MethodInfo>();
            var type = method.DeclaringType;
            if (type is null || type.IsInterface)
            {
                return found;
            }

            foreach (var @interface in type.GetInterfaces())
            {
                InterfaceMapping map;
                try
                {
                    map = type.GetInterfaceMap(@interface);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException)
                {
                    // Generic or runtime-built interfaces the map cannot describe: nothing to find there.
                    TraceDiagnostics.Report("deprecations: no interface map of " + type.FullName + " for " + @interface.FullName, ex);
                    continue;
                }

                for (var i = 0; i < map.TargetMethods.Length; i++)
                {
                    if (Same(map.TargetMethods[i], method))
                    {
                        found.Add(map.InterfaceMethods[i]);
                    }
                }
            }

            return found;
        }

        private static bool Same(MethodInfo? a, MethodInfo? b) =>
            a != null && b != null && a.MetadataToken == b.MetadataToken && a.Module == b.Module;
    }
}
