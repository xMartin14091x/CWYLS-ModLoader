using System;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Mods;

namespace CwyLs.ModLoader.Bulbul.Data.Mods
{
    /// <summary>
    /// The data plugin's half of the code-mod front door: the player-data bus
    /// (<see cref="GameData"/>) as the framework's
    /// <see cref="IModGameDataSource"/>, so a mod's <c>helper.GameData</c> reads the same
    /// snapshot and change stream without naming a game type.
    /// </summary>
    internal sealed class GameDataModBackend : IModGameDataSource
    {
        /// <summary>Registers the backend. Called once from <c>GameDataPlugin.Awake</c>.</summary>
        internal static void Register() => ModBackends.UseGameData(new GameDataModBackend());

        public bool IsReady => GameData.IsReady;

        public GameDataSnapshot Snapshot() => GameData.Snapshot();

        public IDisposable Subscribe(Action<GameDataChanged> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            GameData.Changed += handler;
            return new Subscription(handler);
        }

        private sealed class Subscription : IDisposable
        {
            private Action<GameDataChanged>? _handler;

            internal Subscription(Action<GameDataChanged> handler) => _handler = handler;

            public void Dispose()
            {
                var handler = System.Threading.Interlocked.Exchange(ref _handler, null);
                if (handler != null)
                {
                    GameData.Changed -= handler;
                }
            }
        }
    }
}
