using System;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// The whole policy of "a pack language is a language you can pick", as three numbers and no
    /// Unity: what the player <em>selected</em>, what is <em>applied</em> in memory, and what may
    /// be <em>persisted</em> to the game's save file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule that makes the feature safe: the game's save file never holds a language value the
    /// game does not define. With <c>(GameLanguageType)100</c> on disk and this framework absent,
    /// <c>FontSupplier</c> returns null and every localized label throws in <c>Awake</c>.
    /// So a pack language lives in memory
    /// only, the save file gets its base language (<see cref="ForSave"/>), and the choice itself is
    /// persisted as a code in the framework's own config (<see cref="Selected"/>).
    /// </para>
    /// <para>
    /// Unity-free and deterministic: <c>LanguageChoiceTests</c> exercises every transition off-engine.
    /// </para>
    /// </remarks>
    public sealed class LanguageChoice
    {
        private readonly LanguageRegistry _registry;

        /// <summary>Creates a choice.</summary>
        /// <param name="registry">The registered pack languages.</param>
        /// <param name="enabled">
        /// The feature's master switch (<c>Language.Enabled</c>). False leaves the game exactly as it ships.
        /// </param>
        /// <param name="selected">
        /// The persisted choice (<c>Language.Selected</c>): a pack language code, or null/blank for
        /// "follow the game's own setting". A built-in code also means "follow the game": the game's
        /// own save is the right place for one of its own languages.
        /// </param>
        /// <param name="targetsVerified">
        /// False when a patch target the feature needs is missing (a game update). No pack language
        /// is then applied, so a broken seam degrades to the vanilla game rather than a broken one.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
        public LanguageChoice(
            LanguageRegistry registry,
            bool enabled = true,
            string? selected = null,
            bool targetsVerified = true)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Enabled = enabled;
            TargetsVerified = targetsVerified;

            var code = SubtitleLanguages.Normalize(selected);
            Selected = SubtitleLanguages.IsInGame(code) ? string.Empty : code;
            Applied = GameLanguages.None;
        }

        /// <summary>The master switch, as given.</summary>
        public bool Enabled { get; }

        /// <summary>False when a seam the feature needs was not found in the running game.</summary>
        public bool TargetsVerified { get; }

        /// <summary>True when the feature may change anything at all.</summary>
        public bool Active => Enabled && TargetsVerified;

        /// <summary>
        /// The persisted choice: a pack language code, or the empty string for "follow the game's
        /// own language setting". Kept as written even when no loaded pack declares it, so a pack
        /// that fails to load for one session does not erase the player's choice.
        /// </summary>
        public string Selected { get; private set; }

        /// <summary>The selected language when a loaded pack declares it and the feature is active; otherwise null.</summary>
        public PackLanguage? SelectedLanguage =>
            Active && Selected.Length > 0 && _registry.TryGet(Selected, out var language) ? language : null;

        /// <summary>True when a pack language is chosen <em>and</em> may be applied.</summary>
        public bool IsPackSelected => SelectedLanguage != null;

        /// <summary>
        /// The language value in memory as far as this object knows: the result of the last
        /// <see cref="Adopt"/> or <see cref="Observe"/>. <c>None</c> before either has run.
        /// </summary>
        public int Applied { get; private set; }

        /// <summary>What <see cref="Applied"/> is allowed to become on disk.</summary>
        public int Persisted => ForSave(Applied);

        /// <summary>
        /// The language the game should be in after it has loaded <paramref name="saved"/> from the
        /// save file.
        /// </summary>
        /// <param name="saved">The value the save file held.</param>
        /// <returns>
        /// The selected pack language when there is one; the vanilla-safe value when the save somehow
        /// holds a pack-range value that is not going to be applied (never leave a value the game
        /// cannot draw in memory); otherwise the saved value, untouched.
        /// </returns>
        public int Adopt(int saved)
        {
            var selected = SelectedLanguage;
            var value = selected != null
                ? selected.Value
                : GameLanguages.IsPackRange(saved) ? _registry.SaveValueFor(saved) : saved;

            Applied = value;
            return value;
        }

        /// <summary>
        /// Records that the game's language changed - the player clicked an entry in the selector,
        /// or another part of the game called <c>LanguageSupplier.Set</c>.
        /// </summary>
        /// <param name="value">The new <c>Bulbul.GameLanguageType</c>, as an int.</param>
        /// <returns>True when <see cref="Selected"/> changed, i.e. when the config file has to be rewritten.</returns>
        public bool Observe(int value)
        {
            Applied = value;

            var wanted = _registry.TryGet(value, out var language) ? language!.Code : string.Empty;
            if (string.Equals(wanted, Selected, StringComparison.Ordinal))
            {
                return false;
            }

            Selected = wanted;
            return true;
        }

        /// <summary>
        /// The integer that may be written to the save file while <paramref name="inMemory"/> is the
        /// language in memory (see <see cref="LanguageRegistry.SaveValueFor"/>).
        /// </summary>
        /// <param name="inMemory">The in-memory <c>Bulbul.GameLanguageType</c>, as an int.</param>
        public int ForSave(int inMemory) => _registry.SaveValueFor(inMemory);

        /// <summary>One line for a log: what is selected, applied and persisted.</summary>
        public override string ToString() =>
            (Selected.Length == 0 ? "selected=game" : "selected=" + Selected)
            + " applied=" + _registry.ToName(Applied)
            + " persisted=" + _registry.ToName(Persisted)
            + (Active ? string.Empty : Enabled ? " (patch target missing)" : " (disabled)");
    }
}
