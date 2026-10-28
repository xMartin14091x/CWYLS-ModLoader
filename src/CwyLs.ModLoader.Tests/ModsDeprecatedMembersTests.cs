using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using CwyLs.ModLoader.Core.Mods;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FIX (review #8, probe P15): <c>MethodBase.GetCurrentMethod()</c> inside a deprecated
    /// property, event or interface implementation is a method that does not carry the attribute;
    /// <see cref="DeprecatedMembers.Find"/> must find the one the author wrote.
    /// </summary>
    public class ModsDeprecatedMembersTests
    {
        [Fact]
        public void P15_a_property_getter_finds_the_property_attribute()
        {
            var info = DeprecatedMembers.Find(new Facet().OldPropGetter());

            Assert.NotNull(info);
            Assert.Equal("Facet.OldProp", info!.Name);
            Assert.Equal("0.9.1", info.Attribute.Since);
            Assert.IsAssignableFrom<PropertyInfo>(info.DeclaredOn);
        }

        [Fact]
        public void P15_an_interface_implementation_finds_the_interface_attribute()
        {
            var info = DeprecatedMembers.Find(((IFacet)new Facet()).OldCall());

            Assert.NotNull(info);
            Assert.Equal("IFacet.OldCall", info!.Name);
            Assert.Equal(DeprecationStage.Info, info.Attribute.Stage);
            Assert.Equal("IFacet.NewCall", info.Attribute.Replacement);
        }

        [Fact]
        public void An_interface_property_implemented_by_a_class_finds_the_interface_property()
        {
            var info = DeprecatedMembers.Find(((IFacet)new Facet()).OldLevelGetter());

            Assert.NotNull(info);
            Assert.Equal("IFacet.OldLevel", info!.Name);
        }

        [Fact]
        public void An_event_accessor_finds_the_event_attribute()
        {
            var facet = new Facet();
            facet.Changed += () => { };

            var info = DeprecatedMembers.Find(facet.LastAccessor);

            Assert.NotNull(info);
            Assert.Equal("Facet.Changed", info!.Name);
            Assert.Equal(DeprecationStage.PendingRemoval, info.Attribute.Stage);
        }

        [Fact]
        public void A_plain_method_keeps_working()
        {
            var info = DeprecatedMembers.Find(new Facet().OldMethod());

            Assert.Equal("Facet.OldMethod", info!.Name);
        }

        [Fact]
        public void A_member_of_a_deprecated_class_reports_the_member_under_the_class_attribute()
        {
            var info = DeprecatedMembers.Find(new OldHelper().Work());

            Assert.NotNull(info);
            Assert.Equal("OldHelper.Work", info!.Name);
            Assert.Equal("0.8.0", info.Attribute.Since);
            Assert.Equal(typeof(OldHelper), info.DeclaredOn);
        }

        [Fact]
        public void An_undeprecated_member_finds_nothing()
        {
            Assert.Null(DeprecatedMembers.Find(new Facet().Current()));
            Assert.Null(DeprecatedMembers.Find(null));
        }

        [Fact]
        public void The_answer_is_cached_and_stable()
        {
            var method = new Facet().OldPropGetter();

            Assert.Same(DeprecatedMembers.Find(method), DeprecatedMembers.Find(method));
        }

        [Fact]
        public void The_plugin_constant_and_the_version_text_agree()
        {
            Assert.Equal(CwyApi.PluginVersionText, CwyApi.Version.ToString());
            Assert.Equal(CwyApi.VersionText, CwyApi.Version.ToString());
        }

        /// <summary>The shape the review probe used, plus an interface property and an event.</summary>
        public interface IFacet
        {
            [CwyDeprecated("0.9.0", DeprecationStage.Info, Replacement = "IFacet.NewCall")]
            MethodBase OldCall();

            [CwyDeprecated("0.9.0", DeprecationStage.Notice)]
            int OldLevel { get; }

            MethodBase OldLevelGetter();
        }

        /// <summary>Each deprecated member hands back the method it runs in, as a member calling Notify would see it.</summary>
        public sealed class Facet : IFacet
        {
            private MethodBase? _lastAccessor;

            [CwyDeprecated("0.9.1", DeprecationStage.Info)]
            public MethodBase OldProp
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                get => MethodBase.GetCurrentMethod()!;
            }

            public int OldLevel
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                get
                {
                    _lastAccessor = MethodBase.GetCurrentMethod();
                    return 1;
                }
            }

            [CwyDeprecated("0.9.0", DeprecationStage.PendingRemoval)]
            public event Action Changed
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                add => _lastAccessor = MethodBase.GetCurrentMethod();
                [MethodImpl(MethodImplOptions.NoInlining)]
                remove => _lastAccessor = MethodBase.GetCurrentMethod();
            }

            public MethodBase? LastAccessor => _lastAccessor;

            public MethodBase OldPropGetter() => OldProp;

            public MethodBase OldLevelGetter()
            {
                _ = OldLevel;
                return _lastAccessor!;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public MethodBase OldCall() => MethodBase.GetCurrentMethod()!;

            [CwyDeprecated("0.9.0", DeprecationStage.Info)]
            [MethodImpl(MethodImplOptions.NoInlining)]
            public MethodBase OldMethod() => MethodBase.GetCurrentMethod()!;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public MethodBase Current() => MethodBase.GetCurrentMethod()!;
        }

        /// <summary>A class deprecated as a whole.</summary>
        [CwyDeprecated("0.8.0", DeprecationStage.Notice)]
        public sealed class OldHelper
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public MethodBase Work() => MethodBase.GetCurrentMethod()!;
        }
    }
}
