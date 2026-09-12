namespace Wasm2Cs;

// Canonical instructions keep the validated WASM instruction as source
// metadata while attaching the semantic operation that target backends consume.
// Control flow and calls remain source instructions for now; they are still
// represented in the same function-level IR and can be lowered independently.
internal sealed record CanonicalVectorConstant(byte[] Bytes);

internal sealed record CanonicalInstruction(
    Instruction Source,
    CanonicalOperation? Operation = null,
    CanonicalVectorOperation? VectorOperation = null,
    CanonicalVectorConstant? VectorConstant = null);

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
