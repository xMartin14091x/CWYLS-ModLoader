using System.Collections.Generic;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    [Collection("subtitle-sources")]
    public class SubtitlesSourcesTests
    {
        private sealed class Table : ISubtitleSource
        {
            private readonly Dictionary<string, string> _rows = new Dictionary<string, string>();

            internal Table Add(string id, string lang, string text)
            {
                _rows[id + "/" + lang] = text;
                return this;
            }

            public bool TryGet(string localizationId, string lang, out string text)
            {
                if (_rows.TryGetValue(localizationId + "/" + lang, out var found))
                {
                    text = found;
                    return true;
                }

                text = string.Empty;
                return false;
            }
        }

        private sealed class Broken : ISubtitleSource
        {
            public bool TryGet(string localizationId, string lang, out string text) =>
                throw new KeyNotFoundException("this source is broken");
        }

        [Fact]
        public void The_first_registered_source_that_has_the_line_wins()
        {
            SubtitleSources.Clear();
            try
            {
                SubtitleSources.Register(new Table().Add("A", "th", "first"), "first");
                SubtitleSources.Register(new Table().Add("A", "th", "second"), "second");

                Assert.True(SubtitleSources.TryGet("A", "th", out var text));
                Assert.Equal("first", text);
                Assert.Equal(2, SubtitleSources.Count);
                Assert.Equal(new[] { "first", "second" }, SubtitleSources.Names);
            }
            finally
            {
                SubtitleSources.Clear();
            }
        }

        [Fact]
        public void A_source_that_throws_is_reported_and_skipped()
        {
            SubtitleSources.Clear();
            var reported = new List<string>();
            SubtitleSources.ErrorSink = reported.Add;
            try
            {
                SubtitleSources.Register(new Broken(), "broken");
                SubtitleSources.Register(new Table().Add("A", "th", "kept going"), "good");

                Assert.True(SubtitleSources.TryGet("A", "th", out var text));
                Assert.Equal("kept going", text);
                Assert.Single(reported);
                Assert.Contains("broken", reported[0]);
            }
            finally
            {
                SubtitleSources.ErrorSink = null;
                SubtitleSources.Clear();
            }
        }

        [Fact]
        public void Disposing_the_token_unregisters_the_source()
        {
            SubtitleSources.Clear();
            try
            {
                var token = SubtitleSources.Register(new Table().Add("A", "th", "x"), "temporary");
                Assert.Equal(1, SubtitleSources.Count);

                token.Dispose();

                Assert.Equal(0, SubtitleSources.Count);
                Assert.False(SubtitleSources.TryGet("A", "th", out _));
                token.Dispose();
            }
            finally
            {
                SubtitleSources.Clear();
            }
        }

        [Fact]
        public void An_empty_id_or_language_is_never_looked_up()
        {
            SubtitleSources.Clear();
            try
            {
                SubtitleSources.Register(new Table().Add("A", "th", "x"), "table");

                Assert.False(SubtitleSources.TryGet(null, "th", out var byId));
                Assert.Equal(string.Empty, byId);
                Assert.False(SubtitleSources.TryGet("A", string.Empty, out var byLang));
                Assert.Equal(string.Empty, byLang);
            }
            finally
            {
                SubtitleSources.Clear();
            }
        }
    }
}
