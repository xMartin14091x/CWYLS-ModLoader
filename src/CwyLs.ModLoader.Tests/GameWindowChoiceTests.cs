using System;
using CwyLs.ModLoader.Core.Maker;
using Xunit;

public class GameWindowChoiceTests
{
    [Fact]
    public void PrefersFirstUnityWindowOverEarlierOtherWindow()
    {
        var candidates = new[] { new WindowCandidate((IntPtr)1, false), new WindowCandidate((IntPtr)2, true), new WindowCandidate((IntPtr)3, true) };
        Assert.Equal((IntPtr)2, GameWindowChoice.Choose(candidates, (IntPtr)4));
    }

    [Fact]
    public void UsesFirstNonzeroCandidateWhenNoUnityWindow()
    {
        var candidates = new[] { new WindowCandidate(IntPtr.Zero, true), new WindowCandidate((IntPtr)1, false), new WindowCandidate((IntPtr)2, false) };
        Assert.Equal((IntPtr)1, GameWindowChoice.Choose(candidates, (IntPtr)3));
    }

    [Fact]
    public void UsesFallbackWhenNoCandidateExists()
    {
        Assert.Equal((IntPtr)4, GameWindowChoice.Choose(Array.Empty<WindowCandidate>(), (IntPtr)4));
        Assert.Equal(IntPtr.Zero, GameWindowChoice.Choose(Array.Empty<WindowCandidate>(), IntPtr.Zero));
    }
}
