using System;
using System.Threading;
using CwyLs.ModLoader.Core.Api;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SocketTestCollection : ICollectionFixture<SocketThreadPoolFixture>
    {
        public const string Name = "socket-and-deadline";
    }

    // HttpListener dispatches on the pool. Provision its bounded 16-reader exercise before the
    // non-parallel collection starts, rather than testing the pool's machine-load-dependent ramp-up.
    public sealed class SocketThreadPoolFixture : IDisposable
    {
        private readonly int _workers;
        private readonly int _completion;
        public SocketThreadPoolFixture()
        {
            ThreadPool.GetMinThreads(out _workers, out _completion);
            ThreadPool.SetMinThreads(Math.Max(64, _workers), _completion);
        }
        public void Dispose() => ThreadPool.SetMinThreads(_workers, _completion);
    }

    internal static class SocketTestServer
    {
        internal static (int Port, T Rig, IApiTransport Transport) Start<T>(
            ApiTransportMode mode, Func<int, T> create, Func<T, ApiRequest, ApiResponse> handle,
            Action<string>? diagnostics = null, Func<int>? nextPort = null, int attempts = 10,
            Action<System.Net.HttpListenerResponse>? abortResponse = null) where T : class
        {
            diagnostics ??= _ => { };
            if (mode == ApiTransportMode.Tcp)
            {
                T? rig = null;
                var transport = new TcpApiTransport(0, request => handle(rig!, request), diagnostics);
                transport.Start(); // Bind once to port 0; never release a probe and race its clients.
                try
                {
                    rig = create(transport.Port);
                    return (transport.Port, rig, transport);
                }
                catch { transport.Dispose(); throw; }
            }
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var port = (nextPort ?? ApiTestKit.FreePort)();
                var rig = create(port);
                if (abortResponse != null)
                {
                    var custom = new HttpListenerApiTransport(port, request => handle(rig, request), diagnostics, abortResponse);
                    try { custom.Start(); return (port, rig, custom); }
                    catch (System.Net.HttpListenerException) { custom.Dispose(); continue; }
                    catch (System.Net.Sockets.SocketException) { custom.Dispose(); continue; }
                }
                var started = ApiServer.Start(port, mode, request => handle(rig, request), diagnostics);
                if (started.IsRunning) return (port, rig, started.Transport!);
                started.Transport?.Dispose();
                if (started.Problem != ApiStartProblem.PortTaken) throw new InvalidOperationException(started.Detail);
            }
            throw new InvalidOperationException(attempts + " fresh HttpListener port candidates were taken.");
        }

        internal static void Until(Func<bool> ready, string reason, Action? tick = null, int seconds = 30)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!ready())
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(seconds), reason);
                tick?.Invoke();
                Thread.Sleep(25);
            }
        }
    }
}
