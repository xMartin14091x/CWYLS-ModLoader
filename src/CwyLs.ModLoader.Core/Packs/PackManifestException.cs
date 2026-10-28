using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// Thrown by <see cref="PackManifestParser.Parse"/> when a <c>manifest.json</c> is unreadable
    /// or invalid. The message is written for the pack author: it names the offending member and
    /// what was expected, and for a JSON syntax error it starts with <c>line:column</c>.
    /// </summary>
    public sealed class PackManifestException : Exception
    {
        /// <summary>Creates an exception describing an invalid manifest.</summary>
        /// <param name="message">What is wrong, in terms the pack author can act on.</param>
        public PackManifestException(string message)
            : base(message)
        {
        }
    }
}
