using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace CwyLs.ModLoader.Tests;

/// <summary>Compiled 1.x contract. Metadata-only: never instantiate plugins or load Unity into the test host.</summary>
public sealed class FrozenSdkTests
{
    internal const string Policy = "a frozen 1.x API changed: only a major version may do this; additions need a deliberate baseline update + CHANGELOG line";
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Frozen_CSharp_surface_matches_1_0()
    {
        using var sdk = new FrozenMetadata();
        EqualLines("frozen C# surface", Baseline("frozen-1.0.txt"), CaptureSurface(sdk));
    }

    [Fact]
    public void Every_public_SDK_type_is_classified()
    {
        using var sdk = new FrozenMetadata();
        var classified = Classifications();
        Assert.Equal(classified.Count, classified.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(classified, x => Assert.Contains(x.Status, new[] { "frozen", "experimental", "internal" }));
        var closure = new HashSet<string>(new[] {
            "CwyLs.ModLoader.Core.Packs.SemVer", "CwyLs.ModLoader.Core.Packs.PackDependency",
            "CwyLs.ModLoader.Core.Events.ICwyEvent", "CwyLs.ModLoader.Core.Threading.DelayedWork",
            "CwyLs.ModLoader.Core.Threading.MainThreadRejectReason", "CwyLs.ModLoader.Core.Threading.MainThreadRejectedException",
            "CwyLs.ModLoader.Api.ApiPlugin", // Appendix also classifies the HTTP plugin as internal.
        }, StringComparer.Ordinal);
        var actual = sdk.Assemblies.SelectMany(a => a.GetExportedTypes())
            .Where(t => t.Namespace == "CwyLs.ModLoader.Mods" || t.Namespace == "CwyLs.ModLoader.Core.Mods" || closure.Contains(t.FullName!))
            .Select(t => t.FullName!).ToArray();
        EqualLines("public type classification (new types need an explicit status)", classified.Select(x => x.Name), actual);
    }

    internal static string[] CaptureSurface(FrozenMetadata sdk)
    {
        var exceptions = Baseline("member-exceptions-1.0.txt").ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var item in Classifications().Where(x => x.Status == "frozen"))
        {
            var type = sdk.Assemblies.Select(a => a.GetType(item.Name)).SingleOrDefault(t => t != null);
            if (type is null) continue; // The final diff names the removed type and its members.
            result.Add(TypeLine(type));
            foreach (var member in type.GetMembers(Declared).Where(Exposed))
            {
                if (member is MethodInfo m && m.IsSpecialName && !m.Name.StartsWith("op_", StringComparison.Ordinal)) continue;
                if (member is FieldInfo f && f.IsSpecialName) continue; // enum value__
                if (member is Type) continue; // classified separately, if exposed
                var key = type.FullName + "::" + member.Name
                    + (member is MethodInfo method ? "(" + string.Join(",", method.GetParameters().Select(p => Name(p.ParameterType))) + ")" : "");
                if (exceptions.Contains(key)) { used.Add(key); continue; }
                result.Add(MemberLine(member));
            }
        }
        EqualLines("member exception list (removed exceptions need review)", exceptions, used);
        return result.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static bool Exposed(MemberInfo member) => member switch
    {
        MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p => p.GetAccessors(true).Any(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly),
        EventInfo e => e.GetAddMethod(true) is { } m && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly),
        Type t => t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem,
        _ => false,
    };

    private static string TypeLine(Type t)
    {
        var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";
        return "type " + kind + " " + Name(t) + (t.IsAbstract ? " abstract" : "") + (t.IsSealed ? " sealed" : "")
            + (t.BaseType == null ? "" : " base=" + Name(t.BaseType))
            + (t.IsEnum ? " underlying=" + Name(t.GetEnumUnderlyingType()) : "")
            + " interfaces=[" + string.Join(",", t.GetInterfaces().Select(Name).OrderBy(x => x, StringComparer.Ordinal)) + "]"
            + Constraints(t.IsGenericTypeDefinition ? t.GetGenericArguments() : Array.Empty<Type>()) + Annotations(t);
    }

