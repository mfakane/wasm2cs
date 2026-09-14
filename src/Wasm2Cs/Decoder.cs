namespace Wasm2Cs;
internal static class Decoder
{
    public static Module Decode(byte[] wasm)
    {
        var reader = new Reader(wasm);
        foreach (var expected in new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 })
            if (reader.Byte() != expected) throw new WasmException("Invalid WASM magic or version.");

        var types = new List<Signature>();
        var functions = new List<int>();
        var exports = new Dictionary<string, int>(StringComparer.Ordinal);
        var bodies = new List<Reader>();
        var decoded = new List<Function>();
        var module = new Module(types, functions, exports, decoded);
        int previousOrder = 0;
        while (!reader.End)
        {
            var id = reader.Byte();
            var section = reader.Slice(reader.Count());
            if (id == 0)
            {
                section.Name(); // Custom sections still require a well-formed name.
                continue;
            }
            int order = SectionOrder(id);
            if (order <= previousOrder) throw new WasmException("Duplicate or out-of-order section.");
            previousOrder = order;
            switch (id)
            {
                case 1:
                    for (int i = 0, count = section.Count(); i < count; i++)
                    {
                        if (section.Byte() != 0x60) throw new WasmException("Only function types are supported.");
                        types.Add(new(section.ValueTypes(), section.ValueTypes()));
                    }
                    break;
                case 2:
                    for (int i = 0, count = section.Count(); i < count; i++)
                    {
                        string importModule = section.Name(), importName = section.Name();
                        byte kind = section.Byte();
                        switch (kind)
                        {
                            case 0:
                                int type = section.Count();
                                if (type >= types.Count) throw new WasmException("Invalid imported function type index.");
                                module.ImportBindings.Add(new ImportBinding(kind, module.Imports.Count, importModule, importName));
                                module.Imports.Add(new FunctionImport(importModule, importName, type));
                                break;
                            case 1:
                                int tableIndex = module.Tables.Count;
                                module.Tables.Add(Table(importModule, importName, section));
                                module.ImportBindings.Add(new ImportBinding(kind, tableIndex, importModule, importName));
                                break;
                            case 2:
                                if (module.Memory != null) throw new WasmException("Only one memory32 is supported.");
                                module.Memory = Memory(importModule, importName, section);
                                module.ImportBindings.Add(new ImportBinding(kind, 0, importModule, importName));
                                break;
                            case 3:
                                var globalType = section.ValueType();
                                byte mutable = section.Byte();
                                if (mutable > 1) throw new WasmException("Invalid global mutability.");
                                int globalIndex = module.Globals.Count;
                                module.Globals.Add(new Global(globalType, mutable == 1, null, true, importModule, importName));
                                module.ImportBindings.Add(new ImportBinding(kind, globalIndex, importModule, importName));
                                break;
                            case 4:
                                if (section.Byte() != 0) throw new WasmException("Unsupported tag attribute.");
                                int tagType = section.Count();
                                if (tagType >= types.Count) throw new WasmException("Invalid imported tag type index.");
                                module.Tags.Add(new TagDefinition(types[tagType], true, importModule, importName));
                                module.ImportBindings.Add(new ImportBinding(kind, module.Tags.Count - 1, importModule, importName));
                                break;
                            default:
                                throw new WasmException("Unsupported import kind.");
                        }
                    }
                    break;
                case 3:
                    for (int i = 0, count = section.Count(); i < count; i++)
                        functions.Add(section.Count());
                    break;
                case 4:
                    for (int i = 0, count = section.Count(); i < count; i++)
                        module.Tables.Add(Table(null, null, section));
                    break;
                case 7:
                    for (int i = 0, count = section.Count(); i < count; i++)
                    {
                        string name = section.Name();
                        byte kind = section.Byte();
                        if (kind != 0 && kind != 1 && kind != 2 && kind != 3 && kind != 4) throw new WasmException("Unsupported export kind.");
                        if (exports.ContainsKey(name)) throw new WasmException("Duplicate export name.");
                        exports.Add(name, section.Count());
                        module.ExportKinds.Add(name, kind);
                    }
                    break;
                case 5:
                    if (section.Count() > 1) throw new WasmException("Only one memory32 is supported.");
                    if (!section.End)
                    {
                        if (module.Memory != null) throw new WasmException("Only one memory32 is supported.");
                        module.Memory = Memory(null, null, section);
                    }
                    break;
                case 6:
                    for(int i=0,count=section.Count();i<count;i++)
                    {
                        var globalType = section.ValueType();
                        byte mutable = section.Byte();
                        if (mutable > 1) throw new WasmException("Invalid global mutability.");
                        module.Globals.Add(new Global(globalType, mutable == 1, Constant(section, module)));
                    }
                    break;
                case 13:
                    for (int i = 0, count = section.Count(); i < count; i++)
                    {
                        if (section.Byte() != 0) throw new WasmException("Unsupported tag attribute.");
                        int tagType = section.Count();
                        if (tagType >= types.Count) throw new WasmException("Invalid tag type index.");
                        module.Tags.Add(new TagDefinition(types[tagType]));
                    }
                    break;
                case 8: module.Start = section.Count(); break;
                case 9:
                    for (int i = 0, count = section.Count(); i < count; i++)
                        module.Elements.Add(Element(section, module));
                    break;
                case 12: module.DataCount = section.Count(); break;
                case 10:
                    for (int i = 0, count = section.Count(); i < count; i++)
                        bodies.Add(section.Slice(section.Count()));
                    break;
                case 11:
                    for(int i=0,count=section.Count();i<count;i++)
                    {
                        int flags = section.Count();
                        if (flags != 0 && flags != 1 && flags != 2) throw new WasmException("Invalid data segment flags.");
                        if (flags != 1 && module.Memory == null) throw new WasmException("Active data segments require memory.");
                        ConstantValue? initialOffset = null;
                        if (flags == 2 && section.Count() != 0) throw new WasmException("Invalid data memory index.");
                        if (flags != 1) initialOffset = Constant(section, module);
                        if (initialOffset != null && initialOffset.Type != ValueType.I32)
                            throw new WasmException("Data offset must have type i32.");
                        uint offset = initialOffset != null ? (uint)initialOffset.Bits : 0;
                        module.Data.Add(new DataSegment(offset, section.Bytes(section.Count()), flags == 1, initialOffset));
                    }
                    break;
                default:
                    throw new WasmException($"Unsupported WASM section {id}.");
            }
            section.RequireEnd();
        }
        if (functions.Count != bodies.Count) throw new WasmException("Function and code counts differ.");
        if (module.DataCount.HasValue && module.DataCount.Value != module.Data.Count)
            throw new WasmException("Data count and data segment count differ.");

