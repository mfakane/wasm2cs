namespace Wasm2Cs;
internal sealed record Signature(int Parameters, int Results);
internal sealed record Instruction(byte Opcode, int Operand, int Offset, int[]? Targets = null, uint Immediate = 0);
internal sealed record Function(Signature Signature, int Locals, List<Instruction> Instructions);
internal sealed record Module(List<Signature> Types, List<int> Functions,
    Dictionary<string, int> Exports, List<Function> Bodies)
{
    public Dictionary<string, byte> ExportKinds { get; } = new Dictionary<string, byte>();
    public MemoryDefinition? Memory { get; set; }
    public List<Global> Globals { get; } = new List<Global>();
    public List<DataSegment> Data { get; } = new List<DataSegment>();
    public int? Start { get; set; }
}
internal sealed record MemoryDefinition(int Minimum, int Maximum);
internal sealed record Global(bool Mutable, int InitialValue);
internal sealed record DataSegment(uint Offset, byte[] Bytes);
