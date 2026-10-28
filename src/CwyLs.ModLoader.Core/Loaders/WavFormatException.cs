using System;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// A WAV file this framework will not decode. The message is written for whoever made the
    /// pack ("24-bit WAV is not supported; save as 16-bit PCM"), because it is copied verbatim
    /// into the <see cref="LoadReport"/> line and the warning in LogOutput.log.
    /// </summary>
    public sealed class WavFormatException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">What is wrong with the file, and what to do about it.</param>
        public WavFormatException(string message)
            : base(message)
        {
        }
    }
}
