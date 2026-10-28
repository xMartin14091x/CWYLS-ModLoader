using System;

namespace CwyLs.ModLoader.Core.Language
{
    /// <summary>
    /// A <c>language.json</c> that cannot be used. The message names the file and the member, e.g.
    /// <c>language.json: "code": "en" is one of the game's own languages</c>.
    /// </summary>
    public sealed class LanguageDeclarationException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">What is wrong, naming the file and member.</param>
        public LanguageDeclarationException(string message)
            : base(message)
        {
        }
    }
}
