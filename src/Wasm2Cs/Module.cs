namespace Wasm2Cs;
// Representable value types are independent of the decoder's supported opcodes.
internal enum ValueType : byte { V128 = 0x7b, I32 = 0x7f, I64 = 0x7e, F32 = 0x7d, F64 = 0x7c, FuncRef = 0x70, ExternRef = 0x6f }
internal sealed record Signature(ValueType[] Parameters, ValueType[] Results)
{
    public static readonly Signature Empty = new Signature(Array.Empty<ValueType>(), Array.Empty<ValueType>());
}
internal sealed record Instruction(byte Opcode, int Operand, int Offset, int[]? Targets = null,
    uint Immediate = 0, Signature? BlockType = null, ValueType? SelectType = null, ConstantValue? Constant = null,
    uint Secondary = 0, byte[]? VectorConstant = null, ReferenceValue? Reference = null)
{
    // Filled by validation. null is the polymorphic bottom, never an executable value type.
    public ValueType?[] ResultTypes { get; set; } = Array.Empty<ValueType?>();
}
internal sealed record Function(Signature Signature, ValueType[] Locals, List<Instruction> Instructions);
internal sealed record Module(List<Signature> Types, List<int> Functions,
    Dictionary<string, int> Exports, List<Function> Bodies)
{
    public List<FunctionImport> Imports { get; } = new List<FunctionImport>();
    public List<ImportBinding> ImportBindings { get; } = new List<ImportBinding>();
    public int FunctionCount => Imports.Count + Bodies.Count;
    public Signature FunctionSignature(int index) => index < Imports.Count ? Types[Imports[index].TypeIndex] : Bodies[index - Imports.Count].Signature;
    public Dictionary<string, byte> ExportKinds { get; } = new Dictionary<string, byte>();
    public MemoryDefinition? Memory { get; set; }
    public List<Global> Globals { get; } = new List<Global>();
    public List<DataSegment> Data { get; } = new List<DataSegment>();
    public List<TableDefinition> Tables { get; } = new List<TableDefinition>();
    public List<ElementSegment> Elements { get; } = new List<ElementSegment>();
    public List<TagDefinition> Tags { get; } = new List<TagDefinition>();
    public int? DataCount { get; set; }
    public int? Start { get; set; }
}
internal sealed record FunctionImport(string ModuleName, string Name, int TypeIndex);
internal sealed record ImportBinding(byte Kind, int Index, string ModuleName, string Name);
internal sealed record MemoryDefinition(int Minimum, int? Maximum, bool Imported = false, string? ModuleName = null, string? Name = null);
internal sealed record ConstantValue(ValueType Type, ulong Bits, int? GlobalIndex = null);
internal sealed record ReferenceValue(ValueType Type, int? FunctionIndex);
internal sealed record Global(ValueType Type, bool Mutable, ConstantValue? InitialValue,
    bool Imported = false, string? ModuleName = null, string? Name = null);
internal sealed record DataSegment(uint Offset, byte[] Bytes, bool Passive = false, ConstantValue? OffsetExpression = null);
internal sealed record TableDefinition(ValueType ElementType, int Minimum, int? Maximum,
    bool Imported = false, string? ModuleName = null, string? Name = null);
internal sealed record ElementSegment(int TableIndex, ValueType ElementType, ReferenceValue[] Values,
    bool Passive = false, bool Declarative = false, ConstantValue? OffsetExpression = null);
internal sealed record TagDefinition(Signature Signature, bool Imported = false,
    string? ModuleName = null, string? Name = null);
