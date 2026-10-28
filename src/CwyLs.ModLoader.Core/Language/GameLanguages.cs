namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// The values of the game's own <c>Bulbul.GameLanguageType</c>, as plain integers so the core
    /// assembly stays free of any game type, plus the range this framework hands out to languages
    /// that content packs declare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game's enum is <c>None=0, Japanese=1, English=2, ChineseSimplified=3,
    /// ChineseTraditional=4, Portuguese=5, Korean=6, Russian=7</c>
    /// (<c>Bulbul.GameLanguageType</c>, game 1.17.x). It is never enumerated
    /// and never used as an array index - the only <c>(int)</c> cast in the whole assembly is the
    /// save field (<c>Bulbul.SettingData</c>) - so a value the game does not define can exist
    /// at runtime as long as the <c>switch</c> sites that read it are handled and the value never
    /// reaches the save file.
    /// </para>
    /// <para>
    /// Pack languages start at <see cref="FirstPackValue"/> rather than 8 so that an official
    /// eighth language, should the game ever add one, cannot collide with ours.
    /// </para>
    /// </remarks>
    public static class GameLanguages
    {
        /// <summary><c>GameLanguageType.None</c>: the value a fresh save holds.</summary>
        public const int None = 0;

        /// <summary><c>GameLanguageType.Japanese</c>.</summary>
        public const int Japanese = 1;

        /// <summary><c>GameLanguageType.English</c>: the vanilla-safe default for a pack language.</summary>
        public const int English = 2;

        /// <summary><c>GameLanguageType.ChineseSimplified</c>.</summary>
        public const int ChineseSimplified = 3;

        /// <summary><c>GameLanguageType.ChineseTraditional</c>.</summary>
        public const int ChineseTraditional = 4;

        /// <summary><c>GameLanguageType.Portuguese</c>.</summary>
        public const int Portuguese = 5;

        /// <summary><c>GameLanguageType.Korean</c>.</summary>
        public const int Korean = 6;

        /// <summary><c>GameLanguageType.Russian</c>: the highest value the game defines.</summary>
        public const int Russian = 7;

        /// <summary>The runtime value of the first language a pack declares; the next gets 101, and so on.</summary>
        public const int FirstPackValue = 100;

        /// <summary>
        /// How many pack languages one session accepts. A guard against a runaway pack folder, not a
        /// design limit: the enum is an <c>int</c>.
        /// </summary>
        public const int MaxPackLanguages = 64;

        /// <summary>True when the game itself can display this value: one of its own seven languages.</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        public static bool IsBuiltIn(int value) => value >= Japanese && value <= Russian;

        /// <summary>
        /// True when the value lies in the range handed out to pack languages, whether or not a pack
        /// declared it in this session. Such a value must never be written to the save file.
        /// </summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        public static bool IsPackRange(int value) => value >= FirstPackValue;
    }
}
