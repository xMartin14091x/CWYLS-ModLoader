using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// Every language the loaded packs declare, each with a runtime <c>Bulbul.GameLanguageType</c>
    /// value: 100 for the first, 101 for the next, in pack order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity-free and deterministic, so the rules are unit-tested: which declaration wins a code
    /// (the first pack to declare it), what each value falls back to, and what may be written to
    /// the game's save file (<see cref="SaveValueFor"/>, never a pack value).
    /// </para>
    /// <para>
    /// Values are only stable within a session - installing a pack whose folder sorts earlier
    /// shifts the numbers - which is harmless because a value never leaves memory: the player's
    /// choice is persisted as a <em>code</em> in the framework's own config.
    /// </para>
    /// <para>
    /// Build it once, at startup, before the game reads its save; a registry is not changed while
    /// the game runs.
    /// </para>
    /// </remarks>
    public sealed class LanguageRegistry
    {
        private readonly List<LanguageDeclaration> _declared = new List<LanguageDeclaration>();
        private readonly List<string> _rejected = new List<string>();
        private readonly Dictionary<string, int> _codeToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private List<PackLanguage>? _languages;

        /// <summary>The registered languages, in value order. Never null.</summary>
        public IReadOnlyList<PackLanguage> Languages => Built();

        /// <summary>How many languages are registered.</summary>
        public int Count => _declared.Count;

        /// <summary>Why each refused declaration was refused, in the order they were offered. Never null.</summary>
        public IReadOnlyList<string> Rejected => _rejected;

        /// <summary>
        /// Offers one declaration. The first pack to declare a code keeps it; a later one is
        /// refused with a reason (see <see cref="Rejected"/>).
        /// </summary>
        /// <param name="declaration">A parsed declaration.</param>
        /// <param name="reason">Why it was refused, or null when it was registered.</param>
        /// <returns>True when the declaration was registered.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="declaration"/> is null.</exception>
        public bool Register(LanguageDeclaration declaration, out string? reason)
        {
            if (declaration is null)
            {
                throw new ArgumentNullException(nameof(declaration));
            }

            reason = null;
            if (SubtitleLanguages.IsInGame(declaration.Code))
            {
                reason = declaration + ": \"" + declaration.Code + "\" is one of the game's own languages";
            }
            else if (_codeToIndex.TryGetValue(declaration.Code, out var existing))
            {
                reason = declaration + ": \"" + declaration.Code + "\" is already declared by "
                    + _declared[existing].PackId + ", which loads first";
            }
            else if (_declared.Count >= GameLanguages.MaxPackLanguages)
            {
                reason = declaration + ": more than " + GameLanguages.MaxPackLanguages.ToString(CultureInfo.InvariantCulture)
                    + " pack languages";
            }

            if (reason != null)
            {
                _rejected.Add(reason);
                return false;
            }

            _codeToIndex.Add(declaration.Code, _declared.Count);
            _declared.Add(declaration);
            _languages = null;
            return true;
        }

        /// <summary>Records a declaration that never reached <see cref="Register"/> - a file that did not parse.</summary>
        /// <param name="reason">What was wrong, naming the pack and file.</param>
        public void Reject(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                _rejected.Add(reason);
            }
        }

        /// <summary>True when <paramref name="value"/> is a registered pack language.</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        public bool IsPackLanguage(int value) => TryGet(value, out _);

        /// <summary>The registered language with this runtime value.</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        /// <param name="language">The language, or null.</param>
        /// <returns>True when found.</returns>
        public bool TryGet(int value, out PackLanguage? language)
        {
            var index = value - GameLanguages.FirstPackValue;
            var languages = Built();
            if (index >= 0 && index < languages.Count)
            {
                language = languages[index];
                return true;
            }

            language = null;
            return false;
        }

        /// <summary>The registered language with this code, in any spelling <see cref="SubtitleLanguages.Normalize"/> accepts.</summary>
        /// <param name="code">A language code.</param>
        /// <param name="language">The language, or null.</param>
        /// <returns>True when found.</returns>
        public bool TryGet(string? code, out PackLanguage? language)
        {
            var normalized = SubtitleLanguages.Normalize(code);
            if (normalized.Length > 0 && _codeToIndex.TryGetValue(normalized, out var index))
            {
                language = Built()[index];
                return true;
            }

            language = null;
            return false;
        }

        /// <summary>
        /// The runtime value for a code: a registered pack language or one of the game's own.
        /// </summary>
        /// <param name="code">A language code.</param>
        /// <param name="value">The value on success; <see cref="GameLanguages.None"/> otherwise.</param>
        /// <returns>True when the code names a language this session can display.</returns>
        public bool TryFromCode(string? code, out int value)
        {
            if (TryGet(code, out var language))
            {
                value = language!.Value;
                return true;
            }

            return LanguageCodes.TryFromCode(code, out value);
        }

        /// <summary>The code for a runtime value, pack or built-in; empty when unknown.</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        public string ToCode(int value) =>
            TryGet(value, out var language) ? language!.Code : LanguageCodes.ToCode(value);

        /// <summary>A readable name for a runtime value, for logs: <c>Thai</c>, <c>English</c>, or the number.</summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        public string ToName(int value) =>
            TryGet(value, out var language) ? language!.Declaration.EnglishName : LanguageCodes.ToName(value);

        /// <summary>
        /// The integer that may be written to the game's save file while <paramref name="inMemory"/>
        /// is the language in memory: a pack language becomes its <see cref="PackLanguage.BaseValue"/>,
        /// a pack-range value nobody declared this session becomes English, and every value the game
        /// defines is left alone.
        /// </summary>
        /// <param name="inMemory">The in-memory <c>Bulbul.GameLanguageType</c>, as an int.</param>
        public int SaveValueFor(int inMemory)
        {
            if (!GameLanguages.IsPackRange(inMemory))
            {
                return inMemory;
            }

            return TryGet(inMemory, out var language) ? language!.BaseValue : GameLanguages.English;
        }

        /// <summary>
        /// The game language that stands in for <paramref name="value"/> where the game only knows its
        /// own seven; <paramref name="value"/> itself when it is not a pack value.
        /// </summary>
        /// <param name="value">A <c>Bulbul.GameLanguageType</c> cast to int.</param>
        public int BaseValueOf(int value) => SaveValueFor(value);

        private List<PackLanguage> Built()
        {
            var built = _languages;
            if (built != null)
            {
                return built;
            }

            built = new List<PackLanguage>(_declared.Count);
            for (var i = 0; i < _declared.Count; i++)
            {
                var declaration = _declared[i];
                var chain = ExpandChain(declaration);
                var baseValue = GameLanguages.English;
                foreach (var step in chain)
                {
                    if (step.IsBuiltIn)
                    {
                        baseValue = step.Value;
                        break;
                    }
                }

                built.Add(new PackLanguage(
                    declaration,
                    GameLanguages.FirstPackValue + i,
                    baseValue,
                    chain,
                    LineBreakHelpers.Resolve(declaration.LineBreaks, declaration.Script)));
            }

            _languages = built;
            return built;
        }

        /// <summary>
        /// The declaration's own code, then its fallback codes depth-first through other pack
        /// languages, each code once, ending in a built-in language (English appended when the
        /// chain reaches none). A code nobody declared is skipped.
        /// </summary>
        private List<LanguageStep> ExpandChain(LanguageDeclaration start)
        {
            var steps = new List<LanguageStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Visit(start.Code, steps, seen);

            var hasBuiltIn = false;
            foreach (var step in steps)
            {
                hasBuiltIn |= step.IsBuiltIn;
            }

            if (!hasBuiltIn)
            {
                steps.Add(new LanguageStep(SubtitleLanguages.English, GameLanguages.English));
            }

            return steps;
        }

        private void Visit(string code, List<LanguageStep> steps, HashSet<string> seen)
        {
            if (!seen.Add(code))
            {
                return;
            }

            if (LanguageCodes.TryFromCode(code, out var builtIn))
            {
                steps.Add(new LanguageStep(code, builtIn));
                return;
            }

            if (!_codeToIndex.TryGetValue(code, out var index))
            {
                return;
            }

            steps.Add(new LanguageStep(code, GameLanguages.FirstPackValue + index));
            foreach (var next in _declared[index].Fallback)
            {
                Visit(next, steps, seen);
            }
        }
    }
}
