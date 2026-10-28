using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>
    /// Process-wide list of smoke checks. Static because the plugins that contribute checks are
    /// separate BepInEx assemblies with no reference to each other: each one registers on Awake
    /// and the single runner in CwyLs.ModLoader executes whatever it finds.
    /// Every member takes the same lock, so registration from a plugin thread while the runner
    /// snapshots on the main thread is safe.
    /// </summary>
    public static class SmokeRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<SmokeCheck> Checks = new List<SmokeCheck>();

        /// <summary>Number of registered checks.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Checks.Count;
                }
            }
        }

        /// <summary>
        /// Registers a check. Re-registering an existing <see cref="SmokeCheck.Id"/> replaces it in
        /// place (keeping its position), so a plugin that runs Awake twice cannot double-report.
        /// </summary>
        public static void Register(SmokeCheck check) => Register(check, loud: false);

        /// <summary>
        /// Registers a check, marking it <see cref="SmokeCheck.Loud"/> when it is something the
        /// player sees or hears. A loud check runs only on an explicit trigger (the hotkey or the
        /// trigger file); an automatic run reports it as skipped instead of playing a clip or
        /// repainting the character behind the player's back.
        /// </summary>
        /// <param name="check">The check. Required.</param>
        /// <param name="loud">
        /// True to mark it loud. False leaves <see cref="SmokeCheck.Loud"/> as the check was built
        /// with, so a check constructed loud stays loud through the plain overload.
        /// </param>
        public static void Register(SmokeCheck check, bool loud) => Register(check, loud, restore: null);

        /// <summary>
        /// Registers a check together with what puts the game back afterwards. A loud check
        /// that changes what the player sees - a facial index, a motion, the look target - must
        /// hand in the undo here, and the runner runs it in a <c>finally</c> after the body
        /// (<see cref="SmokeCheck.ExecuteAndRestore"/>).
        /// </summary>
        /// <param name="check">The check. Required.</param>
        /// <param name="loud">
        /// True to mark it loud. False leaves <see cref="SmokeCheck.Loud"/> as the check was built
        /// with, so a check constructed loud stays loud through the plain overload.
        /// </param>
        /// <param name="restore">
        /// The undo. Null leaves the check's own <see cref="SmokeCheck.Restore"/> alone, so a
        /// check constructed with one keeps it.
        /// </param>
        public static void Register(SmokeCheck check, bool loud, Action? restore)
        {
            if (check is null)
            {
                throw new ArgumentNullException(nameof(check));
            }

            if (loud)
            {
                check = check.AsLoud();
            }

            if (restore != null)
            {
                check = check.WithRestore(restore);
            }

            lock (Gate)
            {
                for (var i = 0; i < Checks.Count; i++)
                {
                    if (string.Equals(Checks[i].Id, check.Id, StringComparison.Ordinal))
                    {
                        Checks[i] = check;
                        return;
                    }
                }

                Checks.Add(check);
            }
        }

        /// <summary>
        /// A copy of the registered checks, in registration order. The copy is detached: later
        /// registrations never mutate a snapshot a run is already iterating.
        /// </summary>
        /// <param name="phase">
        /// When given, keeps only checks of that <see cref="SmokeCheck.Phase"/> (case-insensitive).
        /// </param>
        public static IReadOnlyList<SmokeCheck> Snapshot(string? phase = null)
        {
            lock (Gate)
            {
                if (phase is null)
                {
                    return new List<SmokeCheck>(Checks);
                }

                var filtered = new List<SmokeCheck>(Checks.Count);
                foreach (var check in Checks)
                {
                    if (string.Equals(check.Phase, phase, StringComparison.OrdinalIgnoreCase))
                    {
                        filtered.Add(check);
                    }
                }

                return filtered;
            }
        }

        /// <summary>Removes every registered check. Used by tests; harmless at runtime.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Checks.Clear();
            }
        }
    }
}
