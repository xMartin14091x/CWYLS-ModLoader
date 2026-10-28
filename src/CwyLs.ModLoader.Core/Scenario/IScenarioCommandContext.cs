using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// Runs one script-command row. Called on the Unity main thread by the script-command runtime when
    /// the game's reader reaches a row whose command is this one.
    /// </summary>
    /// <param name="context">The row and the three ways to go on.</param>
    /// <remarks>
    /// <para>The contract, which the runtime enforces:</para>
    /// <list type="bullet">
    /// <item>Decide at most once: <see cref="IScenarioCommandContext.Continue"/>,
    /// <see cref="IScenarioCommandContext.JumpTo"/> or <see cref="IScenarioCommandContext.WaitFor"/>.
    /// Later calls are ignored with a warning.</item>
    /// <item>Returning without deciding means <see cref="IScenarioCommandContext.Continue"/>.</item>
    /// <item>Throwing is logged against the owner and the scene continues with the next row: a
    /// broken command never stalls a scene.</item>
    /// <item>Keep it short. Long work belongs in <see cref="IScenarioCommandContext.WaitFor"/> or in a
    /// deferred action.</item>
    /// </list>
    /// </remarks>
    public delegate void ScenarioCommandHandler(IScenarioCommandContext context);

    /// <summary>One script row as the game holds it (<c>Bulbul.MasterData.NovelData</c>), read-only.</summary>
    public sealed class ScenarioCommandRow
    {
        /// <summary>Creates a row view. Called by the runtime.</summary>
        /// <param name="id">The row id (a jump target within the group).</param>
        /// <param name="groupId">The scenario group.</param>
        /// <param name="index">The row's position in the group, from 0.</param>
        /// <param name="command">The command number, 5000-5999.</param>
        /// <param name="commandName">The command name, or empty when the number has none.</param>
        /// <param name="arg1">Arg1 as written.</param>
        /// <param name="arg2">Arg2, a number in the game's data.</param>
        /// <param name="arg3">Arg3 as written.</param>
        /// <param name="arg4">Arg4 as written.</param>
        /// <param name="localizationId">The row's text id, or <c>0</c>/empty.</param>
        public ScenarioCommandRow(
            string id,
            string groupId,
            int index,
            int command,
            string commandName,
            string? arg1,
            float arg2,
            string? arg3,
            string? arg4,
            string? localizationId)
        {
            Id = id ?? string.Empty;
            GroupId = groupId ?? string.Empty;
            Index = index;
            Command = command;
            CommandName = commandName ?? string.Empty;
            Arg1 = arg1 ?? string.Empty;
            Arg2 = arg2;
            Arg3 = arg3 ?? string.Empty;
            Arg4 = arg4 ?? string.Empty;
            LocalizationId = localizationId ?? string.Empty;
        }

        /// <summary>The row id.</summary>
        public string Id { get; }

        /// <summary>The scenario group.</summary>
        public string GroupId { get; }

        /// <summary>The row's position in the group, from 0.</summary>
        public int Index { get; }

        /// <summary>The command number.</summary>
        public int Command { get; }

        /// <summary>The command name, or empty.</summary>
        public string CommandName { get; }

        /// <summary>Arg1 as written.</summary>
        public string Arg1 { get; }

        /// <summary>Arg2 (a number in the game's data).</summary>
        public float Arg2 { get; }

        /// <summary>Arg3 as written.</summary>
        public string Arg3 { get; }

        /// <summary>Arg4 as written.</summary>
        public string Arg4 { get; }

        /// <summary>The row's text id, or <c>0</c>/empty.</summary>
        public string LocalizationId { get; }

        /// <summary><c>cwy_gn_012 in cwy_goodnight_01 (#12 cwy.do)</c>.</summary>
        public override string ToString() =>
            Id + " in " + GroupId + " (#" + Index.ToString(CultureInfo.InvariantCulture) + " " +
            (CommandName.Length == 0 ? Command.ToString(CultureInfo.InvariantCulture) : CommandName) + ")";
    }

    /// <summary>What a <see cref="ScenarioCommandHandler"/> gets: the row, and how the scene goes on.</summary>
    public interface IScenarioCommandContext
    {
        /// <summary>The row being run.</summary>
        ScenarioCommandRow Row { get; }

        /// <summary>The scenario group being played (shorthand for <c>Row.GroupId</c>).</summary>
        string GroupId { get; }

        /// <summary>The group's scene type name, e.g. <c>ExtraScenario</c> or <c>MotionScenario</c>.</summary>
        string ScenarioType { get; }

        /// <summary>True once the handler chose how to go on.</summary>
        bool HasDecided { get; }

        /// <summary>Go on with the next row.</summary>
        void Continue();

        /// <summary>
        /// Go on at the row with this id in the same group (like the game's <c>Jump</c>). An id the
        /// group does not have logs a warning and continues with the next row.
        /// </summary>
        /// <param name="rowId">The target row's id.</param>
        void JumpTo(string rowId);

        /// <summary>
        /// Hold the scene until <paramref name="isDone"/> returns true (checked once a frame on the
        /// main thread), then go on with the next row. At <paramref name="timeout"/> the scene goes
        /// on anyway, with a warning; a throwing check counts as done, with a warning. To wait for
        /// a task: <c>WaitFor(() =&gt; task.IsCompleted, TimeSpan.FromSeconds(10))</c>.
        /// </summary>
        /// <param name="isDone">The check.</param>
        /// <param name="timeout">The longest hold; at most 5 minutes.</param>
        void WaitFor(Func<bool> isDone, TimeSpan timeout);
    }
}
