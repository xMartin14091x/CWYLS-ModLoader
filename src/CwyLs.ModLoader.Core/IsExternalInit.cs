namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Compiler shim. The C# <c>record</c> types in this assembly - the event arguments in
    /// <c>Events/</c>, the data views in <c>Data/</c> - have generated <c>init</c> accessors, and
    /// those require this marker; .NET Standard 2.1, the target framework every plugin in this
    /// solution uses, does not ship it.
    /// </summary>
    /// <remarks>
    /// One canonical copy, at the root of CwyLs.ModLoader.Core rather than in any one feature folder:
    /// it belongs to no feature, and a second copy anywhere in the same assembly is a duplicate
    /// definition. Internal on purpose - the type is a compiler detail, and an internal copy
    /// cannot collide with the real one in a consumer targeting a framework that does ship it
    /// (the xunit project targets net10.0 and has its own).
    /// </remarks>
    internal static class IsExternalInit
    {
    }
}
