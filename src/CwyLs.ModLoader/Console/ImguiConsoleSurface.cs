namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The IMGUI overlay as a <see cref="IConsoleSurface"/>: the always-available fallback. It
    /// needs no canvas, no sprite and no font from the game, so it works before <c>RoomScene</c>
    /// exists and after a game update that renamed the game's UI assets.
    /// </summary>
    internal sealed class ImguiConsoleSurface : IConsoleSurface
    {
        /// <summary>The name the log and the smoke report use.</summary>
        internal const string SurfaceName = "imgui";

        /// <summary>The one instance.</summary>
        internal static readonly ImguiConsoleSurface Instance = new ImguiConsoleSurface();

        private ImguiConsoleSurface()
        {
        }

        /// <inheritdoc />
        public string Name => SurfaceName;

        /// <inheritdoc />
        /// <remarks>The IMGUI overlay does not follow the game's Hide UI, so this is always 1.</remarks>
        public float GameUiAlpha => 1f;

        /// <inheritdoc />
        public bool TryShow(out string detail)
        {
            var host = ConsoleHost.Ensure();
            if (host == null)
            {
                detail = "the overlay host could not be created (see the startup log)";
                return false;
            }

            ConsoleHost.SetDrawing(true);
            ConsoleWindow.OnOpened();
            detail = string.Empty;
            return true;
        }

        /// <inheritdoc />
        public void Hide() => ConsoleHost.SetDrawing(false);
    }
}
