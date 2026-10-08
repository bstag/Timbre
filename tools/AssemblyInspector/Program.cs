using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.RegularExpressions;

// Reads PE metadata and IL as data. Never loads or executes vendor assemblies.
if (args.Length < 1) { Console.Error.WriteLine("Usage: AssemblyInspector assembly [type regex] [method regex]"); return 1; }
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
if (!pe.HasMetadata) { Console.Error.WriteLine("This PE has no managed metadata."); return 2; }
var md = pe.GetMetadataReader();
if (args.Length >= 2 && args[1] == "--array") {
    if (args.Length != 4 || !int.TryParse(args[3], out var count) || count is < 1 or > 1024) {
        Console.Error.WriteLine("Usage: AssemblyInspector assembly --array exact-field-name int32-count (1..1024)"); return 1;
    }
    var fields = md.FieldDefinitions.Select(h => md.GetFieldDefinition(h)).Where(f => md.GetString(f.Name) == args[2]).ToArray();
    if (fields.Length != 1 || fields[0].GetRelativeVirtualAddress() == 0) {
        Console.Error.WriteLine("Expected one static field with RVA data."); return 1;
    }
    var rva = fields[0].GetRelativeVirtualAddress();
    var bytes = pe.GetSectionData(rva).GetContent(0, checked(count * 4)).ToArray();
    var values = Enumerable.Range(0, count).Select(i => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4))).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { file = args[0], field = args[2], rva, count, values,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), scope = "Static PE data only; caller supplies array length from inspected IL." }, new JsonSerializerOptions { WriteIndented = true })); return 0;
}
var typeFilter = new Regex(args.Length > 1 ? args[1] : "Pipe|IPC|Agent|Request|Apo", RegexOptions.IgnoreCase);
var methodFilter = new Regex(args.Length > 2 ? args[2] : "SendMessage|ConnectAgentParams|SetSideTone|SetMicGain", RegexOptions.IgnoreCase);
var codes = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode))
    .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(c => unchecked((ushort)c.Value));
string Name(EntityHandle h) => h.Kind switch {
    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name),
    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
    HandleKind.MethodDefinition => md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name),
    HandleKind.MemberReference => Name(md.GetMemberReference((MemberReferenceHandle)h).Parent) + "." + md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name),
    HandleKind.FieldDefinition => md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)h).Name),
    _ => h.Kind + ":" + MetadataTokens.GetToken(h).ToString("X8")
};
object? Constant(ConstantHandle h) {
    if (h.IsNil) return null;
    var c = md.GetConstant(h); var b = md.GetBlobReader(c.Value);
    return c.TypeCode switch {
        ConstantTypeCode.Int32 => b.ReadInt32(), ConstantTypeCode.UInt32 => b.ReadUInt32(),
        ConstantTypeCode.Int16 => b.ReadInt16(), ConstantTypeCode.UInt16 => b.ReadUInt16(),
        ConstantTypeCode.Byte => b.ReadByte(), ConstantTypeCode.SByte => b.ReadSByte(),
        ConstantTypeCode.Boolean => b.ReadBoolean(), ConstantTypeCode.String => b.ReadUTF16(b.Length),
        _ => Convert.ToHexString(md.GetBlobBytes(c.Value))
    };
}
List<object> Instructions(MethodDefinition m) {
    var result = new List<object>();
    if (m.RelativeVirtualAddress == 0) return result;
    var b = pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
    while (b.RemainingBytes > 0) {
        var offset = b.Offset; ushort key = b.ReadByte(); if (key == 0xFE) key = (ushort)(0xFE00 | b.ReadByte());
        if (!codes.TryGetValue(key, out var op)) throw new InvalidDataException("Unknown opcode");
        object? operand = null;
        switch (op.OperandType) {
            case OperandType.InlineNone: break;
            case OperandType.ShortInlineI: operand = b.ReadSByte(); break;
            case OperandType.InlineI: operand = b.ReadInt32(); break;
            case OperandType.InlineI8: operand = b.ReadInt64(); break;
            case OperandType.ShortInlineR: operand = b.ReadSingle(); break;
            case OperandType.InlineR: operand = b.ReadDouble(); break;
            case OperandType.ShortInlineVar: operand = b.ReadByte(); break;
            case OperandType.InlineVar: operand = b.ReadUInt16(); break;
            case OperandType.ShortInlineBrTarget: { var delta = b.ReadSByte(); operand = b.Offset + delta; break; }
            case OperandType.InlineBrTarget: { var delta = b.ReadInt32(); operand = b.Offset + delta; break; }
            case OperandType.InlineSwitch: { var n = b.ReadInt32(); var targets = new int[n]; for (int i=0;i<n;i++) targets[i]=b.ReadInt32(); var end=b.Offset; operand=targets.Select(t=>end+t).ToArray(); break; }
            case OperandType.InlineString: operand = md.GetUserString(MetadataTokens.UserStringHandle(b.ReadInt32() & 0x00FFFFFF)); break;
            default: operand = Name(MetadataTokens.EntityHandle(b.ReadInt32())); break;
        }
        result.Add(new { offset, op = op.Name, operand });
    }
    return result;
}
var types = new List<object>();
foreach (var h in md.TypeDefinitions) {
    var t = md.GetTypeDefinition(h); var name = md.GetString(t.Namespace) + "." + md.GetString(t.Name);
    if (!typeFilter.IsMatch(name)) continue;
    types.Add(new { name,
        fields = t.GetFields().Select(fh => { var f = md.GetFieldDefinition(fh); return new { name=md.GetString(f.Name), attributes=f.Attributes.ToString(), value=Constant(f.GetDefaultValue()) }; }).ToArray(),
        methods = t.GetMethods().Select(mh => { var m=md.GetMethodDefinition(mh); var n=md.GetString(m.Name); return new { name=n, parameters=m.GetParameters().Select(p=>md.GetString(md.GetParameter(p).Name)).ToArray(), il=methodFilter.IsMatch(n) ? Instructions(m) : null }; }).ToArray()
    });
}
Console.WriteLine(JsonSerializer.Serialize(new { file=args[0], types }, new JsonSerializerOptions { WriteIndented=true }));
return 0;
