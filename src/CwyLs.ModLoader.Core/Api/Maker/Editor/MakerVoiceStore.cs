using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>Dub-only disk operations for Pack Maker.</summary>
    public static class MakerVoiceStore
    {
        public static string VoicePath(MakerPackFiles files, string code, string clip)
        {
            if (files is null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            var folder = code;
            var voiceRoot = Path.Combine(files.Root, "audio", "voice");
            if (Directory.Exists(voiceRoot))
            {
                foreach (var existing in Directory.GetDirectories(voiceRoot))
                {
                    var name = Path.GetFileName(existing);
                    if (string.Equals(name, code, StringComparison.OrdinalIgnoreCase))
                    {
                        folder = name;
                        break;
                    }
                }
            }

            return PackContentIndex.VoiceFolder + folder + "/" + clip + MakerEditRules.VoiceExtension;
        }

        public static MakerVoiceWrite Save(MakerPackFiles files, string code, string clip, byte[] wav)
        {
            if (wav is null)
            {
                throw new ArgumentNullException(nameof(wav));
            }

            var target = VoicePath(files, code, clip);
            var result = new MakerVoiceWrite { Path = target, Existed = files.Exists(target) };

            foreach (var other in files.VoiceFiles(code, clip))
            {
                if (!SamePath(other, target))
                {
                    result.MovedAside.Add(new KeyValuePair<string, string>(other, files.MoveAside(other)));
                }
            }

            if (result.Existed && !files.IsMakerWritten(target))
            {
                result.MovedAside.Add(new KeyValuePair<string, string>(target, files.MoveAside(target)));
            }

            files.WriteAtomic(target, wav);
            files.RecordWritten(target, wav);
            return result;
        }

        public static MakerVoiceWrite Remove(MakerPackFiles files, string code, string clip)
        {
            if (files is null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            var target = VoicePath(files, code, clip);
            var result = new MakerVoiceWrite { Path = target, Existed = files.Exists(target) };

            if (result.Existed)
            {
                if (files.IsMakerWritten(target))
                {
                    files.Delete(target);
                    result.Deleted = true;
                }
                else
                {
                    result.MovedAside.Add(new KeyValuePair<string, string>(target, files.MoveAside(target)));
                }
            }

            files.ForgetWritten(target);

            foreach (var original in files.VoiceBackups(code, clip))
            {
                var justMoved = false;
                foreach (var moved in result.MovedAside)
                {
                    justMoved |= SamePath(moved.Key, original);
                }

                if (!justMoved && files.TryRestore(original))
                {
                    result.Restored.Add(original);
                }
            }

            return result;
        }

        private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
