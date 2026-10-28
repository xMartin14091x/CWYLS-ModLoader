using System;
using System.Reflection;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// The reflection half of the voice feature: everything this adapter needs from <c>AudioManager.dll</c>,
    /// resolved once and cached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of it goes through reflection, and all of it is about one awkward fact:
    /// <c>VoiceManager</c>'s clip cache and the method that reads it are <em>private members of the
    /// open generic base</em> <c>KanKikuchi.AudioManager.AudioManager&lt;T&gt;</c> (game 1.17.2). A
    /// private base member is not inherited, so nothing on <c>VoiceManager</c> itself finds them. The
    /// way in is the verified one: take the generic definition, close it over
    /// <c>VoiceManager</c>, and look the member up on that closed type - which is also the method
    /// Harmony is asked to patch.
    /// </para>
    /// <para>
    /// The assembly is loaded by name rather than referenced at compile time: it is a third-party
    /// DLL this solution does not link against, and Mono only loads it once the game touches audio,
    /// which is typically after our plugins have Awoken.
    /// </para>
    /// </remarks>
    internal static class VoiceSeam
    {
        /// <summary>Simple name of the assembly the voice types live in.</summary>
        internal const string AudioAssemblyName = "AudioManager";

        /// <summary>Reflection name of the game's voice manager.</summary>
        internal const string VoiceManagerTypeName = "KanKikuchi.AudioManager.VoiceManager";

        /// <summary>Reflection name of the open generic base that owns the clip cache.</summary>
        internal const string AudioManagerTypeName = "KanKikuchi.AudioManager.AudioManager`1";

        /// <summary>Reflection name of the open generic singleton base that owns <c>Instance</c>.</summary>
        internal const string SingletonTypeName = "KanKikuchi.AudioManager.SingletonMonoBehaviour`1";

        /// <summary>The private lookup this feature prefixes.</summary>
        internal const string GetAudioClipMethodName = "GetAudioClip";

        /// <summary>The public entry point a line is played through.</summary>
        internal const string PlayMethodName = "Play";

        /// <summary>The private clip cache on the open generic base.</summary>
        internal const string ClipCacheFieldName = "_audioClipDict";

        /// <summary>
        /// What <see cref="ClosedAudioManagerTypeName"/> falls back to when the type cannot be
        /// resolved, so a patch-target report still names what was looked for. Verified to resolve
        /// against the shipped v1.17.2 AudioManager.dll.
        /// </summary>
        internal const string ClosedAudioManagerFallbackName =
            AudioManagerTypeName + "[[" + VoiceManagerTypeName + ", " + AudioAssemblyName + "]]";

        /// <summary>As above, for the singleton base.</summary>
        internal const string ClosedSingletonFallbackName =
            SingletonTypeName + "[[" + VoiceManagerTypeName + ", " + AudioAssemblyName + "]]";

        private static readonly object Gate = new object();

        private static bool _resolved;
        private static string _error = string.Empty;
        private static Assembly? _assembly;
        private static Type? _voiceManagerType;
        private static Type? _closedAudioManagerType;
        private static Type? _closedSingletonType;
        private static MethodInfo? _getAudioClip;
        private static MethodInfo? _play;
        private static PropertyInfo? _instance;
        private static FieldInfo? _clipCache;
        private static bool _clipCacheReported;

        /// <summary>Why resolution failed, or empty when it succeeded (or was never attempted).</summary>
        internal static string Error
        {
            get
            {
                lock (Gate)
                {
                    return _error;
                }
            }
        }

        /// <summary><c>KanKikuchi.AudioManager.VoiceManager</c>, or null when it is not there.</summary>
        internal static Type? VoiceManagerType
        {
            get
            {
                TryResolve(out _);
                return _voiceManagerType;
            }
        }

        /// <summary><c>AudioManager&lt;VoiceManager&gt;</c>, or null when it could not be built.</summary>
        internal static Type? ClosedAudioManagerType
        {
            get
            {
                TryResolve(out _);
                return _closedAudioManagerType;
            }
        }

        /// <summary>The closed generic's private <c>GetAudioClip(string)</c>, or null.</summary>
        internal static MethodInfo? GetAudioClipMethod
        {
            get
            {
                TryResolve(out _);
                return _getAudioClip;
            }
        }

        /// <summary>
        /// Reflection name of the closed <c>AudioManager&lt;VoiceManager&gt;</c>, taken from the
        /// runtime's own <see cref="Type.FullName"/> so it round-trips through
        /// <see cref="Assembly.GetType(string, bool)"/>; the constant fallback is used only when
        /// the type could not be resolved at all.
        /// </summary>
        internal static string ClosedAudioManagerTypeName =>
            ClosedAudioManagerType?.FullName ?? ClosedAudioManagerFallbackName;

        /// <summary>As above, for <c>SingletonMonoBehaviour&lt;VoiceManager&gt;</c>.</summary>
        internal static string ClosedSingletonTypeName
        {
            get
            {
                TryResolve(out _);
                return _closedSingletonType?.FullName ?? ClosedSingletonFallbackName;
            }
        }

        /// <summary>
        /// Resolves the assembly, the closed generic type and the members, once. Later calls return
        /// the cached outcome.
        /// </summary>
        /// <param name="error">Why it failed, or empty on success.</param>
        /// <returns>True when <see cref="GetAudioClipMethod"/> is available.</returns>
        internal static bool TryResolve(out string error)
        {
            lock (Gate)
            {
                if (_resolved)
                {
                    error = _error;
                    return _getAudioClip != null;
                }

                _resolved = true;
                _error = Resolve();
                error = _error;
                return _getAudioClip != null;
            }
        }

        /// <summary>
        /// Human-readable description of the patch target, e.g.
        /// <c>AudioManager!KanKikuchi.AudioManager.AudioManager`1[[...]].GetAudioClip(string)</c>.
        /// </summary>
        internal static string Describe() =>
            AudioAssemblyName + "!" + ClosedAudioManagerTypeName + "." + GetAudioClipMethodName + "(string)";

        /// <summary>
        /// <c>SingletonMonoBehaviour&lt;VoiceManager&gt;.Instance</c> - the live voice manager.
        /// </summary>
        /// <param name="error">Why there is none, or empty on success.</param>
        /// <returns>The instance, or null before the game has created it.</returns>
        internal static object? TryGetInstance(out string error)
        {
            if (!TryResolve(out error))
            {
                return null;
            }

            if (_instance is null)
            {
                error = "the Instance property is missing on " + ClosedSingletonTypeName;
                return null;
            }

            try
            {
                var instance = _instance.GetValue(null, null);
                if (instance is null)
                {
                    error = "VoiceManager.Instance is null; the game has not created it yet";
                    return null;
                }

                error = string.Empty;
                return instance;
            }
            catch (Exception ex)
            {
                error = "VoiceManager.Instance threw: " + ex.GetType().Name + ": " + Message(ex);
                CwyLsModLoader.Log.LogError("VOICE " + error);
                return null;
            }
        }

        /// <summary>
        /// Calls the (patched) <c>GetAudioClip</c> through reflection - how a check proves the
        /// prefix is really in the game's lookup path and not merely registered.
        /// </summary>
        /// <param name="instance">A live <c>VoiceManager</c>, from <see cref="TryGetInstance"/>.</param>
        /// <param name="audioPath">What the game would pass, normally a bare voice name.</param>
        /// <param name="error">Why it failed, or empty on success.</param>
        /// <returns>The clip the game would get (a <c>UnityEngine.AudioClip</c>), or null.</returns>
        internal static object? TryInvokeGetAudioClip(object instance, string audioPath, out string error)
        {
            if (!TryResolve(out error))
            {
                return null;
            }

            if (instance is null)
            {
                error = "no VoiceManager instance was supplied";
                return null;
            }

            try
            {
                var clip = _getAudioClip!.Invoke(instance, new object[] { audioPath });
                error = clip is null ? "GetAudioClip returned null for '" + audioPath + "'" : string.Empty;
                return clip;
            }
            catch (Exception ex)
            {
                error = "GetAudioClip('" + audioPath + "') threw: " + ex.GetType().Name + ": " + Message(ex);
                CwyLsModLoader.Log.LogError("VOICE " + error);
                return null;
            }
        }

        /// <summary>
        /// Plays a voice name through <c>VoiceManager.Play</c> exactly as the game does, with its
        /// default arguments.
        /// </summary>
        /// <param name="audioPath">The voice name to play.</param>
        /// <param name="error">Why it failed, or empty on success.</param>
        /// <returns>The <c>AudioPlayer</c> the game returned, or null when nothing played.</returns>
        internal static object? TryPlay(string audioPath, out string error)
        {
            var instance = TryGetInstance(out error);
            if (instance is null)
            {
                return null;
            }

            if (_play is null)
            {
                error = "the Play method is missing on " + VoiceManagerTypeName;
                return null;
            }

            try
            {
                // The full argument list, because reflection does not fill in optional parameters:
                // Play(audioPath, volumeRate, delay, pitch, isLoop, callback, key, sourceTransform).
                var player = _play.Invoke(
                    instance,
                    new object?[] { audioPath, 1f, 0f, 1f, false, null, string.Empty, null });

                error = player is null ? "Play('" + audioPath + "') returned no AudioPlayer" : string.Empty;
                return player;
            }
            catch (Exception ex)
            {
                error = "Play('" + audioPath + "') threw: " + ex.GetType().Name + ": " + Message(ex);
                CwyLsModLoader.Log.LogError("VOICE " + error);
                return null;
            }
        }

        /// <summary>
        /// Language voice sets: the game's own clip for a voice name, read straight from the voice manager's
        /// private cache (<c>_audioClipDict</c>) - never through the patched
        /// lookup, so no override can answer. Used to compare a language set's clip with the game's
        /// and by the smoke check that proves a non-set language still gets the game's clip.
        /// </summary>
        /// <param name="instance">A live <c>VoiceManager</c>, from <see cref="TryGetInstance"/>.</param>
        /// <param name="name">The voice name (the cache key).</param>
        /// <returns>The game's clip (a <c>UnityEngine.AudioClip</c>), or null when the cache has none.</returns>
        internal static object? TryGetGameClip(object? instance, string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var cache = TryGetGameClipCache(instance);
            if (cache is null)
            {
                return null;
            }

            try
            {
                return cache.Contains(name!) ? cache[name!] : null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("VOICE could not read '" + name + "' from the game's clip cache: " + ex.GetType().Name + ": " + Message(ex));
                return null;
            }
        }

        /// <summary>
        /// Language voice sets: the game's whole voice clip cache (name to <c>UnityEngine.AudioClip</c>) as a
        /// read-only view for bulk questions such as "which set clips have no game clip at all".
        /// Callers must not write to it.
        /// </summary>
        /// <param name="instance">A live <c>VoiceManager</c>, from <see cref="TryGetInstance"/>.</param>
        /// <returns>The cache, or null when it cannot be read (logged once).</returns>
        internal static System.Collections.IDictionary? TryGetGameClipCache(object? instance)
        {
            if (instance is null || !TryResolve(out _))
            {
                return null;
            }

            try
            {
                _clipCache ??= _closedAudioManagerType!.GetField(
                    ClipCacheFieldName, BindingFlags.NonPublic | BindingFlags.Instance);

                if (_clipCache is null)
                {
                    if (!_clipCacheReported)
                    {
                        _clipCacheReported = true;
                        CwyLsModLoader.Log.LogWarning(
                            "VOICE " + ClipCacheFieldName + " not found on " + ClosedAudioManagerTypeName
                            + "; language clips cannot be compared with the game's");
                    }

                    return null;
                }

                // Dictionary<string, AudioClip> implements the non-generic IDictionary.
                return _clipCache.GetValue(instance) as System.Collections.IDictionary;
            }
            catch (Exception ex)
            {
                if (!_clipCacheReported)
                {
                    _clipCacheReported = true;
                    CwyLsModLoader.Log.LogWarning(
                        "VOICE could not read the game's clip cache: " + ex.GetType().Name + ": " + Message(ex));
                }
                else
                {
                    CwyLsModLoader.Log.LogDebug("VOICE the game's clip cache is unreadable: " + ex.GetType().Name + ": " + Message(ex));
                }

                return null;
            }
        }

        /// <summary>Caller must hold <see cref="Gate"/>. Returns the failure reason, or empty.</summary>
        private static string Resolve()
        {
            _assembly = FindAssembly(AudioAssemblyName);
            if (_assembly is null)
            {
                return "assembly '" + AudioAssemblyName + "' is not loaded and could not be loaded";
            }

            _voiceManagerType = FindType(_assembly, VoiceManagerTypeName);
            if (_voiceManagerType is null)
            {
                return "type '" + VoiceManagerTypeName + "' not found in " + AudioAssemblyName;
            }

            var openAudioManager = FindType(_assembly, AudioManagerTypeName);
            if (openAudioManager is null)
            {
                return "type '" + AudioManagerTypeName + "' not found in " + AudioAssemblyName;
            }

            try
            {
                _closedAudioManagerType = openAudioManager.MakeGenericType(_voiceManagerType);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    "VOICE could not close " + AudioManagerTypeName + " over " + VoiceManagerTypeName
                    + ": " + ex.GetType().Name + ": " + Message(ex));
                return "could not build AudioManager<VoiceManager>: " + ex.GetType().Name;
            }

            var openSingleton = FindType(_assembly, SingletonTypeName);
            if (openSingleton != null)
            {
                try
                {
                    _closedSingletonType = openSingleton.MakeGenericType(_voiceManagerType);
                    _instance = _closedSingletonType.GetProperty(
                        "Instance", BindingFlags.Public | BindingFlags.Static);
                }
                catch (Exception ex)
                {
                    // Not fatal: the prefix does not need Instance, only the smoke checks and
                    // anything that wants to play a line right now do.
                    CwyLsModLoader.Log.LogError(
                        "VOICE could not reach SingletonMonoBehaviour<VoiceManager>.Instance: "
                        + ex.GetType().Name + ": " + Message(ex));
                }
            }

            _play = _voiceManagerType.GetMethod(
                PlayMethodName, BindingFlags.Public | BindingFlags.Instance);

            _getAudioClip = _closedAudioManagerType!.GetMethod(
                GetAudioClipMethodName,
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            if (_getAudioClip is null)
            {
                return "private instance method '" + GetAudioClipMethodName + "(string)' not found on "
                    + ClosedAudioManagerTypeName;
            }

            return string.Empty;
        }

        /// <summary>
        /// Finds a loaded assembly by simple name, falling back to a load - AudioManager.dll is
        /// usually still untouched when our plugins Awake.
        /// </summary>
        private static Assembly? FindAssembly(string simpleName)
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.Ordinal))
                {
                    return loaded;
                }
            }

            try
            {
                return Assembly.Load(new AssemblyName(simpleName));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    "VOICE assembly '" + simpleName + "' could not be loaded: "
                    + ex.GetType().Name + ": " + Message(ex));
                return null;
            }
        }

        private static Type? FindType(Assembly assembly, string typeName)
        {
            try
            {
                return assembly.GetType(typeName, throwOnError: false);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    "VOICE type '" + typeName + "' could not be read from " + assembly.GetName().Name
                    + ": " + ex.GetType().Name + ": " + Message(ex));
                return null;
            }
        }

        /// <summary>A reflection call reports the real fault as the inner exception; say that one.</summary>
        private static string Message(Exception ex) =>
            (ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex).Message;
    }
}
