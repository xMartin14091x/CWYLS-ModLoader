using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests;

public sealed class FrozenManifestTests
{
    [Fact]
    public void Frozen_manifest_fields_and_extension_escape_hatch_are_accepted()
    {
        var samples = FrozenSdkTests.Baseline("manifest-1.0.txt").Select(line => line.Split('\t')).ToArray();
        Assert.Equal(12, samples.Length);
        // The adopted draft explicitly retains this experimental legacy alias. It is not a 13th frozen field.
        var actual = (string[])typeof(PackManifestParser).GetField("KnownKeys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        FrozenSdkTests.EqualLines("manifest accepted-key classification", samples.Select(s => s[0]).Append("contentPatcherVersion"), actual);
        foreach (var sample in samples)
        {
            var json = Minimal();
            json[sample[0]] = JsonReader.Parse(sample[1]);
            var parsed = PackManifestParser.Parse(CwyLs.ModLoader.Core.Api.ApiJson.Write(json));
            Assert.NotNull(parsed);
        }
        var extensions = Minimal();
        extensions["x-freeze"] = new JsonObject { ["anything"] = new List<object?> { true, 1L, null } };
        Assert.NotNull(PackManifestParser.Parse(CwyLs.ModLoader.Core.Api.ApiJson.Write(extensions)));
        var unknown = Minimal();
        unknown["unexpectedOrdinary"] = true;
        Assert.Contains("unexpectedOrdinary", Assert.Throws<PackManifestException>(() => PackManifestParser.Parse(CwyLs.ModLoader.Core.Api.ApiJson.Write(unknown))).Message);
        unknown = Minimal();
        unknown["Author"] = "case-sensitive";
        Assert.Throws<PackManifestException>(() => PackManifestParser.Parse(CwyLs.ModLoader.Core.Api.ApiJson.Write(unknown)));
        var legacy = Minimal();
        legacy["contentPatcherVersion"] = "0.9.0";
        Assert.NotNull(PackManifestParser.Parse(CwyLs.ModLoader.Core.Api.ApiJson.Write(legacy)));
    }

    private static JsonObject Minimal() => new JsonObject { ["id"] = "cwy.freeze", ["name"] = "Freeze", ["version"] = "1.0.0" };
}
