using System.Globalization;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// What Satone looked like at one instant: expression, body motion, gaze and the AI state that
    /// owns them. Taken before a change so the change can be described - and, where the values are
    /// readable, so a restore can be proved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value is optional, because two of the three are read back through
    /// <c>UnityEngine.Animator</c> by reflection and that read-back is a diagnostic, not a
    /// guarantee: an unknown value is <c>null</c>, never a made-up 0. <see cref="Diff"/> therefore
    /// only reports a channel as changed when both snapshots know it.
    /// </para>
    /// <para>
    /// Engine-free on purpose (anything worth a unit test lives in Core), so the
    /// capture rules can be tested without Unity.
    /// </para>
    /// </remarks>
    public readonly struct SatoneSnapshot
    {
        /// <summary>How close two <c>LookScale</c> values count as the same by default.</summary>
        public const float DefaultLookTolerance = 0.05f;

        /// <summary>The "nothing is known" snapshot.</summary>
        public static readonly SatoneSnapshot Unknown =
            new SatoneSnapshot(false, null, null, null, -1, string.Empty, 0d);

        /// <summary>Creates a snapshot.</summary>
        /// <param name="valid">False when the room scope could not be read at all.</param>
        /// <param name="facialId">The Animator's <c>Facial</c> integer, or null when unreadable.</param>
        /// <param name="motionId">The <c>HeroineService.AnimationType</c> value, or null.</param>
        /// <param name="lookScale">The Animator's <c>LookScale</c> float, or null when unreadable.</param>
        /// <param name="stateId">The <c>HeroineAI.ActionStateType</c> value, or -1.</param>
        /// <param name="stateName">Table name of the AI state, e.g. <c>work_pc</c>; null becomes empty.</param>
        /// <param name="takenSeconds">A monotonic clock reading, for the log line.</param>
        public SatoneSnapshot(
            bool valid,
            int? facialId,
            int? motionId,
            float? lookScale,
            int stateId,
            string? stateName,
            double takenSeconds)
        {
            Valid = valid;
            FacialId = facialId;
            MotionId = motionId;
            LookScale = lookScale;
            StateId = stateId;
            StateName = stateName ?? string.Empty;
            TakenSeconds = takenSeconds;
        }

        /// <summary>False when the snapshot could not be taken (no room scene, no service).</summary>
        public bool Valid { get; }

        /// <summary>The <c>Facial</c> Animator integer, or null when the read-back is unavailable.</summary>
        public int? FacialId { get; }

        /// <summary>The body motion the game last recorded, or null.</summary>
        public int? MotionId { get; }

        /// <summary>The <c>LookScale</c> Animator float, or null when the read-back is unavailable.</summary>
        public float? LookScale { get; }

        /// <summary>The <c>HeroineAI.ActionStateType</c> value, or -1 when it is not known.</summary>
        public int StateId { get; }

        /// <summary>Table name of the AI state, e.g. <c>work_pc</c>. Never null.</summary>
        public string StateName { get; }

        /// <summary>Monotonic seconds at capture; 0 when nobody supplied a clock.</summary>
        public double TakenSeconds { get; }

        /// <summary>True when the expression was readable.</summary>
        public bool HasFacial => FacialId.HasValue;

        /// <summary>True when the body motion was readable.</summary>
        public bool HasMotion => MotionId.HasValue;

        /// <summary>True when the gaze was readable.</summary>
        public bool HasLook => LookScale.HasValue;

        /// <summary>
        /// Which channels differ between <paramref name="before"/> and this snapshot. A channel
        /// either side does not know is never reported as changed - "unknown" is not "different".
        /// </summary>
        /// <param name="before">The earlier snapshot.</param>
        /// <param name="lookTolerance">
        /// How far <c>LookScale</c> may drift before it counts as changed; the gaze is tweened, so
        /// an exact comparison would be noise. Negative values are treated as 0.
        /// </param>
        public RestoreScope Diff(SatoneSnapshot before, float lookTolerance = DefaultLookTolerance)
        {
            var scope = RestoreScope.None;

            if (before.HasFacial && HasFacial && before.FacialId!.Value != FacialId!.Value)
            {
                scope |= RestoreScope.Face;
            }

            if (before.HasMotion && HasMotion && before.MotionId!.Value != MotionId!.Value)
            {
                scope |= RestoreScope.Motion;
            }

            if (before.HasLook && HasLook)
            {
                var tolerance = lookTolerance < 0f ? 0f : lookTolerance;
                var delta = before.LookScale!.Value - LookScale!.Value;
                if (delta < 0f)
                {
                    delta = -delta;
                }

                if (delta > tolerance)
                {
                    scope |= RestoreScope.Gaze;
                }
            }

            return scope;
        }

        /// <summary>
        /// True when nothing this snapshot knows differs from <paramref name="other"/>.
        /// </summary>
        /// <param name="other">The snapshot to compare with.</param>
        /// <param name="lookTolerance">See <see cref="Diff"/>.</param>
        public bool Matches(SatoneSnapshot other, float lookTolerance = DefaultLookTolerance) =>
            Diff(other, lookTolerance) == RestoreScope.None;

        /// <summary>This snapshot with a different capture time; everything else is kept.</summary>
        /// <param name="takenSeconds">The new clock reading.</param>
        public SatoneSnapshot At(double takenSeconds) =>
            new SatoneSnapshot(Valid, FacialId, MotionId, LookScale, StateId, StateName, takenSeconds);

        /// <summary>e.g. <c>facial=0 motion=200 look=0 state=work_pc</c>.</summary>
        public override string ToString()
        {
            if (!Valid)
            {
                return "unknown";
            }

            return "facial=" + Show(FacialId)
                   + " motion=" + Show(MotionId)
                   + " look=" + Show(LookScale)
                   + " state=" + (StateName.Length == 0 ? "?" : StateName);
        }

        private static string Show(int? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "?";

        private static string Show(float? value) =>
            value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?";
    }
}
