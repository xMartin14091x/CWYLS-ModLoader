using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>
    /// The per-launch access key of the local API: 32 random bytes as 64 lower-case hex
    /// characters, minted when the API starts and on <c>api rotate</c>. It is compared in
    /// constant time and never appears in a log, a trace record, an exception message or
    /// <see cref="ToString"/>; the only way to read it is <see cref="Reveal"/>, which the key-file
    /// writer calls. Immutable.
    /// </summary>
    public sealed class ApiToken
    {
        /// <summary>Random bytes per key (256 bits).</summary>
        public const int ByteLength = 32;

        /// <summary>Characters of the hex form.</summary>
        public const int TextLength = ByteLength * 2;

        /// <summary>The HTTP authorization scheme name clients use (RFC 6750).</summary>
        public const string Scheme = "Bearer";

        private readonly byte[] _ascii;
        private readonly string _text;

        private ApiToken(string text)
        {
            _text = text;
            _ascii = Encoding.ASCII.GetBytes(text);
            Id = ComputeId(text);
        }

        /// <summary>
        /// A short non-secret label for status lines: the first 8 hex characters of the key's
        /// SHA-256, so a rotation is visible without revealing anything usable.
        /// </summary>
        public string Id { get; }

        /// <summary>Mints a new key from the system's cryptographic random source.</summary>
        public static ApiToken Create()
        {
            var bytes = new byte[ByteLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return new ApiToken(Hex(bytes));
        }

        /// <summary>Rebuilds a key from its hex form (tests, and tools that read the key file).</summary>
        /// <param name="text">64 lower-case hex characters.</param>
        /// <param name="token">The key, or null.</param>
        /// <returns>True when <paramref name="text"/> is well formed.</returns>
        public static bool TryParse(string? text, out ApiToken? token)
        {
            token = null;
            if (text is null || text.Length != TextLength)
            {
                return false;
            }

            foreach (var c in text)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            token = new ApiToken(text);
            return true;
        }

        /// <summary>The key itself. Only the key-file writer and tests call this; never log the result.</summary>
        public string Reveal() => _text;

        /// <summary>
        /// True when an authorization header value is the <see cref="Scheme"/> word (any case),
        /// one space, then exactly this key. The comparison takes the same time whatever the
        /// value, so the key cannot be guessed byte by byte from response timing.
        /// </summary>
        /// <param name="authorization">The header value, or null when the header is absent.</param>
        public bool Matches(string? authorization)
        {
            if (authorization is null || authorization.Length < Scheme.Length + 1)
            {
                return FixedTimeEquals(null);
            }

            if (!string.Equals(authorization.Substring(0, Scheme.Length), Scheme, StringComparison.OrdinalIgnoreCase)
                || authorization[Scheme.Length] != ' ')
            {
                return FixedTimeEquals(null);
            }

            return FixedTimeEquals(authorization.Substring(Scheme.Length + 1));
        }

        /// <summary>Never the key itself: <c>ApiToken(id=1a2b3c4d, redacted)</c>.</summary>
        public override string ToString() => "ApiToken(id=" + Id + ", redacted)";

        private bool FixedTimeEquals(string? given)
        {
            // Compare every byte of the expected key whatever the input, so neither the length
            // nor the position of the first difference changes the time taken.
            var diff = given is null || given.Length != _ascii.Length ? 1 : 0;
            for (var i = 0; i < _ascii.Length; i++)
            {
                var g = given != null && i < given.Length ? given[i] : 0;
                diff |= _ascii[i] ^ g;
            }

            return diff == 0;
        }

        private static string ComputeId(string text)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(text));
                return Hex(hash).Substring(0, 8);
            }
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