    private static string MemberLine(MemberInfo member)
    {
        var owner = Name(member.DeclaringType!) + "::";
        switch (member)
        {
            case PropertyInfo p:
                return "property " + owner + p.Name + "(" + string.Join(",", p.GetIndexParameters().Select(Parameter)) + "): " + Name(p.PropertyType)
                    + " { " + string.Join("; ", p.GetAccessors(true).Where(m => Exposed(m)).OrderBy(m => m.Name, StringComparer.Ordinal)
                        .Select(m => Flags(m) + (m.Name.StartsWith("get_", StringComparison.Ordinal) ? " get" : " set") + Annotations(m) + Annotations(m.ReturnParameter) + Modifiers(m.ReturnParameter.GetRequiredCustomModifiers(), m.ReturnParameter.GetOptionalCustomModifiers())))
                    + "; }" + Annotations(p);
            case MethodBase m:
                var args = m.IsGenericMethodDefinition ? m.GetGenericArguments() : Array.Empty<Type>();
                return (m is ConstructorInfo ? "constructor " : "method ") + Flags(m) + " " + owner + m.Name
                    + (args.Length == 0 ? "" : "<" + string.Join(",", args.Select(Name)) + ">")
                    + "(" + string.Join(",", m.GetParameters().Select(Parameter)) + ")"
                    + (m is MethodInfo method ? ": " + Name(method.ReturnType) + Annotations(method.ReturnParameter) + Modifiers(method.ReturnParameter.GetRequiredCustomModifiers(), method.ReturnParameter.GetOptionalCustomModifiers()) : "")
                    + Constraints(args) + Annotations(m);
            case FieldInfo f:
                var flags = (f.IsPublic ? "public" : "protected") + (f.IsStatic ? " static" : "") + (f.IsInitOnly ? " readonly" : "") + (f.IsLiteral ? " const" : "");
                // CwyApi version literals must advance with releases. Their shape, not current values, is frozen.
                return (f.DeclaringType!.IsEnum ? "enum-value " : "field ") + flags + " " + owner + f.Name + ": " + Name(f.FieldType)
                    + (f.IsLiteral && f.DeclaringType.FullName != "CwyLs.ModLoader.Core.Mods.CwyApi" ? " = " + Literal(f.GetRawConstantValue()) : "") + Annotations(f);
            case EventInfo e:
                return "event " + owner + e.Name + ": " + Name(e.EventHandlerType!) + " " + Flags(e.GetAddMethod(true)!) + Annotations(e);
            default: throw new InvalidOperationException("Unformatted frozen member: " + member);
        }
    }

    private static string Flags(MethodBase m) =>
        (m.IsPublic ? "public" : m.IsFamilyOrAssembly ? "protected internal" : "protected")
        + (m.IsStatic ? " static" : " instance") + (m.IsAbstract ? " abstract" : "")
        + (m.IsVirtual ? " virtual" : "") + (m.IsFinal ? " final" : "")
        + ((m.Attributes & MethodAttributes.NewSlot) != 0 ? " newslot" : "");

