using System;
using System.IO;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>What the editor knows about one pack when deciding whether Pack Maker may write to it.</summary>
    public sealed class MakerPackFacts
    {
        /// <summary>The pack id asked for.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>True when the pack loaded this session.</summary>
        public bool Loaded { get; set; }

        /// <summary>Why the pack did not load (the scan's skip record), or null.</summary>
        public string? SkipReason { get; set; }

        /// <summary>True when the manifest declares an <c>entryDll</c>.</summary>
        public bool IsCodeMod { get; set; }

        /// <summary>Absolute path of the pack folder, or null when it did not load.</summary>
        public string? RootPath { get; set; }

        /// <summary>Absolute path of <c>&lt;game root&gt;/CWYLS Mods</c>.</summary>
        public string PacksRoot { get; set; } = string.Empty;

        /// <summary>Null when the folder accepted a test write; otherwise why it did not.</summary>
        public string? WriteProblem { get; set; }
    }

    /// <summary>The editor's decision about one pack.</summary>
    public sealed class MakerPackVerdict
    {
        private MakerPackVerdict(bool known, bool editable, string? reason)
        {
            Known = known;
            Editable = editable;
            Reason = reason;
        }

        /// <summary>False when no pack of that id loaded or was refused this session (<c>not_found</c>).</summary>
        public bool Known { get; }

        /// <summary>True when Pack Maker may write to the pack.</summary>
        public bool Editable { get; }

        /// <summary>Why it is read-only (or unknown), in one sentence; null when editable.</summary>
        public string? Reason { get; }

        /// <summary>A pack Pack Maker may write to.</summary>
        public static MakerPackVerdict Writable() => new MakerPackVerdict(true, true, null);

        /// <summary>A pack Pack Maker must not write to.</summary>
        /// <param name="reason">Why, for the status bar.</param>
        public static MakerPackVerdict ReadOnly(string reason) => new MakerPackVerdict(true, false, reason);

        /// <summary>No such pack.</summary>
        /// <param name="reason">What was looked for.</param>
        public static MakerPackVerdict Unknown(string reason) => new MakerPackVerdict(false, false, reason);
    }

    /// <summary>
    /// Decides whether Pack Maker may write to a pack (CONTRACT.md "Editable packs": DS-03 decides,
    /// DS-02 reports). Editable = a content pack (no <c>entryDll</c>) directly in <c>CWYLS Mods</c>,
    /// loaded this session, whose folder is writable, and that is not one of the loader's reference
    /// packs. Pure: the adapter gathers the facts.
    /// </summary>
    public static class MakerPackAccess
    {
        /// <summary>Applies the rules in a fixed order, so the reason given is always the most basic one.</summary>
        /// <param name="facts">What is known about the pack.</param>
        public static MakerPackVerdict Judge(MakerPackFacts facts)
        {
            if (facts is null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            if (!facts.Loaded)
            {
                return facts.SkipReason is null
                    ? MakerPackVerdict.Unknown("no pack '" + facts.Id + "' is installed")
                    : MakerPackVerdict.ReadOnly("it did not load this session: " + facts.SkipReason);
            }

            if (facts.IsCodeMod)
            {
                return MakerPackVerdict.ReadOnly("it is a code mod (entryDll); Pack Maker writes to content packs only");
            }

            if (MakerEditRules.IsReferencePack(facts.Id))
            {
                return MakerPackVerdict.ReadOnly(
                    "it is a reference pack that ships with the loader and deploy-packs.sh replaces it; copy it to a pack id of your own");
            }

            if (!IsDirectChild(facts.RootPath, facts.PacksRoot))
            {
                return MakerPackVerdict.ReadOnly("its folder is not in CWYLS Mods");
            }

            if (facts.WriteProblem != null)
            {
                return MakerPackVerdict.ReadOnly("its folder is not writable: " + facts.WriteProblem);
            }

            return MakerPackVerdict.Writable();
        }

        /// <summary>True when <paramref name="folder"/> sits directly in <paramref name="parent"/>.</summary>
        /// <param name="folder">An absolute folder path.</param>
        /// <param name="parent">An absolute folder path.</param>
        public static bool IsDirectChild(string? folder, string? parent)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            try
            {
                var child = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var actualParent = Path.GetDirectoryName(child);
                return actualParent != null && string.Equals(actualParent, root, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }
    }
}
