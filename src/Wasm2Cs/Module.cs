namespace Wasm2Cs;
internal sealed record Signature(int Parameters, int Results);
internal sealed record Instruction(byte Opcode, int Operand, int Offset, int[]? Targets = null);
internal sealed record Function(Signature Signature, int Locals, List<Instruction> Instructions);
internal sealed record Module(List<Signature> Types, List<int> Functions,
    Dictionary<string, int> Exports, List<Function> Bodies);
