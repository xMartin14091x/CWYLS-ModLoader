using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Where the game-side backends of the Phases D-S facets come from (EditData, Audio, UiSkin,
    /// Camera, VoiceStream, Mic, Api). The feature that implements a facet registers its backend
    /// from its own <c>Initialize</c>, which runs in a plugin's <c>Awake</c> before any mod loads:
    /// <c>ModFacetBackends.Use&lt;ICwyModAudio&gt;("audio", backend)</c>. A facet with no backend reports
    /// <c>IsAvailable = false</c> and its documented "not available" status.
    /// </summary>
    /// <remarks>
    /// One generic table instead of a property per facet so a new feature never edits a shared
    /// file to plug in. The original facets (events, Satone, game data, language, windows) keep their
    /// typed slots in <see cref="ModBackends"/>; Conditions, Actions and Scenes are backed by the
    /// Core registries (<c>ConditionRegistry</c>, <c>ActionServices</c>, <c>ScenarioCommandRegistry</c>).
    /// </remarks>
    public static class ModFacetBackends
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Type, object> ByType = new Dictionary<Type, object>();
        private static readonly List<string> Names = new List<string>();

        /// <summary>
        /// Registers the backend of one facet (framework assemblies only, like <c>ModBackends.Use*</c>).
        /// A second registration of the same facet type replaces the first.
        /// </summary>
        /// <typeparam name="T">The facet interface, e.g. <see cref="ICwyModAudio"/>.</typeparam>
        /// <param name="facet">A short name for logs and <c>mods status</c>, e.g. <c>audio</c>.</param>
        /// <param name="backend">The implementation.</param>
        /// <exception cref="ArgumentException"><paramref name="facet"/> is blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="backend"/> is null.</exception>
        internal static void Use<T>(string facet, T backend)
            where T : class, ICwyModFacet
        {
            if (string.IsNullOrWhiteSpace(facet))
            {
                throw new ArgumentException("a facet name is required", nameof(facet));
            }

            if (backend is null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            lock (Gate)
            {
                ByType[typeof(T)] = backend;
                if (!Names.Contains(facet))
                {
                    Names.Add(facet);
                }
            }

            ConsoleEvents.File(ModConsoleLines.LoaderSystem, "backend", ("facet", facet), ("type", backend.GetType().FullName));
        }

        /// <summary>The backend of one facet, or null when none is installed.</summary>
        /// <typeparam name="T">The facet interface.</typeparam>
        public static T? Get<T>()
            where T : class
        {
            lock (Gate)
            {
                return ByType.TryGetValue(typeof(T), out var backend) ? backend as T : null;
            }
        }

        /// <summary>The facets that have a backend, e.g. <c>audio,uiSkin</c>; empty when none.</summary>
        public static string Describe()
        {
            lock (Gate)
            {
                return string.Join(",", Names);
            }
        }
    }

    /// <summary>
    /// The per-mod side of a facet backed by <see cref="ModFacetBackends"/>: availability and status
    /// from the backend when there is one, the documented "not available" answer otherwise.
    /// </summary>
    /// <typeparam name="T">The facet interface.</typeparam>
    internal abstract class ModFacetProxy<T> : ICwyModFacet
        where T : class, ICwyModFacet
    {
        private readonly string _facet;

        protected ModFacetProxy(ModHelper helper, string facet, string unavailable)
        {
            Helper = helper;
            _facet = facet;
            Unavailable = unavailable;
        }

        /// <summary>The documented status when no backend is installed.</summary>
        public string Unavailable { get; }

        public bool IsAvailable => Backend?.IsAvailable ?? false;

        public string Status => Backend?.Status ?? Unavailable;

        protected ModHelper Helper { get; }

        protected T? Backend => ModFacetBackends.Get<T>();

        /// <summary>The backend, or null after one <c>MODS</c> warning per mod and facet.</summary>
        protected T? BackendFor(string call)
        {
            var backend = Backend;
            if (backend is null)
            {
                Helper.WarnOnce(_facet, Unavailable + "; " + call + " does nothing");
            }

            return backend;
        }
    }
}
