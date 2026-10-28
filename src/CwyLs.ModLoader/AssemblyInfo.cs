using System.Runtime.CompilerServices;

// API freeze: framework plumbing that mods must not call - ModBackends.Use*, the
// owner-taking MainThread.Arm/Cancel/CancelDelayed/RemoveTickers/PostAfterFor - is internal and
// visible only to the framework's own assemblies listed here. Mods see the public surface only.
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Bulbul")]
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Bulbul.Data")]
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Smoke")]
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Trace")]
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Tests")]
// Optional feature assemblies (microphone, local API): their facet backends register through ModFacetBackends.Use.
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Mic")]
[assembly: InternalsVisibleTo("CwyLs.ModLoader.Api")]
