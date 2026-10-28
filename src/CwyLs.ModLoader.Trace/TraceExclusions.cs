using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>Operating systems a <see cref="TraceExclusion"/> can be declared for.</summary>
    [Flags]
    internal enum TracePlatforms
    {
        /// <summary>No platform, or one this table does not know.</summary>
        None = 0,

        /// <summary>The Windows player.</summary>
        Windows = 1,

        /// <summary>The macOS player.</summary>
        MacOS = 2,

        /// <summary>The Linux player (not shipped by the game today).</summary>
        Linux = 4,

        /// <summary>Every platform above.</summary>
        All = Windows | MacOS | Linux,
    }

    /// <summary>
    /// One game type the trace must never patch on some platforms, and the reason, which is printed
    /// once at startup. Exclusion is per type, not per method: the hazards it exists for belong to
    /// the type (its static constructor), so skipping one method and patching its neighbour would
    /// trigger the same failure.
    /// </summary>
    internal sealed class TraceExclusion
    {
        /// <summary>Creates an exclusion.</summary>
        /// <param name="typeFullName">Reflection full name of the type, e.g. <c>NestopiSystem.ScreenSystem</c>.</param>
        /// <param name="platforms">Where the type must not be patched; must not be <see cref="TracePlatforms.None"/>.</param>
        /// <param name="reason">Why, as one clause; printed after "skipped ... on &lt;platform&gt;: ".</param>
        /// <exception cref="ArgumentException">A name or the reason is blank, or no platform is given.</exception>
        internal TraceExclusion(string typeFullName, TracePlatforms platforms, string reason)
        {
            if (string.IsNullOrWhiteSpace(typeFullName))
            {
                throw new ArgumentException("must not be null or blank", nameof(typeFullName));
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("must not be null or blank", nameof(reason));
            }

            if ((platforms & TracePlatforms.All) == TracePlatforms.None)
            {
                throw new ArgumentException("an exclusion must name at least one platform", nameof(platforms));
            }

            TypeFullName = typeFullName;
            Platforms = platforms & TracePlatforms.All;
            Reason = reason;
        }

        /// <summary>Reflection full name of the excluded type.</summary>
        internal string TypeFullName { get; }

        /// <summary>The platforms on which the type is not patched.</summary>
        internal TracePlatforms Platforms { get; }

        /// <summary>Why the type is not patched.</summary>
        internal string Reason { get; }

        /// <summary>True when this exclusion applies on <paramref name="platform"/>.</summary>
        /// <param name="platform">The running platform; <see cref="TracePlatforms.None"/> never matches.</param>
        internal bool AppliesTo(TracePlatforms platform) => (Platforms & platform) != TracePlatforms.None;
    }

    /// <summary>
    /// The declared per-platform trace exclusions, and the Unity-free logic around them, so the
    /// decision can be unit-tested and the offline checker (<c>tools/seamcheck</c>, which compiles
    /// this file in) can verify each entry against the game assemblies of its platform.
    /// </summary>
    internal static class TraceExclusions
    {
        /// <summary>
        /// Windows <c>NestopiSystem.ScreenSystem</c> (the always-on-top window code). Its static
        /// constructor (<c>.cctor</c> IL_0015..IL_001a in the Windows 1.17.3 Assembly-CSharp) calls
        /// <c>ProjectLifetimeScope.get_GameObject()</c>, i.e. <c>inst.gameObject</c> with <c>inst</c>
        /// still null at plugin load. HarmonyX pins the original through MonoMod, which JIT-compiles
        /// it (<c>RuntimeHelpers.PrepareMethod</c>), and Mono runs a precise-init type's static
        /// constructor when one of its methods is compiled: the constructor throws, HarmonyX reports
        /// "IL Compile Error (unknown location)", and the type's initializer stays failed for the
        /// session, so the game's own <c>SettingService.Setup</c> and <c>SettingData.SaveReady</c>
        /// would throw on it. The macOS <c>ScreenSystem</c> constructor only builds a
        /// <c>ReactiveProperty</c>, so macOS keeps tracing it.
        /// </summary>
        internal static readonly TraceExclusion WindowsScreenSystem = new TraceExclusion(
            "NestopiSystem.ScreenSystem",
            TracePlatforms.Windows,
            "its static constructor dereferences ProjectLifetimeScope.GameObject, which does not exist at plugin load; "
            + "patching compiles the method, Mono then runs that constructor early, it throws, and the type would stay "
            + "unusable for the session (the game's always-on-top and settings save call it)");

        /// <summary>Every declared exclusion, in the order they are checked.</summary>
        internal static IReadOnlyList<TraceExclusion> Declared { get; } = new[] { WindowsScreenSystem };

        /// <summary>
        /// Maps a Unity <c>RuntimePlatform</c> name (<c>Application.platform.ToString()</c>) to a
        /// <see cref="TracePlatforms"/> value: <c>WindowsPlayer</c>/<c>WindowsEditor</c> to Windows,
        /// <c>OSXPlayer</c>/<c>OSXEditor</c> to macOS, <c>LinuxPlayer</c>/<c>LinuxEditor</c> to Linux,
        /// anything else to <see cref="TracePlatforms.None"/>, so no exclusion applies.
        /// </summary>
        /// <param name="runtimePlatformName">The enum value's name; null or blank maps to None.</param>
        internal static TracePlatforms FromUnityPlatformName(string? runtimePlatformName)
        {
            if (string.IsNullOrWhiteSpace(runtimePlatformName))
            {
                return TracePlatforms.None;
            }

            if (runtimePlatformName!.StartsWith("Windows", StringComparison.Ordinal))
            {
                return TracePlatforms.Windows;
            }

            if (runtimePlatformName.StartsWith("OSX", StringComparison.Ordinal))
            {
                return TracePlatforms.MacOS;
            }

            if (runtimePlatformName.StartsWith("Linux", StringComparison.Ordinal))
            {
                return TracePlatforms.Linux;
            }

            return TracePlatforms.None;
        }

        /// <summary>Human name of one platform, as printed in the skip line.</summary>
        /// <param name="platform">A single platform flag.</param>
        internal static string Name(TracePlatforms platform)
        {
            switch (platform)
            {
                case TracePlatforms.Windows:
                    return "Windows";
                case TracePlatforms.MacOS:
                    return "macOS";
                case TracePlatforms.Linux:
                    return "Linux";
                default:
                    return platform.ToString();
            }
        }

        /// <summary>
        /// The exclusion that applies to a type on a platform, or null. Type names are compared
        /// ordinally, exactly as reflection spells them (nested types with <c>+</c>).
        /// </summary>
        /// <param name="table">The exclusions to search, normally <see cref="Declared"/>.</param>
        /// <param name="platform">The running platform.</param>
        /// <param name="typeFullName">The type about to be patched; null never matches.</param>
        /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
        internal static TraceExclusion? Find(IReadOnlyList<TraceExclusion> table, TracePlatforms platform, string? typeFullName)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            if (typeFullName == null)
            {
                return null;
            }

            foreach (var exclusion in table)
            {
                if (exclusion != null
                    && exclusion.AppliesTo(platform)
                    && string.Equals(exclusion.TypeFullName, typeFullName, StringComparison.Ordinal))
                {
                    return exclusion;
                }
            }

            return null;
        }

        /// <summary>
        /// The one INFO line for an excluded type, naming the methods the filter would have patched:
        /// <c>skipped NestopiSystem.ScreenSystem.{BringWindowToTopAlways, ResetWindowOrder, Initialize}
        /// on Windows: &lt;reason&gt;</c>. Returns null when no method would have been patched, since
        /// then nothing was actually skipped and there is nothing to say.
        /// </summary>
        /// <param name="exclusion">The exclusion that applied.</param>
        /// <param name="platform">The running platform.</param>
        /// <param name="skippedMethods">Names of the methods the trace filter accepted, in declaration order.</param>
        /// <exception cref="ArgumentNullException"><paramref name="exclusion"/> or <paramref name="skippedMethods"/> is null.</exception>
        internal static string? FormatSkipped(
            TraceExclusion exclusion,
            TracePlatforms platform,
            IReadOnlyList<string> skippedMethods)
        {
            if (exclusion == null)
            {
                throw new ArgumentNullException(nameof(exclusion));
            }

            if (skippedMethods == null)
            {
                throw new ArgumentNullException(nameof(skippedMethods));
            }

            if (skippedMethods.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder("skipped ").Append(exclusion.TypeFullName).Append('.');
            if (skippedMethods.Count == 1)
            {
                sb.Append(skippedMethods[0]);
            }
            else
            {
                sb.Append('{');
                for (var i = 0; i < skippedMethods.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(skippedMethods[i]);
                }

                sb.Append('}');
            }

            return sb.Append(" on ").Append(Name(platform)).Append(": ").Append(exclusion.Reason).ToString();
        }
    }
}
