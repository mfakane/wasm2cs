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
            (module.FunctionSignature(module.Start.Value).Parameters.Length != 0 || module.FunctionSignature(module.Start.Value).Results.Length != 0))) throw new WasmException("Invalid start function.");
        foreach (var global in module.Globals)
            if (global.Type != global.InitialValue.Type) throw new WasmException("Global initializer type mismatch.");
        for (int i = 0; i < module.Bodies.Count; i++) ValidateBody(module, module.Bodies[i], i);
    }
    private sealed class Control(byte opcode, int height, Signature signature)
    {
        public byte Opcode = opcode;
        public int Height = height;
        public Signature Signature = signature;
        public bool Unreachable, ElseSeen;
        public ValueType[] LabelTypes => Opcode == 0x03 ? Signature.Parameters : Signature.Results;
    }
    private static void ValidateBody(Module module, Function function, int index)
    {
        var stack = new List<ValueType?>();
        var controls = new List<Control> { new Control(0xff, 0, function.Signature) };
        foreach (var instruction in function.Instructions)
        {
            void Fail(string message) => throw new WasmException($"Function {index}, offset 0x{instruction.Offset:x}: {message}");
            var current = controls[controls.Count-1];
            var produced = new List<ValueType?>();
            ValueType? Pop(ValueType? expected = null)
            {
                if (stack.Count == current.Height && current.Unreachable) return null;
                if (stack.Count <= current.Height) Fail("Operand stack underflow.");
                var actual = stack[stack.Count-1];
                stack.RemoveAt(stack.Count-1);
                if (actual.HasValue && expected.HasValue && actual != expected)
                    Fail($"Operand type mismatch: expected {expected}, got {actual}.");
                return actual;
            }
            void PopTypes(ValueType[] types) { for (int p = types.Length-1; p >= 0; p--) Pop(types[p]); }
            void Push(ValueType? type) { stack.Add(type); produced.Add(type); }
            void PushTypes(ValueType[] types) { foreach (var type in types) Push(type); }
            void Unreachable() { stack.RemoveRange(current.Height, stack.Count-current.Height); current.Unreachable = true; }
            void EndArm()
            {
                PopTypes(current.Signature.Results);
                if (stack.Count != current.Height) Fail("Invalid result stack height.");
            }
            Control Target(int depth)
            {
                if (depth >= controls.Count) Fail("Invalid branch label.");
                return controls[controls.Count-1-depth];
            }
            switch (instruction.Opcode)
            {
                case 0x02: case 0x03: case 0x04:
                    if (instruction.Opcode == 0x04) Pop(ValueType.I32);
                    var block = instruction.BlockType!;
                    PopTypes(block.Parameters);
                    controls.Add(new Control(instruction.Opcode, stack.Count, block));
                    PushTypes(block.Parameters);
                    break;
                case 0x05:
                    if (current.Opcode != 0x04 || current.ElseSeen) Fail("Unexpected else.");
                    EndArm();
                    current.Unreachable = false; current.ElseSeen = true;
                    PushTypes(current.Signature.Parameters);
                    break;
                case 0x0b:
                    EndArm();
                    if (current.Opcode == 0x04 && !current.ElseSeen &&
                        !current.Signature.Parameters.SequenceEqual(current.Signature.Results))
                        Fail("If without else requires matching parameter and result types.");
                    controls.RemoveAt(controls.Count-1);
                    PushTypes(current.Signature.Results);
                    break;
                case 0x00: Unreachable(); break;
                case 0x0c: case 0x0d:
                    if (instruction.Opcode == 0x0d) Pop(ValueType.I32);
                    var labelTypes = Target(instruction.Operand).LabelTypes;
                    PopTypes(labelTypes);
                    if (instruction.Opcode == 0x0d) PushTypes(labelTypes); else Unreachable();
                    break;
                case 0x0e:
                    Pop(ValueType.I32);
                    var results = Target(instruction.Targets![instruction.Targets.Length-1]).LabelTypes;
                    var branchValues = new ValueType?[results.Length];
                    for (int p = results.Length-1; p >= 0; p--) branchValues[p] = Pop();
                    foreach (int target in instruction.Targets)
                    {
                        var targetTypes = Target(target).LabelTypes;
                        if (targetTypes.Length != results.Length) Fail("Branch table target arities differ.");
                        for (int p = 0; p < results.Length; p++)
                            if (branchValues[p].HasValue && branchValues[p] != targetTypes[p])
                                Fail("Branch table target type mismatch.");
                    }
                    Unreachable();
                    break;
                case 0x0f: PopTypes(function.Signature.Results); Unreachable(); break;
                case 0x10:
                    if (instruction.Operand >= module.FunctionCount) Fail("Invalid called function index.");
                    var signature = module.FunctionSignature(instruction.Operand);
                    PopTypes(signature.Parameters); PushTypes(signature.Results);
                    break;
                case 0x1a: Pop(); break;
                case 0x1b: case 0x1c:
                    Pop(ValueType.I32);
                    var right = Pop(instruction.SelectType);
                    var left = Pop(instruction.SelectType);
                    if (instruction.Opcode == 0x1b &&
                        (left == ValueType.FuncRef || left == ValueType.ExternRef || right == ValueType.FuncRef || right == ValueType.ExternRef))
                        Fail("Untyped select requires numeric operands.");
                    if (left.HasValue && right.HasValue && left != right) Fail("Select operand type mismatch.");
                    Push(instruction.SelectType ?? left ?? right);
                    break;
                case 0x20: case 0x21: case 0x22:
                    if (instruction.Operand >= function.Locals.Length) Fail("Invalid local index.");
                    var localType = function.Locals[instruction.Operand];
                    if (instruction.Opcode != 0x20) Pop(localType);
                    if (instruction.Opcode != 0x21) Push(localType);
                    break;
                case 0x41: case 0x42: case 0x43: case 0x44: Push(instruction.Constant!.Type); break;
                case 0x23: case 0x24:
                    if (instruction.Operand >= module.Globals.Count) Fail("Invalid global index.");
                    var global = module.Globals[instruction.Operand];
                    if (instruction.Opcode == 0x23) Push(global.Type);
                    else { if (!global.Mutable) Fail("Cannot set immutable global."); Pop(global.Type); }
                    break;
                case 0x2a: case 0x2b:
                case 0x28: case 0x2c: case 0x2d: case 0x2e: case 0x2f:
                case 0x29: case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
                case 0x38: case 0x39:
                case 0x36: case 0x3a: case 0x3b:
                case 0x37: case 0x3c: case 0x3d: case 0x3e:
                    if (module.Memory == null) Fail("Memory instruction requires memory.");
                    int alignment = MemoryOperations.Width(instruction.Opcode) == 8 ? 3 : MemoryOperations.Width(instruction.Opcode) == 4 ? 2 : MemoryOperations.Width(instruction.Opcode) == 2 ? 1 : 0;
                    if (instruction.Operand > alignment) Fail("Alignment exceeds natural alignment.");
                    var memoryType = MemoryOperations.Type(instruction.Opcode);
                    if (MemoryOperations.IsStore(instruction.Opcode)) { Pop(memoryType); Pop(ValueType.I32); }
                    else { Pop(ValueType.I32); Push(memoryType); }
                    break;
                case 0x3f: case 0x40:
                    if (module.Memory == null) Fail("Memory instruction requires memory.");
                    if (instruction.Opcode == 0x40) Pop(ValueType.I32);
                    Push(ValueType.I32); break;
                default:
                    if (FloatOperations.Arity(instruction.Opcode) != 0)
                    {
                        for (int p = 0; p < FloatOperations.Arity(instruction.Opcode); p++) Pop(FloatOperations.InputType(instruction.Opcode));
                        Push(FloatOperations.ResultType(instruction.Opcode));
                        break;
                    }
                    int operands = I64Operations.Arity(instruction.Opcode);
                    if (operands != 0)
                    {
                        for (int p = 0; p < operands; p++) Pop(I64Operations.InputType(instruction.Opcode));
                        Push(I64Operations.ResultType(instruction.Opcode));
                    }
                    else
                    {
                        operands = I32Operations.Arity(instruction.Opcode);
                        for (int p = 0; p < operands; p++) Pop(ValueType.I32);
                        if (operands != 0) Push(ValueType.I32);
                    }
                    break;
            }
            instruction.ResultTypes = produced.ToArray();
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
