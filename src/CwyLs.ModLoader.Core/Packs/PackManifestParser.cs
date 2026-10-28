using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// Turns the text of a <c>manifest.json</c> into a <see cref="PackManifest"/>.
    /// <para>
    /// Strict on purpose. A pack that half-loads because a field was misspelled is worse than a
    /// pack that is skipped with a message naming the field, so every unknown member is an error
    /// unless it is namespaced with an <c>x-</c> prefix, which is reserved for a pack author's own
    /// metadata and is never read by the framework.
    /// </para>
    /// </summary>
    public static class PackManifestParser
    {
        /// <summary>Prefix that marks a member as the pack author's own, to be ignored here.</summary>
        public const string ExtensionPrefix = "x-";

        private const string KeyId = "id";
        private const string KeyName = "name";
        private const string KeyVersion = "version";
        private const string KeyAuthor = "author";
        private const string KeyDescription = "description";
        private const string KeyGameVersion = "gameVersion";
        private const string KeyMinGameVersion = "minGameVersion";
        private const string KeyMaxGameVersion = "maxGameVersion";
        private const string KeyLoaderVersion = "loaderVersion";

        /// <summary>The same field under the loader's old name ("CWY Patcher"); read when the new one is absent.</summary>
        private const string KeyLoaderVersionOld = "contentPatcherVersion";
        private const string KeyDependsOn = "dependsOn";
        private const string KeyMinVersion = "minVersion";
        private const string KeyOptional = "optional";
        private const string KeyEntryDll = "entryDll";
        private const string KeyContentPackFor = "contentPackFor";

        private static readonly string[] KnownKeys =
        {
            KeyId, KeyName, KeyVersion, KeyAuthor, KeyDescription,
            KeyGameVersion, KeyMinGameVersion, KeyMaxGameVersion, KeyLoaderVersion, KeyLoaderVersionOld, KeyDependsOn,
            KeyEntryDll, KeyContentPackFor,
        };

        /// <summary>Parses and validates a manifest.</summary>
        /// <param name="json">The whole text of <c>manifest.json</c>.</param>
        /// <exception cref="PackManifestException">The text is not a valid manifest.</exception>
        public static PackManifest Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new PackManifestException("the manifest is empty");
            }

            JsonObject root;
            try
            {
                var value = JsonReader.Parse(json!);
                if (!(value is JsonObject obj))
                {
                    throw new PackManifestException(
                        "the manifest must be a JSON object, not " + JsonReader.TypeName(value));
                }

                root = obj;
            }
            catch (JsonParseException ex)
            {
                // The reader's message already carries line:column, which is the one thing a pack
                // author needs to fix a syntax error.
                throw new PackManifestException("invalid JSON at " + ex.Message);
            }

            RejectUnknownMembers(root);

            var id = RequiredString(root, KeyId);
            if (!PackManifest.IsValidId(id))
            {
                throw new PackManifestException(
                    "\"" + KeyId + "\": \"" + id + "\" must match " + PackManifest.IdPattern +
                    " (lowercase letters, digits, '.', '_' and '-'; 3 to 64 characters)");
            }

            var name = RequiredString(root, KeyName);
            var version = RequiredVersion(root, KeyVersion);
            var loaderVersion = OptionalVersion(root, KeyLoaderVersion) ?? OptionalVersion(root, KeyLoaderVersionOld);
            var minGameVersion = OptionalVersion(root, KeyMinGameVersion);
            var maxGameVersion = OptionalVersion(root, KeyMaxGameVersion);

            if (minGameVersion != null && maxGameVersion != null && minGameVersion > maxGameVersion)
            {
                throw new PackManifestException(
                    "\"" + KeyMinGameVersion + "\": " + minGameVersion + " is above \"" + KeyMaxGameVersion +
                    "\" " + maxGameVersion + "; the range is inclusive, so use the same version twice for exactly one build");
            }

            var dependencies = ParseDependencies(root, id);
            var entryDll = ParseEntryDll(root);
            var contentPackFor = ParseContentPackFor(root, id, entryDll, dependencies);

            return new PackManifest(
                id,
                name,
                version,
                OptionalString(root, KeyAuthor),
                OptionalString(root, KeyDescription),
                OptionalString(root, KeyGameVersion),
                loaderVersion,
                dependencies,
                minGameVersion,
                maxGameVersion,
                entryDll,
                contentPackFor,
                CollectExtensions(root));
        }

        /// <summary>Parses a manifest, reporting failure instead of throwing.</summary>
        /// <param name="json">The whole text of <c>manifest.json</c>.</param>
        /// <param name="manifest">The parsed manifest, or null on failure.</param>
        /// <param name="error">Why it failed, or null on success.</param>
        /// <returns>True when the manifest is valid.</returns>
        public static bool TryParse(string? json, out PackManifest? manifest, out string? error)
        {
            try
            {
                manifest = Parse(json);
                error = null;
                return true;
            }
            catch (PackManifestException ex)
            {
                manifest = null;
                error = ex.Message;
                return false;
            }
        }

        private static void RejectUnknownMembers(JsonObject root)
        {
            foreach (var member in root)
            {
                if (member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var known = false;
                foreach (var key in KnownKeys)
                {
                    if (string.Equals(member.Key, key, StringComparison.Ordinal))
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                {
                    throw new PackManifestException(
                        "unknown member \"" + member.Key + "\"; expected one of " +
                        string.Join(", ", KnownKeys) + " (prefix your own fields with \"" +
                        ExtensionPrefix + "\" to have them ignored)");
                }
            }
        }

        private static string RequiredString(JsonObject root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value is null)
            {
                throw new PackManifestException("\"" + key + "\" is required");
            }

            if (!(value is string text))
            {
                throw new PackManifestException(
                    "\"" + key + "\" must be a string, not " + JsonReader.TypeName(value));
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new PackManifestException("\"" + key + "\" must not be blank");
            }

            return text.Trim();
        }

        private static string? OptionalString(JsonObject root, string key)
        {
            if (!root.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            if (!(value is string text))
            {
                throw new PackManifestException(
                    "\"" + key + "\" must be a string, not " + JsonReader.TypeName(value));
            }

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static SemVer RequiredVersion(JsonObject root, string key)
        {
            var text = RequiredString(root, key);
            if (!SemVer.TryParse(text, out var version, out var error))
            {
                throw new PackManifestException("\"" + key + "\": " + error + " (expected SemVer, e.g. 1.0.0)");
            }

            return version!;
        }

        private static SemVer? OptionalVersion(JsonObject root, string key)
        {
            var text = OptionalString(root, key);
            if (text is null)
            {
                return null;
            }

            if (!SemVer.TryParse(text, out var version, out var error))
            {
                throw new PackManifestException("\"" + key + "\": " + error + " (expected SemVer, e.g. 1.0.0)");
            }

            return version;
        }

        private static IReadOnlyList<PackDependency> ParseDependencies(JsonObject root, string ownId)
        {
            if (!root.TryGetValue(KeyDependsOn, out var value) || value is null)
            {
                return new PackDependency[0];
            }

            if (!(value is IList<object?> entries))
            {
                throw new PackManifestException(
                    "\"" + KeyDependsOn + "\" must be an array, not " + JsonReader.TypeName(value));
            }

            var dependencies = new List<PackDependency>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var where = KeyDependsOn + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                var dependency = ParseDependency(entries[i], where);

                if (string.Equals(dependency.Id, ownId, StringComparison.Ordinal))
                {
                    throw new PackManifestException("\"" + where + "\": a pack cannot depend on itself");
                }

                foreach (var existing in dependencies)
                {
                    if (string.Equals(existing.Id, dependency.Id, StringComparison.Ordinal))
                    {
                        throw new PackManifestException(
                            "\"" + where + "\": \"" + dependency.Id + "\" is listed twice");
                    }
                }

                dependencies.Add(dependency);
            }

            return dependencies;
        }

        private static PackDependency ParseDependency(object? entry, string where, bool allowOptional = true)
        {
            if (entry is string shorthand)
            {
                if (!PackManifest.IsValidId(shorthand))
                {
                    throw new PackManifestException(
                        "\"" + where + "\": \"" + shorthand + "\" must match " + PackManifest.IdPattern);
                }

                return new PackDependency(shorthand);
            }

            if (!(entry is JsonObject obj))
            {
                throw new PackManifestException(
                    "\"" + where + "\" must be an object or a pack id string, not " + JsonReader.TypeName(entry));
            }

            // Read as raw values so the error messages can name the full path (dependsOn[0].id)
            // instead of the bare member name.
            obj.TryGetValue(KeyId, out var rawId);
            var id = rawId as string;
            if (!PackManifest.IsValidId(id))
            {
                throw new PackManifestException(
                    "\"" + where + ".id\": " +
                    (rawId is null || rawId is string
                        ? "\"" + (id ?? string.Empty) + "\" must match " + PackManifest.IdPattern
                        : "must be a string, not " + JsonReader.TypeName(rawId)));
            }

            obj.TryGetValue(KeyMinVersion, out var rawMinVersion);
            SemVer? minVersion = null;
            if (rawMinVersion != null)
            {
                if (!(rawMinVersion is string minVersionText))
                {
                    throw new PackManifestException(
                        "\"" + where + ".minVersion\" must be a string, not " + JsonReader.TypeName(rawMinVersion));
                }

                if (!SemVer.TryParse(minVersionText, out minVersion, out var error))
                {
                    throw new PackManifestException("\"" + where + ".minVersion\": " + error);
                }
            }

            var optional = false;
            if (allowOptional && obj.TryGetValue(KeyOptional, out var rawOptional) && rawOptional != null)
            {
                if (!(rawOptional is bool flag))
                {
                    throw new PackManifestException(
                        "\"" + where + "." + KeyOptional + "\" must be true or false, not " + JsonReader.TypeName(rawOptional));
                }

                optional = flag;
            }

            foreach (var member in obj)
            {
                if (!string.Equals(member.Key, KeyId, StringComparison.Ordinal) &&
                    !string.Equals(member.Key, KeyMinVersion, StringComparison.Ordinal) &&
                    !(allowOptional && string.Equals(member.Key, KeyOptional, StringComparison.Ordinal)) &&
                    !member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                {
                    throw new PackManifestException(
                        "\"" + where + "\": unknown member \"" + member.Key + "\"; expected \"" + KeyId +
                        "\" and optionally \"" + KeyMinVersion + "\"" +
                        (allowOptional ? " and \"" + KeyOptional + "\"" : string.Empty));
                }
            }

            return new PackDependency(id!, minVersion, optional);
        }
        private static string? ParseEntryDll(JsonObject root)
        {
            // Read raw rather than through OptionalString: a blank entryDll is a mistake to report,
            // not an absent member that would quietly turn the code mod into a content pack.
            if (!root.TryGetValue(KeyEntryDll, out var value) || value is null)
            {
                return null;
            }

            if (!(value is string raw))
            {
                throw new PackManifestException(
                    "\"" + KeyEntryDll + "\" must be a string, not " + JsonReader.TypeName(value));
            }

            if (!EntryDllPath.TryNormalise(raw, out var entryDll, out var problem))
            {
                throw new PackManifestException("\"" + KeyEntryDll + "\": " + problem);
            }

            return entryDll;
        }

        private static PackDependency? ParseContentPackFor(
            JsonObject root,
            string ownId,
            string? entryDll,
            IReadOnlyList<PackDependency> dependencies)
        {
            if (!root.TryGetValue(KeyContentPackFor, out var value) || value is null)
            {
                return null;
            }

            var target = ParseDependency(value, KeyContentPackFor, allowOptional: false);

            if (entryDll != null)
            {
                throw new PackManifestException(
                    "\"" + KeyContentPackFor + "\" and \"" + KeyEntryDll + "\" cannot both be set: a manifest is " +
                    "either a code mod or a content pack for one");
            }

            if (string.Equals(target.Id, ownId, StringComparison.Ordinal))
            {
                throw new PackManifestException("\"" + KeyContentPackFor + "\": a pack cannot be a content pack for itself");
            }

            foreach (var dependency in dependencies)
            {
                // An optional entry for the same mod is harmless: contentPackFor makes it required
                // and the stricter minVersion wins (PackManifest merges the two). A required one is
                // the same dependency written twice.
                if (string.Equals(dependency.Id, target.Id, StringComparison.Ordinal) && !dependency.IsOptional)
                {
                    throw new PackManifestException(
                        "\"" + KeyContentPackFor + "\" already makes \"" + target.Id + "\" a required dependency; " +
                        "remove it from \"" + KeyDependsOn + "\"");
                }
            }

            return target;
        }

        private static IReadOnlyDictionary<string, object?>? CollectExtensions(JsonObject root)
        {
            Dictionary<string, object?>? extensions = null;
            foreach (var member in root)
            {
                if (member.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                {
                    extensions ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                    extensions[member.Key] = member.Value;
                }
            }

            return extensions;
        }
    }
}
