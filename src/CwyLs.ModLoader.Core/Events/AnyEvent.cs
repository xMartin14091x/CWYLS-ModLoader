namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// One event on the catch-all stream: the name of the channel it came from and the arguments.
    /// A mod that wants to log or forward everything subscribes here once instead of to thirteen
    /// typed channels.
    /// </summary>
    /// <param name="Name">Channel name, which is the event type's name, e.g. <c>LineSpoken</c>.</param>
    /// <param name="Args">The event arguments; cast to the concrete type when <paramref name="Name"/> matches.</param>
    public sealed record AnyEvent(string Name, ICwyEvent Args)
    {
        /// <summary>The event as the console mirror prints it: <c>LineSpoken Voice_x [...]</c>.</summary>
        public string Describe() => Name + " " + Args.Describe();
    }
}
