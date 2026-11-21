using System;
using System.IO;
using CwyLs.ModLoader.Core.Api.Maker.Studio;

namespace CwyLs.ModLoader.Tests
{
    internal sealed class MakerProjectFixture : IDisposable
    {
        internal MakerProjectFixture(long quota = 2L * 1024 * 1024 * 1024)
        {
            Root = Path.Combine(Path.GetTempPath(), "cwy-maker-project-" + Guid.NewGuid().ToString("N"), "sample.pack");
            Directory.CreateDirectory(Root);
            Clock = new ManualApiClock();
            Store = new MakerProjectStore(Root, Clock, quota);
        }

        internal string Root { get; }
        internal ManualApiClock Clock { get; }
        internal MakerProjectStore Store { get; }

        internal string Operation(int n) => n.ToString("x32");

        internal void Write(string relative, string text)
        {
            var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        internal string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(Root)!, true); } catch (IOException) { }
        }
    }
}
