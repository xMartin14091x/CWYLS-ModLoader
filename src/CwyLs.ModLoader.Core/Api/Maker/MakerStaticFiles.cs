using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>What <see cref="MakerStaticFiles.Read"/> found.</summary>
    public enum MakerStaticStatus
    {
        /// <summary>The file was read.</summary>
        Ok = 0,

        /// <summary>The path is not a plain path into the web folder (traversal, dotfile, odd character): 400.</summary>
        BadPath = 1,

        /// <summary>No such file, or a type that is not served: 404.</summary>
        NotFound = 2,

        /// <summary>The web folder is missing or the file could not be read: 503.</summary>
        Unavailable = 3,
    }

    /// <summary>
    /// The web app's files under <c>BepInEx/plugins/CwyLsModLoader/maker/</c>, served at <c>/maker/</c>.
    /// Strict: every path segment is <c>[A-Za-z0-9][A-Za-z0-9._-]*</c> (so no <c>..</c>, no dotfiles, no
    /// <c>_drafts</c>, no backslash or percent sign), only known file types are served, the resolved path
    /// must stay inside the folder, links are not followed, and files over
    /// <see cref="MakerDefaults.MaxStaticFileBytes"/> are refused. <c>index.html</c> at the top gets the
    /// per-start token, only after the router authenticates the browser session, as
    /// <c>&lt;meta name="cwyls-maker-token" content="…"&gt;</c> right after
    /// <c>&lt;head&gt;</c>. Files are read on each request (no cache), so a rebuilt web app shows at once.
    /// </summary>
    public static class MakerStaticFiles
    {
        /// <summary>The page the bare <c>/maker/</c> serves.</summary>
        public const string IndexFile = "index.html";

        private static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".map"] = "application/json; charset=utf-8",
            [".webmanifest"] = "application/manifest+json; charset=utf-8",
            [".txt"] = "text/plain; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf",
        };

        /// <summary>The <c>Content-Type</c> for a file name, or null when the type is not served.</summary>
        /// <param name="fileName">The file name.</param>
        public static string? ContentTypeOf(string fileName)
        {
            var ext = Path.GetExtension(fileName ?? string.Empty);
            return ext.Length > 0 && Types.TryGetValue(ext, out var type) ? type : null;
        }

        /// <summary>
        /// Reads one file of the web app.
        /// </summary>
        /// <param name="webRoot">The web folder, or null when unknown.</param>
        /// <param name="relative">The path below <c>/maker/</c> without the query (empty = <c>index.html</c>).</param>
        /// <param name="token">The maker token to inject into the top <c>index.html</c>, or null for none.</param>
        /// <param name="bytes">The file (with the token for the index); empty unless <see cref="MakerStaticStatus.Ok"/>.</param>
        /// <param name="contentType">The type to send; empty unless <see cref="MakerStaticStatus.Ok"/>.</param>
        /// <param name="problem">Why it was not served; empty on success. Never contains the token.</param>
        public static MakerStaticStatus Read(
            string? webRoot, string relative, string? token, out byte[] bytes, out string contentType, out string problem)
        {
            bytes = new byte[0];
            contentType = string.Empty;
            if (relative is null)
            {
                relative = string.Empty;
            }

            if (relative.Length == 0)
            {
                relative = IndexFile;
            }

            var segments = relative.Split('/');
            foreach (var segment in segments)
            {
                if (!IsPlainSegment(segment))
                {
                    problem = "the path must be plain: segments of letters, digits and . _ - that do not start with a dot or underscore";
                    return MakerStaticStatus.BadPath;
                }
            }

            var name = segments[segments.Length - 1];
            var type = ContentTypeOf(name);
            if (type is null)
            {
                problem = "no Pack Maker file /maker/" + relative + " (this file type is not served)";
                return MakerStaticStatus.NotFound;
            }

            if (string.IsNullOrEmpty(webRoot) || !Directory.Exists(webRoot))
            {
                problem = "the Pack Maker web files are not installed (BepInEx/plugins/CwyLsModLoader/" + MakerDefaults.WebFolderName + "/ is missing)";
                return MakerStaticStatus.Unavailable;
            }

            try
            {
                var root = Path.GetFullPath(webRoot!);
                var rootWithSlash = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? root : root + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
                if (!full.StartsWith(rootWithSlash, StringComparison.Ordinal))
                {
                    problem = "the path leaves the Pack Maker folder";
                    return MakerStaticStatus.BadPath;
                }

                if (!File.Exists(full) || HasLinkOnTheWay(rootWithSlash, full))
                {
                    problem = "no Pack Maker file /maker/" + relative;
                    return MakerStaticStatus.NotFound;
                }

                var info = new FileInfo(full);
                if (info.Length > MakerDefaults.MaxStaticFileBytes)
                {
                    problem = "the file /maker/" + relative + " is larger than " + MakerDefaults.MaxStaticFileBytes + " bytes and is not served";
                    return MakerStaticStatus.NotFound;
                }

                var data = File.ReadAllBytes(full);
                var isTopIndex = segments.Length == 1 && string.Equals(name, IndexFile, StringComparison.OrdinalIgnoreCase);
                bytes = isTopIndex && token != null ? InjectToken(data, token) : data;
                contentType = type;
                problem = string.Empty;
                return MakerStaticStatus.Ok;
            }
            catch (Exception ex)
            {
                problem = "the file /maker/" + relative + " could not be read: " + ex.GetType().Name;
                return MakerStaticStatus.Unavailable;
            }
        }

        /// <summary>
        /// Puts <c>&lt;meta name="cwyls-maker-token" content="…"&gt;</c> right after the opening
        /// <c>&lt;head…&gt;</c> tag (so it is the first such meta in the document); after the doctype when
        /// there is no head element; at the very start otherwise. The token is hex, so it needs no escaping.
        /// </summary>
        /// <param name="html">The page as UTF-8 bytes.</param>
        /// <param name="token">The token (64 hex characters).</param>
        public static byte[] InjectToken(byte[] html, string token)
        {
            if (html is null)
            {
                throw new ArgumentNullException(nameof(html));
            }

            var text = new UTF8Encoding(false, false).GetString(html);
            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text.Substring(1);
            }

            var meta = "<meta name=\"" + MakerDefaults.TokenMetaName + "\" content=\"" + HexOnly(token) + "\">";
            return new UTF8Encoding(false).GetBytes(text.Insert(InsertionPoint(text), meta));
        }

        private static int InsertionPoint(string text)
        {
            var search = 0;
            while (search < text.Length)
            {
                var head = text.IndexOf("<head", search, StringComparison.OrdinalIgnoreCase);
                if (head < 0)
                {
                    break;
                }

                // "<head" must be the whole tag name (not <header>).
                var next = head + 5 < text.Length ? text[head + 5] : '\0';
                if (next == '>' || next == ' ' || next == '\t' || next == '\n' || next == '\r' || next == '/')
                {
                    var close = text.IndexOf('>', head);
                    return close < 0 ? text.Length : close + 1;
                }

                search = head + 5;
            }

            var doctype = text.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
            if (doctype >= 0)
            {
                var close = text.IndexOf('>', doctype);
                if (close >= 0)
                {
                    return close + 1;
                }
            }

            return 0;
        }

        private static string HexOnly(string token)
        {
            foreach (var c in token ?? string.Empty)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    throw new ArgumentException("the maker token must be hex", nameof(token));
                }
            }

            return token ?? string.Empty;
        }

        private static bool IsPlainSegment(string segment)
        {
            if (segment.Length == 0 || segment.Length > 128)
            {
                return false;
            }

            var first = segment[0];
            if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || (first >= '0' && first <= '9')))
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>True when the file or a folder between it and the root is a symbolic link or junction.</summary>
        private static bool HasLinkOnTheWay(string rootWithSlash, string full)
        {
            var path = full;
            while (path.Length >= rootWithSlash.Length)
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                var parent = Path.GetDirectoryName(path);
                if (parent is null || parent.Length >= path.Length)
                {
                    break;
                }

                path = parent;
            }

            return false;
        }
    }
}
