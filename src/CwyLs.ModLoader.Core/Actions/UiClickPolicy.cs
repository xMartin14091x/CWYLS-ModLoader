using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>What <see cref="UiClickPolicy"/> decided about one control. Immutable.</summary>
    public sealed class UiClickVerdict
    {
        private UiClickVerdict(bool denied, string category, string rule, string where)
        {
            IsDenied = denied;
            Category = category;
            Rule = rule;
            Where = where;
        }

        /// <summary>The shared "allowed" answer.</summary>
        public static UiClickVerdict Allowed { get; } = new UiClickVerdict(false, string.Empty, string.Empty, string.Empty);

        /// <summary>True when the click must not happen.</summary>
        public bool IsDenied { get; }

        /// <summary>The protected area: <c>shop</c>, <c>account</c>, <c>cloud sync</c>, <c>sns/browser</c>; empty when allowed.</summary>
        public string Category { get; }

        /// <summary>The word or type that matched; empty when allowed.</summary>
        public string Rule { get; }

        /// <summary>The path segment or component type it matched in; empty when allowed.</summary>
        public string Where { get; }

        /// <summary>One line for the refusal: <c>sns/browser ("sns" in segment "SNS")</c>.</summary>
        public string Reason => IsDenied ? Category + " (\"" + Rule + "\" in " + Where + ")" : "allowed";

        internal static UiClickVerdict Deny(string category, string rule, string where) =>
            new UiClickVerdict(true, category, rule, where);

        /// <inheritdoc />
        public override string ToString() => Reason;
    }

    /// <summary>
    /// The deny list of <c>UiClick.Invoke</c> (project rule: never touch the shop,
    /// account linking, cloud sync, SNS or the browser). Pure, so every rule is unit-tested.
    /// </summary>
    /// <remarks>
    /// <para><b>Words.</b> Every segment of a hierarchy path is split into lower-case words at
    /// separators and case changes (<c>InquiryIDButton</c> → inquiry, id, button; <c>SNS</c> → sns)
    /// and a click is refused when any segment holds a denied word. Whole words only, so
    /// <c>CloudyButton</c> (weather) stays clickable while <c>SaveDataUploadInterval</c> does not.
    /// The words were chosen against the game's RoomScene (game 1.17.3), e.g.
    /// <c>…/UI_FacilitySetting/SNS/Discord</c>, <c>…/PC_Account_Setting/…/LinkButton</c>,
    /// <c>…/General/…/SaveDataUploadInterval</c>, <c>…/UI_FacilityEnviroment/…/ShopButton</c>,
    /// <c>…/General/…/FAQ/TitleText/FAQButton</c>.</para>
    /// <para><b>Types.</b> The game adapter adds the exact component types that open these areas
    /// (the account-transfer dialog, purchase popover, hyperlinks, ...); a click is refused when the
    /// control or any ancestor carries one (<see cref="CheckTypes"/>).</para>
    /// </remarks>
    public static class UiClickPolicy
    {
        /// <summary>Category of shop and purchase controls.</summary>
        public const string Shop = "shop";

        /// <summary>Category of account sign-in, linking and transfer controls.</summary>
        public const string Account = "account";

        /// <summary>Category of cloud save and upload controls.</summary>
        public const string CloudSync = "cloud sync";

        /// <summary>Category of controls that open SNS pages or the web browser.</summary>
        public const string SnsBrowser = "sns/browser";

        private static readonly Dictionary<string, string> Words = Build();

        /// <summary>Every denied word and its category, sorted by word.</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> DeniedWords
        {
            get
            {
                var list = new List<KeyValuePair<string, string>>(Words);
                list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                return list;
            }
        }

        /// <summary>
        /// Splits a name into lower-case words: at any character that is not a letter or digit, and
        /// at case and digit boundaries (<c>UI_FacilitySetting</c> → ui, facility, setting;
        /// <c>SNSButton</c> → sns, button; <c>Cook(B)Button</c> → cook, b, button).
        /// </summary>
        /// <param name="name">A name; null gives no words.</param>
        public static IReadOnlyList<string> Tokens(string? name)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(name))
            {
                return tokens;
            }

            var current = new StringBuilder();
            for (var i = 0; i < name!.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c))
                {
                    Flush(current, tokens);
                    continue;
                }

                if (current.Length > 0)
                {
                    var previous = name[i - 1];
                    var next = i + 1 < name.Length ? name[i + 1] : '\0';
                    var lowerToUpper = char.IsLower(previous) && char.IsUpper(c);
                    var acronymEnd = char.IsUpper(previous) && char.IsUpper(c) && char.IsLower(next);
                    var digitEdge = char.IsDigit(previous) != char.IsDigit(c);
                    if (lowerToUpper || acronymEnd || digitEdge)
                    {
                        Flush(current, tokens);
                    }
                }

                current.Append(c);
            }

            Flush(current, tokens);
            return tokens;
        }

        /// <summary>Checks every segment of a <c>Root/Child/Leaf</c> hierarchy path.</summary>
        /// <param name="path">The path; null or blank is allowed (nothing to match).</param>
        public static UiClickVerdict CheckPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return UiClickVerdict.Allowed;
            }

            foreach (var segment in path!.Split('/'))
            {
                var verdict = CheckName(segment, "segment \"" + segment + "\"");
                if (verdict.IsDenied)
                {
                    return verdict;
                }
            }

            return UiClickVerdict.Allowed;
        }

        /// <summary>Checks one name's words.</summary>
        /// <param name="name">A object or type name.</param>
        /// <param name="where">How to name it in the reason.</param>
        public static UiClickVerdict CheckName(string? name, string where)
        {
            foreach (var token in Tokens(name))
            {
                if (Words.TryGetValue(token, out var category))
                {
                    return UiClickVerdict.Deny(category, token, where);
                }
            }

            return UiClickVerdict.Allowed;
        }

        /// <summary>Checks component types against an exact deny list (full names, ordinal).</summary>
        /// <param name="typeFullNames">The full names of the components on the control and its ancestors.</param>
        /// <param name="deniedTypes">Full name → category, supplied by the game adapter.</param>
        public static UiClickVerdict CheckTypes(IEnumerable<string?>? typeFullNames, IReadOnlyDictionary<string, string>? deniedTypes)
        {
            if (typeFullNames is null || deniedTypes is null || deniedTypes.Count == 0)
            {
                return UiClickVerdict.Allowed;
            }

            foreach (var name in typeFullNames)
            {
                if (name != null && deniedTypes.TryGetValue(name, out var category))
                {
                    return UiClickVerdict.Deny(category, name, "component " + name);
                }
            }

            return UiClickVerdict.Allowed;
        }

        private static void Flush(StringBuilder current, List<string> tokens)
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString().ToLowerInvariant());
                current.Length = 0;
            }
        }

        private static Dictionary<string, string> Build()
        {
            var words = new Dictionary<string, string>(StringComparer.Ordinal);
            Add(words, Shop, "shop", "store", "purchase", "purchased", "buy", "dlc", "price", "payment", "billing", "product");
            Add(words, Account, "account", "login", "logout", "signin", "auth", "apple", "google", "link", "unlink", "linked", "transfer", "inquiry");
            Add(words, CloudSync, "cloud", "sync", "upload", "backup", "offline", "online", "server");
            Add(words, SnsBrowser, "sns", "discord", "twitter", "youtube", "instagram", "rednote", "bilibili", "heybox", "facebook", "tiktok",
                "share", "url", "web", "browser", "hyperlink", "faq", "terms", "privacy", "review", "support", "contact", "mail", "email");
            return words;
        }

        private static void Add(Dictionary<string, string> words, string category, params string[] list)
        {
            foreach (var word in list)
            {
                words[word] = category;
            }
        }
    }
}
