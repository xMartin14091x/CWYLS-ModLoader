using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace CWY.Seamcheck
{
    /// <summary>One decoded IL instruction: where it is, what it is, and its metadata token if it has one.</summary>
    /// <param name="Offset">Byte offset in the method body (the <c>IL_xxxx</c> label).</param>
    /// <param name="OpCode">The opcode.</param>
    /// <param name="Token">The operand token for field/method/type/string/token operands, else 0.</param>
    internal readonly record struct IlInstruction(int Offset, ILOpCode OpCode, int Token)
    {
        /// <summary>True for <c>call</c> and <c>callvirt</c>.</summary>
        internal bool IsCall => OpCode == ILOpCode.Call || OpCode == ILOpCode.Callvirt;

        /// <summary>True for <c>ldarg</c> in any of its forms.</summary>
        internal bool IsLoadArgument =>
            OpCode == ILOpCode.Ldarg || OpCode == ILOpCode.Ldarg_s
            || OpCode == ILOpCode.Ldarg_0 || OpCode == ILOpCode.Ldarg_1
            || OpCode == ILOpCode.Ldarg_2 || OpCode == ILOpCode.Ldarg_3;
    }

    /// <summary>
    /// A deliberately small IL walker: it knows each opcode's operand size (taken from
    /// <see cref="OpCodes"/>, so no table is hand-written) and keeps only the token operands. That is
    /// all the registration scan and the static-constructor check need.
    /// </summary>
    internal static class IlDecoder
    {
        private static readonly Dictionary<short, OperandType> OperandTypes = BuildOperandTable();

        /// <summary>Decodes a whole method body.</summary>
        /// <param name="body">The body, from <c>PEReader.GetMethodBody</c>.</param>
        /// <returns>The instructions in order.</returns>
        /// <exception cref="InvalidDataException">The body holds an opcode this runtime does not define.</exception>
        internal static List<IlInstruction> Decode(MethodBodyBlock body)
        {
            var reader = body.GetILReader();
            var instructions = new List<IlInstruction>();
            while (reader.RemainingBytes > 0)
            {
                var offset = reader.Offset;
                short value = reader.ReadByte();
                if (value == 0xFE)
                {
                    value = unchecked((short)(0xFE00 | reader.ReadByte()));
                }

                if (!OperandTypes.TryGetValue(value, out var operandType))
                {
                    throw new InvalidDataException("unknown opcode 0x" + ((ushort)value).ToString("X4") + " at IL_" + offset.ToString("X4"));
                }

                var token = 0;
                switch (operandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        reader.ReadByte();
                        break;
                    case OperandType.InlineVar:
                        reader.ReadInt16();
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.ShortInlineR:
                        reader.ReadInt32();
                        break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        token = reader.ReadInt32();
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        reader.ReadInt64();
                        break;
                    case OperandType.InlineSwitch:
                        var targets = reader.ReadInt32();
                        for (var i = 0; i < targets; i++)
                        {
                            reader.ReadInt32();
                        }

                        break;
                    default:
                        throw new InvalidDataException("unsupported operand type " + operandType + " at IL_" + offset.ToString("X4"));
                }

                instructions.Add(new IlInstruction(offset, (ILOpCode)(ushort)value, token));
            }

            return instructions;
        }

        private static Dictionary<short, OperandType> BuildOperandTable()
        {
            var table = new Dictionary<short, OperandType>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is OpCode opCode)
                {
                    table[opCode.Value] = opCode.OperandType;
                }
            }

            return table;
        }
    }

    /// <summary>What a <c>call</c> or <c>ldsfld</c> token points at, in reflection-name terms.</summary>
    /// <param name="Assembly">Simple name of the assembly that declares the member.</param>
    /// <param name="DeclaringType">Reflection full name of the declaring type (nested with <c>+</c>).</param>
    /// <param name="Name">Member name, e.g. <c>get_All</c> or <c>Register</c>.</param>
    /// <param name="ParameterCount">Parameters of a method; 0 for a field.</param>
    internal readonly record struct MemberRefName(string Assembly, string DeclaringType, string Name, int ParameterCount)
    {
        /// <summary><c>Type.Member</c>, for messages.</summary>
        public override string ToString() => DeclaringType + "." + Name;
    }

    /// <summary>Reads reflection-style names out of raw metadata handles for one assembly.</summary>
    internal sealed class MetadataNames
    {
        private readonly MetadataReader _reader;
        private readonly string _assemblyName;

        /// <summary>Creates a name reader for one module.</summary>
        /// <param name="reader">The module's metadata.</param>
        internal MetadataNames(MetadataReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _assemblyName = reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : "?";
        }

        /// <summary>The module's own assembly name.</summary>
        internal string AssemblyName => _assemblyName;

        /// <summary>The string a <c>ldstr</c> token names.</summary>
        /// <param name="token">The user-string token.</param>
        internal string UserString(int token) => _reader.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF));

        /// <summary>
        /// The method or field a token names, or null for anything else (a signature, a type).
        /// Generic instantiations are reduced to their definition's name.
        /// </summary>
        /// <param name="token">A MethodDef, MemberRef, MethodSpec or FieldDef token.</param>
        internal MemberRefName? Member(int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var method = _reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return new MemberRefName(
                        _assemblyName,
                        TypeDefinitionName(method.GetDeclaringType()),
                        _reader.GetString(method.Name),
                        ReadParameterCount(method.Signature));
                }

                case HandleKind.FieldDefinition:
                {
                    var field = _reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return new MemberRefName(_assemblyName, TypeDefinitionName(field.GetDeclaringType()), _reader.GetString(field.Name), 0);
                }

                case HandleKind.MemberReference:
                {
                    var reference = _reader.GetMemberReference((MemberReferenceHandle)handle);
                    var (assembly, type) = ParentName(reference.Parent);
                    var parameters = reference.GetKind() == MemberReferenceKind.Method
                        ? ReadParameterCount(reference.Signature)
                        : 0;
                    return new MemberRefName(assembly, type, _reader.GetString(reference.Name), parameters);
                }

                case HandleKind.MethodSpecification:
                {
                    var spec = _reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return Member(MetadataTokens.GetToken(spec.Method));
                }

                default:
                    return null;
            }
        }

        /// <summary>Reflection full name of a type definition in this module.</summary>
        /// <param name="handle">The type.</param>
        internal string TypeDefinitionName(TypeDefinitionHandle handle)
        {
            var type = _reader.GetTypeDefinition(handle);
            var name = _reader.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return TypeDefinitionName(declaring) + "+" + name;
            }

            var ns = _reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        private (string Assembly, string Type) ParentName(EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeDefinition:
                    return (_assemblyName, TypeDefinitionName((TypeDefinitionHandle)parent));
                case HandleKind.TypeReference:
                    return TypeReferenceName((TypeReferenceHandle)parent);
                case HandleKind.TypeSpecification:
                    return TypeSpecificationName((TypeSpecificationHandle)parent);
                default:
                    return (_assemblyName, "<" + parent.Kind + ">");
            }
        }

        private (string Assembly, string Type) TypeReferenceName(TypeReferenceHandle handle)
        {
            var reference = _reader.GetTypeReference(handle);
            var name = _reader.GetString(reference.Name);
            var scope = reference.ResolutionScope;
            switch (scope.Kind)
            {
                case HandleKind.TypeReference:
                {
                    var (assembly, outer) = TypeReferenceName((TypeReferenceHandle)scope);
                    return (assembly, outer + "+" + name);
                }

                case HandleKind.AssemblyReference:
                {
                    var assembly = _reader.GetString(_reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                    return (assembly, Qualify(reference.Namespace, name));
                }

                default:
                    return (_assemblyName, Qualify(reference.Namespace, name));
            }
        }

        /// <summary>A generic instantiation (<c>List&lt;int&gt;</c>) is named by its definition.</summary>
        private (string Assembly, string Type) TypeSpecificationName(TypeSpecificationHandle handle)
        {
            var blob = _reader.GetBlobReader(_reader.GetTypeSpecification(handle).Signature);
            if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
            {
                blob.ReadSignatureTypeCode();
                var definition = blob.ReadTypeHandle();
                return ParentName(definition);
            }

            return (_assemblyName, "<typespec>");
        }

        private string Qualify(StringHandle ns, string name)
        {
            var space = _reader.GetString(ns);
            return space.Length == 0 ? name : space + "." + name;
        }

        /// <summary>A method signature blob: header, [generic count], parameter count, ...</summary>
        private int ReadParameterCount(BlobHandle signature)
        {
            var blob = _reader.GetBlobReader(signature);
            var header = blob.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                blob.ReadCompressedInteger();
            }

            return blob.ReadCompressedInteger();
        }
    }
}
