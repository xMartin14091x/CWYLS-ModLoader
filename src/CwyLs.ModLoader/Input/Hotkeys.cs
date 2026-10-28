using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Input;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CwyLs.ModLoader.Input
{
    /// <summary>
    /// The one place a framework function is bound to a key. Every function gets two chords: the
    /// <c>Ctrl+Shift+&lt;letter&gt;</c> chord that works everywhere, and the F-key as the alternate.
    /// <para>
    /// On macOS an F-key is a media key unless Fn is held, so <c>F9</c> alone reaches the system,
    /// not the game. The chord is therefore the <em>primary</em> default on both platforms -
    /// macOS and Windows .cfg files read the same, and the advertised key is the one that always
    /// works. Both chords stay live, and both are config entries the user can change
    /// (<c>[Keys] &lt;id&gt;.Primary</c> / <c>&lt;id&gt;.Alt</c>); a .cfg written by an older build
    /// is migrated once, see <see cref="HotkeyDefaults"/>.
    /// </para>
    /// <para>
    /// Polling happens once per frame on the hidden <see cref="HotkeyHost"/>, because this game
    /// destroys BepInEx plugin components shortly after boot.
    /// </para>
    /// </summary>
    public static class Hotkeys
    {
        /// <summary>Config section every framework key is bound in (a mod's keys: <see cref="HotkeyClaims.ModConfigSection"/>).</summary>
        public const string ConfigSection = "Keys";

        /// <summary>Config key suffix of the primary chord.</summary>
        public const string PrimarySuffix = ".Primary";

        /// <summary>Config key suffix of the alternate chord.</summary>
        public const string AlternateSuffix = ".Alt";

        private static readonly object Gate = new object();
        private static readonly List<HotkeyBinding> Bindings = new List<HotkeyBinding>();

        // Who registered each id: the path of the config file its chords are bound in.
        private static readonly Dictionary<string, string> Owners = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static bool _announced;

        /// <summary>How many functions are bound.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Bindings.Count;
                }
            }
        }

        /// <summary>
        /// Binds a function to two chords and starts polling. The same owner (the same config file)
        /// registering the same <paramref name="id"/> again replaces the action and keeps the
        /// existing config entries, so a plugin whose Awake runs twice cannot end up firing twice.
        /// Another owner's id is refused with one warning and never replaced;
        /// a chord another key already uses is registered, with one warning naming the other key.
        /// </summary>
        /// <param name="id">
        /// Stable id, e.g. <c>smoke.run</c>, also the config key prefix in section <c>[Keys]</c>; a mod's
        /// key is <c>mod:&lt;modId&gt;/&lt;key&gt;</c> and is bound as <c>[Hotkeys] &lt;key&gt;</c> in the mod's own .cfg. Required.
        /// </param>
        /// <param name="description">One short sentence: what pressing it does. Becomes the config comment.</param>
        /// <param name="primaryDefault">Default primary chord - normally Ctrl+Shift+letter.</param>
        /// <param name="alternateDefault">Default alternate chord - normally the F-key.</param>
        /// <param name="onPressed">What to run. Exceptions are logged, never thrown at the game.</param>
        /// <param name="owner">
        /// Config file the two entries are bound in - pass the registering plugin's <c>Config</c>,
        /// so the keys live in the .cfg of whoever owns the function.
        /// </param>
        /// <param name="legacyPrimaryDefault">
        /// What an older build used as the primary default. When the config file still holds
        /// exactly that pair, both entries are rewritten to the current defaults once and the
        /// migration is logged. Omit it (the default) to migrate nothing.
        /// </param>
        /// <param name="legacyAlternateDefault">
        /// What an older build used as the alternate default; see <paramref name="legacyPrimaryDefault"/>.
        /// </param>
        /// <returns>True when registered or replaced; false when another owner holds <paramref name="id"/> (logged once).</returns>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="onPressed"/> or <paramref name="owner"/> is null.</exception>
        public static bool Register(
            string id,
            string description,
            KeyboardShortcut primaryDefault,
            KeyboardShortcut alternateDefault,
            Action onPressed,
            ConfigFile owner,
            KeyboardShortcut legacyPrimaryDefault = default,
            KeyboardShortcut legacyAlternateDefault = default)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("a hotkey id is required", nameof(id));
            }

            if (onPressed is null)
            {
                throw new ArgumentNullException(nameof(onPressed));
            }

            if (owner is null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            // Decided before anything is bound, so a refused id leaves no entries in the other
            // owner's (or this owner's) config file.
            var ownerKey = OwnerKey(owner);
            HotkeyClaim claim;
            string? holder;
            lock (Gate)
            {
                claim = HotkeyClaims.Decide(id, ownerKey, HeldLocked(), out holder);
            }

            if (claim == HotkeyClaim.OtherOwner)
            {
                WarnOnce(
                    "id:" + id + "|" + ownerKey,
                    $"key {id} is already registered by {System.IO.Path.GetFileName(holder)}; this registration " +
                    $"(from {System.IO.Path.GetFileName(ownerKey)}) was refused, the existing key is unchanged");
                return false;
            }

            // A mod's key (mod:<modId>/<key>) lives in its own .cfg as [Hotkeys] <key>.Primary/.Alt;
            // a framework key as [Keys] <id>.Primary/.Alt in the registering plugin's .cfg.
            HotkeyClaims.ConfigSlot(id, ConfigSection, out var section, out var configKey);

            var primary = owner.Bind(
                section, configKey + PrimarySuffix, primaryDefault,
                description + " The chord to use: it works on macOS and Windows, where an F-key is a " +
                "media key on macOS unless Fn is held.");

            var alternate = owner.Bind(
                section, configKey + AlternateSuffix, alternateDefault,
                description + " Alternate chord, normally the F-key. Both are always live.");

            Migrate(id, primary, alternate, primaryDefault, alternateDefault,
                legacyPrimaryDefault, legacyAlternateDefault);

            var binding = new HotkeyBinding(id, description, primary, alternate, onPressed);

            bool announce;
            IReadOnlyList<string> clashes;
            lock (Gate)
            {
                // Again under the lock: another thread may have claimed the id since the check above.
                if (HotkeyClaims.Decide(id, ownerKey, HeldLocked(), out holder) == HotkeyClaim.OtherOwner)
                {
                    claim = HotkeyClaim.OtherOwner;
                    clashes = Array.Empty<string>();
                    announce = false;
                }
                else
                {
                    var replaced = false;
                    for (var i = 0; i < Bindings.Count; i++)
                    {
                        if (string.Equals(Bindings[i].Id, id, StringComparison.Ordinal))
                        {
                            Bindings[i] = binding;
                            replaced = true;
                            break;
                        }
                    }

                    if (!replaced)
                    {
                        Bindings.Add(binding);
                    }

                    Owners[id] = ownerKey;
                    var described = binding.Describe();
                    clashes = HotkeyClaims.Clashes(id, described.Primary, described.Alternate, DescribeAllLocked());

                    // The startup summary is logged once, on the first frame, after every plugin's
                    // Awake has had its chance to register. A key that arrives later announces itself.
                    announce = _announced;
                }
            }

            if (claim == HotkeyClaim.OtherOwner)
            {
                WarnOnce(
                    "id:" + id + "|" + ownerKey,
                    $"key {id} is already registered by {System.IO.Path.GetFileName(holder)}; this registration " +
                    $"(from {System.IO.Path.GetFileName(ownerKey)}) was refused, the existing key is unchanged");
                return false;
            }

            foreach (var clash in clashes)
            {
                WarnOnce(
                    "chord:" + id + "|" + clash,
                    $"key {id} shares a chord with {clash}; both fire on that chord. Rebind one in its .cfg ([{ConfigSection}] or a mod's [{HotkeyClaims.ModConfigSection}])");
            }

            HotkeyHost.Ensure();

            if (announce)
            {
                ConsoleEvents.Emit(
                    "keys", "registered", ConsoleTags.Keys, binding.Describe().Format(), null,
                    ("id", binding.Id));
            }

            return true;
        }

        /// <summary>The owner key of a config file: its path, so two handles on one file are one owner.</summary>
        private static string OwnerKey(ConfigFile owner)
        {
            var path = owner.ConfigFilePath;
            return string.IsNullOrEmpty(path) ? "(config " + owner.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : path;
        }

        /// <summary>Every registered id with its owner key. Caller holds <see cref="Gate"/>.</summary>
        private static List<KeyValuePair<string, string>> HeldLocked()
        {
            var held = new List<KeyValuePair<string, string>>(Owners.Count);
            foreach (var pair in Owners)
            {
                held.Add(pair);
            }

            return held;
        }

        /// <summary>Every binding described. Caller holds <see cref="Gate"/>.</summary>
        private static List<HotkeyInfo> DescribeAllLocked()
        {
            var described = new List<HotkeyInfo>(Bindings.Count);
            foreach (var existing in Bindings)
            {
                described.Add(existing.Describe());
            }

            return described;
        }

        /// <summary>One warning per key: a refused id or a shared chord is reported once per session.</summary>
        private static void WarnOnce(string key, string message)
        {
            lock (Gate)
            {
                if (!Warned.Add(key))
                {
                    return;
                }
            }

            ConsoleEvents.EmitAt("keys", "conflict", ConsoleTags.Keys, ConsoleLevel.Warn, message, null, ("key", key));
        }

        /// <summary>
        /// Every bound function and its chords, in registration order. This is what an in-game
        /// console would list.
        /// </summary>
        public static IReadOnlyList<HotkeyInfo> List()
        {
            lock (Gate)
            {
                var described = new List<HotkeyInfo>(Bindings.Count);
                foreach (var binding in Bindings)
                {
                    described.Add(binding.Describe());
                }

                return described;
            }
        }

        /// <summary>One bound function by id, or null when nothing registered that id.</summary>
        /// <param name="id">Registry id, e.g. <c>console.toggle</c>.</param>
        public static HotkeyInfo? Find(string? id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            lock (Gate)
            {
                foreach (var binding in Bindings)
                {
                    if (string.Equals(binding.Id, id, StringComparison.Ordinal))
                    {
                        return binding.Describe();
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// A BepInEx shortcut as the framework prints it. BepInEx's own <c>ToString</c> is
        /// <c>"C + LeftShift + LeftControl"</c>; this is the half a person reads,
        /// <c>Ctrl+Shift+C</c>.
        /// </summary>
        /// <param name="shortcut">The chord to describe.</param>
        public static HotkeyChord Chord(KeyboardShortcut shortcut) => HotkeyBinding.Chord(shortcut);

        /// <summary>
        /// The one-line summary: <c>KEYS: smoke.run = Ctrl+Shift+S | F9</c>. Always
        /// <see cref="HotkeyChord.Format"/>, never BepInEx's serialised form.
        /// </summary>
        public static string StartupLine() => HotkeyInfo.Line(List());

        /// <summary>
        /// Checks every chord and runs whatever fired. Called once per frame by
        /// <see cref="HotkeyHost"/>; safe to call with no bindings.
        /// </summary>
        /// <param name="frame">Unity's frame counter, for the once-per-frame rule.</param>
        internal static void Poll(int frame)
        {
            // Null until something fires: this runs every frame, so the common path allocates
            // nothing at all.
            List<HotkeyBinding>? fired = null;

            lock (Gate)
            {
                foreach (var binding in Bindings)
                {
                    if (binding.Pressed(frame))
                    {
                        (fired ??= new List<HotkeyBinding>(1)).Add(binding);
                    }
                }
            }

            if (fired is null)
            {
                return;
            }

            // Only now, because reading the focused control allocates: while the user is typing a
            // note or a todo, a letter must stay a letter.
            if (IsTyping())
            {
                foreach (var binding in fired)
                {
                    // Debug, not Info: this fires while the user types a note
                    // and is pure noise on the console; the file still has it.
                    CwyLsModLoader.Log.LogDebug($"key {binding.Id} ignored: a text field has focus");
                }

                return;
            }

            foreach (var binding in fired)
            {
                Invoke(binding);
            }
        }

        /// <summary>Marks the summary as logged; called by the host on its first frame.</summary>
        internal static bool TryMarkAnnounced()
        {
            lock (Gate)
            {
                if (_announced)
                {
                    return false;
                }

                _announced = true;
                return true;
            }
        }

        /// <summary>
        /// Rewrites a pair of config entries that still hold an older build's defaults, once.
        /// Anything the user changed is left exactly as it is; see <see cref="HotkeyDefaults"/>
        /// for the rule and its tests.
        /// </summary>
        private static void Migrate(
            string id,
            ConfigEntry<KeyboardShortcut> primary,
            ConfigEntry<KeyboardShortcut> alternate,
            KeyboardShortcut primaryDefault,
            KeyboardShortcut alternateDefault,
            KeyboardShortcut legacyPrimaryDefault,
            KeyboardShortcut legacyAlternateDefault)
        {
            try
            {
                if (!HotkeyDefaults.ShouldMigrate(
                        HotkeyBinding.Chord(primary.Value),
                        HotkeyBinding.Chord(alternate.Value),
                        HotkeyBinding.Chord(legacyPrimaryDefault),
                        HotkeyBinding.Chord(legacyAlternateDefault),
                        HotkeyBinding.Chord(primaryDefault),
                        HotkeyBinding.Chord(alternateDefault)))
                {
                    return;
                }

                primary.Value = primaryDefault;
                alternate.Value = alternateDefault;

                CwyLsModLoader.Log.LogDebug(HotkeyDefaults.MigratedLine(id));
            }
            catch (Exception ex)
            {
                // A read-only .cfg, or a value BepInEx cannot write back: the key still works with
                // whatever is bound, so this is a notice, not a failure.
                CwyLsModLoader.Log.LogWarning(
                    $"key {id} could not be migrated to the chord-first defaults: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Invoke(HotkeyBinding binding)
        {
            try
            {
                ConsoleEvents.Emit(
                    "keys", "pressed", ConsoleTags.Keys, binding.Id + " pressed", null,
                    ("id", binding.Id));
                binding.OnPressed();
            }
            catch (Exception ex)
            {
                // A hotkey action that throws must not take the frame - or the next press - down.
                CwyLsModLoader.Log.LogError(
                    $"key {binding.Id} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// True when the control with keyboard focus accepts typed text. Type names rather than
        /// types, so this assembly needs no TextMeshPro reference (see <see cref="TextEntryGuard"/>).
        /// </summary>
        private static bool IsTyping()
        {
            try
            {
                var system = EventSystem.current;
                var selected = system == null ? null : system.currentSelectedGameObject;
                if (selected == null)
                {
                    return false;
                }

                foreach (var component in selected.GetComponents<Component>())
                {
                    if (component != null && TextEntryGuard.IsTextEntryType(component.GetType().Name))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                // No EventSystem, or a UI assembly the build does not have: assume nobody is
                // typing rather than making every key dead, and say so once per press.
                CwyLsModLoader.Log.LogWarning(
                    $"could not read the focused control, hotkeys stay live: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
