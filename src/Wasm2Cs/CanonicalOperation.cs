namespace Wasm2Cs;

// A validated scalar instruction is represented by its semantic operation and
// value types before a target backend chooses the generated expression. The
// original WASM opcode remains available for the operation-specific helper.
internal enum CanonicalOperationKind
{
    I32,
    I64,
    Float,
    Conversion
}

internal sealed record CanonicalOperation(
    CanonicalOperationKind Kind,
    byte Opcode,
    int Operand,
    ValueType[] Inputs,
    ValueType Result);

internal static class CanonicalOperations
{
    public static bool TryCreate(byte opcode, int operand, out CanonicalOperation? operation)
    {
        if (ConversionOperations.Supports(opcode))
        {
            operation = new CanonicalOperation(
                CanonicalOperationKind.Conversion,
                opcode,
                operand,
                new[] { ConversionOperations.InputType(opcode, operand) },
                ConversionOperations.ResultType(opcode, operand));
            return true;
        }

        int arity = FloatOperations.Arity(opcode);
        if (arity != 0)
        {
            ValueType input = FloatOperations.InputType(opcode);
            operation = new CanonicalOperation(
                CanonicalOperationKind.Float,
                opcode,
                operand,
                Enumerable.Repeat(input, arity).ToArray(),
                FloatOperations.ResultType(opcode));
            return true;
        }

        arity = I64Operations.Arity(opcode);
        if (arity != 0)
        {
            ValueType input = I64Operations.InputType(opcode);
            operation = new CanonicalOperation(
                CanonicalOperationKind.I64,
                opcode,
                operand,
                Enumerable.Repeat(input, arity).ToArray(),
                I64Operations.ResultType(opcode));
            return true;
        }

        arity = I32Operations.Arity(opcode);
        if (arity != 0)
        {
            operation = new CanonicalOperation(
                CanonicalOperationKind.I32,
                opcode,
                operand,
                Enumerable.Repeat(ValueType.I32, arity).ToArray(),
                ValueType.I32);
            return true;
        }

        operation = null;
        return false;
    }
}
