using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>One call to a hooked <c>SaveDataManager</c> method, as the shared hook hands it out.</summary>
    internal readonly struct SaveCall
    {
        /// <summary>Creates a call record.</summary>
        /// <param name="instance">The save manager the game called.</param>
        /// <param name="methodName">The method's name, e.g. <c>SaveTodoList</c>.</param>
        internal SaveCall(Game.SaveDataManager? instance, string methodName)
        {
            Instance = instance;
            MethodName = methodName;
        }

        /// <summary>The save manager the game called; null only if Harmony could not bind it.</summary>
        internal Game.SaveDataManager? Instance { get; }

        /// <summary>The method's name, e.g. <c>SaveTodoList</c> or <c>DeleteTodoList</c>.</summary>
        internal string MethodName { get; }
    }

    /// <summary>
    /// The one Harmony postfix on every public <c>SaveDataManager.Save*</c> and <c>Delete*</c> method
    /// (<see cref="SaveMethodRules.IsHooked"/>), shared by the event bus (<c>SaveSection</c>) and the
    /// data bus (<c>GameData.Changed</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this hook, the event bus (<c>EventPatches</c>) and the data bus (<c>SaveDataPatches</c>)
    /// each patched the save family, so every save ran two CWY postfixes. Now each method carries one,
    /// and the consumers subscribe here, in plugin load order: the event bus first (adapter Awake),
    /// then the data bus.
    /// </para>
    /// <para>
    /// Whichever consumer starts first installs it (<see cref="Install"/> is idempotent), so the data
    /// bus still hears saves if the event bus failed to start. The postfix never throws into the
    /// game's save, and a subscriber that saves from inside a notification is stopped after
    /// <see cref="MaxDepth"/> levels instead of recursing.
    /// </para>
    /// </remarks>
    internal static class SaveDataHook
    {
        /// <summary>How deep a save notification may re-enter before it stops being passed on.</summary>
        internal const int MaxDepth = 4;

        /// <summary>
        /// Assembly-name prefix of the two buses (the adapter and its data plugin), for the postfix
        /// census. The trace plugin's own diagnostic hooks are a separate layer and not counted.
        /// </summary>
        internal const string BusAssemblyPrefix = "CwyLs.ModLoader.Bulbul";

        private static readonly object Gate = new object();
        private static readonly List<MethodInfo> HookedMethods = new List<MethodInfo>();
        private static readonly EventDispatcher<SaveCall> Channel =
            new EventDispatcher<SaveCall>("SaveDataHook", failure => CwyLsModLoader.Log.LogError("SAVE " + failure));

        [ThreadStatic]
        private static int _depth;

        private static bool _installed;
        private static string _installedBy = string.Empty;
        private static IReadOnlyList<string> _failures = Array.Empty<string>();

        /// <summary>True once the postfixes are in place.</summary>
        internal static bool Installed => _installed;

        /// <summary>The Harmony id that installed the hook, or empty.</summary>
        internal static string InstalledBy => _installedBy;

        /// <summary>How many methods carry the postfix (<c>Save*</c> and <c>Delete*</c>).</summary>
        internal static int PatchedCount { get; private set; }

        /// <summary>How many of those are <c>Save*</c> methods, i.e. produce a <c>SaveSection</c>.</summary>
        internal static int SaveMethodCount { get; private set; }

        /// <summary>Names of the methods Harmony refused to patch; every one was logged.</summary>
        internal static IReadOnlyList<string> Failures => _failures;

        /// <summary>
        /// Patches every hooked method with the one postfix. Idempotent: the first caller's Harmony
        /// instance owns the patches, later calls return at once.
        /// </summary>
        /// <param name="harmony">The installing plugin's Harmony instance.</param>
        /// <exception cref="ArgumentNullException"><paramref name="harmony"/> is null.</exception>
        internal static void Install(Harmony harmony)
        {
            if (harmony is null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            lock (Gate)
            {
                if (_installed)
                {
                    return;
                }

                _installed = true;
                _installedBy = harmony.Id;
            }

            var failures = new List<string>();
            var patched = 0;
            var saves = 0;

            foreach (var method in typeof(Game.SaveDataManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || !SaveMethodRules.IsHooked(method.Name))
                {
                    continue;
                }

                try
                {
                    // A fresh HarmonyMethod per target: Harmony treats it as mutable patch state.
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(SaveDataHook), nameof(Postfix)));
                    HookedMethods.Add(method);
                    patched++;
                    if (SaveMethodRules.IsSave(method.Name))
                    {
                        saves++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(method.Name);
                    CwyLsModLoader.Log.LogError(
                        $"SAVE could not patch SaveDataManager.{method.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            PatchedCount = patched;
            SaveMethodCount = saves;
            _failures = failures;
        }

        /// <summary>
        /// Adds a listener. Listeners run in subscription order on the thread that saved (in
        /// practice the main thread), each isolated: one that throws is logged and the rest still run.
        /// </summary>
        /// <param name="listener">The callback. Must be quick; it runs inside the game's save call.</param>
        /// <returns>The token that removes the listener.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="listener"/> is null.</exception>
        internal static IDisposable Subscribe(Action<SaveCall> listener) => Channel.Subscribe(listener);

        /// <summary>How many listeners are subscribed (2 in a normal session: events, data).</summary>
        internal static int ListenerCount => Channel.SubscriberCount;

        /// <summary>How many save/delete calls the hook has passed on this session.</summary>
        internal static long CallCount => Channel.PublishCount;

        /// <summary>
        /// Counts the buses' postfixes (patch methods declared in <see cref="BusAssemblyPrefix"/>
        /// assemblies) on each hooked method and returns the largest count, which is expected to be 1.
        /// A number above 1 means some feature patches the save family a second time again. Reads
        /// Harmony's patch info only; never throws.
        /// </summary>
        /// <returns>The largest per-method count of bus postfixes, or -1 when it cannot be read.</returns>
        internal static int MaxBusPostfixesPerMethod()
        {
            try
            {
                var max = 0;
                MethodInfo[] methods;
                lock (Gate)
                {
                    methods = HookedMethods.ToArray();
                }

                foreach (var method in methods)
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info is null)
                    {
                        continue;
                    }

                    var ours = 0;
                    foreach (var postfix in info.Postfixes)
                    {
                        var assembly = postfix.PatchMethod?.DeclaringType?.Assembly.GetName().Name;
                        if (assembly != null && assembly.StartsWith(BusAssemblyPrefix, StringComparison.Ordinal))
                        {
                            ours++;
                        }
                    }

                    max = Math.Max(max, ours);
                }

                return max;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"SAVE could not count the save postfixes: {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Writes the census to the trace file (not the console): methods hooked, listeners, and the
        /// largest number of bus postfixes on one save method. The evidence that each save is
        /// observed once.
        /// </summary>
        /// <param name="by">Who asks, e.g. <c>data</c>.</param>
        internal static void TraceCensus(string by) =>
            ConsoleEvents.File(
                "events",
                "saveHook",
                ("by", by),
                ("installedBy", _installedBy),
                ("methods", PatchedCount),
                ("saves", SaveMethodCount),
                ("failed", _failures.Count),
                ("listeners", ListenerCount),
                ("busPostfixesPerMethod", MaxBusPostfixesPerMethod()));

        /// <summary>Harmony postfix on every hooked method. Never throws into the game's save.</summary>
        /// <param name="__instance">The save manager.</param>
        /// <param name="__originalMethod">The method that ran.</param>
        internal static void Postfix(Game.SaveDataManager __instance, MethodBase __originalMethod)
        {
            var name = __originalMethod?.Name ?? "Save";

            // A subscriber that saves would re-enter this postfix; the guard turns that into a
            // bounded recursion instead of a stack overflow inside the player's save.
            if (_depth >= MaxDepth)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"SAVE notification for {name} suppressed: a subscriber is saving from inside a " +
                    $"notification ({MaxDepth} levels deep)");
                return;
            }

            _depth++;
            try
            {
                Channel.Publish(new SaveCall(__instance, name));
            }
            catch (Exception ex)
            {
                // Publish isolates listeners; reaching here means the hook itself failed.
                CwyLsModLoader.Log.LogError($"SAVE notification for {name} failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _depth--;
            }
        }
    }
}
