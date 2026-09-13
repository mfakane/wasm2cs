namespace Wasm2Cs;

// Canonical instructions keep the validated WASM instruction as source
// metadata while attaching the semantic operation that target backends consume.
// Control flow and calls remain source instructions for now; they are still
// represented in the same function-level IR and can be lowered independently.
internal sealed record CanonicalVectorConstant(byte[] Bytes);

internal enum CanonicalInstructionKind
{
    Source,
    StructuredControl,
    Call,
    Stack,
    Local,
    Constant,
    Global,
    Memory,
    BulkMemory,
    ScalarOperation,
    VectorConstant,
    VectorOperation
}

internal sealed record CanonicalInstruction(
    Instruction Source,
    CanonicalOperation? Operation = null,
    CanonicalVectorOperation? VectorOperation = null,
    CanonicalVectorConstant? VectorConstant = null)
{
    public CanonicalInstructionKind Kind => VectorConstant is not null ? CanonicalInstructionKind.VectorConstant :
        VectorOperation.HasValue ? CanonicalInstructionKind.VectorOperation :
        Operation is not null ? CanonicalInstructionKind.ScalarOperation : Classify(Source);

    private static CanonicalInstructionKind Classify(Instruction instruction) => instruction.Opcode switch
    {
        0x00 or 0x02 or 0x03 or 0x04 or 0x05 or 0x0b or 0x0c or 0x0d or 0x0e or 0x0f => CanonicalInstructionKind.StructuredControl,
        0x10 or 0x11 => CanonicalInstructionKind.Call,
        0x1a or 0x1b or 0x1c => CanonicalInstructionKind.Stack,
        0x20 or 0x21 or 0x22 => CanonicalInstructionKind.Local,
        0x41 or 0x42 or 0x43 or 0x44 => CanonicalInstructionKind.Constant,
        0x23 or 0x24 => CanonicalInstructionKind.Global,
        >= 0x28 and <= 0x3e or 0x3f or 0x40 => CanonicalInstructionKind.Memory,
        0xfc when instruction.Operand >= 8 => CanonicalInstructionKind.BulkMemory,
        _ => CanonicalInstructionKind.Source
    };
}

internal sealed record CanonicalFunction(
    Signature Signature,
    ValueType[] Locals,
    IReadOnlyList<CanonicalInstruction> Instructions);

internal sealed record CanonicalModule(
    Module Source,
    IReadOnlyList<CanonicalFunction> Bodies);

internal static class CanonicalLowering
{
    public static CanonicalModule Lower(Module module) => new(
        module,
        module.Bodies.Select(LowerFunction).ToArray());

    private static CanonicalFunction LowerFunction(Function function) => new(
        function.Signature,
        function.Locals,
        function.Instructions.Select(LowerInstruction).ToArray());

    private static CanonicalInstruction LowerInstruction(Instruction instruction)
    {
        if (instruction.Opcode == 0xfd)
        {
            if (instruction.Operand == 12)
                return new CanonicalInstruction(
                    instruction,
                    VectorConstant: new CanonicalVectorConstant((instruction.VectorConstant ?? Array.Empty<byte>()).ToArray()));
            if (instruction.Operand == 228)
                return new CanonicalInstruction(instruction, VectorOperation: CanonicalVectorOperation.AddF32x4);
            if (instruction.Operand == 230)
                return new CanonicalInstruction(instruction, VectorOperation: CanonicalVectorOperation.MultiplyF32x4);
        }

        // 0xfc uses the same byte prefix for numeric conversions (0..7) and
        // bulk-memory instructions (8..11). The latter stay structured source
        // instructions and must not be handed to the conversion backend.
        bool scalarOperation = instruction.Opcode != 0xfc || instruction.Operand < 8;
        return scalarOperation && CanonicalOperations.TryCreate(instruction.Opcode, instruction.Operand, out var operation) && operation is not null
            ? new CanonicalInstruction(instruction, Operation: operation)
            : new CanonicalInstruction(instruction);
    }
}
