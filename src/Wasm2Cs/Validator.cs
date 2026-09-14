namespace Wasm2Cs;
internal static class Validator
{
    public static void Validate(Module module, string className, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsIdentifier(className)) throw new WasmException("The WASM filename must be an ASCII C# identifier.");
        if (Reserved(className)) throw new WasmException("Module name conflicts with generated support members.");
        foreach (var export in module.Exports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsIdentifier(export.Key) || export.Key == className || Reserved(export.Key))
                throw new WasmException($"Export '{export.Key}' cannot be represented as a C# method name.");
            byte kind = module.ExportKinds[export.Key];
            if (kind == 0 && export.Value >= module.FunctionCount) throw new WasmException("Invalid exported function index.");
            if (kind == 1 && export.Value >= module.Tables.Count) throw new WasmException("Invalid exported table index.");
            if (kind == 2 && (export.Value != 0 || module.Memory == null)) throw new WasmException("Invalid exported memory index.");
            if (kind == 3 && export.Value >= module.Globals.Count) throw new WasmException("Invalid exported global index.");
            if (kind == 4 && export.Value >= module.Tags.Count) throw new WasmException("Invalid exported tag index.");
        }
        foreach (var table in module.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (table.Minimum < 0 || table.Maximum.HasValue && (table.Maximum.Value < table.Minimum))
                throw new WasmException("Invalid table limits.");
            if (table.ElementType != ValueType.FuncRef && table.ElementType != ValueType.ExternRef)
                throw new WasmException("Unsupported table element type.");
        }
        foreach (var tag in module.Tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tag.Signature.Results.Length != 0)
                throw new WasmException("Tag types must not have results.");
        }
        foreach (var element in module.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.ElementType != ValueType.FuncRef && element.ElementType != ValueType.ExternRef)
                throw new WasmException("Unsupported element type.");
            foreach (var value in element.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.Type != element.ElementType) throw new WasmException("Element expression type mismatch.");
                if (value.FunctionIndex.HasValue && (value.Type != ValueType.FuncRef ||
                    value.FunctionIndex.Value < 0 || value.FunctionIndex.Value >= module.FunctionCount))
                    throw new WasmException("Invalid element function index.");
            }
            if (!element.Passive && !element.Declarative)
            {
                if (element.TableIndex >= module.Tables.Count) throw new WasmException("Invalid element table index.");
                if (module.Tables[element.TableIndex].ElementType != element.ElementType)
                    throw new WasmException("Element type does not match table type.");
                if (element.OffsetExpression == null || element.OffsetExpression.Type != ValueType.I32)
                    throw new WasmException("Element offset must have type i32.");
            }
        }
        if (module.Data.Any(data => !data.Passive) && module.Memory == null)
            throw new WasmException("Active data segments require memory.");
        if (module.DataCount.HasValue && module.DataCount.Value != module.Data.Count)
            throw new WasmException("Data count and data segment count differ.");
        if (module.Start.HasValue && (module.Start.Value >= module.FunctionCount ||
            (module.FunctionSignature(module.Start.Value).Parameters.Length != 0 || module.FunctionSignature(module.Start.Value).Results.Length != 0))) throw new WasmException("Invalid start function.");
        foreach (var global in module.Globals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (global.Type == ValueType.V128)
                throw new WasmException("v128 globals are not supported by the current host boundary.");
            if (global.Imported)
            {
                if (global.InitialValue != null) throw new WasmException("Imported global cannot have an initializer.");
                continue;
            }
            if (global.InitialValue == null || global.Type != global.InitialValue.Type)
                throw new WasmException("Global initializer type mismatch.");
            if (global.InitialValue.GlobalIndex.HasValue)
            {
                int index = global.InitialValue.GlobalIndex.Value;
                if (index >= module.Globals.Count || !module.Globals[index].Imported || module.Globals[index].Mutable)
                    throw new WasmException("Global initializer must use an imported immutable global.");
            }
        }
        foreach (var data in module.Data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.OffsetExpression != null && data.OffsetExpression.Type != ValueType.I32)
                throw new WasmException("Data offset must have type i32.");
        }
        bool requiresDataCount = module.Bodies.Any(f => f.Instructions.Any(i => i.Opcode == 0xfc && (i.Operand == 8 || i.Operand == 9)));
        if (requiresDataCount && !module.DataCount.HasValue)
            throw new WasmException("Bulk data instructions require a data count section.");
        for (int i = 0; i < module.Bodies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBody(module, module.Bodies[i], i, cancellationToken);
        }
    }
    private sealed class Control(byte opcode, int height, Signature signature)
    {
        public byte Opcode = opcode;
        public int Height = height;
        public Signature Signature = signature;
        public bool Unreachable, ElseSeen, CatchSeen, CatchAllSeen;
        public ValueType[] LabelTypes => Opcode == 0x03 ? Signature.Parameters : Signature.Results;
    }
    private static void ValidateBody(Module module, Function function, int index, CancellationToken cancellationToken)
    {
        var stack = new List<ValueType?>();
        var controls = new List<Control> { new Control(0xff, 0, function.Signature) };
        foreach (var instruction in function.Instructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                case 0x02: case 0x03: case 0x04: case 0x06:
                    if (instruction.Opcode == 0x04) Pop(ValueType.I32);
                    var block = instruction.BlockType!;
                    PopTypes(block.Parameters);
                    controls.Add(new Control(instruction.Opcode, stack.Count, block));
                    PushTypes(block.Parameters);
                    break;
                case 0x07:
                    if (current.Opcode != 0x06 || current.CatchAllSeen) Fail("Unexpected catch.");
                    EndArm();
                    if (instruction.Operand >= module.Tags.Count) Fail("Invalid catch tag index.");
                    current.CatchSeen = true; current.Unreachable = false;
                    PushTypes(module.Tags[instruction.Operand].Signature.Parameters);
                    break;
                case 0x19:
                    if (current.Opcode != 0x06 || current.CatchAllSeen) Fail("Unexpected catch_all.");
                    EndArm();
                    current.CatchSeen = true; current.CatchAllSeen = true; current.Unreachable = false;
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
                    if (current.Opcode == 0x06 && !current.CatchSeen) Fail("Try requires a catch.");
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
                case 0x08:
                    if (instruction.Operand >= module.Tags.Count) Fail("Invalid thrown tag index.");
                    PopTypes(module.Tags[instruction.Operand].Signature.Parameters);
                    Unreachable();
                    break;
                case 0x09:
                    var rethrowTarget = Target(instruction.Operand);
                    if (rethrowTarget.Opcode != 0x06 || !rethrowTarget.CatchSeen)
                        Fail("Rethrow must target a caught exception.");
                    Unreachable();
                    break;
                case 0x18:
                    Fail("Legacy delegate is not supported.");
                    break;
                case 0x11:
                    if (instruction.Operand >= module.Types.Count) Fail("Invalid indirect call type index.");
                    if (instruction.Immediate >= module.Tables.Count) Fail("Invalid indirect call table index.");
                    if (module.Tables[(int)instruction.Immediate].ElementType != ValueType.FuncRef)
                        Fail("Indirect calls require a funcref table.");
                    Pop(ValueType.I32);
                    var indirectSignature = module.Types[instruction.Operand];
                    PopTypes(indirectSignature.Parameters); PushTypes(indirectSignature.Results);
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
                case 0x25:
                    Table(module, instruction.Operand, Fail, out var getTable);
                    Pop(ValueType.I32); Push(getTable.ElementType); break;
                case 0x26:
                    Table(module, instruction.Operand, Fail, out var setTable);
                    Pop(setTable.ElementType); Pop(ValueType.I32); break;
                case 0xd0:
                    var nullReference = instruction.Reference;
                    if (nullReference == null) Fail("Missing ref.null type.");
                    if (nullReference!.Type != ValueType.FuncRef && nullReference.Type != ValueType.ExternRef)
                        Fail("Unsupported ref.null type.");
                    Push(nullReference.Type);
                    break;
                case 0xd1:
                    var reference = Pop();
                    if (reference != ValueType.FuncRef && reference != ValueType.ExternRef && reference.HasValue)
                        Fail("ref.is_null requires a reference.");
                    Push(ValueType.I32);
                    break;
                case 0xd2:
                    if (instruction.Reference?.Type != ValueType.FuncRef || !instruction.Reference.FunctionIndex.HasValue ||
                        instruction.Reference.FunctionIndex.Value >= module.FunctionCount)
                        Fail("Invalid ref.func function index.");
                    Push(ValueType.FuncRef);
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
                case 0xfc when instruction.Operand >= 8:
                    if (instruction.Operand <= 11 && module.Memory == null) Fail("Memory instruction requires memory.");
                    switch (instruction.Operand)
                    {
                        case 8:
                            if (instruction.Secondary != 0) Fail("Invalid memory index.");
                            if (instruction.Immediate >= module.Data.Count) Fail("Invalid data segment index.");
                            if (!module.Data[(int)instruction.Immediate].Passive) Fail("memory.init requires a passive data segment.");
                            Pop(ValueType.I32); Pop(ValueType.I32); Pop(ValueType.I32);
                            break;
                        case 9:
                            if (instruction.Immediate >= module.Data.Count) Fail("Invalid data segment index.");
                            if (!module.Data[(int)instruction.Immediate].Passive) Fail("data.drop requires a passive data segment.");
                            break;
                        case 10:
                            if (instruction.Immediate != 0 || instruction.Secondary != 0) Fail("Invalid memory index.");
                            Pop(ValueType.I32); Pop(ValueType.I32); Pop(ValueType.I32);
                            break;
                        case 11:
                            if (module.Memory == null) Fail("Memory instruction requires memory.");
                            if (instruction.Secondary != 0) Fail("Invalid memory index.");
                            Pop(ValueType.I32); Pop(ValueType.I32); Pop(ValueType.I32);
                            break;
                        case 12:
                            if (instruction.Immediate >= module.Elements.Count) Fail("Invalid element segment index.");
                            if (instruction.Secondary >= module.Tables.Count) Fail("Invalid table index.");
                            var initElement = module.Elements[(int)instruction.Immediate];
                            if (!initElement.Passive) Fail("table.init requires a passive element segment.");
                            if (module.Tables[(int)instruction.Secondary].ElementType != initElement.ElementType)
                                Fail("Element type does not match table type.");
                            Pop(ValueType.I32); Pop(ValueType.I32); Pop(ValueType.I32);
                            break;
                        case 13:
                            if (instruction.Immediate >= module.Elements.Count || !module.Elements[(int)instruction.Immediate].Passive)
                                Fail("elem.drop requires a passive element segment.");
                            break;
                        case 14:
                            if (instruction.Immediate >= module.Tables.Count || instruction.Secondary >= module.Tables.Count)
                                Fail("Invalid table index.");
                            if (module.Tables[(int)instruction.Immediate].ElementType != module.Tables[(int)instruction.Secondary].ElementType)
                                Fail("table.copy requires matching table types.");
                            Pop(ValueType.I32); Pop(ValueType.I32); Pop(ValueType.I32);
                            break;
                        case 15:
                            Table(module, (int)instruction.Secondary, Fail, out var growTable);
                            Pop(growTable.ElementType); Pop(ValueType.I32); Push(ValueType.I32);
                            break;
                        case 16:
                            Table(module, (int)instruction.Secondary, Fail, out _);
                            Push(ValueType.I32);
                            break;
                        case 17:
                            Table(module, (int)instruction.Secondary, Fail, out var fillTable);
                            Pop(ValueType.I32); Pop(fillTable.ElementType); Pop(ValueType.I32);
                            break;
                        default: Fail("Unsupported bulk memory instruction."); break;
                    }
                    break;
                case 0xfd when instruction.Operand == 12:
                    if (instruction.VectorConstant is null || instruction.VectorConstant.Length != 16)
                        Fail("Invalid v128.const immediate.");
                    Push(ValueType.V128);
                    break;
                case 0xfd when instruction.Operand is 228 or 230:
                    Pop(ValueType.V128); Pop(ValueType.V128); Push(ValueType.V128);
                    break;
                default:
                    if (CanonicalOperations.TryCreate(instruction.Opcode, instruction.Operand, out var operation) && operation is not null)
                    {
                        for (int p = operation.Inputs.Length - 1; p >= 0; p--) Pop(operation.Inputs[p]);
                        Push(operation.Result);
                    }
                    break;
            }
            instruction.ResultTypes = produced.ToArray();
        }
        if (controls.Count != 0) throw new WasmException($"Function {index}: Unclosed control structure.");
    }
    private static void Table(Module module, int index, Action<string> fail, out TableDefinition table)
    {
        if (index < 0 || index >= module.Tables.Count) { fail("Invalid table index."); table = new TableDefinition(ValueType.FuncRef, 0, 0); return; }
        table = module.Tables[index];
    }
    private static bool IsIdentifier(string name) => name.Length > 0 &&
        (IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => IsLetter(c) || (c >= '0' && c <= '9') || c == '_');
    private static bool IsLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    private static bool Reserved(string name) => name.StartsWith("__wasm_", StringComparison.Ordinal) ||
        name == "TrapKind" || name == "TrapException" || name == "ReadMemory" || name == "WriteMemory" || name == "MemorySize";
}
