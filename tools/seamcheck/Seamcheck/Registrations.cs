using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CwyLs.ModLoader.Core;

namespace CWY.Seamcheck
{
    /// <summary>How the second argument of a <c>PatchTargetRegistry.Register</c> call is supplied.</summary>
    internal enum TableSourceKind
    {
        /// <summary>A static property getter, e.g. <c>BulbulPatchTargets.All</c>. The normal case.</summary>
        StaticProperty,

        /// <summary>A static field load.</summary>
        StaticField,

        /// <summary>A static parameterless method call.</summary>
        StaticMethod,

        /// <summary>
        /// The caller's own parameter: an API that registers on someone else's behalf (a mod helper).
        /// Its targets come from its callers, which may be outside the scanned assemblies.
        /// </summary>
        Forwarded,

        /// <summary>Anything else (a local, an inline array, a LINQ chain): cannot be read offline.</summary>
        Unenumerable,
    }

    /// <summary>One <c>PatchTargetRegistry.Register</c> call site found in a plugin assembly's IL.</summary>
    /// <param name="Assembly">The plugin assembly that makes the call.</param>
    /// <param name="Caller">Declaring type and method of the call, e.g. <c>CwyLs.ModLoader.Bulbul.BulbulPlugin.Awake</c>.</param>
    /// <param name="IlOffset">Offset of the call instruction.</param>
    /// <param name="Owner">The owner string when it is a literal (constants fold into one), else null.</param>
    /// <param name="Kind">How the table is supplied.</param>
    /// <param name="Source">The static member that supplies the table, for the three static kinds.</param>
    /// <param name="Instruction">The instruction that supplies the table, for messages.</param>
    internal sealed record RegistrationSite(
        string Assembly,
        string Caller,
        int IlOffset,
        string? Owner,
        TableSourceKind Kind,
        MemberRefName? Source,
        string Instruction)
    {
        /// <summary>
        /// The table's name for the report: <c>Type.Member</c> (a property without its <c>get_</c>),
        /// or the instruction and caller when there is no static member.
        /// </summary>
        internal string TableName =>
            Source is not { } source ? "(" + Instruction + " in " + Caller + ")"
            : Kind == TableSourceKind.StaticProperty && source.Name.StartsWith("get_", StringComparison.Ordinal)
                ? source.DeclaringType + "." + source.Name.Substring(4)
                : source.ToString();

        /// <summary>Where the call is, for messages: <c>CwyLs.ModLoader.Bulbul!BulbulPlugin.Awake IL_0012</c>.</summary>
        internal string Location => Assembly + "!" + Caller + " IL_" + IlOffset.ToString("X4");
    }

    /// <summary>A registration whose table was read: the site, and the targets in table order.</summary>
    /// <param name="Site">Where it is registered.</param>
    /// <param name="Targets">The table's targets.</param>
    internal sealed record LoadedRegistration(RegistrationSite Site, IReadOnlyList<PatchTarget> Targets)
    {
        /// <summary>The owner label for the report: the literal when there is one, else the table name.</summary>
        internal string OwnerLabel => Site.Owner ?? Site.TableName;
    }

    /// <summary>
    /// Finds every <c>PatchTargetRegistry.Register</c> call in the CWY plugin assemblies by reading
    /// their IL, so the tool checks exactly what the plugins register - including a newly added
    /// table - without a hand-kept list and without running any plugin code for discovery.
    /// </summary>
    /// <remarks>
    /// The contract this relies on (and reports when broken): the table argument is a static
    /// property, field or parameterless method, evaluated immediately before the call. Every
    /// registration in the plugins today is <c>Register(owner, SomeTargets.All)</c>.
    /// </remarks>
    internal static class RegistrationScanner
    {
        /// <summary>Reflection name of the registry type.</summary>
        internal const string RegistryTypeName = "CwyLs.ModLoader.PatchTargetRegistry";

        /// <summary>The registration method's name.</summary>
        internal const string RegisterMethodName = "Register";

