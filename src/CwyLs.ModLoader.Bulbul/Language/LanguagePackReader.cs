using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Packs;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// Reads every loaded pack's <c>language.json</c> - and the mark table each declaration names -
    /// off disk into a <see cref="LanguageRegistry"/>. The only part of the language feature that
    /// touches a filesystem, which keeps every rule it applies unit-testable in Core.
    /// </summary>
    internal static class LanguagePackReader
    {
        /// <summary>
        /// Builds the registry from <see cref="PackManager.Loaded"/>, in its dependency order, so
        /// the first pack gets value 100.
        /// </summary>
        /// <param name="marks">Receives each registered language's parsed mark table, by code.</param>
        /// <returns>The registry; refused files and declarations are in <see cref="LanguageRegistry.Rejected"/>.</returns>
        internal static LanguageRegistry ReadAll(out Dictionary<string, MarkTable> marks)
        {
            var registry = new LanguageRegistry();
            marks = new Dictionary<string, MarkTable>(StringComparer.Ordinal);

            foreach (var pack in PackManager.Loaded)
            {
                var file = pack.Index.Language;
                if (file is null)
                {
                    continue;
                }

                var where = pack.Id + "/" + file.Path;
                var json = ReadText(pack.FullPath(file), where, out var readError);
                if (json is null)
                {
                    registry.Reject(where + ": " + readError);
                    continue;
                }

                if (!LanguageDeclarationParser.TryParse(json, pack.Id, out var declarations, out var parseError, where))
                {
                    registry.Reject(parseError ?? where + ": not a valid language declaration");
                    continue;
                }

                foreach (var declaration in declarations)
                {
                    if (!registry.Register(declaration, out var reason))
                    {
                        continue;
                    }

                    if (declaration.MarksPath is null)
                    {
                        continue;
                    }

                    var marksWhere = pack.Id + "/" + declaration.MarksPath;
                    var marksJson = ReadText(pack.FullPath(declaration.MarksPath), marksWhere, out var marksError);
                    if (marksJson is null)
                    {
                        registry.Reject(declaration + ": mark table " + marksWhere + " " + marksError + "; the language loads without it");
                        continue;
                    }

                    if (MarkTable.TryParse(marksJson, marksWhere, out var table, out var tableError))
                    {
                        marks[declaration.Code] = table!;
                    }
                    else
                    {
                        registry.Reject(declaration + ": " + tableError + "; the language loads without it");
                    }
                }
            }

            // Values and chains are computed on first use; do it now, on the thread that built the
            // registry, so the Harmony patches only ever read a finished list.
            _ = registry.Languages;
            return registry;
        }

        /// <summary>Reads one pack file as UTF-8, or returns null with a reason. Every failure is reported.</summary>
        private static string? ReadText(string absolute, string where, out string error)
        {
            error = string.Empty;
            try
            {
                if (!File.Exists(absolute))
                {
                    error = "is not in the pack";
                    return null;
                }

                return File.ReadAllText(absolute);
            }
            catch (Exception ex) when (ex is IOException
                                       || ex is UnauthorizedAccessException
                                       || ex is ArgumentException
                                       || ex is NotSupportedException)
            {
                error = "could not be read: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError("LANGUAGE " + where + " " + error);
                return null;
            }
        }
    }
}
