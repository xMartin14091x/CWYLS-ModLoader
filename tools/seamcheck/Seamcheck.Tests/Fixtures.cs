using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core;

// Types the tests read back out of this assembly's own metadata. Nothing here is meant to run,
// except the tables TableLoader evaluates.
#pragma warning disable CS0169, CS0414, CS0649, IDE0051, IDE0060

namespace CWY.Seamcheck.Tests.Fixtures
{
    /// <summary>A class with one of every member shape the patch-target tables use.</summary>
    public class Sample
    {
        private static int s_count;

        /// <summary>An instance field.</summary>
        public string Name = string.Empty;

        /// <summary>A nested enum, used as a parameter type.</summary>
        public enum Mode
        {
            /// <summary>First.</summary>
            A,

            /// <summary>Second.</summary>
            B,
        }

        /// <summary>A nested class, used by reference.</summary>
        public class Inner
        {
            /// <summary>A field.</summary>
            public int Value;
        }

        /// <summary>A read-write property.</summary>
        public int Count { get; set; }

        /// <summary>A static read-only property.</summary>
        public static bool Flag { get; } = true;

        /// <summary>Like HeroineService.PlayVoice.</summary>
        public void Play(string voice, bool mouth, bool story)
        {
        }

        /// <summary>Overload one.</summary>
        public void Overloaded(int value)
        {
        }

        /// <summary>Overload two.</summary>
        public void Overloaded(string value)
        {
        }

        /// <summary>Two generic overloads with the same parameter list: ambiguous by parameters.</summary>
        public void Ambiguous<T>(int value)
        {
        }

        /// <summary>See the other overload.</summary>
        public void Ambiguous<T, TOther>(int value)
        {
        }

        /// <summary>An awaitable return.</summary>
        public Task<int> Load() => Task.FromResult(0);

        /// <summary>A nullable parameter.</summary>
        public void SetWhen(DateTime? when)
        {
        }

        /// <summary>A by-ref parameter.</summary>
        public bool TryGet(ulong id, out Inner inner)
        {
            inner = new Inner();
            return true;
        }

        /// <summary>Like ProjectLifetimeScope.TryResolve.</summary>
        public static bool TryResolve<T>(out T value)
        {
            value = default!;
            return false;
        }

        /// <summary>A nested generic return.</summary>
        public Dictionary<string, List<int>> Map() => new Dictionary<string, List<int>>();

        private int Hidden(Mode mode) => (int)mode;
    }

    /// <summary>A generic definition, and through a closed name, a closed generic.</summary>
    /// <typeparam name="T">The argument.</typeparam>
    public class Holder<T>
    {
        /// <summary>A field of the type parameter.</summary>
        public T? Value;

        private T Get(string key) => default!;
    }

    /// <summary>A method with a switch, to exercise the decoder's variable-length operand.</summary>
    public static class Switches
    {
        /// <summary>Compiles to a <c>switch</c> instruction.</summary>
        /// <param name="value">Selector.</param>
        public static int Pick(int value)
        {
            switch (value)
            {
                case 0: return 10;
                case 1: return 11;
                case 2: return 12;
                case 3: return 13;
                default: return -1;
            }
        }
    }

    /// <summary>Methods whose trace shape is known: body, no body, pointer signature.</summary>
    public abstract unsafe class TraceShapes
    {
        /// <summary>Has IL.</summary>
        public void Normal()
        {
        }

        /// <summary>No IL.</summary>
        public abstract void Abstract();

        /// <summary>No IL: a P/Invoke.</summary>
        [DllImport("libc")]
        public static extern int getpid();

        /// <summary>Pointer parameter.</summary>
        public void Pointer(byte* data)
        {
        }

        /// <summary>By-ref to pointer.</summary>
        public void RefPointer(ref int* data)
        {
        }
    }

    /// <summary>Named like the game's VContainer scopes, so a static constructor calling it is a hazard.</summary>
    public static class FakeLifetimeScope
    {
        /// <summary>Like ProjectLifetimeScope.GameObject.</summary>
        public static object? GameObject => null;
    }