    private static string Parameter(ParameterInfo p) =>
        (p.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute") ? "params " : "")
        + (p.ParameterType.IsByRef ? p.IsOut ? "out " : p.IsIn ? "in " : "ref " : "")
        + Name(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType) + " " + p.Name
        + (p.IsOptional ? " = " + Literal(p.RawDefaultValue) : "") + Annotations(p)
        + Modifiers(p.GetRequiredCustomModifiers(), p.GetOptionalCustomModifiers());

    private static string Modifiers(Type[] required, Type[] optional) =>
        string.Concat(required.Select(t => " modreq(" + Name(t) + ")")) + string.Concat(optional.Select(t => " modopt(" + Name(t) + ")"));

    private static string Constraints(IEnumerable<Type> args) => string.Concat(args.Select(t =>
    {
        var flags = t.GenericParameterAttributes;
        var terms = new List<string>();
        if ((flags & GenericParameterAttributes.ReferenceTypeConstraint) != 0) terms.Add("class");
        if ((flags & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) terms.Add("struct");
        terms.AddRange(t.GetGenericParameterConstraints().Select(Name).OrderBy(x => x, StringComparer.Ordinal));
        if ((flags & GenericParameterAttributes.DefaultConstructorConstraint) != 0) terms.Add("new()");
        return (terms.Count == 0 ? "" : " where " + t.Name + ": " + string.Join(",", terms)) + Annotations(t);
    }));

    internal static string Name(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        if (t.IsByRef) return Name(t.GetElementType()!) + "&";
        if (t.IsPointer) return Name(t.GetElementType()!) + "*";
        if (t.IsArray) return Name(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (!t.IsGenericType) return t.FullName!;
        var name = t.GetGenericTypeDefinition().FullName!;
        name = System.Text.RegularExpressions.Regex.Replace(name, @"`\d+", "");
        return name + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">";
    }

    private static string Literal(object? value) => value switch
    {
        null => "null/default",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    // Raw nullable metadata is deterministic and covers annotations that ordinary Type reflection erases.
    private static string Annotations(object item)
    {
        var attrs = item is ParameterInfo p ? p.GetCustomAttributesData() : ((MemberInfo)item).GetCustomAttributesData();
        return string.Concat(attrs.Where(a => a.AttributeType.FullName is "System.Runtime.CompilerServices.NullableAttribute" or "System.Runtime.CompilerServices.NullableContextAttribute")
            .OrderBy(a => a.AttributeType.FullName, StringComparer.Ordinal)
            .Select(a => " [" + a.AttributeType.Name + "=" + string.Join(",", a.ConstructorArguments.Select(AttributeValue)) + "]"));
    }
    private static string AttributeValue(CustomAttributeTypedArgument a) => a.Value is IEnumerable<CustomAttributeTypedArgument> values
        ? "[" + string.Join(",", values.Select(AttributeValue)) + "]" : Literal(a.Value);

    internal static List<(string Status, string Name)> Classifications() => Baseline("classified-1.0.txt")
        .Select(line => { var bits = line.Split(' '); Assert.Equal(2, bits.Length); return (bits[0], bits[1]); }).ToList();

    internal static string[] Baseline(string file)
    {
        var assembly = typeof(FrozenSdkTests).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".ApiSurface." + file, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd().Replace("\r", "").Split('\n').Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)).ToArray();
    }

    internal static void EqualLines(string label, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var before = expected.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var after = actual.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var removed = before.Except(after, StringComparer.Ordinal).Select(x => "- " + x);
        var added = after.Except(before, StringComparer.Ordinal).Select(x => "+ " + x);
        Assert.True(before.SequenceEqual(after, StringComparer.Ordinal), label + ": " + Policy + "\n" + string.Join("\n", removed.Concat(added)));
    }
}

internal sealed class FrozenMetadata : IDisposable
{
    internal string Root { get; } = TestRepo.Root ?? throw new InvalidOperationException("Freeze tests require a repository checkout.");
    internal string Configuration { get; } = typeof(FrozenSdkTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
    internal MetadataLoadContext Context { get; }
    internal Assembly[] Assemblies { get; }

    internal FrozenMetadata()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        var metadata = typeof(FrozenSdkTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value!);
        foreach (var key in new[] { "FreezeGameManagedPath", "FreezeBepInExCorePath" })
        {
            Assert.True(metadata.TryGetValue(key, out var directory) && Directory.Exists(directory), "Missing metadata reference directory: " + key);
            paths.AddRange(Directory.GetFiles(directory!, "*.dll"));
        }
        var assemblies = new[] { "CwyLs.ModLoader.Core", "CwyLs.ModLoader", "CwyLs.ModLoader.Api" }
            .Select(name => Path.Combine(Root, "src", name, "bin", Configuration, "netstandard2.1", name + ".dll")).ToArray();
        Assert.All(assemblies, path => Assert.True(File.Exists(path), "Build the solution before freeze tests: " + path));
        // Prefer .NET runtime facades over the installed game's mscorlib; dependencies are inspected only.
        paths.InsertRange(0, assemblies);
        paths = paths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        Context = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
        Assemblies = assemblies.Select(Context.LoadFromAssemblyPath).ToArray();
    }
    public void Dispose() => Context.Dispose();
}
