namespace Wasm2Cs;
// Representable value types are independent of the decoder's supported opcodes.
internal enum ValueType : byte { I32 = 0x7f, I64 = 0x7e, F32 = 0x7d, F64 = 0x7c, FuncRef = 0x70, ExternRef = 0x6f }
internal sealed record Signature(ValueType[] Parameters, ValueType[] Results)
{
    public static readonly Signature Empty = new Signature(Array.Empty<ValueType>(), Array.Empty<ValueType>());
}
internal sealed record Instruction(byte Opcode, int Operand, int Offset, int[]? Targets = null,
    uint Immediate = 0, Signature? BlockType = null, ValueType? SelectType = null, ConstantValue? Constant = null)
{
    // Filled by validation. null is the polymorphic bottom, never an executable value type.
    public ValueType?[] ResultTypes { get; set; } = Array.Empty<ValueType?>();
}
internal sealed record Function(Signature Signature, ValueType[] Locals, List<Instruction> Instructions);
internal sealed record Module(List<Signature> Types, List<int> Functions,
    Dictionary<string, int> Exports, List<Function> Bodies)
{
    public List<FunctionImport> Imports { get; } = new List<FunctionImport>();
    public int FunctionCount => Imports.Count + Bodies.Count;
    public Signature FunctionSignature(int index) => index < Imports.Count ? Types[Imports[index].TypeIndex] : Bodies[index - Imports.Count].Signature;
    public Dictionary<string, byte> ExportKinds { get; } = new Dictionary<string, byte>();
    public MemoryDefinition? Memory { get; set; }
    public List<Global> Globals { get; } = new List<Global>();
    public List<DataSegment> Data { get; } = new List<DataSegment>();
    public int? Start { get; set; }
}
internal sealed record FunctionImport(string ModuleName, string Name, int TypeIndex);
internal sealed record MemoryDefinition(int Minimum, int Maximum);
internal sealed record ConstantValue(ValueType Type, ulong Bits);
internal sealed record Global(ValueType Type, bool Mutable, ConstantValue InitialValue);
internal sealed record DataSegment(uint Offset, byte[] Bytes);
