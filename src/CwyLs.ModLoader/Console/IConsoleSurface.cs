namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// One way of drawing the console. The framework ships the IMGUI overlay, which needs nothing
    /// from the game; a game adapter can register a surface that looks like the game's own windows
    /// through <see cref="ConsoleFeature.RegisterSurface"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A surface only draws. What the console knows and does - tabs, the scrollback, running a
    /// line, completion, history - is in <see cref="ConsoleSession"/> and <see cref="ConsoleFeature"/>,
    /// shared by every surface, so switching from one to the other mid-session loses nothing.
    /// </para>
    /// <para>
    /// The framework calls a surface on the Unity main thread. A surface that cannot be shown
    /// returns false from <see cref="TryShow"/> and the framework falls back to IMGUI; a surface
    /// that fails while running reports it through <see cref="ConsoleFeature.ReportSurfaceFailure"/>.
    /// </para>
    /// </remarks>
    public interface IConsoleSurface
    {
        /// <summary>Short name for the log and the smoke report, e.g. <c>game-style</c>.</summary>
        string Name { get; }

        /// <summary>
        /// How visible the game's own UI is right now, 0 (hidden) to 1 (shown). A surface that
        /// does not track the game's UI returns 1. Drives <c>Console.FollowHideUI</c>.
        /// </summary>
        float GameUiAlpha { get; }

        /// <summary>
        /// Builds the surface if needed and shows it. Must not throw.
        /// </summary>
        /// <param name="detail">
        /// On success, a short note on what was built (or empty); on failure, why the surface
        /// cannot be shown, e.g. which game assets are missing.
        /// </param>
        /// <returns>True when the console is now on screen.</returns>
        bool TryShow(out string detail);

        /// <summary>Hides the surface. Must not throw; hiding a hidden surface does nothing.</summary>
        void Hide();
    }
}