        /// <summary>The assembly that defines the registry; a plugin must reference it to register.</summary>
        internal const string FrameworkAssemblyName = "CwyLs.ModLoader";

        /// <summary>
        /// The plugin assemblies in a folder: every <c>*.dll</c> that is the framework or references
        /// it, except the names in <paramref name="excludedNames"/> (the tool itself, test hosts).
        /// </summary>
        /// <param name="directory">Normally the tool's own folder, where the build copied the plugins.</param>
        /// <param name="excludedNames">Simple assembly names to leave out.</param>
        /// <param name="log">Diagnostics.</param>
        /// <returns>Paths, ordered by file name.</returns>
        internal static IReadOnlyList<string> DiscoverPluginAssemblies(
            string directory,
            IReadOnlyCollection<string> excludedNames,
            ToolLog log)
        {
            var found = new List<string>();
            foreach (var path in Directory.GetFiles(directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (excludedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(path);
                    using var pe = new PEReader(stream);
                    if (!pe.HasMetadata)
                    {
                        continue;
                    }

                    var reader = pe.GetMetadataReader();
                    if (!reader.IsAssembly)
                    {
                        continue;
                    }

                    var own = reader.GetString(reader.GetAssemblyDefinition().Name);
                    if (string.Equals(own, FrameworkAssemblyName, StringComparison.Ordinal)
                        || reader.AssemblyReferences.Any(r =>
                            string.Equals(reader.GetString(reader.GetAssemblyReference(r).Name), FrameworkAssemblyName, StringComparison.Ordinal)))
                    {
                        found.Add(path);
                    }
                }
                catch (BadImageFormatException ex)
                {
                    log.Debug("skipped " + Path.GetFileName(path) + ": not a .NET assembly (" + ex.Message + ")");
                }
            }

            return found;
        }

        /// <summary>Finds every <c>Register</c> call site in one assembly.</summary>
        /// <param name="assemblyPath">The plugin assembly file.</param>
        /// <returns>The sites in metadata order (type, method, offset).</returns>
        /// <exception cref="SeamcheckException">The file cannot be read as an assembly.</exception>
        internal static IReadOnlyList<RegistrationSite> Scan(string assemblyPath)
        {
            try
            {
                using var stream = File.OpenRead(assemblyPath);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                var names = new MetadataNames(reader);
                var sites = new List<RegistrationSite>();

                foreach (var typeHandle in reader.TypeDefinitions)
                {
                    var typeName = names.TypeDefinitionName(typeHandle);
                    foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
                    {
                        var method = reader.GetMethodDefinition(methodHandle);
                        if (method.RelativeVirtualAddress == 0)
                        {
                            continue;
                        }

                        var caller = typeName + "." + reader.GetString(method.Name);
                        var instructions = IlDecoder.Decode(pe.GetMethodBody(method.RelativeVirtualAddress));
                        for (var i = 0; i < instructions.Count; i++)
                        {
                            if (IsRegisterCall(names, instructions[i]))
                            {
                                sites.Add(Classify(names, caller, instructions, i));
                            }
                        }
                    }
                }

                return sites;
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidDataException || ex is IOException)
            {
                throw new SeamcheckException("could not scan " + assemblyPath + ": " + ex.Message, ex);
            }
        }

        private static bool IsRegisterCall(MetadataNames names, IlInstruction instruction)
        {
            if (!instruction.IsCall)
            {
                return false;
            }

            var member = names.Member(instruction.Token);
            return member is { } target
                && target.ParameterCount == 2
                && string.Equals(target.Name, RegisterMethodName, StringComparison.Ordinal)
                && string.Equals(target.DeclaringType, RegistryTypeName, StringComparison.Ordinal);
        }

        private static RegistrationSite Classify(MetadataNames names, string caller, List<IlInstruction> instructions, int callIndex)
        {
            var call = instructions[callIndex];
            string? owner = null;
            if (callIndex >= 2 && instructions[callIndex - 2].OpCode == ILOpCode.Ldstr)
            {
                owner = names.UserString(instructions[callIndex - 2].Token);
            }

            if (callIndex < 1)
            {
                return new RegistrationSite(names.AssemblyName, caller, call.Offset, owner, TableSourceKind.Unenumerable, null, "nothing");
            }

            var argument = instructions[callIndex - 1];
            var text = argument.OpCode.ToString().ToLowerInvariant().Replace('_', '.');

            if (argument.IsLoadArgument)
            {
                return new RegistrationSite(names.AssemblyName, caller, call.Offset, owner, TableSourceKind.Forwarded, null, text);
            }

            var member = argument.Token != 0 ? names.Member(argument.Token) : null;
            if (member is { } source)
            {
                if (argument.OpCode == ILOpCode.Ldsfld)
                {
                    return new RegistrationSite(names.AssemblyName, caller, call.Offset, owner, TableSourceKind.StaticField, source, text + " " + source);
                }

                if (argument.IsCall && source.ParameterCount == 0)
                {
                    var kind = source.Name.StartsWith("get_", StringComparison.Ordinal)
                        ? TableSourceKind.StaticProperty
                        : TableSourceKind.StaticMethod;
                    return new RegistrationSite(names.AssemblyName, caller, call.Offset, owner, kind, source, text + " " + source);
                }

                text += " " + source;
            }

            return new RegistrationSite(names.AssemblyName, caller, call.Offset, owner, TableSourceKind.Unenumerable, null, text);
        }
    }

