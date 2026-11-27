using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

public static class MetadataCalls
{
    private static readonly Dictionary<ushort, OpCode> Opcodes = BuildOpcodes();

    private static Dictionary<ushort, OpCode> BuildOpcodes()
    {
        var result = new Dictionary<ushort, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is OpCode code)
                result[unchecked((ushort)code.Value)] = code;
        return result;
    }

    public static IEnumerable<string> Scan(string path, string callerPattern, string targetPattern, bool flow)
    {
        var callers = new Regex(callerPattern);
        var targets = new Regex(targetPattern);
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
        {
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0)
                    continue;
                var caller = TypeName(reader, method.GetDeclaringType()) + "." + reader.GetString(method.Name);
                if (!callers.IsMatch(caller))
                    continue;
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                while (il.RemainingBytes > 0)
                {
                    var offset = il.Offset;
                    ushort code = il.ReadByte();
                    if (code == 0xFE)
                        code = (ushort)(0xFE00 | il.ReadByte());
                    var instruction = Opcodes[code];
                    var operand = instruction.OperandType;
                    if (flow && (instruction.Name.StartsWith("ldc.i4.", StringComparison.Ordinal) || instruction == OpCodes.Ret))
                        yield return caller + " IL_" + offset.ToString("X4") + " " + instruction.Name;
                    switch (operand)
                    {
                        case OperandType.InlineMethod:
                            var target = MethodName(reader, MetadataTokens.EntityHandle(il.ReadInt32()));
                            if (targets.IsMatch(target))
                                yield return caller + " IL_" + offset.ToString("X4") +
                                    (flow && (instruction == OpCodes.Ldftn || instruction == OpCodes.Ldvirtftn)
                                        ? " " + instruction.Name : "") + " -> " + target;
                            break;
                        case OperandType.InlineField:
                            var fieldHandle = MetadataTokens.EntityHandle(il.ReadInt32());
                            if (flow && fieldHandle.Kind == HandleKind.FieldDefinition)
                            {
                                var field = reader.GetFieldDefinition((FieldDefinitionHandle)fieldHandle);
                                var targetField = TypeName(reader, field.GetDeclaringType()) + "." + reader.GetString(field.Name);
                                if (targets.IsMatch(targetField))
                                    yield return caller + " IL_" + offset.ToString("X4") + " " + instruction.Name + " -> " + targetField;
                            }
                            if (flow && fieldHandle.Kind == HandleKind.MemberReference)
                            {
                                var field = reader.GetMemberReference((MemberReferenceHandle)fieldHandle);
                                var targetField = TypeName(reader, field.Parent) + "." + reader.GetString(field.Name);
                                if (targets.IsMatch(targetField))
                                    yield return caller + " IL_" + offset.ToString("X4") + " " + instruction.Name + " -> " + targetField;
                            }
                            break;
                        case OperandType.InlineSig:
                        case OperandType.InlineTok:
                        case OperandType.InlineType:
                        case OperandType.InlineI:
                            il.Offset += 4; break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            il.Offset += 8; break;
                        case OperandType.ShortInlineVar:
                        case OperandType.ShortInlineI:
                            il.Offset += 1; break;
                        case OperandType.InlineVar:
                            il.Offset += 2; break;
                        case OperandType.InlineBrTarget:
                        case OperandType.ShortInlineBrTarget:
                            int delta = operand == OperandType.InlineBrTarget ? il.ReadInt32() : il.ReadSByte();
                            if (flow)
                                yield return caller + " IL_" + offset.ToString("X4") + " " + instruction.Name +
                                    " IL_" + (il.Offset + delta).ToString("X4");
                            break;
                        case OperandType.InlineString:
                            var literal = reader.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32() & 0x00ffffff));
                            if (flow && targets.IsMatch(literal))
                                yield return caller + " IL_" + offset.ToString("X4") + " string " + literal;
                            break;
                        case OperandType.ShortInlineR:
                            var value = il.ReadSingle();
                            if (flow)
                                yield return caller + " IL_" + offset.ToString("X4") + " float " + value;
                            break;
                        case OperandType.InlineSwitch:
                            int count = il.ReadInt32();
                            il.Offset += count * 4; break;
                        case OperandType.InlineNone:
                            break;
                        default:
                            throw new InvalidDataException("Unexpected IL operand: " + operand);
                    }
                }
            }
        }
    }

    private static string TypeName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var def = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                return reader.GetString(def.Namespace) + "." + reader.GetString(def.Name);
            case HandleKind.TypeReference:
                var reference = reader.GetTypeReference((TypeReferenceHandle)handle);
                return reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name);
            default:
                return handle.Kind.ToString();
        }
    }

    private static string MethodName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodSpecification:
                return MethodName(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            case HandleKind.MethodDefinition:
                var def = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return TypeName(reader, def.GetDeclaringType()) + "." + reader.GetString(def.Name);
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                return TypeName(reader, member.Parent) + "." + reader.GetString(member.Name);
            default:
                return handle.Kind.ToString();
        }
    }
}




