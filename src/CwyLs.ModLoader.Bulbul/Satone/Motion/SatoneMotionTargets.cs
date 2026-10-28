using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The <c>HeroineService</c> members the motion API calls that the framework's main table does not
    /// already cover, as patch-target data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BulbulPatchTargets</c> already lists the four verified motion seams -
    /// <c>ChangeHeroineFacialAnimation</c>, <c>ChangeHeroineAnimationForInteger</c>,
    /// <c>ChangeAnimation</c> and <c>ChangeLookScaleAnimation</c>. This table adds the members the
    /// motion API reaches beyond them, so a game update that renames any of them says
    /// <c>PATCH TARGET MISSING</c> at startup rather than turning
    /// <see cref="Satone.CanInteract"/> silently false forever.
    /// </para>
    /// <para>
    /// Nothing here is patched; these are called directly. The registry is about signatures, not
    /// about Harmony, which is exactly what a direct call needs too.
    /// </para>
    /// </remarks>
    internal static class SatoneMotionTargets
    {
        private const string GameAssembly = "Assembly-CSharp";
        private const string Heroine = "Bulbul.HeroineService";

        private static readonly PatchTarget[] Table =
        {
            // The trigger path, for the 23 ids with a Trigger parameter.
            new PatchTarget(
                "satone.animation.trigger", GameAssembly, Heroine,
                MemberKind.Method, "ChangeHeroineAnimationForTrigger",
                new[] { "Bulbul.HeroineService+AnimationType" },
                expectedSignature: "void ChangeHeroineAnimationForTrigger(HeroineService.AnimationType)"),

            // Satone.FaceReset().
            new PatchTarget(
                "satone.facial.init", GameAssembly, Heroine,
                MemberKind.Method, "InitHeroineFacial",
                expectedSignature: "void InitHeroineFacial()"),

            // The expression half of Satone.RestoreDefaults. This is the reset every game path
            // pairs with a facial change (RoomGameManager, HeroineAI, Bulbul.ScenarioReader).
            new PatchTarget(
                "satone.facial.initdelay", GameAssembly, Heroine,
                MemberKind.Method, "InitHeroineFacialAfterDelay",
                new[] { "System.Single" },
                expectedSignature: "void InitHeroineFacialAfterDelay(float)"),

            // HeroineService.DebugChangeState -> HeroineAI.DebugChangeState -> ChangeState -> a
            // dispatch on HeroineAI's action state machine: the motion half of RestoreDefaults, and the
            // identical dispatch HeroineAI.FinishHeroineClickReaction makes.
            new PatchTarget(
                "satone.state.redispatch", GameAssembly, Heroine,
                MemberKind.Method, "DebugChangeState",
                new[] { "HeroineAI+ActionStateType" },
                expectedSignature: "void DebugChangeState(HeroineAI.ActionStateType)"),

            // Bulbul.MyDefine: the 0.77 s the expression reset waits. Read at runtime
            // rather than referenced, because a const would be inlined into this assembly and a
            // retune in a game update would then be invisible (SatoneSeam.FacialResetDelaySeconds).
            new PatchTarget(
                "satone.facial.delayconst", GameAssembly, "Bulbul.MyDefine",
                MemberKind.Field, "InitFacialDelaySecondsAfterTalk",
                isStatic: true,
                expectedSignature: "static float InitFacialDelaySecondsAfterTalk"),

            // Satone.LookReset().
            new PatchTarget(
                "satone.look.init", GameAssembly, Heroine,
                MemberKind.Method, "LookInitSlowly",
                expectedSignature: "void LookInitSlowly()"),

            // Satone.CanInteract.
            new PatchTarget(
                "satone.caninteract", GameAssembly, Heroine,
                MemberKind.Method, "IsPossibleTalk",
                expectedSignature: "bool IsPossibleTalk()"),

            // Satone.CurrentMotion.
            new PatchTarget(
                "satone.currentmotion", GameAssembly, Heroine,
                MemberKind.Method, "GetCurrentAnimationType",
                expectedSignature: "HeroineService.AnimationType GetCurrentAnimationType()"),

            // Satone.State.
            new PatchTarget(
                "satone.currentstate", GameAssembly, Heroine,
                MemberKind.Method, "GetCurrentAIState",
                expectedSignature: "HeroineAI.ActionStateType GetCurrentAIState()"),

            // Satone.IsAnimationEnded.
            new PatchTarget(
                "satone.isendanimation", GameAssembly, Heroine,
                MemberKind.Method, "IsEndAnimation",
                new[] { "System.Int32" },
                expectedSignature: "bool IsEndAnimation(int)"),

            // How a motion request is confirmed. MyAnimatorUtil forwards
            // this to AnimatorStateInfo.IsName, so the string is an Animator *state* name, which
            // is what the generated table's `state` field holds.
            new PatchTarget(
                "satone.iscurrentanimation", GameAssembly, Heroine,
                MemberKind.Method, "IsCurrentAnimation",
                new[] { "System.String" },
                expectedSignature: "bool IsCurrentAnimation(string)")
        };

        /// <summary>The targets, in table order.</summary>
        internal static IReadOnlyList<PatchTarget> All => Table;
    }
}
