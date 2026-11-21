using System;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>Subtitle-only disk operations for Pack Maker.</summary>
    public static class MakerSubtitleStore
    {
        public static MakerSubtitleWrite Save(MakerPackFiles files, string code, string id, string? text)
        {
            if (files is null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            var path = MakerEditRules.SubtitlePath(code);
            var result = new MakerSubtitleWrite { Path = path };
            var original = files.ReadBytes(path);
            var existing = files.ReadText(path, out var bom);

            var edit = MakerSubtitleFile.Apply(existing, id, text);
            if (!edit.Ok)
            {
                result.Problem = path + " cannot be edited: " + edit.Error;
                return result;
            }

            result.Ok = true;
            result.Previous = edit.Previous;
            result.Current = text;
            if (!edit.Changed || edit.Text is null)
            {
                result.Current = edit.Previous;
                return result;
            }

            var copy = MakerEditRules.BackupPathOf(path);
            if (original != null && !files.Exists(copy))
            {
                files.WriteAtomic(copy, original);
                result.SafetyCopy = copy;
            }

            files.WriteTextAtomic(path, edit.Text, bom);
            result.Written = true;
            return result;
        }
    }
}
