using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// One value of the <c>MotionType</c> Animator integer: the body animation
    /// <c>HeroineService.ChangeHeroineAnimationForInteger(int)</c> takes, with a name attached.
    /// </summary>
    /// <remarks>
    /// Built from the game's
    /// <c>HeroineService.AnimationType</c> enum plus the Base layer of
    /// <c>Character_Animator_ForScript.controller</c> (game 1.17.3). Three ids exist only in the controller and
    /// are named <c>motion_&lt;id&gt;</c>; every other row is named after its enum member.
    /// </remarks>
    public sealed class SatoneMotion
    {
        private static readonly string[] NoAliases = new string[0];

        /// <summary>Creates a row. Only <paramref name="name"/> is required.</summary>
        /// <param name="id">The integer the game takes. Matches an <c>AnimationType</c> value where one exists.</param>
        /// <param name="name">Canonical name, e.g. <c>wild001_stretch_shoulder</c>. Required.</param>
        /// <param name="animationType">Matching <c>AnimationType</c> member, or null for a controller-only id.</param>
        /// <param name="category">Coarse group: <c>base</c>, <c>wild</c>, <c>work</c>, <c>break</c>, <c>story</c>, ...</param>
        /// <param name="state">Animator state the id selects, or null when the mapping is not provable.</param>
        /// <param name="clip">Animation clip that state plays, or null.</param>
        /// <param name="loopable">Whether that clip loops; null when there is no clip.</param>
        /// <param name="trigger">The controller has a Trigger parameter of the same name.</param>
        /// <param name="gameSetsTrigger">The game's own code fires that trigger for this id.</param>
        /// <param name="usedByLines">How many <c>NovelMaster</c> rows use this id as <c>BodyMotion</c>.</param>
        /// <param name="aliases">Other names that resolve to this row; null becomes empty.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        public SatoneMotion(
            int id,
            string name,
            string? animationType,
            string? category,
            string? state,
            string? clip,
            bool? loopable,
            bool trigger,
            bool gameSetsTrigger,
            int usedByLines,
            IReadOnlyList<string>? aliases)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a motion needs a name", nameof(name));
            }

            Id = id;
            Name = name;
            AnimationType = animationType;
            Category = category ?? "unknown";
            State = state;
            Clip = clip;
            Loopable = loopable;
            Trigger = trigger;
            GameSetsTrigger = gameSetsTrigger;
            UsedByLines = usedByLines;
            Aliases = aliases ?? NoAliases;
        }

        /// <summary>The integer passed to <c>HeroineService.ChangeHeroineAnimationForInteger</c>.</summary>
        public int Id { get; }

        /// <summary>Canonical name a mod types, e.g. <c>wild001_stretch_shoulder</c>.</summary>
        public string Name { get; }

        /// <summary>The game's own <c>AnimationType</c> member, when this id has one.</summary>
        public string? AnimationType { get; }

        /// <summary>Coarse group used for listing and for the smoke report. Never null.</summary>
        public string Category { get; }

        /// <summary>
        /// Animator state this id selects. This is the string
        /// <c>HeroineService.IsCurrentAnimation</c> and <c>IsEndAnimation</c> compare against
        /// (<c>MyAnimatorUtil</c> calls <c>AnimatorStateInfo.IsName</c>), so it is how a caller
        /// can tell whether the motion really started.
        /// </summary>
        public string? State { get; }

        /// <summary>Animation clip the state plays, e.g. <c>anim_wild_stretchshoulder</c>.</summary>
        public string? Clip { get; }

        /// <summary>Whether the clip loops; null when the state has no clip.</summary>
        public bool? Loopable { get; }

        /// <summary>
        /// True when the controller declares a Trigger parameter named exactly
        /// <see cref="AnimationType"/> - the only ids that can be driven through
        /// <c>ChangeHeroineAnimationForTrigger</c>.
        /// </summary>
        public bool Trigger { get; }

        /// <summary>
        /// True when the game itself fires that trigger, i.e. the id appears in the switch inside
        /// <c>HeroineService.ChangeHeroineAnimationForInteger</c>.
        /// </summary>
        public bool GameSetsTrigger { get; }

        /// <summary>Rows in the game's own <c>NovelMaster</c> that play this motion.</summary>
        public int UsedByLines { get; }

        /// <summary>Other accepted names - the enum member, its descriptive tail, the state, the clip.</summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary>e.g. <c>wild001_stretch_shoulder (id 51)</c>.</summary>
        public override string ToString() => Name + " (id " + Id + ")";
    }
}
