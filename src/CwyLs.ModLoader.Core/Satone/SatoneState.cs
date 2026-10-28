using System;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// One member of the game's <c>HeroineAI.ActionStateType</c> - what Satone is currently doing
    /// (working at the PC, on a tea break, asleep, ...), as opposed to which clip is playing.
    /// </summary>
    /// <remarks>
    /// Read-only here. The state is owned by <c>HeroineAI.ChangeState</c> (private); the event bus
    /// already reports transitions, and this table exists so a name can be put on the number.
    /// </remarks>
    public sealed class SatoneState
    {
        /// <summary>Creates a row.</summary>
        /// <param name="id">The enum value, -1 for <c>None</c>.</param>
        /// <param name="name">snake_case name, e.g. <c>break_tea_time</c>. Required.</param>
        /// <param name="actionStateType">The <c>ActionStateType</c> member name. Required.</param>
        /// <exception cref="ArgumentException">A required name is null or blank.</exception>
        public SatoneState(int id, string name, string actionStateType)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a state needs a name", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(actionStateType))
            {
                throw new ArgumentException("a state needs its enum member name", nameof(actionStateType));
            }

            Id = id;
            Name = name;
            ActionStateType = actionStateType;
        }

        /// <summary>The <c>HeroineAI.ActionStateType</c> value.</summary>
        public int Id { get; }

        /// <summary>snake_case name, e.g. <c>break_tea_time</c>.</summary>
        public string Name { get; }

        /// <summary>The enum member name as the game spells it, e.g. <c>BreakTeaTime</c>.</summary>
        public string ActionStateType { get; }

        /// <summary>e.g. <c>break_tea_time (id 23)</c>.</summary>
        public override string ToString() => Name + " (id " + Id + ")";
    }
}
