namespace Wasm2Cs;
internal static class Validator
{
    public static void Validate(Module module, string className)
    {
        if (!IsIdentifier(className)) throw new WasmException("The WASM filename must be an ASCII C# identifier.");
        if (Reserved(className)) throw new WasmException("Module name conflicts with generated support members.");
        foreach (var export in module.Exports)
        {
            if (!IsIdentifier(export.Key) || export.Key == className || Reserved(export.Key))
                throw new WasmException($"Export '{export.Key}' cannot be represented as a C# method name.");
            byte kind = module.ExportKinds[export.Key];
            if (kind == 0 && export.Value >= module.FunctionCount) throw new WasmException("Invalid exported function index.");
            if (kind == 2 && (export.Value != 0 || module.Memory == null)) throw new WasmException("Invalid exported memory index.");
            if (kind == 3 && export.Value >= module.Globals.Count) throw new WasmException("Invalid exported global index.");
        }
        if (module.Data.Count != 0 && module.Memory == null) throw new WasmException("Data segments require memory.");
        if (module.Start.HasValue && (module.Start.Value >= module.FunctionCount ||
            module.FunctionSignature(module.Start.Value) != new Signature(0,0))) throw new WasmException("Invalid start function.");
        for (int i = 0; i < module.Bodies.Count; i++) ValidateBody(module, module.Bodies[i], i);
    }
    private sealed class Control(byte opcode, int height, int results)
    {
        public byte Opcode = opcode;
        public int Height = height, Results = results;
        public bool Unreachable, ElseSeen;
        public int BranchResults => Opcode == 0x03 ? 0 : Results;
    }
    private static void ValidateBody(Module module, Function function, int index)
    {
        int height = 0;
        var controls = new List<Control> { new Control(0xff, 0, function.Signature.Results) };
        foreach (var instruction in function.Instructions)
        {
            void Fail(string message) => throw new WasmException($"Function {index}, offset 0x{instruction.Offset:x}: {message}");
            var current = controls[controls.Count-1];
            void Pop()
            {
                if (height == current.Height && current.Unreachable) return;
                if (height <= current.Height) Fail("Operand stack underflow.");
                height--;
            }
            void PopResults(int count) { for(int p=0;p<count;p++) Pop(); }
            void Unreachable() { height = current.Height; current.Unreachable = true; }
            Control Target(int depth)
            {
                if (depth >= controls.Count) Fail("Invalid branch label.");
                return controls[controls.Count-1-depth];
            }
            switch (instruction.Opcode)
            {
                case 0x02: case 0x03: case 0x04:
                    if (instruction.Opcode == 0x04) Pop();
                    controls.Add(new Control(instruction.Opcode,height,instruction.Operand));
                    break;
                case 0x05:
                    if (current.Opcode != 0x04 || current.ElseSeen) Fail("Unexpected else.");
                    PopResults(current.Results);
                    if (height != current.Height) Fail("Invalid result stack height.");
                    height = current.Height; current.Unreachable = false; current.ElseSeen = true;
                    break;
                case 0x0b:
                    PopResults(current.Results);
                    if (height != current.Height) Fail("Invalid result stack height.");
                    if (current.Opcode == 0x04 && !current.ElseSeen && current.Results != 0) Fail("Result-producing if requires else.");
                    controls.RemoveAt(controls.Count-1);
                    height = current.Height + current.Results;
                    break;
                case 0x00: Unreachable(); break;
                case 0x0c: case 0x0d:
                    if (instruction.Opcode == 0x0d) Pop();
                    int arity = Target(instruction.Operand).BranchResults;
                    PopResults(arity);
                    if (instruction.Opcode == 0x0d) height += arity; else Unreachable();
                    break;
                case 0x0e:
                    Pop();
                    int results = Target(instruction.Targets![instruction.Targets.Length-1]).BranchResults;
                    foreach (int target in instruction.Targets)
                        if (Target(target).BranchResults != results) Fail("Branch table target arities differ.");
                    PopResults(results); Unreachable();
                    break;
                case 0x0f: PopResults(function.Signature.Results); Unreachable(); break;
                case 0x10:
                    if (instruction.Operand >= module.FunctionCount) Fail("Invalid called function index.");
                    var signature = module.FunctionSignature(instruction.Operand);
                    PopResults(signature.Parameters); height += signature.Results;
                    break;
                case 0x1a: Pop(); break;
                case 0x1b: Pop(); Pop(); Pop(); height++; break;
                case 0x20: case 0x21: case 0x22:
                    if (instruction.Operand >= function.Locals) Fail("Invalid local index.");
                    if (instruction.Opcode != 0x20) Pop();
                    if (instruction.Opcode != 0x21) height++;
                    break;
                case 0x41: height++; break;
                case 0x23: case 0x24:
                    if (instruction.Operand >= module.Globals.Count) Fail("Invalid global index.");
                    if (instruction.Opcode == 0x23) height++;
                    else { if (!module.Globals[instruction.Operand].Mutable) Fail("Cannot set immutable global."); Pop(); }
                    break;
                case 0x28: case 0x2c: case 0x2d: case 0x2e: case 0x2f:
                case 0x36: case 0x3a: case 0x3b:
                    if (module.Memory == null) Fail("Memory instruction requires memory.");
                    int alignment = MemoryOperations.Width(instruction.Opcode) == 4 ? 2 : MemoryOperations.Width(instruction.Opcode) == 2 ? 1 : 0;
                    if (instruction.Operand > alignment) Fail("Alignment exceeds natural alignment.");
                    Pop();
                    if (MemoryOperations.IsStore(instruction.Opcode)) Pop(); else height++;
                    break;
                case 0x3f: case 0x40:
                    if (module.Memory == null) Fail("Memory instruction requires memory.");
                    if (instruction.Opcode == 0x40) Pop();
                    height++; break;
                default:
                    int operands = I32Operations.Arity(instruction.Opcode);
                    PopResults(operands);
                    if (operands != 0) height++;
                    break;
            }
        }
        if (controls.Count != 0) throw new WasmException($"Function {index}: Unclosed control structure.");
    }
    private static bool IsIdentifier(string name) => name.Length > 0 &&
        (IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => IsLetter(c) || (c >= '0' && c <= '9') || c == '_');
    private static bool IsLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    private static bool Reserved(string name) => name.StartsWith("__wasm_", StringComparison.Ordinal) ||
        name == "TrapKind" || name == "TrapException" || name == "ReadMemory" || name == "WriteMemory" || name == "MemorySize";
}
