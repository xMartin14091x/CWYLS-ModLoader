using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Input;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>What a hotkey registration means for an id that may already be taken.</summary>
    public enum HotkeyClaim
    {
        /// <summary>Nobody holds the id: register it.</summary>
        New = 0,

        /// <summary>The same owner registers it again (a plugin whose Awake ran twice): replace its action.</summary>
        SameOwner = 1,

        /// <summary>Another owner holds it: refuse, never replace.</summary>
        OtherOwner = 2,
    }

    /// <summary>
    /// The rules the hotkey registry applies when a feature or a mod claims an id and chords. Pure;
    /// the Unity-side registry (<c>CwyLs.ModLoader.Input.Hotkeys</c>) only maps its bindings onto these.
    /// An owner is the config file the keys are bound in (its path), so every mod is its own owner
    /// and the framework's features share one.
    /// </summary>
    public static class HotkeyClaims
    {
        /// <summary>Prefix of a mod hotkey's registry id: <c>mod:&lt;modId&gt;/&lt;key&gt;</c>.</summary>
        public const string ModIdPrefix = "mod:";

        /// <summary>The config section a mod's hotkeys are bound in, inside the mod's own .cfg.</summary>
        public const string ModConfigSection = "Hotkeys";

        /// <summary>
        /// Where a hotkey's two chords live in its owner's config file. A mod hotkey
        /// (<c>mod:&lt;modId&gt;/&lt;key&gt;</c>) is bound in the mod's own .cfg as section
        /// <see cref="ModConfigSection"/>, key <c>&lt;key&gt;</c> - the mod id is the file already.
        /// Any other id keeps <paramref name="frameworkSection"/> and the id itself.
        /// </summary>
        /// <param name="id">The registry id.</param>
        /// <param name="frameworkSection">The section for framework keys, e.g. <c>Keys</c>.</param>
        /// <param name="section">The config section.</param>
        /// <param name="key">The config key prefix (the registry adds <c>.Primary</c> and <c>.Alt</c>).</param>
        public static void ConfigSlot(string id, string frameworkSection, out string section, out string key)
        {
            var slash = id?.IndexOf('/') ?? -1;
            if (id != null && id.StartsWith(ModIdPrefix, StringComparison.Ordinal) &&
                slash > ModIdPrefix.Length && slash < id.Length - 1)
            {
                section = ModConfigSection;
                key = id.Substring(slash + 1);
                return;
            }

            section = frameworkSection;
            key = id ?? string.Empty;
        }

        /// <summary>Decides what registering <paramref name="id"/> for <paramref name="owner"/> means.</summary>
        /// <param name="id">The id being registered.</param>
        /// <param name="owner">Who registers it (a config file path).</param>
        /// <param name="held">Every registered id with its owner.</param>
        /// <param name="holder">The owner that holds the id, or null when nobody does.</param>
        /// <returns>The verdict.</returns>
        public static HotkeyClaim Decide(
            string id,
            string? owner,
            IEnumerable<KeyValuePair<string, string>>? held,
            out string? holder)
        {
            holder = null;
            if (held is null)
            {
                return HotkeyClaim.New;
            }

            foreach (var pair in held)
            {
                if (!string.Equals(pair.Key, id, StringComparison.Ordinal))
                {
                    continue;
                }

                holder = pair.Value;
                return string.Equals(pair.Value ?? string.Empty, owner ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    ? HotkeyClaim.SameOwner
                    : HotkeyClaim.OtherOwner;
            }

            return HotkeyClaim.New;
        }

        /// <summary>
        /// The other bindings that already use one of these chords, e.g. <c>console.toggle (Ctrl+Shift+C)</c>.
        /// A clash is a warning, not a refusal: both keys fire, and the player can rebind either.
        /// Unset chords never clash.
        /// </summary>
        /// <param name="id">The id being registered; its own binding is skipped.</param>
        /// <param name="primary">Its primary chord.</param>
        /// <param name="alternate">Its alternate chord.</param>
        /// <param name="others">Every registered binding.</param>
        /// <returns>One entry per clashing binding and chord, in registry order. Never null.</returns>
        public static IReadOnlyList<string> Clashes(
            string id,
            HotkeyChord? primary,
            HotkeyChord? alternate,
            IEnumerable<HotkeyInfo>? others)
        {
            var clashes = new List<string>();
            if (others is null)
            {
                return clashes;
            }

            foreach (var other in others)
            {
                if (other is null || string.Equals(other.Id, id, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var mine in new[] { primary, alternate })
                {
                    if (mine is null || !mine.IsSet)
                    {
                        continue;
                    }

                    if (mine.Equals(other.Primary) || mine.Equals(other.Alternate))
                    {
                        var entry = other.Id + " (" + mine.Format() + ")";
                        if (!clashes.Contains(entry))
                        {
                            clashes.Add(entry);
                        }
                    }
                }
            }

            return clashes;
        }
    }
}
