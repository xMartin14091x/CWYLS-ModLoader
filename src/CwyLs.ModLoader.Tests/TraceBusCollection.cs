using Xunit;

// The named "TraceBus" collection only serialises the classes inside it; every other collection
// still ran beside it, and many of those (Maker*, Api* routers) publish to the static TraceBus
// through the audit log. DisableParallelization makes the collection run alone, so nothing else
// can publish between a test's TraceBus.Reset() and its read of the buffer.
[CollectionDefinition("TraceBus", DisableParallelization = true)]
public sealed class TraceBusCollection
{
}
