using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// A declared language after registration: the declaration, the runtime
    /// <c>Bulbul.GameLanguageType</c> value it was given, and the facts derived from its fallback
    /// chain.
    /// </summary>
    public sealed class PackLanguage
    {
        internal PackLanguage(
            LanguageDeclaration declaration,
            int value,
            int baseValue,
            IReadOnlyList<LanguageStep> chain,
            string lineBreakHelper)
        {
            Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
            Value = value;
            BaseValue = baseValue;
            Chain = chain;
            LineBreakHelper = lineBreakHelper;
        }

        /// <summary>What the pack declared.</summary>
        public LanguageDeclaration Declaration { get; }

        /// <summary>The runtime <c>Bulbul.GameLanguageType</c> value, 100 and up.</summary>
        public int Value { get; }

        /// <summary>Shorthand for <c>Declaration.Code</c>.</summary>
        public string Code => Declaration.Code;

        /// <summary>Shorthand for <c>Declaration.Caption</c>.</summary>
        public string Caption => Declaration.Caption;

        /// <summary>
        /// The game language this one stands in for wherever the game only understands its own
        /// seven: the value written to the save file, the font and materials <c>FontSupplier</c>
        /// hands out, the typing speed, the news and the web requests. The first built-in language
        /// of the fallback chain; English when the chain names none.
        /// </summary>
        public int BaseValue { get; }

        /// <summary>
        /// Where a missing row comes from, in order: this language's own table first, then every
        /// code of the declared fallback chain, expanded through other pack languages and always
        /// ending in a built-in language.
        /// </summary>
        public IReadOnlyList<LanguageStep> Chain { get; }

        /// <summary>The resolved line-break helper id, or empty for none (see <see cref="LineBreakHelpers"/>).</summary>
        public string LineBreakHelper { get; }

        /// <summary><c>th=100 (Thai, my.lang)</c>.</summary>
        public override string ToString() =>
            Code + "=" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " (" + Declaration.EnglishName + ", " + Declaration.PackId + ")";
    }

    /// <summary>One step of a <see cref="PackLanguage.Chain"/>.</summary>
    public readonly struct LanguageStep
    {
        /// <summary>Creates a step.</summary>
        /// <param name="code">The language code.</param>
        /// <param name="value">The runtime value: a pack value (100+) or a built-in one (1-7).</param>
        public LanguageStep(string code, int value)
        {
            Code = code;
            Value = value;
        }

        /// <summary>The language code.</summary>
        public string Code { get; }

        /// <summary>The runtime <c>Bulbul.GameLanguageType</c> value.</summary>
        public int Value { get; }

        /// <summary>True when this step reads one of the game's own columns rather than a pack table.</summary>
        public bool IsBuiltIn => GameLanguages.IsBuiltIn(Value);

        /// <summary>The code.</summary>
        public override string ToString() => Code;
    }
}