    /// <summary>
    /// Reads the targets a registration site passes, by evaluating that one static member of the
    /// plugin assembly in the tool's own runtime. Target tables are strings-only data by design
    /// (see <c>PatchTarget</c>), so no game code is needed to build them.
    /// </summary>
    internal static class TableLoader
    {
        private const BindingFlags StaticMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>Evaluates the site's table.</summary>
        /// <param name="site">A site of a static kind.</param>
        /// <returns>The non-null targets, in table order.</returns>
        /// <exception cref="SeamcheckException">The site is not static, or the member is missing, throws, or is not a target table.</exception>
        internal static IReadOnlyList<PatchTarget> Load(RegistrationSite site)
        {
            if (site.Source is not { } source
                || (site.Kind != TableSourceKind.StaticProperty
                    && site.Kind != TableSourceKind.StaticField
                    && site.Kind != TableSourceKind.StaticMethod))
            {
                throw new SeamcheckException("registration at " + site.Location + " has no static table to read (" + site.Instruction + ")");
            }

            object? value;
            try
            {
                var assembly = Assembly.Load(new AssemblyName(source.Assembly));
                var type = assembly.GetType(source.DeclaringType, throwOnError: true)!;
                value = site.Kind switch
                {
                    TableSourceKind.StaticProperty => RequireStatic(type.GetProperty(source.Name.Substring(4), StaticMembers), source, site).GetValue(null),
                    TableSourceKind.StaticField => RequireStatic(type.GetField(source.Name, StaticMembers), source, site).GetValue(null),
                    _ => RequireStatic(type.GetMethod(source.Name, StaticMembers, Type.EmptyTypes), source, site).Invoke(null, null),
                };
            }
            catch (SeamcheckException)
            {
                throw;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new SeamcheckException(
                    "reading " + source + " (registered at " + site.Location + ") threw "
                    + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message, ex.InnerException);
            }
            catch (Exception ex)
            {
                throw new SeamcheckException(
                    "could not read " + source + " (registered at " + site.Location + "): "
                    + ex.GetType().Name + ": " + ex.Message, ex);
            }

            if (value is not IEnumerable<PatchTarget> targets)
            {
                throw new SeamcheckException(
                    source + " (registered at " + site.Location + ") is not a patch-target table: "
                    + (value?.GetType().FullName ?? "null"));
            }

            return targets.Where(t => t != null).ToList();
        }

        private static T RequireStatic<T>(T? member, MemberRefName source, RegistrationSite site)
            where T : MemberInfo =>
            member ?? throw new SeamcheckException(
                source + " (registered at " + site.Location + ") is not a static member the tool can read");
    }
}
