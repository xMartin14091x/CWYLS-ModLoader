namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>Helpers for TextMeshPro rich text in text parts (<see cref="ICwyTextView"/>).</summary>
    public static class CwyRichText
    {
        private const string Open = "<noparse>";
        private const string Close = "</noparse>";

        /// <summary>
        /// Text shown exactly as written: wrapped in <c>&lt;noparse&gt;</c>, with any
        /// <c>&lt;/noparse&gt;</c> inside broken by an invisible zero-width space so it cannot end the
        /// wrapper early. Use it for anything a player, a file or a language model wrote.
        /// </summary>
        /// <param name="text">Untrusted text; null is empty.</param>
        public static string Escape(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var inner = text!.Replace(Close, "</\u200Bnoparse>");
            return Open + inner + Close;
        }
    }
}
