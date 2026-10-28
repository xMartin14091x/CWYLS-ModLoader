using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Core.Actions;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// E-ACT-CORE: <c>[ActionFamily]</c> discovery - adding a family is adding a file. The families
    /// below live in this test assembly; each test discovers them into its own registry.
    /// </summary>
    public class ActionsFamilyTests
    {
        private static readonly Assembly Here = typeof(ActionsFamilyTests).Assembly;

        private static (ActionRegistry Registry, ActionFamilyCatalog Catalog, Dictionary<string, ActionFamilyOutcome> ByFamily) Discover()
        {
            var registry = new ActionRegistry();
            var catalog = new ActionFamilyCatalog(registry);
            var byFamily = new Dictionary<string, ActionFamilyOutcome>();
            foreach (var outcome in catalog.Discover(new[] { Here, null }))
            {
                byFamily[outcome.Family] = outcome;
            }

            return (registry, catalog, byFamily);
        }

        [Fact]
        public void A_family_registers_its_actions_as_framework_actions()
        {
            var (registry, _, byFamily) = Discover();

            var good = byFamily["testfam"];
            Assert.Equal(ActionFamilyState.Registered, good.State);
            Assert.Equal(new[] { "testfam.ping", "testfam.echo" }, good.Actions);
            Assert.Equal(typeof(GoodFamily).FullName, good.TypeName);
            Assert.True(registry.TryGet("testfam.ping", out var binding));
            Assert.Equal(ActionRegistry.FrameworkOwner, binding!.Owner);
            Assert.Equal("pong", binding.Handler(new ActionInvocation(binding.Descriptor, new ActionRequest("testfam.ping", null, ActionCaller.Console, "t"), false)).Message);
        }

        [Fact]
        public void An_action_outside_the_family_is_refused_and_reported()
        {
            var (registry, _, byFamily) = Discover();

            var good = byFamily["testfam"];
            Assert.Single(good.Problems);
            Assert.Contains("other.thing: not in the family testfam", good.Problems[0]);
            Assert.False(registry.TryGet("other.thing", out _));
            Assert.EndsWith("[1 refused]", good.ToString());
        }

        [Fact]
        public void A_family_can_skip_itself_and_takes_back_what_it_added()
        {
            var (registry, _, byFamily) = Discover();

            var skipped = byFamily["testskip"];
            Assert.Equal(ActionFamilyState.Skipped, skipped.State);
            Assert.Equal("seam FacilityMusic.PlayMusic missing", skipped.Detail);
            Assert.Empty(skipped.Actions);
            Assert.False(registry.TryGet("testskip.a", out _));
            Assert.Contains("testskip.b: the family was skipped", skipped.Problems[0]);
        }

        [Fact]
        public void A_family_that_throws_fails_and_registers_nothing()
        {
            var (registry, _, byFamily) = Discover();

            var thrown = byFamily["testthrow"];
            Assert.Equal(ActionFamilyState.Failed, thrown.State);
            Assert.Contains("Register threw InvalidOperationException: broken family", thrown.Detail);
            Assert.False(registry.TryGet("testthrow.a", out _));
        }

        [Fact]
        public void Unusable_classes_fail_with_a_reason()
        {
            var (_, _, byFamily) = Discover();

            Assert.Contains("is not a family name", byFamily["Bad Name"].Detail);
            Assert.Contains("implement IActionFamily", byFamily["testnotfamily"].Detail);
            Assert.Contains("implement IActionFamily", byFamily["testabstract"].Detail);
            Assert.Equal("could not be created: NotSupportedException: no game here", byFamily["testctor"].Detail);
            Assert.Equal(ActionFamilyState.Registered, byFamily["testprivate"].State);
        }

        [Fact]
        public void Discovery_is_ordered_counted_and_never_registers_a_family_twice()
        {
            var registry = new ActionRegistry();
            var catalog = new ActionFamilyCatalog(registry);

            var first = catalog.Discover(new[] { Here });
            var count = registry.Count;
            var second = catalog.Discover(new[] { Here });

            Assert.Empty(second);
            Assert.Equal(count, registry.Count);
            Assert.Equal(first.Count, catalog.Outcomes.Count);
            for (var i = 1; i < first.Count; i++)
            {
                Assert.True(string.CompareOrdinal(first[i - 1].Family, first[i].Family) <= 0);
            }

            Assert.Equal("8 families: 2 registered (3 actions), 1 skipped, 5 failed", catalog.Summary());
            Assert.Empty(catalog.AssemblyProblems);
            Assert.Empty(catalog.Discover(null!));
        }

        [Theory]
        [InlineData("music", true)]
        [InlineData("cwy.camera", true)]
        [InlineData("todo-2", true)]
        [InlineData("Music", false)]
        [InlineData("music.", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Family_names_are_action_name_prefixes(string? name, bool valid) =>
            Assert.Equal(valid, ActionFamilyCatalog.IsValidFamily(name));

        [Fact]
        public void The_context_checks_its_arguments()
        {
            var registry = new ActionRegistry();
            var catalog = new ActionFamilyCatalog(registry);
            Assert.Throws<ArgumentNullException>(() => new ActionFamilyCatalog(null!));
            Assert.Empty(catalog.Outcomes);
        }
    }

    [ActionFamily("testfam")]
    internal sealed class GoodFamily : IActionFamily
    {
        public void Register(ActionFamilyContext context)
        {
            Assert.Equal("testfam", context.Family);
            context.Add(new ActionDescriptor("testfam.ping", ActionTier.Read, "Answers pong."), _ => ActionResult.Ok("pong"));
            context.Add(new ActionDescriptor("testfam.echo", ActionTier.Act, "Echoes."), _ => ActionResult.Ok("echo"));
            context.Add(new ActionDescriptor("other.thing", ActionTier.Act, "Not ours."), _ => ActionResult.Ok("x"));
        }
    }

    [ActionFamily("testskip")]
    internal sealed class SkippingFamily : IActionFamily
    {
        public void Register(ActionFamilyContext context)
        {
            context.Add(new ActionDescriptor("testskip.a", ActionTier.Act, "A."), _ => ActionResult.Ok("a"));
            context.Skip("seam FacilityMusic.PlayMusic missing");
            Assert.False(context.Add(new ActionDescriptor("testskip.b", ActionTier.Act, "B."), _ => ActionResult.Ok("b")));
        }
    }

    [ActionFamily("testthrow")]
    internal sealed class ThrowingFamily : IActionFamily
    {
        public void Register(ActionFamilyContext context)
        {
            context.Add(new ActionDescriptor("testthrow.a", ActionTier.Act, "A."), _ => ActionResult.Ok("a"));
            throw new InvalidOperationException("broken family");
        }
    }

    [ActionFamily("Bad Name")]
    internal sealed class BadNameFamily : IActionFamily
    {
        public void Register(ActionFamilyContext context) => throw new InvalidOperationException("never called");
    }

    [ActionFamily("testnotfamily")]
    internal sealed class NotAFamily
    {
    }

    [ActionFamily("testabstract")]
    internal abstract class AbstractFamily : IActionFamily
    {
        public abstract void Register(ActionFamilyContext context);
    }

    [ActionFamily("testctor")]
    internal sealed class ThrowingConstructorFamily : IActionFamily
    {
        public ThrowingConstructorFamily() => throw new NotSupportedException("no game here");

        public void Register(ActionFamilyContext context)
        {
        }
    }

    [ActionFamily("testprivate")]
    internal sealed class PrivateConstructorFamily : IActionFamily
    {
        private PrivateConstructorFamily()
        {
        }

        public void Register(ActionFamilyContext context) =>
            context.Add(new ActionDescriptor("testprivate.go", ActionTier.Act, "Goes."), _ => ActionResult.Ok("gone"));
    }
}
