using System;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// One Pack Maker session from the <c>maker</c> command to its close: a fresh token (32 random bytes as
    /// hex, held in memory only, never logged), the event ring and the router. Unity-free so its lifecycle is
    /// tested; the plugin owns the listener (its own, or the API's through <see cref="ApiRouter.UseMaker"/>)
    /// and the browser.
    /// </summary>
    public sealed class MakerSurface
    {
        private readonly ApiToken _token;
        private int _open;

        /// <summary>Creates a session (nothing listens and no events are taken yet).</summary>
        /// <param name="options">The router options; <see cref="MakerRouterOptions.Events"/> is filled when null.</param>
        /// <param name="token">The token; null mints a new one (tests pass their own).</param>
        public MakerSurface(MakerRouterOptions options, ApiToken? token = null)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _token = token ?? ApiToken.Create();
            options.Events ??= new MakerEventRing(MakerDefaults.EventCapacity, () => options.Clock.Now, options.Diagnostics);
            Events = options.Events;
            Router = new MakerRouter(_token, options);
        }

        /// <summary>The request pipeline.</summary>
        public MakerRouter Router { get; }

        /// <summary>The event stream the adapters publish into while the session is open.</summary>
        public MakerEventRing Events { get; }

        /// <summary>The token's non-secret id, for status lines.</summary>
        public string TokenId => _token.Id;

        /// <summary>The clean page address (opening it requires a launch link or an existing browser session).</summary>
        public string Url => MakerDefaults.WebUrl(Router.Port);

        /// <summary>True between <see cref="Open"/> and <see cref="Close"/>.</summary>
        public bool IsOpen => System.Threading.Volatile.Read(ref _open) == 1;

        /// <summary>Installs <see cref="Events"/> as <see cref="MakerBackends.Events"/>, so the adapters' events reach the page.</summary>
        public void Open()
        {
            if (Router.IsStopping)
            {
                return;
            }

            MakerBackends.UseEvents(Events);
            System.Threading.Volatile.Write(ref _open, 1);
        }

        /// <summary>
        /// Ends the session: the router answers 503, long-polls wake, backend calls are cancelled, and the event
        /// slot is emptied when it still holds this session's ring. Idempotent.
        /// </summary>
        public void Close()
        {
            System.Threading.Volatile.Write(ref _open, 0);
            Router.BeginStop();
            if (ReferenceEquals(MakerBackends.Events, Events))
            {
                MakerBackends.UseEvents(null);
            }
        }
    }
}
