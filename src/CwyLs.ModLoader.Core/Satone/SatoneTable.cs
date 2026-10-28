using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// The Satone table, embedded in this assembly: every expression, body motion and AI
    /// state of the game, named. This is what lets a mod write <c>Satone.Face("smile")</c> instead
    /// of <c>ChangeHeroineFacialAnimation(10)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table lists the game's own facial, motion and state names (game 1.17.3); a game update
    /// that changes them needs a new table.
    /// </para>
    /// <para>
    /// Loading is lazy, once, and never throws: a missing or broken resource leaves
    /// <see cref="Loaded"/> false and <see cref="LoadError"/> set, and every query answers "no".
    /// The alternative - throwing out of a static constructor during a plugin's Awake - would take
    /// down the adapter for a data problem.
    /// </para>
    /// </remarks>
    public static class SatoneTable
    {
        /// <summary>Name of the embedded resource this class reads.</summary>
        public const string ResourceName = "CwyLs.ModLoader.Core.Satone.satone-table.json";

        private static readonly object Gate = new object();
        private static SatoneCatalog? _catalog;
        private static string? _loadError;
        private static bool _attempted;

        /// <summary>
        /// The parsed table, or an empty one when it could not be read (see <see cref="LoadError"/>).
        /// </summary>
        public static SatoneCatalog Catalog
        {
            get
            {
                lock (Gate)
                {
                    if (!_attempted)
                    {
                        _attempted = true;
                        Load();
                    }

                    return _catalog ??= Fallback();
                }
            }
        }

        /// <summary>True when the embedded table parsed.</summary>
        public static bool Loaded
        {
            get
            {
                _ = Catalog;
                lock (Gate)
                {
                    return _loadError is null;
                }
            }
        }

        /// <summary>Why the table could not be read, or null when it was.</summary>
        public static string? LoadError
        {
            get
            {
                _ = Catalog;
                lock (Gate)
                {
                    return _loadError;
                }
            }
        }

        /// <summary>Every expression, in id order.</summary>
        public static IReadOnlyList<SatoneFacial> Facials => Catalog.Facials;

        /// <summary>Every body motion, in id order.</summary>
        public static IReadOnlyList<SatoneMotion> Motions => Catalog.Motions;

        /// <summary>Every <c>HeroineAI.ActionStateType</c> member, in id order.</summary>
        public static IReadOnlyList<SatoneState> States => Catalog.States;

        /// <summary>Looks up an expression by name, alias or id. See <see cref="SatoneCatalog.TryFacial"/>.</summary>
        /// <param name="nameOrId">e.g. <c>smile</c> or <c>10</c>.</param>
        /// <param name="facial">The row, or null.</param>
        public static bool TryFacial(string? nameOrId, out SatoneFacial? facial) =>
            Catalog.TryFacial(nameOrId, out facial);

        /// <summary>Looks up a body motion by name, alias or id. See <see cref="SatoneCatalog.TryMotion"/>.</summary>
        /// <param name="nameOrId">e.g. <c>wild001_stretch_shoulder</c> or <c>51</c>.</param>
        /// <param name="motion">The row, or null.</param>
        public static bool TryMotion(string? nameOrId, out SatoneMotion? motion) =>
            Catalog.TryMotion(nameOrId, out motion);

        /// <summary>Looks up an AI state by its <c>ActionStateType</c> value.</summary>
        /// <param name="id">The enum value.</param>
        /// <param name="state">The row, or null.</param>
        public static bool TryState(int id, out SatoneState? state) => Catalog.TryState(id, out state);

        /// <summary>Canonical names close to <paramref name="name"/>, best first.</summary>
        /// <param name="name">What the caller typed.</param>
        /// <param name="max">How many names at most.</param>
        public static IReadOnlyList<string> Suggest(string? name, int max = SatoneCatalog.DefaultSuggestions) =>
            Catalog.Suggest(name, max);

        /// <summary>
        /// A one-line summary for the startup log, e.g.
        /// <c>satone table: 20 facials, 181 motions, 32 states (schema 1)</c>.
        /// </summary>
        public static string Summary()
        {
            var catalog = Catalog;
            var error = LoadError;
            return error is null
                ? $"satone table: {catalog.Facials.Count} facials, {catalog.Motions.Count} motions, "
                  + $"{catalog.States.Count} states (schema {catalog.Schema})"
                : "satone table UNAVAILABLE: " + error;
        }

        /// <summary>Test hook: forget the loaded table so the next query re-reads the resource.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _catalog = null;
                _loadError = null;
                _attempted = false;
            }
        }

        private static void Load()
        {
            try
            {
                var assembly = typeof(SatoneTable).Assembly;
                using var stream = assembly.GetManifestResourceStream(ResourceName);
                if (stream is null)
                {
                    _loadError =
                        $"embedded resource '{ResourceName}' is not in {assembly.GetName().Name}.dll; "
                        + "update the loader";
                    return;
                }

                using var reader = new StreamReader(stream);
                _catalog = SatoneCatalog.Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                // Logged by the caller via LoadError; Core has no logger of its own, and a silent
                // catch here would turn a broken table into "no motions exist".
                _loadError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static SatoneCatalog Fallback() => SatoneCatalog.Parse(
            "{\"schema\":1,\"facials\":[],\"motions\":[],\"states\":[]}");
    }
}
