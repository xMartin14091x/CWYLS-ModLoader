using System;
using System.Reflection;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// Language voice sets: loads one Ogg Vorbis (or WAV) file into an <see cref="AudioClip"/> that
    /// stays <em>compressed in memory</em> - the same load type the game's own 1381 voice clips use
    /// (Vorbis, CompressedInMemory, as observed in game 1.17.x) - instead of decoding every clip to
    /// PCM up front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine does the work: <c>UnityWebRequestMultimedia.GetAudioClip</c> with a
    /// <c>DownloadHandlerAudioClip</c> whose <c>compressed</c> is true and <c>streamAudio</c> false.
    /// The file read and the request run off the main thread; the main thread only creates the clip
    /// object when the request is done. With <c>streamAudio</c> false the clip owns its data, so the
    /// request is disposed as soon as the clip exists.
    /// </para>
    /// <para>
    /// Reached by reflection because this adapter does not reference the two UnityWebRequest module
    /// assemblies (only <c>CwyLs.ModLoader</c> does, for its PCM loader, which always decompresses). The
    /// members are resolved once and a missing one switches language voice sets off with one ERROR
    /// line, never a crash. They were checked against the shipped 1.17.3 modules
    /// (<c>UnityEngine.UnityWebRequestAudioModule</c>, <c>UnityEngine.UnityWebRequestModule</c>).
    /// </para>
    /// <para>Main thread only.</para>
    /// </remarks>
    internal static class LanguageVoiceLoader
    {
        private const string AudioModuleAssembly = "UnityEngine.UnityWebRequestAudioModule";
        private const string RequestModuleAssembly = "UnityEngine.UnityWebRequestModule";
        private const string MultimediaTypeName = "UnityEngine.Networking.UnityWebRequestMultimedia";
        private const string HandlerTypeName = "UnityEngine.Networking.DownloadHandlerAudioClip";
        private const string RequestTypeName = "UnityEngine.Networking.UnityWebRequest";

        private static readonly object Gate = new object();

        private static bool _resolved;
        private static string _error = string.Empty;
        private static MethodInfo? _getAudioClip;
        private static PropertyInfo? _downloadHandler;
        private static PropertyInfo? _compressed;
        private static PropertyInfo? _streamAudio;
        private static MethodInfo? _send;
        private static PropertyInfo? _result;
        private static PropertyInfo? _requestError;
        private static MethodInfo? _getContent;

        /// <summary>Resolves the engine members once.</summary>
        /// <param name="error">Why the loader is unavailable, or empty.</param>
        /// <returns>True when files can be loaded.</returns>
        internal static bool TryResolve(out string error)
        {
            lock (Gate)
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _error = Resolve();
                }

                error = _error;
                return _error.Length == 0;
            }
        }

        /// <summary>
        /// Starts loading a file. The returned request is polled with <see cref="IsDone"/> and must be
        /// finished with <see cref="End"/> (which disposes it) or <see cref="Abandon"/>.
        /// </summary>
        /// <param name="absolutePath">The file.</param>
        /// <param name="audioType"><see cref="AudioType.OGGVORBIS"/> or <see cref="AudioType.WAV"/>.</param>
        /// <param name="error">Why it could not start, or empty.</param>
        /// <returns>The in-flight request, or null.</returns>
        internal static Request? Begin(string absolutePath, AudioType audioType, out string error)
        {
            if (!TryResolve(out error))
            {
                return null;
            }

            object? request = null;
            try
            {
                // Uri.AbsoluteUri, not "file://" + path: the game folder has spaces in its name.
                request = _getAudioClip!.Invoke(null, new object[] { new Uri(absolutePath).AbsoluteUri, audioType });
                if (request is null)
                {
                    error = "GetAudioClip returned no request";
                    return null;
                }

                var handler = _downloadHandler!.GetValue(request, null);
                if (handler is null)
                {
                    error = "the request came without an audio download handler";
                    Dispose(request);
                    return null;
                }

                // Compressed: the Vorbis data stays Vorbis in memory and is decoded while it plays,
                // as the game's own clips are. Not streamed: the clip owns its data, so the request
                // can be disposed once the clip exists.
                _compressed!.SetValue(handler, true, null);
                _streamAudio!.SetValue(handler, false, null);

                if (!(_send!.Invoke(request, null) is AsyncOperation operation))
                {
                    error = "SendWebRequest returned no operation";
                    Dispose(request);
                    return null;
                }

                error = string.Empty;
                return new Request(request, operation);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + Message(ex);
                if (request != null)
                {
                    Dispose(request);
                }

                return null;
            }
        }

        /// <summary>True when the request has finished, successfully or not.</summary>
        /// <param name="request">An in-flight request.</param>
        internal static bool IsDone(Request request)
        {
            try
            {
                return request.Operation.isDone;
            }
            catch (Exception ex)
            {
                // A request whose state cannot be read is treated as finished; End then reports why.
                CwyLsModLoader.Log.LogDebug("VOICE a language clip request could not be polled: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        /// <summary>Finishes a request: the clip or the reason there is none. Always disposes the request.</summary>
        /// <param name="request">A request <see cref="IsDone"/> reported done.</param>
        /// <param name="error">Why there is no clip, or empty.</param>
        /// <returns>The clip, or null.</returns>
        internal static AudioClip? End(Request request, out string error)
        {
            try
            {
                var result = _result!.GetValue(request.Handle, null)?.ToString() ?? string.Empty;
                if (!string.Equals(result, "Success", StringComparison.Ordinal))
                {
                    var message = _requestError!.GetValue(request.Handle, null) as string;
                    error = result + (string.IsNullOrEmpty(message) ? string.Empty : ": " + message);
                    return null;
                }

                var clip = _getContent!.Invoke(null, new[] { request.Handle }) as AudioClip;
                if (clip == null)
                {
                    error = "the decoder produced no clip";
                    return null;
                }

                error = string.Empty;
                return clip;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + Message(ex);
                return null;
            }
            finally
            {
                Dispose(request.Handle);
            }
        }

        /// <summary>Drops a request without reading it (timeout, shutdown).</summary>
        /// <param name="request">The request.</param>
        internal static void Abandon(Request request) => Dispose(request.Handle);

        private static void Dispose(object request)
        {
            try
            {
                (request as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("VOICE a language clip request could not be disposed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Caller holds <see cref="Gate"/>. Returns the failure reason, or empty.</summary>
        private static string Resolve()
        {
            var audioModule = FindAssembly(AudioModuleAssembly);
            var requestModule = FindAssembly(RequestModuleAssembly);
            if (audioModule is null || requestModule is null)
            {
                return "the UnityWebRequest modules are not loadable";
            }

            var multimedia = audioModule.GetType(MultimediaTypeName, throwOnError: false);
            var handler = audioModule.GetType(HandlerTypeName, throwOnError: false);
            var request = requestModule.GetType(RequestTypeName, throwOnError: false);
            if (multimedia is null || handler is null || request is null)
            {
                return "a UnityWebRequest type is missing ("
                    + (multimedia is null ? MultimediaTypeName + " " : string.Empty)
                    + (handler is null ? HandlerTypeName + " " : string.Empty)
                    + (request is null ? RequestTypeName : string.Empty) + ")";
            }

            const BindingFlags instance = BindingFlags.Public | BindingFlags.Instance;
            const BindingFlags statics = BindingFlags.Public | BindingFlags.Static;

            _getAudioClip = multimedia.GetMethod("GetAudioClip", statics, null, new[] { typeof(string), typeof(AudioType) }, null);
            _getContent = handler.GetMethod("GetContent", statics, null, new[] { request }, null);
            _compressed = handler.GetProperty("compressed", instance);
            _streamAudio = handler.GetProperty("streamAudio", instance);
            _downloadHandler = request.GetProperty("downloadHandler", instance);
            _send = request.GetMethod("SendWebRequest", instance, null, Type.EmptyTypes, null);
            _result = request.GetProperty("result", instance);
            _requestError = request.GetProperty("error", instance);

            var missing = string.Empty;
            if (_getAudioClip is null) { missing += " UnityWebRequestMultimedia.GetAudioClip(string, AudioType)"; }
            if (_getContent is null) { missing += " DownloadHandlerAudioClip.GetContent"; }
            if (_compressed is null || !_compressed.CanWrite) { missing += " DownloadHandlerAudioClip.compressed"; }
            if (_streamAudio is null || !_streamAudio.CanWrite) { missing += " DownloadHandlerAudioClip.streamAudio"; }
            if (_downloadHandler is null) { missing += " UnityWebRequest.downloadHandler"; }
            if (_send is null) { missing += " UnityWebRequest.SendWebRequest"; }
            if (_result is null) { missing += " UnityWebRequest.result"; }
            if (_requestError is null) { missing += " UnityWebRequest.error"; }

            return missing.Length == 0 ? string.Empty : "missing engine members:" + missing;
        }

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
                CwyLsModLoader.Log.LogWarning("VOICE assembly '" + simpleName + "' could not be loaded: " + ex.GetType().Name + ": " + Message(ex));
                return null;
            }
        }

        private static string Message(Exception ex) =>
            (ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex).Message;

        /// <summary>One in-flight request.</summary>
        internal sealed class Request
        {
            internal Request(object handle, AsyncOperation operation)
            {
                Handle = handle;
                Operation = operation;
            }

            /// <summary>The <c>UnityWebRequest</c>.</summary>
            internal object Handle { get; }

            /// <summary>Its <c>UnityWebRequestAsyncOperation</c>.</summary>
            internal AsyncOperation Operation { get; }
        }
    }
}
