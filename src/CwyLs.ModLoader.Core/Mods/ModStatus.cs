using System;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>Where a code mod is in its life.</summary>
    public enum ModState
    {
        /// <summary>Resolved and waiting for the loader.</summary>
        Pending = 0,

        /// <summary><c>Entry</c> returned normally; the mod is running.</summary>
        Loaded = 1,

        /// <summary>Refused before any of its code ran; see <see cref="ModSkipReason"/>.</summary>
        Skipped = 2,

        /// <summary>Its own code threw during construction or <c>Entry</c>; its registrations were removed.</summary>
        Failed = 3,

        /// <summary><c>OnShutdown</c> ran (the game is quitting).</summary>
        ShutDown = 4,
    }

    /// <summary>
    /// Why the loader refused a code mod that the pack resolver had accepted. The resolver's own
    /// reasons (a missing <c>entryDll</c> file, a framework that is too old, a missing
    /// dependency) are <see cref="PackSkipReason"/>s; these are the ones only known once the
    /// assembly is in hand. Numbered explicitly so the values stay stable.
    /// </summary>
    public enum ModSkipReason
    {
        /// <summary>The assembly has no concrete class deriving from <c>CwyMod</c> with a parameterless constructor.</summary>
        NoEntryType = 0,

        /// <summary>The assembly has more than one concrete <c>CwyMod</c> class, so the entry is ambiguous.</summary>
        MultipleEntryTypes = 1,

        /// <summary>The assembly could not be read or loaded (bad image, missing file, I/O error).</summary>
        AssemblyLoadFailed = 2,

        /// <summary>
        /// An assembly with the entry DLL's name, or the same build under another name (same
        /// MVID), is already loaded: two copies of the mod, or a name clash. A second copy would
        /// cross-wire Harmony patches, so it is never used.
        /// </summary>
        AssemblyAlreadyLoaded = 3,

        /// <summary>A required code-mod dependency was skipped or failed at load time.</summary>
        DependencyFailed = 4,

        /// <summary>Code mods are switched off (<c>[Mods] Enabled=false</c>).</summary>
        Disabled = 5,
    }

    /// <summary>The call site a failure is attributed to.</summary>
    public enum ModStage
    {
        /// <summary>The loader itself (assembly load, type scan).</summary>
        Load = 0,

        /// <summary>The entry class's constructor.</summary>
        Construct = 1,

        /// <summary><c>CwyMod.Entry</c>.</summary>
        Entry = 2,

        /// <summary><c>CwyMod.GetApi</c>.</summary>
        GetApi = 3,

        /// <summary>A game event or data-change handler.</summary>
        Event = 4,

        /// <summary>A console command handler.</summary>
        Command = 5,

        /// <summary>A hotkey callback.</summary>
        Hotkey = 6,

        /// <summary>A per-frame callback.</summary>
        Tick = 7,

        /// <summary>Work posted to the main-thread dispatcher.</summary>
        MainThread = 8,

        /// <summary>A mods-ready handler.</summary>
        ModsReady = 9,

        /// <summary><c>CwyMod.OnShutdown</c>.</summary>
        Shutdown = 10,

        /// <summary>A game action the mod registered (<c>helper.Actions</c>), run by the framework.</summary>
        Action = 11,

        /// <summary>A scenario (cutscene) command handler the mod registered (<c>helper.Scenes</c>).</summary>
        Script = 12,

        /// <summary>A condition token provider the mod registered (<c>helper.Conditions</c>).</summary>
        Token = 13,
    }

    /// <summary>
    /// One code mod's outcome, as the loader recorded it. Immutable; the loader replaces the
    /// record when the state changes.
    /// </summary>
    public sealed class ModStatus
    {
        /// <summary>Creates a record.</summary>
        /// <param name="id">The mod's id. Required.</param>
        /// <param name="version">Its version, or null when unknown.</param>
        /// <param name="state">Its state.</param>
        /// <param name="skipReason">Why it was skipped; set only for <see cref="ModState.Skipped"/>.</param>
        /// <param name="stage">Where it failed; set only for <see cref="ModState.Failed"/>.</param>
        /// <param name="detail">The specifics, for the author. Null becomes empty.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or blank.</exception>
        public ModStatus(
            string id,
            SemVer? version,
            ModState state,
            ModSkipReason? skipReason = null,
            ModStage? stage = null,
            string? detail = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a mod id is required", nameof(id));
            }

            Id = id;
            Version = version;
            State = state;
            SkipReason = state == ModState.Skipped ? skipReason : null;
            Stage = state == ModState.Failed ? stage : null;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The mod's id.</summary>
        public string Id { get; }

        /// <summary>Its version, or null when unknown.</summary>
        public SemVer? Version { get; }

        /// <summary>Its state.</summary>
        public ModState State { get; }

        /// <summary>Why it was skipped, or null.</summary>
        public ModSkipReason? SkipReason { get; }

        /// <summary>Where it failed, or null.</summary>
        public ModStage? Stage { get; }

        /// <summary>The specifics. Never null.</summary>
        public string Detail { get; }

        /// <summary>
        /// The short reason column: <c>NoEntryType</c> for a skip, <c>Entry</c> for a failure,
        /// the state otherwise.
        /// </summary>
        public string ReasonText =>
            SkipReason?.ToString() ?? (Stage is null ? State.ToString() : Stage.ToString()!);

        /// <summary><c>id@1.0.0: Skipped (NoEntryType) - detail</c>.</summary>
        public override string ToString()
        {
            var head = Version is null ? Id : Id + "@" + Version;
            var text = head + ": " + State;
            if (SkipReason != null || Stage != null)
            {
                text += " (" + ReasonText + ")";
            }

            return Detail.Length == 0 ? text : text + " - " + Detail;
        }
    }
}
