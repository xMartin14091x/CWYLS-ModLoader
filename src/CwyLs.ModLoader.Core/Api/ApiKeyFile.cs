using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>The outcome of writing or deleting the key file. Immutable.</summary>
    public sealed class ApiKeyFileResult
    {
        private ApiKeyFileResult(bool ok, string path, string problem)
        {
            Ok = ok;
            Path = path;
            Problem = problem;
        }

        /// <summary>True when the file is in the wanted state.</summary>
        public bool Ok { get; }

        /// <summary>The file's path.</summary>
        public string Path { get; }

        /// <summary>Why not, or empty.</summary>
        public string Problem { get; }

        internal static ApiKeyFileResult Success(string path) => new ApiKeyFileResult(true, path, string.Empty);

        internal static ApiKeyFileResult Failure(string path, string problem) => new ApiKeyFileResult(false, path, problem);
    }

    /// <summary>
    /// The per-launch key file outside programs read (<c>BepInEx/data/api/token.json</c>): written
    /// owner-only before any client can connect, rewritten on rotation, deleted when the API stops.
    /// On macOS/Linux the folder is made 0700 and the file 0600 while it is still empty, before
    /// the key is written into it; on Windows a protected DACL (owner and SYSTEM only) is set on
    /// the empty file the same way. If the permissions cannot be set the file is removed and the
    /// write fails, so the API stays off rather than leave the key readable.
    /// </summary>
    public static class ApiKeyFile
    {
        private const int OwnerReadWrite = 384;      // 0600
        private const int OwnerOnlyFolder = 448;     // 0700
        private const string OwnerOnlyDacl = "D:P(A;;FA;;;OW)(A;;FA;;;SY)";

        /// <summary>
        /// The file's JSON: <c>{"schema":1,"baseUrl":"http://127.0.0.1:7870/","port":7870,"transport":"…",
        /// "token":"…","pid":…,"createdAt":"…"}</c>.
        /// </summary>
        /// <param name="key">The access key.</param>
        /// <param name="port">The port.</param>
        /// <param name="transport">The transport's name.</param>
        /// <param name="createdAt">When it was minted.</param>
        public static string Content(ApiToken key, int port, string transport, DateTimeOffset createdAt)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            int pid;
            using (var self = Process.GetCurrentProcess())
            {
                pid = self.Id;
            }

            return ApiJson.Write(new JsonObject
            {
                ["schema"] = ApiDefaults.TokenFileSchema,
                ["baseUrl"] = ApiDefaults.BaseUrl(port),
                ["port"] = port,
                ["transport"] = transport,
                ["token"] = key.Reveal(),
                ["pid"] = pid,
                ["createdAt"] = ApiJson.Time(createdAt),
            }) + "\n";
        }

        /// <summary>Writes the file owner-only (see the class remarks). Never throws.</summary>
        /// <param name="path">The file path.</param>
        /// <param name="content">The JSON (<see cref="Content"/>).</param>
        public static ApiKeyFileResult Write(string path, string content)
        {
            if (string.IsNullOrEmpty(path))
            {
                return ApiKeyFileResult.Failure(path ?? string.Empty, "no path");
            }

            try
            {
                var folder = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                    if (!IsWindows && chmod(folder!, OwnerOnlyFolder) != 0)
                    {
                        return ApiKeyFileResult.Failure(path, "chmod 0700 on the folder failed (errno " + Marshal.GetLastWin32Error() + ")");
                    }
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var restricted = Restrict(path, out var problem);
                    if (!restricted)
                    {
                        stream.Dispose();
                        TryDelete(path);
                        return ApiKeyFileResult.Failure(path, problem);
                    }

                    var bytes = new UTF8Encoding(false).GetBytes(content ?? string.Empty);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                return ApiKeyFileResult.Success(path);
            }
            catch (Exception ex)
            {
                TryDelete(path);
                return ApiKeyFileResult.Failure(path, ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Deletes the file if it exists. Never throws.</summary>
        /// <param name="path">The file path.</param>
        public static ApiKeyFileResult Delete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return ApiKeyFileResult.Success(path);
            }
            catch (Exception ex)
            {
                return ApiKeyFileResult.Failure(path, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static bool Restrict(string path, out string problem)
        {
            if (!IsWindows)
            {
                if (chmod(path, OwnerReadWrite) != 0)
                {
                    problem = "chmod 0600 failed (errno " + Marshal.GetLastWin32Error() + "); the key was not written";
                    return false;
                }

                problem = string.Empty;
                return true;
            }

            var descriptor = IntPtr.Zero;
            try
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(OwnerOnlyDacl, 1, out descriptor, out _))
                {
                    problem = "building the owner-only ACL failed (Win32 error " + Marshal.GetLastWin32Error() + "); the key was not written";
                    return false;
                }

                if (!SetFileSecurityW(path, DaclSecurityInformation | ProtectedDaclSecurityInformation, descriptor))
                {
                    problem = "setting the owner-only ACL failed (Win32 error " + Marshal.GetLastWin32Error() + "); the key was not written";
                    return false;
                }

                problem = string.Empty;
                return true;
            }
            finally
            {
                if (descriptor != IntPtr.Zero)
                {
                    LocalFree(descriptor);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                // Reported to the trace file; the caller already reports the original failure.
                CwyLs.ModLoader.Core.Logging.ConsoleEvents.File(ApiAuditLog.Sys, "keyFileCleanup", ("path", path), ("error", ex.GetType().Name + ": " + ex.Message));
            }
        }

        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int chmod(string path, int mode);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, out uint size);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool SetFileSecurityW(string fileName, uint securityInformation, IntPtr securityDescriptor);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
