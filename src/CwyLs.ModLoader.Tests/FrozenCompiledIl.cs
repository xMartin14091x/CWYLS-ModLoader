using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace CwyLs.ModLoader.Tests;

/// <summary>Reads registration arguments in shipped IL without executing Unity-dependent registration code.</summary>
internal static class FrozenCompiledIl
{
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(c => unchecked((ushort)c.Value));

    internal static List<(string Name, string Description, string Usage)> Registrations(string dll, string typeName, string methodName)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var instructions = Instructions(pe, reader, typeName, methodName);
        var strings = new List<string>();
        var result = new List<(string, string, string)>();
        foreach (var (op, token) in instructions)
        {
            // Ignore a literal assigned to a local (e.g. the framework group before the first call).
            if (op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3) strings.Clear();
            if (op == OpCodes.Ldstr) strings.Add(reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00ffffff)));
            if (op != OpCodes.Call && op != OpCodes.Callvirt) continue;
            var handle = MetadataTokens.EntityHandle(token);
            string name;
            string owner;
            if (handle.Kind == HandleKind.MemberReference)
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                name = reader.GetString(member.Name);
                owner = member.Parent.Kind == HandleKind.TypeReference ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name) : "";
            }
            else if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                name = reader.GetString(method.Name);
                owner = reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);
            }
            else continue;
            if (name != "Register" || (owner != "CommandRegistry" && owner != "ConsoleFeature")) continue;
            if (strings.Count < 3) throw new InvalidOperationException("Registration no longer has literal name/description/usage: " + typeName);
            result.Add((strings[0], strings[1], strings[2]));
            strings.Clear();
        }
        return result;
    }

    internal static string[] Strings(string dll, string typeName, string methodName)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return Instructions(pe, reader, typeName, methodName).Where(i => i.Op == OpCodes.Ldstr)
            .Select(i => reader.GetUserString(MetadataTokens.UserStringHandle(i.Token & 0x00ffffff))).ToArray();
    }

    private static List<(OpCode Op, int Token)> Instructions(PEReader pe, MetadataReader reader, string typeName, string methodName)
    {
        var type = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(t => reader.GetString(t.Namespace) + "." + reader.GetString(t.Name) == typeName);
        var method = type.GetMethods().Select(reader.GetMethodDefinition).Single(m => reader.GetString(m.Name) == methodName);
        var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        var result = new List<(OpCode, int)>();
        for (var offset = 0; offset < bytes.Length;)
        {
            ushort code = bytes[offset++];
            if (code == 0xfe) code = (ushort)(0xfe00 | bytes[offset++]);
            var op = Codes[code];
            var token = 0;
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
            if (size == 4) token = BitConverter.ToInt32(bytes, offset);
            result.Add((op, token));
            offset += size;
        }
        return result;
    }
}