    /// <summary>A singleton shape.</summary>
    public sealed class FakeSingleton
    {
        /// <summary>Like SaveDataManager.Instance.</summary>
        public static FakeSingleton? Instance => null;
    }

    /// <summary>Precise-init static constructor that reaches a scope: the Windows ScreenSystem shape.</summary>
    public static class HazardousStatics
    {
        /// <summary>What the constructor captured.</summary>
        public static readonly object? Captured;

        static HazardousStatics()
        {
            Captured = FakeLifetimeScope.GameObject;
        }
    }

    /// <summary>Field-initializer-only static constructor (beforefieldinit) reaching a singleton.</summary>
    public static class SingletonStatics
    {
        /// <summary>What the initializer captured.</summary>
        public static readonly object? Captured = FakeSingleton.Instance;
    }

    /// <summary>A static constructor that reaches no game state: the macOS ScreenSystem shape.</summary>
    public static class HarmlessStatics
    {
        /// <summary>Built by the constructor.</summary>
        public static readonly List<int> Items;

        static HarmlessStatics()
        {
            Items = new List<int>();
        }
    }

    /// <summary>
    /// Every way a plugin could call <see cref="PatchTargetRegistry.Register"/>. Never executed:
    /// the scanner reads this IL, and the loader evaluates only the static tables.
    /// </summary>
    public static class RegistrationShapes
    {
        /// <summary>Owner literal of <see cref="ByProperty"/>.</summary>
        public const string PropertyOwner = "fixture (property)";

        /// <summary>A table read through a field.</summary>
        public static readonly IReadOnlyList<PatchTarget> FieldTable = new[]
        {
            new PatchTarget("fixture.field", "Seamcheck.Tests", "CWY.Seamcheck.Tests.Fixtures.Sample", MemberKind.Field, "Name"),
        };

        /// <summary>A table read through a property; shares an id with <see cref="FieldTable"/> on purpose.</summary>
        public static IReadOnlyList<PatchTarget> PropertyTable { get; } = new[]
        {
            new PatchTarget("fixture.property", "Seamcheck.Tests", "CWY.Seamcheck.Tests.Fixtures.Sample", MemberKind.Property, "Count"),
            new PatchTarget("fixture.field", "Seamcheck.Tests", "CWY.Seamcheck.Tests.Fixtures.Sample", MemberKind.Property, "Count"),
        };

        /// <summary>A table whose getter throws.</summary>
        public static IReadOnlyList<PatchTarget> Throwing => throw new InvalidOperationException("fixture table is broken");

        /// <summary>Registers through a property.</summary>
        public static void ByProperty() => PatchTargetRegistry.Register(PropertyOwner, PropertyTable);

        /// <summary>Registers through a field.</summary>
        public static void ByField() => PatchTargetRegistry.Register("fixture (field)", FieldTable);

        /// <summary>Registers through a parameterless static method.</summary>
        public static void ByMethod() => PatchTargetRegistry.Register("fixture (method)", BuildTable());

        /// <summary>A registration API: forwards its parameter.</summary>
        /// <param name="owner">Owner.</param>
        /// <param name="targets">Targets.</param>
        public static void Forward(string owner, IEnumerable<PatchTarget> targets) => PatchTargetRegistry.Register(owner, targets);

        /// <summary>Cannot be read offline: the table is built inline.</summary>
        public static void Inline()
        {
            var table = new List<PatchTarget>();
            table.Add(new PatchTarget("fixture.inline", "Seamcheck.Tests", "X", MemberKind.Method, "Y"));
            PatchTargetRegistry.Register("fixture (inline)", table);
        }

        /// <summary>Registers a table whose getter throws.</summary>
        public static void ByThrowing() => PatchTargetRegistry.Register("fixture (throwing)", Throwing);

        /// <summary>The table <see cref="ByMethod"/> passes.</summary>
        public static IReadOnlyList<PatchTarget> BuildTable() => new[]
        {
            new PatchTarget("fixture.method", "Seamcheck.Tests", "CWY.Seamcheck.Tests.Fixtures.Sample", MemberKind.Method, "Load"),
        };
    }
}
