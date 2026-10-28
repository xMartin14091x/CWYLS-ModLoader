using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader
{
    /// <summary>
    /// The one place every adapter and mod declares the game members it depends on. Registration
    /// is additive and happens during plugin Awake; the framework verifies the whole table once,
    /// so a game update shows up as one readable block in the log instead of as a Harmony failure
    /// halfway through a play session.
    /// </summary>
    public static class PatchTargetRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<PatchTarget> Registered = new List<PatchTarget>();
        private static readonly HashSet<string> KnownIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> RegisteredOwners = new List<string>();

        /// <summary>How many targets are registered.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Registered.Count;
                }
            }
        }

        /// <summary>A snapshot of every registered target, in registration order.</summary>
        public static IReadOnlyList<PatchTarget> All
        {
            get
            {
                lock (Gate)
                {
                    return Registered.ToArray();
                }
            }
        }

        /// <summary>A snapshot of the names that have registered a table, in registration order.</summary>
        public static IReadOnlyList<string> Owners
        {
            get
            {
                lock (Gate)
                {
                    return RegisteredOwners.ToArray();
                }
            }
        }

        /// <summary>
        /// Registers a table. A target whose id is already registered is skipped with a warning
        /// rather than replacing the first one: two owners claiming one id is a bug in the tables,
        /// and silently letting load order decide the winner would hide it.
        /// </summary>
        /// <param name="owner">Who is registering, normally the plugin name; used only in logs.</param>
        /// <param name="targets">The table to register; null entries are ignored.</param>
        /// <exception cref="ArgumentException"><paramref name="owner"/> is null or blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="targets"/> is null.</exception>
        public static void Register(string owner, IEnumerable<PatchTarget> targets)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                throw new ArgumentException("must not be null or blank", nameof(owner));
            }

            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            var added = 0;
            var skipped = 0;
            lock (Gate)
            {
                foreach (var target in targets)
                {
                    if (target == null)
                    {
                        continue;
                    }

                    if (!KnownIds.Add(target.Id))
                    {
                        skipped++;
                        CwyLsModLoader.Log.LogWarning(
                            "patch target id '" + target.Id + "' is already registered; "
                            + owner + "'s copy (" + target.Describe() + ") is ignored");
                        continue;
                    }

                    Registered.Add(target);
                    added++;
                }

                RegisteredOwners.Add(owner);
            }

            ConsoleEvents.Setup(
                "targets",
                "registered " + added + " patch target(s)"
                + (skipped > 0 ? " (" + skipped + " duplicate ids skipped)" : string.Empty),
                ("owner", owner),
                ("total", Count));
        }
    }
}