        for (int i = 0; i < functions.Count; i++)
        {
            if (functions[i] >= types.Count) throw new WasmException("Invalid function type index.");
            try { decoded.Add(DecodeBody(bodies[i], types[functions[i]], types)); }
            catch (WasmException e) { throw new WasmException($"Function {i}: {e.Message}"); }
        }
        return module;
    }
    private static int SectionOrder(byte id) => id switch
    {
        1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, 13 => 6, 6 => 7,
        7 => 8, 8 => 9, 9 => 10, 12 => 11, 10 => 12, 11 => 13,
        _ => 100
    };

    private static MemoryDefinition Memory(string? moduleName, string? name, Reader section)
    {
        int flags = section.Count();
        if (flags != 0 && flags != 1) throw new WasmException("Shared/memory64 memories are not supported.");
        int minimum = section.Count();
        int? maximum = flags == 1 ? section.Count() : null;
        if (minimum > 65536 || maximum.HasValue && (maximum.Value > 65536 || minimum > maximum.Value))
            throw new WasmException("Invalid memory limits.");
        if (minimum > 4096) throw new WasmException("Initial memory exceeds 256 MiB implementation limit.");
        return new MemoryDefinition(minimum, maximum, moduleName != null, moduleName, name);
    }

    private static TableDefinition Table(string? moduleName, string? name, Reader section)
    {
        var elementType = section.ValueType();
        if (elementType != ValueType.FuncRef && elementType != ValueType.ExternRef)
            throw new WasmException("Tables must contain funcref or externref values.");
        int flags = section.Count();
        if (flags != 0 && flags != 1) throw new WasmException("Invalid table limits.");
        int minimum = section.Count();
        int? maximum = flags == 1 ? section.Count() : null;
        if (maximum.HasValue && minimum > maximum.Value)
            throw new WasmException("Invalid table limits.");
        return new TableDefinition(elementType, minimum, maximum, moduleName != null, moduleName, name);
    }

    private static ConstantValue Constant(Reader reader, Module module)
    {
        var value = ReadConstant(reader, reader.Byte(), module);
        if (reader.Byte() != 0x0b) throw new WasmException("Invalid initializer expression.");
        return value;
    }
    private static ConstantValue ReadConstant(Reader reader, byte opcode, Module? module = null) => opcode switch
    {
        0x41 => new ConstantValue(ValueType.I32, unchecked((uint)reader.SignedI32())),
        0x42 => new ConstantValue(ValueType.I64, unchecked((ulong)reader.SignedI64())),
        0x43 => new ConstantValue(ValueType.F32, reader.FloatBits(4)),
        0x44 => new ConstantValue(ValueType.F64, reader.FloatBits(8)),
        0x23 when module != null => ImportedGlobalConstant(reader, module),
        _ => throw new WasmException($"Unsupported constant expression opcode 0x{opcode:x2}.")
    };

    private static ConstantValue ImportedGlobalConstant(Reader reader, Module module)
    {
        int index = reader.Count();
        if (index >= module.Globals.Count || !module.Globals[index].Imported || module.Globals[index].Mutable)
            throw new WasmException("Constant expression must use an imported immutable global.");
        return new ConstantValue(module.Globals[index].Type, 0, index);
    }

    private static ElementSegment Element(Reader section, Module module)
    {
        int flags = section.Count();
        int tableIndex = 0;
        ConstantValue? offset = null;
        bool passive = false, declarative = false;
        ValueType elementType;
        ReferenceValue[] values;
        switch (flags)
        {
            case 0:
                offset = Constant(section, module);
                elementType = ValueType.FuncRef;
                values = FunctionElements(section);
                break;
            case 1:
                RequireElemKind(section);
                passive = true;
                elementType = ValueType.FuncRef;
                values = FunctionElements(section);
                break;
            case 2:
                tableIndex = section.Count();
                offset = Constant(section, module);
                RequireElemKind(section);
                elementType = ValueType.FuncRef;
                values = FunctionElements(section);
                break;
            case 3:
                RequireElemKind(section);
                declarative = true;
                elementType = ValueType.FuncRef;
                values = FunctionElements(section);
                break;
            case 4:
                offset = Constant(section, module);
                elementType = section.ValueType();
                values = ElementExpressions(section, elementType);
                break;
            case 5:
                passive = true;
                elementType = section.ValueType();
                values = ElementExpressions(section, elementType);
                break;
            case 6:
                tableIndex = section.Count();
                offset = Constant(section, module);
                elementType = section.ValueType();
                values = ElementExpressions(section, elementType);
                break;
            case 7:
                declarative = true;
                elementType = section.ValueType();
                values = ElementExpressions(section, elementType);
                break;
            default:
                throw new WasmException("Unsupported element segment kind.");
        }
        if (elementType != ValueType.FuncRef && elementType != ValueType.ExternRef)
            throw new WasmException("Element segments must contain funcref or externref values.");
        return new ElementSegment(tableIndex, elementType, values, passive, declarative, offset);
    }

    private static void RequireElemKind(Reader section)
    {
        if (section.Byte() != 0) throw new WasmException("Only funcref element segments are supported.");
    }

    private static ReferenceValue[] FunctionElements(Reader section)
    {
        int count = section.Count();
        var values = new ReferenceValue[count];
        for (int i = 0; i < count; i++) values[i] = new ReferenceValue(ValueType.FuncRef, section.Count());
        return values;
    }

    private static ReferenceValue[] ElementExpressions(Reader section, ValueType elementType)
    {
        int count = section.Count();
        var values = new ReferenceValue[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ElementExpression(section);
            if (values[i].Type != elementType) throw new WasmException("Element expression type mismatch.");
        }
        return values;
    }

    private static ReferenceValue ElementExpression(Reader section)
    {
        byte opcode = section.Byte();
        ReferenceValue value = opcode switch
        {
            0xd0 => new ReferenceValue(section.ValueType(), null),
            0xd2 => new ReferenceValue(ValueType.FuncRef, section.Count()),
            _ => throw new WasmException("Unsupported element expression.")
        };
        if (section.Byte() != 0x0b) throw new WasmException("Invalid element expression.");
        return value;
    }

    private static Function DecodeBody(Reader body, Signature signature, List<Signature> types)
    {
        var locals = new List<ValueType>(signature.Parameters);
        for (int i = 0, groups = body.Count(); i < groups; i++)
        {
            int count = body.Count();
            var type = body.ValueType();
            if ((long)locals.Count + count > 100_000) throw new WasmException("Too many locals (limit: 100000).");
            for (int j = 0; j < count; j++) locals.Add(type);
        }
        var instructions = new List<Instruction>();
        int depth = 0;
        while (!body.End)
        {
            int offset = body.Offset;
            byte opcode = body.Byte();
            int operand;
            int[]? targets = null;
            uint immediate = 0;
            uint secondary = 0;
            byte[]? vectorConstant = null;
            ReferenceValue? reference = null;
            Signature? blockType = null;
            ValueType? selectType = null;
            ConstantValue? constant = null;
            switch (opcode)
            {
                case 0x02: case 0x03: case 0x04: case 0x06:
                    blockType = body.BlockType(types);
                    operand = 0; depth++; break;
                case 0x1c:
                    if (body.Count() != 1) throw new WasmException("Typed select requires exactly one value type.");
                    selectType = body.ValueType(); operand = 0; break;
                case 0x07: case 0x08: case 0x09: case 0x0c: case 0x0d: case 0x10: case 0x18:
                    operand = body.Count(); break;
                case 0x19: operand = 0; break;
                case 0x11:
                    operand = body.Count();
                    immediate = (uint)body.Count();
                    break;
                case 0x0e:
                    int count = body.Count();
                    if (count >= body.Remaining) throw new WasmException("Branch table extends past body boundary.");
                    targets = new int[count+1];
                    for(int t=0;t<targets.Length;t++) targets[t] = body.Count();
                    operand = 0; break;
                case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: operand = body.Count(); break;
                case 0x2a: case 0x2b:
                case 0x28: case 0x2c: case 0x2d: case 0x2e: case 0x2f:
                case 0x29: case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
                case 0x38: case 0x39:
                case 0x36: case 0x3a: case 0x3b:
                case 0x37: case 0x3c: case 0x3d: case 0x3e:
                    operand = body.Count(); immediate = body.UnsignedI32(); break;
                case 0x3f: case 0x40:
                    if (body.Byte() != 0) throw new WasmException("Invalid memory index.");
                    operand = 0; break;
                case 0x25: case 0x26:
                    operand = body.Count();
                    break;
                case 0x41: case 0x42: case 0x43: case 0x44: constant = ReadConstant(body, opcode); operand = 0; break;
                case 0xd0: reference = new ReferenceValue(body.ValueType(), null); operand = 0; break;
                case 0xd1: operand = 0; break;
                case 0xd2: reference = new ReferenceValue(ValueType.FuncRef, body.Count()); operand = 0; break;
                case 0x00: case 0x01: case 0x05: case 0x0b: case 0x0f: case 0x1a: case 0x1b:
                case 0x6a: case 0x6b: case 0x6c: operand = 0; break;
                case 0xfc:
                    operand = body.Count();
                    switch (operand)
                    {
                        case <= 7: break;
                        case 8: immediate = (uint)body.Count(); secondary = (uint)body.Count(); break; // data index, memory index
                        case 9: immediate = (uint)body.Count(); break; // data.drop data index
                        case 10: immediate = (uint)body.Count(); secondary = (uint)body.Count(); break; // destination, source memory index
                        case 11: secondary = (uint)body.Count(); break; // memory index
                        case 12: immediate = (uint)body.Count(); secondary = (uint)body.Count(); break; // element index, table index
                        case 13: immediate = (uint)body.Count(); break; // element.drop element index
                        case 14: immediate = (uint)body.Count(); secondary = (uint)body.Count(); break; // destination, source table index
                        case 15: secondary = (uint)body.Count(); break; // table index
                        case 16: secondary = (uint)body.Count(); break; // table index
                        case 17: secondary = (uint)body.Count(); break; // table index
                        default: throw new WasmException($"Offset 0x{offset:x}: Unsupported WASM opcode 0xfc/{operand}.");
                    }
                    break;
                case 0xfd:
                    operand = body.Count();
                    switch (operand)
                    {
                        case 12:
                            vectorConstant = body.Bytes(16);
                            break;
                        case 228: // f32x4.add
                        case 230: // f32x4.mul
                            break;
                        default:
                            throw new WasmException($"Offset 0x{offset:x}: Unsupported WASM opcode 0xfd/{operand}.");
                    }
                    break;
                default:
                    if (I32Operations.Arity(opcode) != 0 || I64Operations.Arity(opcode) != 0 || FloatOperations.Arity(opcode) != 0 || ConversionOperations.Supports(opcode)) { operand = 0; break; }
                    throw new WasmException($"Offset 0x{offset:x}: Unsupported WASM opcode 0x{opcode:x2}.");
            }
            instructions.Add(new Instruction(opcode, operand, offset, targets, immediate, blockType, selectType, constant, secondary, vectorConstant, reference));
            if (opcode == 0x0b)
            {
                if (depth != 0) { depth--; continue; }
                body.RequireEnd();
                return new Function(signature, locals.ToArray(), instructions);
            }
        }
        throw new WasmException("Function body is missing end.");
    }
}
