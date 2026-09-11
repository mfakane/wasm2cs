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
        byte previous = 0;
        while (!reader.End)
        {
            var id = reader.Byte();
            var section = reader.Slice(reader.Count());
            if (id == 0)
            {
                section.Name(); // Custom sections still require a well-formed name.
                continue;
            }
            if (id <= previous) throw new WasmException("Duplicate or out-of-order section.");
            previous = id;
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
                        if (section.Byte() != 0) throw new WasmException("Only function imports are supported.");
                        int type = section.Count();
                        if (type >= types.Count) throw new WasmException("Invalid imported function type index.");
                        module.Imports.Add(new FunctionImport(importModule, importName, type));
                    }
                    break;
                case 3:
                    for (int i = 0, count = section.Count(); i < count; i++)
                        functions.Add(section.Count());
                    break;
                case 7:
                    for (int i = 0, count = section.Count(); i < count; i++)
                    {
                        string name = section.Name();
                        byte kind = section.Byte();
                        if (kind != 0 && kind != 2 && kind != 3) throw new WasmException("Unsupported export kind.");
                        if (exports.ContainsKey(name)) throw new WasmException("Duplicate export name.");
                        exports.Add(name, section.Count());
                        module.ExportKinds.Add(name, kind);
                    }
                    break;
                case 5:
                    int memories = section.Count();
                    if (memories > 1) throw new WasmException("Only one memory32 is supported.");
                    if (memories == 1)
                    {
                        int flags = section.Count();
                        if (flags > 1) throw new WasmException("Shared/memory64 memories are not supported.");
                        int minimum = section.Count(), maximum = flags == 1 ? section.Count() : 65536;
                        if (minimum > maximum || maximum > 65536) throw new WasmException("Invalid memory limits.");
                        if (minimum > 4096) throw new WasmException("Initial memory exceeds 256 MiB implementation limit.");
                        module.Memory = new MemoryDefinition(minimum, Math.Min(maximum,4096));
                    }
                    break;
                case 6:
                    for(int i=0,count=section.Count();i<count;i++)
                    {
                        var globalType = section.ValueType();
                        byte mutable = section.Byte();
                        if (mutable > 1) throw new WasmException("Invalid global mutability.");
                        module.Globals.Add(new Global(globalType, mutable == 1, Constant(section)));
                    }
                    break;
                case 8: module.Start = section.Count(); break;
                case 10:
                    for (int i = 0, count = section.Count(); i < count; i++)
                        bodies.Add(section.Slice(section.Count()));
                    break;
                case 11:
                    for(int i=0,count=section.Count();i<count;i++)
                    {
                        int flags = section.Count();
                        if (flags != 0 && flags != 2) throw new WasmException("Only active data segments are supported.");
                        if (flags == 2 && section.Count() != 0) throw new WasmException("Invalid data memory index.");
                        var initialOffset = Constant(section);
                        if (initialOffset.Type != ValueType.I32) throw new WasmException("Data offset must have type i32.");
                        uint offset = (uint)initialOffset.Bits;
                        module.Data.Add(new DataSegment(offset, section.Bytes(section.Count())));
                    }
                    break;
                default:
                    throw new WasmException($"Unsupported WASM section {id}.");
            }
            section.RequireEnd();
        }
        if (functions.Count != bodies.Count) throw new WasmException("Function and code counts differ.");

        for (int i = 0; i < functions.Count; i++)
        {
            if (functions[i] >= types.Count) throw new WasmException("Invalid function type index.");
            try { decoded.Add(DecodeBody(bodies[i], types[functions[i]], types)); }
            catch (WasmException e) { throw new WasmException($"Function {i}: {e.Message}"); }
        }
        return module;
    }
    private static ConstantValue Constant(Reader reader)
    {
        var value = ReadConstant(reader, reader.Byte());
        if (reader.Byte() != 0x0b) throw new WasmException("Invalid initializer expression.");
        return value;
    }
    private static ConstantValue ReadConstant(Reader reader, byte opcode) => opcode switch
    {
        0x41 => new ConstantValue(ValueType.I32, unchecked((uint)reader.SignedI32())),
        0x42 => new ConstantValue(ValueType.I64, unchecked((ulong)reader.SignedI64())),
        _ => throw new WasmException($"Unsupported constant expression opcode 0x{opcode:x2}.")
    };
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
            Signature? blockType = null;
            ValueType? selectType = null;
            ConstantValue? constant = null;
            switch (opcode)
            {
                case 0x02: case 0x03: case 0x04:
                    blockType = body.BlockType(types);
                    operand = 0; depth++; break;
                case 0x1c:
                    if (body.Count() != 1) throw new WasmException("Typed select requires exactly one value type.");
                    selectType = body.ValueType(); operand = 0; break;
                case 0x0c: case 0x0d: case 0x10: operand = body.Count(); break;
                case 0x0e:
                    int count = body.Count();
                    if (count >= body.Remaining) throw new WasmException("Branch table extends past body boundary.");
                    targets = new int[count+1];
                    for(int t=0;t<targets.Length;t++) targets[t] = body.Count();
                    operand = 0; break;
                case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: operand = body.Count(); break;
                case 0x28: case 0x2c: case 0x2d: case 0x2e: case 0x2f:
                case 0x29: case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
                case 0x36: case 0x3a: case 0x3b:
                case 0x37: case 0x3c: case 0x3d: case 0x3e:
                    operand = body.Count(); immediate = body.UnsignedI32(); break;
                case 0x3f: case 0x40:
                    if (body.Byte() != 0) throw new WasmException("Invalid memory index.");
                    operand = 0; break;
                case 0x41: case 0x42: constant = ReadConstant(body, opcode); operand = 0; break;
                case 0x00: case 0x01: case 0x05: case 0x0b: case 0x0f: case 0x1a: case 0x1b:
                case 0x6a: case 0x6b: case 0x6c: operand = 0; break;
                default:
                    if (I32Operations.Arity(opcode) != 0 || I64Operations.Arity(opcode) != 0) { operand = 0; break; }
                    throw new WasmException($"Offset 0x{offset:x}: Unsupported WASM opcode 0x{opcode:x2}.");
            }
            instructions.Add(new Instruction(opcode, operand, offset, targets, immediate, blockType, selectType, constant));
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
