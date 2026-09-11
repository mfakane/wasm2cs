namespace Wasm2Cs;
internal static class Validator
{
    public static void Validate(Module module, string className)
    {
        if (!IsIdentifier(className)) throw new WasmException("The WASM filename must be an ASCII C# identifier.");
        foreach (var export in module.Exports)
        {
            if (!IsIdentifier(export.Key) || export.Key == className)
                throw new WasmException($"Export '{export.Key}' cannot be represented as a C# method name.");
            if (export.Value >= module.Bodies.Count) throw new WasmException("Invalid exported function index.");
        }
        for (int i = 0; i < module.Bodies.Count; i++) ValidateBody(module.Bodies[i], i);
    }
    private static void ValidateBody(Function function, int index)
    {
        int height = 0;
        bool returned = false;
        foreach (var instruction in function.Instructions)
        {
            void Fail(string message) => throw new WasmException($"Function {index}, offset 0x{instruction.Offset:x}: {message}");
            void Pop() { if (height == 0) Fail("Operand stack underflow."); height--; }
            if (instruction.Opcode == 0x0b)
            {
                if (!returned && height != function.Signature.Results) Fail("Invalid result stack height.");
                return;
            }
            if (returned) Fail("Instructions following return are not supported.");
            switch (instruction.Opcode)
            {
                case 0x0f:
                    if (function.Signature.Results == 1) Pop();
                    returned = true;
                    break;
                case 0x1a: Pop(); break;
                case 0x20: case 0x21: case 0x22:
                    if (instruction.Operand >= function.Locals) Fail("Invalid local index.");
                    if (instruction.Opcode != 0x20) Pop();
                    if (instruction.Opcode != 0x21) height++;
                    break;
                case 0x41: height++; break;
                case 0x6a: case 0x6b: case 0x6c: Pop(); Pop(); height++; break;
            }
        }
    }
    private static bool IsIdentifier(string name) => name.Length > 0 &&
        (IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => IsLetter(c) || (c >= '0' && c <= '9') || c == '_');
    private static bool IsLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
