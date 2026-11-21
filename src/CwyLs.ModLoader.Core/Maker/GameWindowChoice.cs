using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Maker
{
    /// <summary>A visible top-level window belonging to the game process.</summary>
    public readonly struct WindowCandidate
    {
        public WindowCandidate(IntPtr handle, bool isUnity) { Handle = handle; IsUnity = isUnity; }
        public IntPtr Handle { get; }
        public bool IsUnity { get; }
    }

    /// <summary>Selects the first Unity window, otherwise the first eligible window or the process fallback.</summary>
    public static class GameWindowChoice
    {
        public static IntPtr Choose(IReadOnlyList<WindowCandidate> candidates, IntPtr fallback)
        {
            var first = IntPtr.Zero;
            foreach (var candidate in candidates)
            {
                if (candidate.Handle == IntPtr.Zero) continue;
                if (candidate.IsUnity) return candidate.Handle;
                if (first == IntPtr.Zero) first = candidate.Handle;
            }
            return first != IntPtr.Zero ? first : fallback;
        }
    }
}
