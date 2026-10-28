using System;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// How loudly a deprecated member announces itself, after SMAPI's deprecation levels. Each
    /// mod that calls the member gets one line per session, at this level.
    /// </summary>
    public enum DeprecationStage
    {
        /// <summary>
        /// Will not be removed soon. The line goes to the trace file only, so players never see it;
        /// mod authors find it there.
        /// </summary>
        Notice = 0,

        /// <summary>A console line at normal level: authors should move off it in their next update.</summary>
        Info = 1,

        /// <summary>
        /// Will be removed at the next breaking version (a 0.x minor before 1.0, a major after): a
        /// console warning.
        /// </summary>
        PendingRemoval = 2,
    }

    /// <summary>
    /// Marks a member of the mod API as deprecated. The attribute documents the
    /// decision; the member's body calls the framework's deprecation notifier, which writes one
    /// line per calling mod per session at <see cref="Stage"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// [CwyDeprecated("0.9.0", DeprecationStage.Info, Replacement = "helper.Satone.Say")]
    /// public static void SayLegacy(string text)
    /// {
    ///     CwyDeprecations.Notify("SayLegacy", "0.9.0", DeprecationStage.Info, "helper.Satone.Say");
    ///     ...
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Interface |
        AttributeTargets.Constructor | AttributeTargets.Field | AttributeTargets.Event,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class CwyDeprecatedAttribute : Attribute
    {
        /// <summary>Marks a member as deprecated.</summary>
        /// <param name="since">The API version that deprecated it, e.g. <c>0.9.0</c>.</param>
        /// <param name="stage">How loudly it announces itself.</param>
        public CwyDeprecatedAttribute(string since, DeprecationStage stage)
        {
            Since = since ?? string.Empty;
            Stage = stage;
        }

        /// <summary>The API version that deprecated the member.</summary>
        public string Since { get; }

        /// <summary>How loudly it announces itself.</summary>
        public DeprecationStage Stage { get; }

        /// <summary>What to use instead, for the log line; null when there is no direct replacement.</summary>
        public string? Replacement { get; set; }
    }
}
