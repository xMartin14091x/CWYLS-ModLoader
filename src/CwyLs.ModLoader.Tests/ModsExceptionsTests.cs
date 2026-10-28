using System;
using System.IO;
using System.Reflection;
using CwyLs.ModLoader.Core.Mods;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FIX: the mod's own exception behind the runtime's wrappers (review #12), and the cause
    /// named in the "type(s) could not load" warning (review #18).
    /// </summary>
    public class ModsExceptionsTests
    {
        [Fact]
        public void A_throwing_static_constructor_is_unwrapped_to_the_mods_exception()
        {
            var original = new InvalidOperationException("static setup failed");
            var wrapped = new TargetInvocationException(new TypeInitializationException("My.ModEntry", original));

            Assert.Same(original, ModExceptions.Unwrap(wrapped));
        }

        [Fact]
        public void Nested_invocation_wrappers_are_peeled_and_plain_errors_stay()
        {
            var original = new FormatException("x");

            Assert.Same(original, ModExceptions.Unwrap(new TargetInvocationException(new TargetInvocationException(original))));
            Assert.Same(original, ModExceptions.Unwrap(original));
            var empty = new TypeInitializationException("T", null);
            Assert.Same(empty, ModExceptions.Unwrap(empty));
            Assert.Throws<ArgumentNullException>(() => ModExceptions.Unwrap(null!));
        }

        [Fact]
        public void Missing_assemblies_are_read_from_file_names_and_messages()
        {
            var names = ModExceptions.MissingAssemblies(new Exception?[]
            {
                new FileNotFoundException("Could not load file or assembly", "Other.Mod, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"),
                null,
                new TypeLoadException("Could not resolve type with token 01000011 from typeref (expected class 'Other.Mod.IApi' in assembly 'other.mod, Version=1.0.0.0')"),
                new TypeLoadException("Could not load type 'X' from assembly 'Helper.Lib, Version=2.0.0.0'."),
                new FileLoadException("load failed", "Signed.Lib, Version=1.0.0.0"),
                new InvalidOperationException("no assembly here"),
            });

            Assert.Equal(new[] { "Other.Mod", "Helper.Lib", "Signed.Lib" }, names);
            Assert.Empty(ModExceptions.MissingAssemblies(null));
        }

        [Fact]
        public void An_absent_optional_dependency_is_named_instead_of_blaming_the_framework_version()
        {
            var hint = ModExceptions.TypeLoadHint(new[] { "Other.Mod" }, new[] { "other.mod" });

            Assert.Contains("the optional dependency other.mod is not running", hint);
            Assert.Contains("out of the entry class", hint);
            Assert.DoesNotContain("CWYLS Mod Loader version", hint);
        }

        [Fact]
        public void A_framework_assembly_mismatch_still_says_so()
        {
            var hint = ModExceptions.TypeLoadHint(new[] { "CwyLs.ModLoader" }, new[] { "other.mod" });

            Assert.Contains("another CWY:LS Mod Loader version", hint);
        }

        [Fact]
        public void A_missing_private_dll_says_where_it_should_be()
        {
            var hint = ModExceptions.TypeLoadHint(new[] { "Helper.Lib" }, null);

            Assert.Contains("Helper.Lib", hint);
            Assert.Contains("ship it next to the entry DLL", hint);
        }

        [Fact]
        public void With_nothing_to_go_on_the_hint_stays_a_question()
        {
            Assert.Equal("was it built against another CWY:LS Mod Loader version?", ModExceptions.TypeLoadHint(null, null));
            Assert.Contains("optional dependency (a.mod)", ModExceptions.TypeLoadHint(null, new[] { "a.mod" }));
        }
    }
}
