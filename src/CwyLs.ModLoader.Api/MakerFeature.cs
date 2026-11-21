using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Api
{
    /// <summary>
    /// Pack Maker's lifecycle in the game (ticket DS-01): the <c>maker</c> command opens a
    /// <see cref="MakerSurface"/> (fresh token, event ring, router) on the local API's listener when the API is
    /// on, otherwise on a maker-only listener on <c>[Api] Port</c> that serves nothing but <c>/maker/</c> and
    /// <c>/v1/maker/*</c> (no key file, no API routes), and opens the default browser there. Everything here runs
    /// on the main thread (console command, <see cref="ApiFeature"/>'s start and stop, the kill switch,
    /// <c>Application.quitting</c>); request threads only ever see the router.
    /// </summary>
    internal static class MakerFeature
    {
        /// <summary>The console command.</summary>
        internal const string CommandName = "maker";

        private static Func<int> _port = () => ApiDefaults.DefaultPort;
        private static Func<ApiTransportMode> _mode = () => ApiTransportMode.Auto;
        private static Func<bool> _consoleReads = () => false;
        private static volatile MakerSurface? _surface;
        private static IApiTransport? _ownListener;
        private static ApiRouter? _attachedTo;
        private static DateTimeOffset _openedAt;
        private static bool _initialised;

        /// <summary>True while Pack Maker is open (on either listener).</summary>
        internal static bool IsOpen => _surface != null;

        /// <summary>The web app folder: <c>BepInEx/plugins/CwyLsModLoader/maker</c> beside this DLL.</summary>
        internal static string? WebRoot
        {
            get
            {
                try
                {
                    var location = typeof(MakerFeature).Assembly.Location;
                    var folder = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
                    return folder is null ? null : Path.Combine(folder, MakerDefaults.WebFolderName);
                }
                catch (Exception ex)
                {
                    Diagnose("finding the web folder failed: " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>Registers the command and the quit hook. Called once from <see cref="ApiFeature.Initialize"/>.</summary>
        /// <param name="port"><c>[Api] Port</c>, read when Pack Maker opens.</param>
        /// <param name="mode"><c>[Api] Transport</c>, read when Pack Maker opens.</param>
        /// <param name="consoleReads"><c>[Api] ConsoleAuditReads</c>, read per request.</param>
        internal static void Initialize(Func<int> port, Func<ApiTransportMode> mode, Func<bool> consoleReads)
        {
            if (_initialised)
            {
                return;
            }

            _initialised = true;
            _port = port ?? _port;
            _mode = mode ?? _mode;
            _consoleReads = consoleReads ?? _consoleReads;
            MakerCommands.Register();
            Application.quitting += () => Close("the game is quitting", announce: false);
        }

        /// <summary>The <c>maker</c> command: opens Pack Maker (starting its listener if needed) and the browser. Main thread.</summary>
        /// <param name="trigger">What asked, for the line.</param>
        /// <param name="openBrowser">False for <c>maker serve</c>: return a fresh one-time link without opening a browser tab;
        /// unattended checks must consume that link within 60 seconds and retain its session cookie.</param>
        /// <returns>One line for the console.</returns>
        internal static string Open(string trigger, bool openBrowser = true)
        {
            try
            {
                var api = ApiFeature.Current;
                var apiRouter = api?.Transport != null ? api.Runtime.Router : null;
                var port = apiRouter?.Port ?? _port();
                if (_surface is MakerSurface stale && stale.Router.Port != port)
                {
                    Close("the port changed to " + port, announce: true);
                }

                var fresh = _surface is null;
                var surface = _surface ?? NewSurface(port);
                string listener;
                if (apiRouter != null)
                {
                    apiRouter.UseMaker(surface.Router);
                    _attachedTo = apiRouter;
                    listener = "api";
                }
                else if (_ownListener is null)
                {
                    var started = ApiServer.Start(port, _mode(), surface.Router.Handle, Diagnose);
                    if (!started.IsRunning || started.Transport is null)
                    {
                        surface.Close();
                        _surface = null;
                        var why = started.Problem == ApiStartProblem.PortTaken
                            ? "port " + port + " is taken by another program (set [Api] Port)"
                            : started.Detail;
                        Warn("makerOff", "Pack Maker did not start: " + why, ("port", port), ("problem", started.Problem.ToString()), ("trigger", trigger));
                        return "Pack Maker did not start: " + why;
                    }

                    _ownListener = started.Transport;
                    listener = started.Transport.Name;
                    if (started.FallbackReason != null)
                    {
                        Warn("makerFallback", "Pack Maker uses the TCP fallback: " + started.FallbackReason, ("port", port));
                    }
                }
                else
                {
                    listener = _ownListener.Name;
                }

                _surface = surface;
                surface.Open();
                if (fresh)
                {
                    // Read the game's lines and the packs now, in the background, not in the page's first requests.
                    MakerBackends.WarmUp("maker opened");
                    _openedAt = DateTimeOffset.Now;
                    ConsoleEvents.Emit(
                        ApiAuditLog.Sys,
                        "makerOn",
                        ConsoleTags.Api,
                        "Pack Maker on " + surface.Url,
                        "listener,webFiles,trigger",
                        ("listener", listener),
                        ("webFiles", Directory.Exists(surface.Router.WebRoot ?? string.Empty) ? "present" : "MISSING"),
                        ("trigger", trigger));
                }

                var openUrl = surface.Router.CreateOpenUrl();
                if (openBrowser)
                {
                    OpenBrowser(openUrl);
                }

                return "Pack Maker: " + openUrl + (openBrowser ? " (opening in your browser" : " (serving; open this one-time link within 60 seconds") +
                       (apiRouter != null ? "; on the local API's listener" : "; this computer only, the local API stays off") + ")";
            }
            catch (Exception ex)
            {
                Warn("makerFailed", "Pack Maker could not open: " + ex.GetType().Name + ": " + ex.Message);
                return "Pack Maker could not open: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>Closes Pack Maker: its routes answer 503 or go away, long-polls wake, its own listener (if any) closes. Main thread; idempotent.</summary>
        /// <param name="reason">Why, for the line.</param>
        /// <param name="announce">False at quit (no console line while the game closes; the file record stays).</param>
        /// <returns>One line for the console.</returns>
        internal static string Close(string reason, bool announce = true)
        {
            var surface = _surface;
            var attached = _attachedTo;
            var own = _ownListener;
            if (surface is null && attached is null && own is null)
            {
                return "Pack Maker is not open";
            }

            // Release ownership before callbacks: a failed close must not leave a listener for Open to reuse.
            _surface = null;
            _attachedTo = null;
            _ownListener = null;
            try
            {
                MakerShutdown.Close(
                    () => attached?.UseMaker(null),
                    () => surface?.Close(),
                    () => own?.Stop());
            }
            catch (Exception ex)
            {
                Diagnose("closing failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (announce)
            {
                ConsoleEvents.Emit(ApiAuditLog.Sys, "makerOff", ConsoleTags.Api, "Pack Maker off (" + reason + ")", null, ("reason", reason));
            }
            else
            {
                ConsoleEvents.File(ApiAuditLog.Sys, "makerOff", ("reason", reason));
            }

            return "Pack Maker closed (" + reason + "); type 'maker' to open it again";
        }

        /// <summary>
        /// Before the API binds its port: a maker-only listener on it is stopped (Pack Maker stays open and moves
        /// to the API's listener in <see cref="AfterApiStart"/>, keeping its token, so an open page keeps working).
        /// </summary>
        internal static void BeforeApiStart()
        {
            var own = _ownListener;
            if (own is null)
            {
                return;
            }

            _ownListener = null;
            try
            {
                own.Stop();
            }
            catch (Exception ex)
            {
                Diagnose("stopping the maker-only listener failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>After the API started (true) or failed (false): Pack Maker joins the API's listener, or gets its own back.</summary>
        /// <param name="router">The API's router, when it started.</param>
        internal static void AfterApiStart(ApiRouter? router)
        {
            var surface = _surface;
            if (surface is null)
            {
                return;
            }

            if (router != null && router.Port == surface.Router.Port)
            {
                router.UseMaker(surface.Router);
                _attachedTo = router;
                return;
            }

            if (router != null)
            {
                Close("the local API started on another port");
                return;
            }

            if (_ownListener is null && _attachedTo is null)
            {
                var started = ApiServer.Start(surface.Router.Port, _mode(), surface.Router.Handle, Diagnose);
                if (started.IsRunning && started.Transport != null)
                {
                    _ownListener = started.Transport;
                    return;
                }

                Close("its listener could not be restarted: " + started.Detail);
            }
        }

        /// <summary>The API is stopping: Pack Maker on its listener closes with it.</summary>
        /// <param name="reason">The API's reason.</param>
        internal static void OnApiStopping(string reason)
        {
            if (_attachedTo != null)
            {
                Close("closed with the local API: " + reason);
            }
        }

        /// <summary>
        /// <c>maker trace on|off</c>: one <c>MAKER-TRACE</c> line per Pack Maker request (phases and size) and the page's own
        /// <c>MAKER-WEB</c> lines, in the console and LogOutput.log. Off at every game start; failures are logged either way.
        /// </summary>
        /// <param name="arg"><c>on</c>, <c>off</c>, or null to report.</param>
        /// <returns>One line for the console.</returns>
        internal static string Trace(string? arg)
        {
            switch (arg?.ToLowerInvariant())
            {
                case null:
                    return "Pack Maker trace is " + (MakerRequestTrace.Enabled ? "on" : "off") + " (maker trace on|off)";
                case "on":
                case "off":
                    var on = arg.ToLowerInvariant() == "on";
                    MakerRequestTrace.Enabled = on;
                    MakerBackends.Publish("maker.trace", new Dictionary<string, object?> { ["on"] = on });
                    ConsoleEvents.Emit(ApiAuditLog.Sys, "makerTrace", ConsoleTags.Api, "Pack Maker trace " + (on ? "on" : "off"), null, ("on", on));
                    return on
                        ? "Pack Maker trace on: one MAKER-TRACE line per request and the page's MAKER-WEB lines go to the console and LogOutput.log (an open page starts sending within a few seconds; 'maker trace off' stops it)"
                        : "Pack Maker trace off (MAKER-FAIL lines for failed requests stay on)";
                default:
                    return "usage: maker trace on | maker trace off";
            }
        }

        /// <summary>The <c>maker status</c> text.</summary>
        internal static string Status()
        {
            var surface = _surface;
            var webRoot = WebRoot;
            var web = "web files " + (webRoot ?? "?") + (Directory.Exists(webRoot ?? string.Empty) ? " (present)" : " (MISSING)");
            var backends = "catalog " + (MakerBackends.Catalog != null ? "installed" : "not installed") +
                           ", editor " + (MakerBackends.Editor != null ? "installed" : "not installed");
            var trace = "trace " + (MakerRequestTrace.Enabled ? "on" : "off") + " (maker trace on|off)";
            if (surface is null)
            {
                return "Pack Maker closed; type 'maker' to open " + MakerDefaults.WebUrl(_port()) + "\n" + backends + "\n" + web + "\n" + trace;
            }

            var router = surface.Router;
            var ring = surface.Events.Ring;
            var listener = _attachedTo != null
                ? "on the local API's listener"
                : "own listener (" + (_ownListener?.Name ?? "-") + "; requests " + router.Counters.Total + ": " + router.Counters.Describe() + ")";
            return "Pack Maker open at " + surface.Url + " since " + _openedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) +
                   ", " + listener +
                   "\nevents: epoch " + ring.Epoch + ", latest " + ring.Latest + ", dropped " + ring.DroppedTotal + ", refused " + surface.Events.Refused +
                   "; long-polls " + router.Waiting + ", backend calls running " + router.BackendCalls +
                   "\n" + backends + "\n" + web + "\n" + trace;
        }

        private static MakerSurface NewSurface(int port) =>
            new MakerSurface(new MakerRouterOptions
            {
                Port = port,
                WebRoot = WebRoot,
                Audit = new ApiAuditLog(SystemApiClock.Instance, () => _consoleReads()),
                Diagnostics = Diagnose,
            });

        private static void OpenBrowser(string url)
        {
            // Application.OpenURL is Unity API: main thread only. Console commands already run there; the hop
            // makes that a guarantee rather than an assumption.
            MainThread.RunOrPost("maker.openBrowser", () =>
            {
                // On macOS, `open` hands the URL to the default browser and brings it to the front, so the page is
                // not left behind the game window; anything else (or a failed launch) uses Unity's own call.
                if (Application.platform == RuntimePlatform.OSXPlayer && OpenWithMacOpen(url)) return;
                Application.OpenURL(url);
            }, ApiFeature.Owner);
        }

        private static bool OpenWithMacOpen(string url)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/open", "\"" + url + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return process != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Warn(string name, string primary, params (string Key, object? Value)[] fields)
        {
            var shown = new List<string>(fields.Length);
            foreach (var (key, _) in fields)
            {
                shown.Add(key);
            }

            ConsoleEvents.EmitAt(ApiAuditLog.Sys, name, ConsoleTags.Api, ConsoleLevel.Warn, primary, string.Join(",", shown), fields);
        }

        private static void Diagnose(string message) =>
            ConsoleEvents.File(ApiAuditLog.Sys, "diagnostic", ("message", "maker: " + message));
    }
}
