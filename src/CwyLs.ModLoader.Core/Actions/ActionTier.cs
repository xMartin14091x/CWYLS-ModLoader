namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// How much an action may change. The tier decides the
    /// default gating in <see cref="ActionGate"/>; the API and its clients publish it as
    /// read-only/destructive hints. Values are explicit and never renumbered.
    /// </summary>
    public enum ActionTier
    {
        /// <summary>Only reads state (the current track, the timer, a snapshot).</summary>
        Read = 0,

        /// <summary>Changes the running game but not the player's saved records (play music, start a timer, open a panel).</summary>
        Act = 1,

        /// <summary>Changes the player's saved records (todos, notes, habits, calendar): backup first, and gated.</summary>
        Write = 2,
    }

    /// <summary>Who asked for an action. The gate treats each differently (<see cref="ActionGate"/>).</summary>
    public enum ActionCaller
    {
        /// <summary>A code mod through <c>helper.Actions</c>.</summary>
        Mod = 0,

        /// <summary>The player typing <c>act run</c> in the in-game console.</summary>
        Console = 1,

        /// <summary>A pack scene through a script command (<c>cwy.do</c>, <c>cwy.defer</c>).</summary>
        Script = 2,

        /// <summary>An outside program through the local game API (for example an MCP server).</summary>
        Api = 3,
    }
}
