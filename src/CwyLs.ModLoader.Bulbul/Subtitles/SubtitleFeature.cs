using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Subtitles;
using HarmonyLib;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// Subtitles through the game's own text box, and nowhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game already draws every story line in its own box, so the framework's job is to put the
    /// right language <em>in</em> that box and leave it there long enough to read:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="SubtitleNative"/> serves a pack language's rows through
    /// <c>LocalizationMasterWrapper.Get</c> and records what the box shows;
    /// </description></item>
    /// <item><description>
    /// <see cref="SubtitleHold"/> keeps every line the framework causes to be shown up for at least
    /// <c>max(2.5 s, characters / 15 per second)</c>;
    /// </description></item>
    /// <item><description>
    /// the text tables are the packs' <c>data/subtitles/&lt;code&gt;.json</c>
    /// (<see cref="MasterDataSubtitleSource"/>) and <c>Satone.Say</c>'s own lines.
    /// </description></item>
    /// </list>
    /// <para>
    /// There is no overlay panel any more: a line the game plays without its box (a clip played
    /// outside a scenario, a <c>Satone.Say</c> line that fell back to voice only) is not drawn by
    /// the framework (see the module README for the inventory of such lines), and the retired
    /// <see cref="Show"/> draws nothing.
    /// </para>
    /// <para>
    /// Pack languages and the text seam are two halves of one feature: the adapter plugin
    /// initialises <see cref="LanguageFeature"/> just before this class (<c>BulbulPlugin.Awake</c>),
    /// and the seam reads its registry on every call.
    /// </para>
    /// </remarks>
    public static class SubtitleFeature
    {
        /// <summary>Config section holding the subtitle settings.</summary>
        public const string ConfigSection = "Subtitles";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the console mirror.</summary>
        public const string ConfigKeyMirrorToConsole = "MirrorToConsole";

        /// <summary>Config key of the reading-time hold.</summary>
        public const string ConfigKeyHoldForReading = "HoldForReading";

        /// <summary>Config key of the script line-break helpers.</summary>
        public const string ConfigKeyLineBreaks = "LineBreaks";

        /// <summary>Prefix of every console line this feature writes, so the log is greppable.</summary>
        public const string LogPrefix = "SUBTITLE";

        /// <summary>The <c>sys</c> tag the diagnostic events are published under.</summary>
        public const string TraceSys = CWYEvents.SatoneTraceSystem;

        /// <summary>The <c>name</c> the diagnostic events are published under.</summary>
        public const string TraceName = "subtitle";

        private static readonly object Gate = new object();
        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _mirrorToConsole;
        private static ConfigEntry<bool>? _holdForReading;
        private static ConfigEntry<bool>? _lineBreaks;
        private static ConfigFile? _ownConfig;
        private static Harmony? _harmony;
        private static bool _initialized;
        private static bool _enabledOverride = true;

        /// <summary>True once <see cref="Initialize"/> has run.</summary>
        public static bool IsReady { get; private set; }

        /// <summary>
        /// True when the framework holds its lines in the box for reading time and mirrors them to
        /// the console (<c>Subtitles.Enabled</c>, default true). A pack language's text is served
        /// either way: switching this off must not turn a chosen language into Japanese.
        /// </summary>
        public static bool Enabled
        {
            get => _enabledOverride && (_enabled?.Value ?? true);
            set => _enabledOverride = value;
        }

        /// <summary>True when every displayed translation is also written to the console (default true).</summary>
        public static bool MirrorToConsole => _mirrorToConsole?.Value ?? true;

        /// <summary>True when the reading-time hold is on (<c>Subtitles.HoldForReading</c>, default true).</summary>
        public static bool HoldForReading => _holdForReading?.Value ?? true;

        /// <summary>
        /// True when a pack language's declared line-break helper runs on its text
        /// (<c>Subtitles.LineBreaks</c>, default true).
        /// </summary>
        public static bool LineBreaks => _lineBreaks?.Value ?? true;

        /// <summary>
        /// Binds the config, registers the checks, verifies and applies the text-box seams. Called
        /// once from the adapter plugin's Awake, after <see cref="LanguageFeature.Initialize"/>;
        /// idempotent.
        /// </summary>
        /// <param name="config">
        /// Config file the <c>Subtitles.*</c> entries are bound in; when omitted the framework binds
        /// them in its own .cfg.
        /// </param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug($"{LogPrefix} already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            if (!LanguageFeature.IsReady)
            {
                // Not fatal: with no registry the text seam serves no pack language and the game
                // keeps its own. It means the adapter's start-up order is wrong.
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix} started before LanguageFeature.Initialize; no pack language is served until "
                    + "it runs (BulbulPlugin.Awake initialises Language first)");
            }

            SubtitleSources.ErrorSink = message => CwyLsModLoader.Log.LogWarning(LogPrefix + " " + message);
            SubtitleSmokeChecks.RegisterAll();

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (subtitles)", SubtitlePatchTargets.All);
            PatchTargetVerifier.VerifyAll(SubtitlePatchTargets.All, new ReflectionMemberResolver());

            _harmony = new Harmony(BulbulPlugin.PluginGuid + ".subtitles");
            SubtitleNative.Apply(_harmony);
            SubtitleHold.Apply(_harmony);
            MasterDataSubtitleSource.Register();

            Subscriptions.Add(CWYEvents.Subscribe<RoomReady>(_ => SubtitleNative.RememberPlaceholders()));
            Application.quitting += Shutdown;
            IsReady = true;

            ConsoleEvents.Setup(
                "subtitles",
                "text box only",
                ("enabled", Enabled),
                ("hold", HoldForReading
                    ? string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "max({0}s, chars/{1}, clip+{2}s)",
                        ReadingTime.MinimumSeconds,
                        ReadingTime.CharactersPerSecond,
                        ReadingTime.VoiceTailSeconds)
                    : "off"),
                ("seams", SubtitleNative.Applied && SubtitleHold.Applied ? "ok" : SubtitleNative.Detail + "; " + SubtitleHold.Detail),
                ("sources", string.Join(", ", SubtitleSources.Names)));
        }

        /// <summary>
        /// Retired with the overlay panel: the framework shows lines only in the game's own text
        /// box, and a line the game does not put there is not drawn. Nothing in the framework calls
        /// it any more; it is kept, behind <see cref="Subtitles.Show"/>, so a mod written against
        /// the old API still compiles and gets an honest <c>false</c>. To show a line, use
        /// <c>Satone.Say</c>.
        /// </summary>
        /// <param name="text">The text a caller wanted shown.</param>
        /// <param name="seconds">Ignored.</param>
        /// <param name="lang">The language the caller named, for the trace.</param>
        /// <returns>Always false: nothing was shown.</returns>
        public static bool Show(string? text, double seconds = 0d, string? lang = null)
        {
            ConsoleEvents.File(
                TraceSys,
                TraceName,
                ("text", text),
                ("lang", lang),
                ("shown", false),
                ("reason", "no overlay: only the game's own text box shows lines"));
            return false;
        }

        /// <summary>Unsubscribes. Called on <see cref="Application.quitting"/>.</summary>
        public static void Shutdown()
        {
            foreach (var subscription in Subscriptions)
            {
                try
                {
                    subscription.Dispose();
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"{LogPrefix} a subscription could not be disposed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Subscriptions.Clear();
            IsReady = false;
        }

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();

            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Hold the lines the framework puts in the game's text box for reading time and mirror "
                + "them to the console. A chosen pack language's text is served either way.");

            _mirrorToConsole = file.Bind(
                ConfigSection,
                ConfigKeyMirrorToConsole,
                true,
                "Write every translated line the game's text box displays to the console as 'SUBTITLE ...'.");

            _holdForReading = file.Bind(
                ConfigSection,
                ConfigKeyHoldForReading,
                true,
                "Keep every line the framework causes to be shown (Satone.Say lines, and story lines while a "
                + "pack language is active) in the text box for at least max(2.5 s, characters / 15 per second, "
                + "and, in a pack language, its voice clip + 0.3 s).");

            _lineBreaks = file.Bind(
                ConfigSection,
                ConfigKeyLineBreaks,
                true,
                "Run a pack language's declared line-break helper (e.g. Thai syllable breaks) on its text so "
                + "long lines wrap somewhere sensible. Turn off if a line breaks in the wrong place.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".subtitles.cfg"),
                saveOnInit: true);
    }
}
