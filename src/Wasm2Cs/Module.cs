namespace Wasm2Cs;
internal sealed record Signature(int Parameters, int Results);
internal sealed record Instruction(byte Opcode, int Operand, int Offset, int[]? Targets = null, uint Immediate = 0);
internal sealed record Function(Signature Signature, int Locals, List<Instruction> Instructions);
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
internal sealed record Global(bool Mutable, int InitialValue);
internal sealed record DataSegment(uint Offset, byte[] Bytes);
