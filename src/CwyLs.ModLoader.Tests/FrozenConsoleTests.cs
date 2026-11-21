using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests;

public sealed class FrozenConsoleTests
{
    [Fact]
    public void Frozen_roots_are_registered_with_their_grammar()
    {
        using var metadata = new FrozenMetadata();
        var framework = metadata.Assemblies.Single(a => a.GetName().Name == "CwyLs.ModLoader").Location;
        var api = metadata.Assemblies.Single(a => a.GetName().Name == "CwyLs.ModLoader.Api").Location;
        var calls = FrozenCompiledIl.Registrations(framework, "CwyLs.ModLoader.Console.BuiltInCommands", "RegisterAll")
            .Concat(FrozenCompiledIl.Registrations(framework, "CwyLs.ModLoader.Mods.ModCommands", "Register"))
            .Concat(FrozenCompiledIl.Registrations(api, "CwyLs.ModLoader.Api.ApiCommands", "Register")).ToArray();
        var registry = new CommandRegistry();
        foreach (var call in calls)
            registry.Register(call.Name, call.Description, call.Usage, "framework", args => string.Join("|", args));
        var frozen = FrozenSdkTests.Baseline("console-1.0.txt");
        var names = frozen.Select(line => line.Split('\t')[0]).ToHashSet(StringComparer.Ordinal);
        FrozenSdkTests.EqualLines("frozen console registrations", frozen,
            registry.All.Where(c => names.Contains(c.Name)).Select(c => c.Name + "\t" + c.Usage));
        // Exercise token dispatch in the real registry with safe handlers. No game command executes.
        foreach (var line in FrozenSdkTests.Baseline("console-inputs-1.0.txt"))
        {
            var bits = line.Split('\t');
            Assert.Equal(bits[1] == "\"\"" ? string.Empty : bits[1], registry.Execute(bits[0]));
        }
    }
}
