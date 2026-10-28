using System;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Which Unity UI controls belong to the framework itself, so the trace's UI layer leaves them
    /// out: our own console and mod windows must never produce <c>TYPED</c>, <c>BUTTON</c> or
    /// <c>CLICK</c> lines (the Esc that closes the game-style console used to write
    /// <c>TYPED ""</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule.</b> A control is the framework's own when the root GameObject of its hierarchy
    /// (<c>Transform.root</c>) has a name that starts with <see cref="FrameworkRootPrefix"/>,
    /// compared ordinally and case-sensitively. Every object the framework creates at the top of a
    /// hierarchy uses that prefix: the game-style overlay canvas every game-style window lives on
    /// is <c>CWY.GameStyleCanvas</c> (<c>CwyLs.ModLoader.Bulbul/UI/GameStyleCanvas.cs</c>), and the hosts
    /// are <c>CWY.TraceHost</c>, <c>CWY.ConsoleHost</c> and so on.
    /// </para>
    /// <para>
    /// The trace plugin cannot reference the adapter's types, so it matches by name, which is the
    /// one thing both sides can see. A control the framework adds under one of the game's own
    /// canvases (for example the language selector's extra entry) keeps the game's root and is
    /// traced like any game control. Anything that creates a new root for framework UI must keep
    /// the prefix.
    /// </para>
    /// </remarks>
    public static class UiOwnership
    {
        /// <summary>The name prefix reserved for the framework's own root GameObjects.</summary>
        public const string FrameworkRootPrefix = "CWY.";

        /// <summary>True when a hierarchy whose root has this name belongs to the framework.</summary>
        /// <param name="rootName">Name of the root GameObject; null or empty is never the framework's.</param>
        public static bool IsFrameworkRoot(string? rootName) =>
            !string.IsNullOrEmpty(rootName)
            && rootName!.Length > FrameworkRootPrefix.Length
            && rootName.StartsWith(FrameworkRootPrefix, StringComparison.Ordinal);
    }
}
