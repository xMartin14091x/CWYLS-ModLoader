using System;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>What a press of the console toggle does.</summary>
    public enum ConsoleToggleIntent
    {
        /// <summary>Open the console.</summary>
        Open,

        /// <summary>Close the console.</summary>
        Close,

        /// <summary>The console is open but faded out with the game's hidden UI: show it anyway.</summary>
        Reveal
    }

    /// <summary>
    /// Makes an open console follow the game's "Hide UI" button, without ever becoming unreachable.
    /// Pure; one instance lives for the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game hides its whole UI by fading the <c>UI</c> object's CanvasGroup to 0 and
    /// deactivating it (<c>UIShowManagerForPC.DeactivateUIParent</c>), and brings it back on the
    /// next click (<c>RoomGameManager</c>, state Idle). An open console fades with it, so a clean
    /// screenshot stays clean.
    /// </para>
    /// <para>
    /// The user's toggle always wins: pressing it while the console is faded out reveals it
    /// instead of closing an invisible window, and opening the console while the UI is hidden
    /// shows it straight away. The reveal lasts until the game's UI comes back.
    /// </para>
    /// </remarks>
    public sealed class ConsoleVisibility
    {
        /// <summary>Game UI opacity at or below which the game's UI counts as hidden.</summary>
        public const float HiddenThreshold = 0.01f;

        /// <summary>True when an open console fades with the game's hidden UI (<c>Console.FollowHideUI</c>).</summary>
        public bool FollowGameUi { get; set; } = true;

        /// <summary>True while the user has asked to see the console in spite of the hidden UI.</summary>
        public bool Revealed { get; private set; }

        /// <summary>True when this opacity of the game's UI means the UI is hidden.</summary>
        /// <param name="gameUiAlpha">0 (hidden) to 1 (shown).</param>
        public static bool IsHidden(float gameUiAlpha) => float.IsNaN(gameUiAlpha) || gameUiAlpha <= HiddenThreshold;

        /// <summary>Decides what a toggle press does, and remembers a reveal.</summary>
        /// <param name="isOpen">True when the console is open.</param>
        /// <param name="gameUiAlpha">How visible the game's UI is right now, 0 to 1 (1 when unknown).</param>
        public ConsoleToggleIntent OnToggle(bool isOpen, float gameUiAlpha)
        {
            var hiddenByGame = FollowGameUi && IsHidden(gameUiAlpha);

            if (!isOpen)
            {
                // Opening while the game's UI is hidden is a request to see the console now.
                Revealed = hiddenByGame;
                return ConsoleToggleIntent.Open;
            }

            if (hiddenByGame && !Revealed)
            {
                Revealed = true;
                return ConsoleToggleIntent.Reveal;
            }

            Revealed = false;
            return ConsoleToggleIntent.Close;
        }

        /// <summary>Notes the game's UI state once a frame; a reveal ends when the UI comes back.</summary>
        /// <param name="gameUiAlpha">How visible the game's UI is, 0 to 1.</param>
        public void Observe(float gameUiAlpha)
        {
            if (!IsHidden(gameUiAlpha))
            {
                Revealed = false;
            }
        }

        /// <summary>Forgets a reveal, e.g. when the console closes.</summary>
        public void Reset() => Revealed = false;

        /// <summary>The console's opacity for this frame, 0 to 1.</summary>
        /// <param name="gameUiAlpha">How visible the game's UI is, 0 to 1 (1 when unknown).</param>
        public float TargetAlpha(float gameUiAlpha)
        {
            if (!FollowGameUi || Revealed || float.IsNaN(gameUiAlpha))
            {
                return 1f;
            }

            return Math.Max(0f, Math.Min(1f, gameUiAlpha));
        }
    }
}
