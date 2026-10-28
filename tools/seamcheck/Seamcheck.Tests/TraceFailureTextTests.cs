using System;
using CwyLs.ModLoader.Trace;
using Xunit;

namespace CWY.Seamcheck.Tests
{
    /// <summary>
    /// Trace failure lines carry the cause chain (src/CwyLs.ModLoader.Trace/TraceFailureText.cs), so a
    /// HarmonyX "IL Compile Error (unknown location)" says what actually failed.
    /// </summary>
    public class TraceFailureTextTests
    {
        [Fact]
        public void The_ScreenSystem_failure_shape_names_the_type_initializer_and_its_cause()
        {
            var nre = new NullReferenceException("Object reference not set to an instance of an object");
            var init = new TypeInitializationException("NestopiSystem.ScreenSystem", nre);
            var harmony = new Exception("IL Compile Error (unknown location)", init);

            Assert.Equal(
                "IL Compile Error (unknown location)"
                + " <- TypeInitializationException: The type initializer for 'NestopiSystem.ScreenSystem' threw an exception."
                + " <- NullReferenceException: Object reference not set to an instance of an object",
                TraceFailureText.Chain(harmony));
        }

        [Fact]
        public void A_plain_exception_is_its_message()
        {
            Assert.Equal("boom", TraceFailureText.Chain(new InvalidOperationException("boom")));
        }

        [Fact]
        public void Messages_are_kept_on_one_line()
        {
            Assert.Equal("a b <- Exception: c d", TraceFailureText.Chain(new Exception("a\nb", new Exception("c\r\nd".Replace("\r\n", "\n")))));
        }

        [Fact]
        public void A_deep_chain_is_cut()
        {
            Exception error = new Exception("0");
            for (var i = 1; i <= TraceFailureText.MaxDepth + 2; i++)
            {
                error = new Exception(i.ToString(), error);
            }

            var text = TraceFailureText.Chain(error);

            Assert.EndsWith(" <- ...", text);
            Assert.Equal(TraceFailureText.MaxDepth + 1, text.Split(" <- ").Length - 1);
        }

        [Fact]
        public void Null_is_named()
        {
            Assert.Equal("(no exception)", TraceFailureText.Chain(null));
        }
    }
}
