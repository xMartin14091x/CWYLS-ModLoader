using CwyLs.ModLoader.Core.Satone;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The mod-facing Satone API: named expressions, body motions and look control.
    /// </summary>
    /// <example>
    /// <code>
    /// using CwyLs.ModLoader.Bulbul.Satone.Motion;
    ///
    /// Satone.Face("smile");                        // expression by name
    /// Satone.Motion("wild001_stretch_shoulder");   // body motion by name
    /// Satone.Motion("stretch_shoulder");           // ... or by its short alias
    /// Satone.Motion(51);                           // ... or by the raw id
    /// Satone.LookAt(1f, 0.4f);                     // turn towards the player
    /// Satone.LookReset();
    /// Satone.FaceReset();
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// Every method returns <c>true</c> when the request was <em>accepted</em>: the name resolved
    /// and the call either ran (main thread) or was queued for the next frame (any other thread).
    /// It never means "Satone visibly did it" - the Animator needs a frame or two, and the result
    /// is reported asynchronously through the log and the trace bus
    /// (<see cref="SatoneMotionHost.LastObservation"/>).
    /// </para>
    /// <para>
    /// An unknown name never throws: it logs a warning with the closest names from
    /// <see cref="SatoneTable.Suggest"/> and returns false. The full list of names lives in
    /// <c>src/CwyLs.ModLoader.Core/Satone/satone-table.json</c>; enumerate it at runtime through
    /// <see cref="SatoneTable.Facials"/> and <see cref="SatoneTable.Motions"/>.
    /// </para>
    /// <para>
    /// Declared <c>partial</c> so another feature (<c>Satone.Say</c>) can add to it from its
    /// own file without this one having to know about it.
    /// </para>
    /// </remarks>
    public static partial class Satone
    {
        /// <summary>What a name-less state or motion reports itself as.</summary>
        public const string UnknownName = "unknown";

        /// <summary>
        /// Changes Satone's expression - <c>HeroineService.ChangeHeroineFacialAnimation(int)</c>.
        /// </summary>
        /// <param name="nameOrId">
        /// Expression name (<c>smile</c>), alias (<c>shy_rest</c>, <c>anim_face_shy_joy</c>,
        /// <c>Shy_Joy</c>) or id (<c>10</c>).
        /// </param>
        /// <returns>True when the request was accepted; false for an unknown name.</returns>
        public static bool Face(string nameOrId) => SatoneMotionFeature.Face(nameOrId);

        /// <summary>Changes Satone's expression by raw id.</summary>
        /// <param name="id">The <c>Facial</c> Animator value, 0-19 in v1.17.2.</param>
        public static bool Face(int id) => SatoneMotionFeature.Face(id.ToString(Invariant));

        /// <summary>
        /// Returns the expression to neutral - <c>HeroineService.InitHeroineFacial()</c>.
        /// </summary>
        public static bool FaceReset() => SatoneMotionFeature.FaceReset();

        /// <summary>
        /// Plays a body motion - <c>HeroineService.ChangeAnimation(AnimationType)</c>, or
        /// <c>ChangeHeroineAnimationForTrigger</c> when <paramref name="trigger"/> is set.
        /// </summary>
        /// <param name="nameOrId">
        /// Motion name (<c>wild001_stretch_shoulder</c>), alias (<c>stretch_shoulder</c>,
        /// <c>Wild001_Motion2_StretchShoulder</c>, <c>anim_wild_stretchshoulder</c>) or id (<c>51</c>).
        /// </param>
        /// <param name="trigger">
        /// Fire the Animator trigger of the same name instead of setting <c>MotionType</c>. Only
        /// the motions the table marks <c>trigger: true</c> have one; for anything else this falls
        /// back to the integer path with a warning.
        /// </param>
        /// <returns>True when the request was accepted; false for an unknown name.</returns>
        public static bool Motion(string nameOrId, bool trigger = false) =>
            SatoneMotionFeature.Motion(nameOrId, trigger);

        /// <summary>Plays a body motion by raw id.</summary>
        /// <param name="id">The <c>MotionType</c> Animator value.</param>
        /// <param name="trigger">See <see cref="Motion(string, bool)"/>.</param>
        public static bool Motion(int id, bool trigger = false) =>
            SatoneMotionFeature.Motion(id.ToString(Invariant), trigger);

        /// <summary>
        /// Turns Satone's gaze towards the player -
        /// <c>HeroineService.ChangeLookScaleAnimation(scale, seconds, Ease.Unset)</c>.
        /// </summary>
        /// <param name="scale">Look weight: 0 looks away, 1 looks straight at the player.</param>
        /// <param name="seconds">How long the turn takes.</param>
        public static bool LookAt(float scale, float seconds) => SatoneMotionFeature.LookAt(scale, seconds);

        /// <summary>
        /// Hands the gaze back to the game - <c>HeroineService.LookInitSlowly()</c>, which eases
        /// out over 1.5 s after a half-second delay.
        /// </summary>
        public static bool LookReset() => SatoneMotionFeature.LookReset();

        // ---- restore ----------------------------------------------------------------------

        /// <summary>
        /// Hands Satone back to the game: her expression to neutral after the game's own delay,
        /// her gaze eased out, and her body motion back to the loop her current AI state owns.
        /// </summary>
        /// <param name="scope">
        /// Which channels to restore. The default is all three; pass
        /// <see cref="RestoreScope.Face"/> (or a combination) to leave the others alone.
        /// </param>
        /// <returns>
        /// What each channel did, and why a channel did nothing. Never null. Writes the single
        /// <c>SATONE restore face=… motion=… gaze=…</c> console line as a side effect.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is the game's own idiom, not an invention: after a click reaction
        /// <c>RoomGameManager.OnEndPlayedReaction</c> calls
        /// <c>LookInitSlowly()</c> and <c>InitHeroineFacialAfterDelay(0.77f)</c>, and restores the
        /// body motion by <em>re-dispatching</em> the AI state
        /// (<c>HeroineAI.FinishHeroineClickReaction</c>) rather than by setting
        /// a motion id - only the state's <c>OnEnter</c> knows which loop belongs to it.
        /// </para>
        /// <para>
        /// The motion is deliberately <em>not</em> restored while she is in a directed state -
        /// a story, a click reaction, leaving the chair, the new-year countdown - because
        /// re-entering one of those restarts the sequence. The result then carries the reason
        /// (see <see cref="SatoneRestorePolicy"/>), and the expression and the gaze are still
        /// restored.
        /// </para>
        /// <para>
        /// Asynchronous by nature: the expression reset waits the game's
        /// <c>MyDefine.InitFacialDelaySecondsAfterTalk</c> (0.77 s in v1.17.2) and the gaze eases
        /// out over 1.5 s after a half-second delay. Called from a background thread, the whole
        /// call is queued for the next frame and the result says so
        /// (<see cref="RestoreResult.Queued"/>).
        /// </para>
        /// </remarks>
        public static RestoreResult RestoreDefaults(RestoreScope scope = RestoreScope.All) =>
            SatoneMotionFeature.RestoreDefaults(scope);

        /// <summary>
        /// Takes a revert handle: whatever happens to <paramref name="scope"/> between here and
        /// <see cref="SatoneRevert.Dispose"/> is handed back to the game at the end.
        /// </summary>
        /// <param name="scope">Which channels the handle restores.</param>
        /// <example>
        /// <code>
        /// using (Satone.Scoped(RestoreScope.Face | RestoreScope.Gaze))
        /// {
        ///     Satone.Face("smile");
        ///     Satone.LookAt(1f, 0.4f);
        /// }   // -> RestoreDefaults(Face | Gaze)
        /// </code>
        /// </example>
        public static SatoneRevert Scoped(RestoreScope scope = RestoreScope.All) =>
            SatoneMotionFeature.Scoped(scope);

        /// <summary>
        /// <see cref="Face(string)"/>, with a handle that puts her expression back on dispose.
        /// </summary>
        /// <param name="nameOrId">Expression name, alias or id.</param>
        /// <returns>
        /// A handle covering <see cref="RestoreScope.Face"/>, or a handle that restores nothing
        /// when the name was not accepted.
        /// </returns>
        public static SatoneRevert FaceScoped(string nameOrId)
        {
            var revert = SatoneMotionFeature.Scoped(RestoreScope.Face);
            return Face(nameOrId) ? revert : SatoneRevert.None(RestoreScope.Face);
        }

        /// <summary>
        /// <see cref="Motion(string, bool)"/>, with a handle that puts her motion back on dispose.
        /// </summary>
        /// <param name="nameOrId">Motion name, alias or id.</param>
        /// <param name="trigger">See <see cref="Motion(string, bool)"/>.</param>
        /// <returns>
        /// A handle covering <see cref="RestoreScope.Motion"/>, or one that restores nothing when
        /// the name was not accepted.
        /// </returns>
        public static SatoneRevert MotionScoped(string nameOrId, bool trigger = false)
        {
            var revert = SatoneMotionFeature.Scoped(RestoreScope.Motion);
            return Motion(nameOrId, trigger) ? revert : SatoneRevert.None(RestoreScope.Motion);
        }

        /// <summary>
        /// <see cref="LookAt"/>, with a handle that eases her gaze back on dispose.
        /// </summary>
        /// <param name="scale">Look weight: 0 looks away, 1 looks straight at the player.</param>
        /// <param name="seconds">How long the turn takes.</param>
        /// <returns>
        /// A handle covering <see cref="RestoreScope.Gaze"/>, or one that restores nothing when the
        /// call was refused.
        /// </returns>
        public static SatoneRevert LookAtScoped(float scale, float seconds)
        {
            var revert = SatoneMotionFeature.Scoped(RestoreScope.Gaze);
            return LookAt(scale, seconds) ? revert : SatoneRevert.None(RestoreScope.Gaze);
        }

        /// <summary>
        /// What Satone looks like right now: expression, motion, gaze and AI state. Read live on
        /// the main thread; from any other thread the values the host last cached, which know the
        /// motion and the state but not the two Animator read-backs.
        /// </summary>
        public static SatoneSnapshot Capture() => SatoneMotionFeature.Capture();

        /// <summary>
        /// What she looked like immediately before the most recent <see cref="Face(string)"/>,
        /// <see cref="Motion(string, bool)"/> or <see cref="LookAt"/> call.
        /// </summary>
        public static SatoneSnapshot LastSnapshot => SatoneMotionFeature.LastSnapshot;

        /// <summary>Which channels our own calls have changed since the last restore.</summary>
        public static RestoreScope TouchedScopes => SatoneMotionFeature.TouchedScopes;

        /// <summary>The most recent <see cref="RestoreDefaults"/> result, or null.</summary>
        public static RestoreResult? LastRestore => SatoneMotionFeature.LastRestore;

        /// <summary>
        /// Name of Satone's current <c>HeroineAI.ActionStateType</c>, e.g. <c>work_pc</c>.
        /// <see cref="UnknownName"/> when the room is not loaded.
        /// </summary>
        public static string State => Read().State;

        /// <summary>The raw <c>ActionStateType</c> value, or -1 when it is not known.</summary>
        public static int StateId => Read().StateId;

        /// <summary>
        /// Name of the motion the game last set, from the table, e.g.
        /// <c>wild001_stretch_shoulder</c>. This is the game's own record
        /// (<c>GetCurrentAnimationType</c>), not necessarily the clip on screen this instant.
        /// </summary>
        public static string CurrentMotion => Read().Motion;

        /// <summary>The raw <c>AnimationType</c> value the game last set.</summary>
        public static int CurrentMotionId => Read().MotionId;

        /// <summary>
        /// <c>HeroineService.IsEndAnimation(0)</c>: the current animation has played through at
        /// least once. False when the room is not loaded.
        /// </summary>
        public static bool IsAnimationEnded => Read().AnimationEnded;

        /// <summary>
        /// Whether Satone can be interrupted right now -
        /// <c>HeroineService.IsPossibleTalk()</c>, which asks her current AI state whether it
        /// accepts a click reaction. False when the room is not loaded.
        /// </summary>
        public static bool CanInteract
        {
            get
            {
                var snapshot = Read();
                return snapshot.Valid && snapshot.CanInteract;
            }
        }

        private static readonly System.Globalization.CultureInfo Invariant =
            System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>
        /// Live values on the main thread; the host's periodic snapshot anywhere else, because
        /// Unity's API throws when it is touched from a background thread.
        /// </summary>
        private static SatoneStateCache Read()
        {
            if (SatoneMotionHost.OnMainThread)
            {
                return SatoneMotionFeature.Snapshot();
            }

            SatoneMotionHost.WantCache();
            return SatoneMotionHost.Cache;
        }
    }
}
