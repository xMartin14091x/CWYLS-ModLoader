using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Subtitles;
using HarmonyLib;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// Pack languages: languages declared by content packs become real, selectable game languages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every loaded pack's <c>language.json</c> is read at startup into a
    /// <see cref="LanguageRegistry"/>; each declared language gets a runtime
    /// <c>Bulbul.GameLanguageType</c> value (100, 101, ... in pack order), a button in the game's own
    /// language selector (<see cref="LanguageSelector"/>), text through the game's own text seam with
    /// fallback along its declared chain (<c>Subtitles/SubtitleNative</c> + <see cref="LanguageText"/>),
    /// and its fonts and mark-positioning table applied to the game's font assets
    /// (<see cref="LanguageFonts"/>).
    /// </para>
    /// <para>
    /// The save file only ever holds one of the game's own languages (<see cref="LanguageChoice"/>);
    /// the player's choice is persisted as a code in <c>Language.Selected</c>, so the game without
    /// this framework still opens, in the chosen language's base language.
    /// </para>
    /// <para>
    /// Static and initialised once from the adapter's Awake (<c>BulbulPlugin.Awake</c>, just before
    /// <c>SubtitleFeature.Initialize</c>, whose text seam and reading-time hold read the registry),
    /// because this game destroys BepInEx plugin components about 0.2 s after boot and because
    /// the patches must be in place before <c>EntryBehavior</c> calls
    /// <c>SaveDataManager.LoadSetting</c>.
    /// </para>
    /// </remarks>
    public static class LanguageFeature
    {
        /// <summary>Config section holding the language settings.</summary>
        public const string ConfigSection = "Language";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the persisted choice.</summary>
        public const string ConfigKeySelected = "Selected";

        /// <summary>Config key of the web-request guard.</summary>
        public const string ConfigKeySafeWebLanguage = "SafeWebLanguage";

        /// <summary>Config key of the mark-placement mode.</summary>
        public const string ConfigKeyMarkPlacement = "MarkPlacement";

        /// <summary>The <c>sys</c> tag this feature's events are published under.</summary>
        public const string TraceSys = "language";

        /// <summary>Prefix of every log line this feature writes.</summary>
        public const string LogPrefix = "LANGUAGE";

        private static readonly object Gate = new object();
        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<string>? _selected;
        private static ConfigEntry<bool>? _safeWebLanguage;
        private static ConfigEntry<MarkPlacementMode>? _markPlacement;
        private static ConfigFile? _ownConfig;
        private static bool _initialized;
        private static Harmony? _harmony;

        /// <summary>True once <see cref="Initialize"/> has run.</summary>
        public static bool IsReady { get; private set; }

        /// <summary>The master switch (<c>Language.Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>
        /// True when the six web-request constructors send the base language instead of a pack value
        /// (<c>Language.SafeWebLanguage</c>, default true).
        /// </summary>
        public static bool SafeWebLanguage => _safeWebLanguage?.Value ?? true;

        /// <summary>Which offsets of a mark table are applied (<c>Language.MarkPlacement</c>, default Full).</summary>
        public static MarkPlacementMode MarkPlacement => _markPlacement?.Value ?? MarkPlacementMode.Full;

        /// <summary>The languages the loaded packs declare. Never null; empty before <see cref="Initialize"/>.</summary>
        public static LanguageRegistry Registry { get; private set; } = new LanguageRegistry();

        /// <summary>The selected/applied/persisted state. Never null.</summary>
        public static LanguageChoice Choice { get; private set; } = new LanguageChoice(new LanguageRegistry());

        /// <summary>Each registered language's parsed mark table, by code.</summary>
        internal static IReadOnlyDictionary<string, MarkTable> MarkTables { get; private set; } =
            new Dictionary<string, MarkTable>();

        /// <summary>
        /// True when every mandatory seam verified and patched, so a pack language may be applied.
        /// False leaves the game in its own languages.
        /// </summary>
        public static bool CanApply { get; private set; }

        /// <summary>The registered languages, in value order. Shorthand for <c>Registry.Languages</c>.</summary>
        public static IReadOnlyList<PackLanguage> Languages => Registry.Languages;

        /// <summary>
        /// The pack language the game is displaying right now, or null when it is in one of its own
        /// languages. Reads the game's live setting, so it is right whoever changed it.
        /// </summary>
        public static PackLanguage? Active =>
            CanApply && TryCurrentValue(out var value) && Registry.TryGet(value, out var language) ? language : null;

        /// <summary>
        /// The code of the language the game is displaying: a pack code such as <c>th</c>, or one of
        /// the game's own (<c>en</c>, <c>ja</c>, ...); empty before the save has loaded.
        /// </summary>
        public static string CurrentCode => TryCurrentValue(out var value) ? Registry.ToCode(value) : string.Empty;

        /// <summary>
        /// Binds the config, reads the packs' declarations, verifies and applies the patches, and
        /// subscribes to <c>RoomReady</c> for the font work. Called once from <c>BulbulPlugin.Awake</c>
        /// before the subtitle feature; idempotent (a second call is logged at debug level and ignored).
        /// </summary>
        /// <param name="config">
        /// Config file the <c>Language.*</c> entries are bound in; when omitted the framework binds
        /// them in its own <c>.language.cfg</c>.
        /// </param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug(LogPrefix + " already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            LanguageSmokeChecks.RegisterAll();

            Registry = LanguagePackReader.ReadAll(out var marks);
            MarkTables = marks;
            foreach (var rejection in Registry.Rejected)
            {
                CwyLsModLoader.Log.LogWarning(LogPrefix + " " + rejection);
            }

            if (!Enabled)
            {
                Choice = new LanguageChoice(Registry, enabled: false, selected: _selected?.Value);
                ConsoleEvents.Setup(
                    "language",
                    "disabled (" + ConfigSection + "." + ConfigKeyEnabled + "=false); the game keeps its own languages",
                    ("declared", Registry.Count));
                IsReady = true;
                return;
            }

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (language)", LanguagePatchTargets.Mandatory);
            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (language, optional)", LanguagePatchTargets.Optional);
            var mandatory = PatchTargetVerifier.VerifyAll(LanguagePatchTargets.Mandatory, new ReflectionMemberResolver());
            var optional = PatchTargetVerifier.VerifyAll(LanguagePatchTargets.Optional, new ReflectionMemberResolver());

            _harmony = new Harmony(BulbulPlugin.PluginGuid + ".language");
            var patched = LanguagePatches.Apply(_harmony, Registry.Count > 0, optional.AllFound);

            CanApply = mandatory.AllFound && patched;
            Choice = new LanguageChoice(Registry, enabled: true, selected: _selected?.Value, targetsVerified: CanApply);

            Subscriptions.Add(CWYEvents.Subscribe<RoomReady>(_ => LanguageFonts.Apply("roomReady")));
            Application.quitting += Shutdown;
            IsReady = true;

            ConsoleEvents.Setup(
                "language",
                Registry.Count == 0
                    ? "no pack declares a language"
                    : Registry.Count + " pack language(s) ready",
                ("languages", Describe()),
                ("selected", Choice.Selected.Length == 0 ? "game" : Choice.Selected),
                ("patches", LanguagePatches.Detail),
                ("rejected", Registry.Rejected.Count));
        }

        /// <summary>
        /// Switches the game to a language by code - a pack language or one of the game's own - the
        /// way the selector does, through <c>SettingService.ChangeGameLanguage</c>.
        /// </summary>
        /// <param name="code">A language code, e.g. <c>th</c> or <c>en</c>.</param>
        /// <param name="problem">Why it could not, or empty.</param>
        /// <returns>True when the game was asked to switch.</returns>
        public static bool Select(string? code, out string problem)
        {
            if (!Registry.TryFromCode(code, out var value))
            {
                problem = "'" + code + "' is neither a pack language nor one of the game's own";
                return false;
            }

            if (Registry.IsPackLanguage(value) && !CanApply)
            {
                problem = "pack languages are inactive: " + (Enabled ? "a mandatory patch target is missing" : "Language.Enabled=false");
                return false;
            }

            if (!GameScopes.TryRoom<Game.SettingService>(out var service) || service is null)
            {
                problem = "the room is not loaded, so the game's SettingService is not reachable";
                return false;
            }

            try
            {
                service.ChangeGameLanguage((Game.GameLanguageType)value);
                problem = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogWarning(LogPrefix + " could not switch to '" + code + "': " + problem);
                return false;
            }
        }

        /// <summary>
        /// Records a language change the game made (a selector click, a reset) and rewrites
        /// <c>Language.Selected</c> when the choice changed. Called by the <c>LanguageSupplier.Set</c> postfix.
        /// </summary>
        /// <param name="value">The new value, as an int.</param>
        /// <param name="via">What changed it, for the log.</param>
        internal static void Observe(int value, string via)
        {
            if (!Choice.Observe(value))
            {
                return;
            }

            if (_selected != null)
            {
                _selected.Value = Choice.Selected;
            }

            ConsoleEvents.Emit(
                TraceSys,
                "selected",
                ConsoleTags.Language,
                "selected " + (Choice.Selected.Length == 0 ? Registry.ToCode(value) : Choice.Selected)
                    + " (persisted as " + LanguageCodes.ToCode(Choice.Persisted) + ")",
                "via,value,saved",
                ("via", via),
                ("value", value),
                ("saved", LanguageCodes.ToName(Choice.Persisted)),
                ("config", Choice.Selected));
        }

        /// <summary>Takes back every in-memory change to the game's font assets. Called on quit.</summary>
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
                    CwyLsModLoader.Log.LogWarning(LogPrefix + " a subscription could not be disposed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Subscriptions.Clear();
            LanguageFonts.Remove();
        }

        /// <summary>The game's current language value, when the save manager exists.</summary>
        internal static bool TryCurrentValue(out int value)
        {
            value = GameLanguages.None;
            try
            {
                if (!Game.SaveDataManager.HasInstance)
                {
                    return false;
                }

                value = (int)Game.SaveDataManager.Instance.SettingData.GameLanguage.Value;
                return true;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(LogPrefix + " the game language is not readable yet: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string Describe()
        {
            if (Registry.Count == 0)
            {
                return "none";
            }

            var parts = new List<string>(Registry.Count);
            foreach (var language in Registry.Languages)
            {
                parts.Add(language.ToString());
            }

            return string.Join(", ", parts);
        }

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();

            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Offer the languages content packs declare (language.json) in the game's own language "
                + "selector. Off leaves the game with its own seven languages.");

            _selected = file.Bind(
                ConfigSection,
                ConfigKeySelected,
                string.Empty,
                "The pack language the player chose, as a code (e.g. th). Empty follows the game's own "
                + "setting. Written by the framework when the player picks a language; the game's save "
                + "file only ever holds one of the game's own languages.");

            _safeWebLanguage = file.Bind(
                ConfigSection,
                ConfigKeySafeWebLanguage,
                true,
                "Send the pack language's base language (e.g. English) instead of its runtime value in "
                + "the game's login, news and purchase requests.");

            _markPlacement = file.Bind(
                ConfigSection,
                ConfigKeyMarkPlacement,
                MarkPlacementMode.Full,
                "How a pack's mark-positioning table is applied to the game's font assets: Full (the "
                + "font's own design), VerticalOnly (raise stacked marks, no sideways shift) or Off.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".language.cfg"),
                saveOnInit: true);
    }
}
