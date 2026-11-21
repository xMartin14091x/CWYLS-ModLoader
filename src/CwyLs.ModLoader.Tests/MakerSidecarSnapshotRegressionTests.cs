using System;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerSidecarSnapshotRegressionTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Sidecar_targets_keep_the_absent_snapshot_when_an_external_editor_creates_the_file(bool voiceDefault)
        {
            var root = Path.Combine(Path.GetTempPath(), "cwy-sidecar-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new MakerProjectStore(root);
                var path = voiceDefault ? MakerStudioPaths.VoiceDefault : MakerStudioPaths.Clip("th", "clip");
                if (voiceDefault) Assert.True(new MakerVoiceDefaultStore(store).Read().Ok);
                else Assert.False(new MakerClipStore(store).Read("th", "clip").RecordPresent);
                store.Files.WriteAtomic(path, Encoding.UTF8.GetBytes("external creator bytes"));
                var target = store.Target(path, Encoding.UTF8.GetBytes("replacement based on the earlier absent snapshot"));
                Assert.Null(target.BeforeFingerprint);
                // Milestone-1 P3 T8: the read-back alone could not fail. Commit the stale target: it must be refused and the
                // external bytes kept (a target built from the current file would replace them).
                var commit = store.Commit(new string('e', 32), new[] { target }, voiceDefault ? "voice.default" : "publish");
                Assert.False(commit.Ok);
                Assert.Equal("external creator bytes", store.Files.ReadText(path, out _));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
