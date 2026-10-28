namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// A main-thread snapshot of what Satone is doing, so an off-thread caller can read
    /// <see cref="Satone.State"/> and friends without touching Unity from the wrong thread.
    /// </summary>
    /// <remarks>
    /// Only refreshed once something has actually read from another thread
    /// (<see cref="SatoneMotionHost.WantCache"/>); a session that only uses the API from the main
    /// thread polls the game zero times.
    /// </remarks>
    public readonly struct SatoneStateCache
    {
        /// <summary>The "nothing is known yet" snapshot.</summary>
        public static readonly SatoneStateCache Unknown = new SatoneStateCache(false, Satone.UnknownName, -1, Satone.UnknownName, 0, false, false);

        /// <summary>Creates a snapshot.</summary>
        /// <param name="valid">False when the room scope could not be read at all.</param>
        /// <param name="state">Name of the current <c>ActionStateType</c>.</param>
        /// <param name="stateId">The <c>ActionStateType</c> value.</param>
        /// <param name="motion">Name of the current motion from the table.</param>
        /// <param name="motionId">The <c>AnimationType</c> value.</param>
        /// <param name="animationEnded">Result of <c>HeroineService.IsEndAnimation(0)</c>.</param>
        /// <param name="canInteract">Result of <c>HeroineService.IsPossibleTalk()</c>.</param>
        public SatoneStateCache(
            bool valid, string state, int stateId, string motion, int motionId, bool animationEnded, bool canInteract)
        {
            Valid = valid;
            State = state;
            StateId = stateId;
            Motion = motion;
            MotionId = motionId;
            AnimationEnded = animationEnded;
            CanInteract = canInteract;
        }

        /// <summary>False when the snapshot could not be taken (no room scene, no service).</summary>
        public bool Valid { get; }

        /// <summary>Name of the current <c>HeroineAI.ActionStateType</c>.</summary>
        public string State { get; }

        /// <summary>The <c>ActionStateType</c> value, or -1.</summary>
        public int StateId { get; }

        /// <summary>Name of the current motion, from the generated table.</summary>
        public string Motion { get; }

        /// <summary>The <c>HeroineService.AnimationType</c> value.</summary>
        public int MotionId { get; }

        /// <summary><c>HeroineService.IsEndAnimation(0)</c> at the snapshot.</summary>
        public bool AnimationEnded { get; }

        /// <summary><c>HeroineService.IsPossibleTalk()</c> at the snapshot.</summary>
        public bool CanInteract { get; }
    }
}
