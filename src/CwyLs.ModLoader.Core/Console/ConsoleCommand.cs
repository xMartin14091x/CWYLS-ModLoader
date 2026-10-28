using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// One command the in-game console can run: how it is typed, what it does, and the work
    /// itself. Pure data plus a delegate, so the whole command layer is unit-testable without
    /// Unity - the handler is supplied by whoever registers the command.
    /// </summary>
    public sealed class ConsoleCommand
    {
        /// <summary>Group a command falls into when its registrar did not name one.</summary>
        public const string DefaultGroup = "general";

        /// <summary>Creates a command.</summary>
        /// <param name="name">
        /// What the user types, e.g. <c>packs</c>. Required, matched case-insensitively, and must
        /// contain no whitespace (the first token of the line is the name).
        /// </param>
        /// <param name="description">One short sentence shown by <c>help</c>. Null becomes empty.</param>
        /// <param name="usage">
        /// The argument shape, e.g. <c>packs list|rescan</c>. Null or empty becomes
        /// <paramref name="name"/>, so <c>help</c> always has something to print.
        /// </param>
        /// <param name="group">
        /// Heading <c>help</c> lists the command under, e.g. <c>framework</c>. Null or blank
        /// becomes <see cref="DefaultGroup"/>.
        /// </param>
        /// <param name="handler">
        /// The work. Receives the arguments after the name (never null, possibly empty) and
        /// returns what to print (null or empty prints nothing). It may throw: the registry turns
        /// an exception into an error line rather than letting it reach the game's frame.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null, blank or contains whitespace.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public ConsoleCommand(
            string name,
            string? description,
            string? usage,
            string? group,
            Func<IReadOnlyList<string>, string?> handler)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a command name is required", nameof(name));
            }

            foreach (var c in name)
            {
                if (char.IsWhiteSpace(c))
                {
                    throw new ArgumentException(
                        "a command name is the first token of the line, so it cannot contain whitespace: '"
                        + name + "'",
                        nameof(name));
                }
            }

            Name = name;
            Description = description ?? string.Empty;
            Usage = string.IsNullOrWhiteSpace(usage) ? name : usage!;
            Group = string.IsNullOrWhiteSpace(group) ? DefaultGroup : group!;
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>What the user types; matched case-insensitively.</summary>
        public string Name { get; }

        /// <summary>One short sentence: what the command does. Never null.</summary>
        public string Description { get; }

        /// <summary>The argument shape, e.g. <c>packs list|rescan</c>. Never null.</summary>
        public string Usage { get; }

        /// <summary>Heading <c>help</c> lists this command under. Never null.</summary>
        public string Group { get; }

        /// <summary>The work. Prefer <see cref="CommandRegistry.Execute(string)"/>, which cannot throw.</summary>
        public Func<IReadOnlyList<string>, string?> Handler { get; }

        /// <summary>The one-line form <c>help</c> prints: <c>packs list|rescan - what it does</c>.</summary>
        public string Describe() =>
            Description.Length == 0 ? Usage : Usage + " - " + Description;

        /// <inheritdoc />
        public override string ToString() => Describe();
    }
}
