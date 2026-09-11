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
                        int parameters = section.Count();
                        for (int p = 0; p < parameters; p++) section.I32Type();
                        int results = section.Count();
                        if (results > 1) throw new WasmException("Multiple results are not supported.");
                        if (results == 1) section.I32Type();
                        types.Add(new(parameters, results));
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
                        if (section.Byte() != 0) throw new WasmException("Only function exports are supported.");
                        if (exports.ContainsKey(name)) throw new WasmException("Duplicate export name.");
                        exports.Add(name, section.Count());
                    }
                    break;
                case 10:
                    for (int i = 0, count = section.Count(); i < count; i++)
                        bodies.Add(section.Slice(section.Count()));
                    break;
                default:
                    throw new WasmException($"Unsupported WASM section {id}.");
            }
            section.RequireEnd();
        }
        if (functions.Count != bodies.Count) throw new WasmException("Function and code counts differ.");

        var decoded = new List<Function>();
        for (int i = 0; i < functions.Count; i++)
        {
            if (functions[i] >= types.Count) throw new WasmException("Invalid function type index.");
            try { decoded.Add(DecodeBody(bodies[i], types[functions[i]])); }
            catch (WasmException e) { throw new WasmException($"Function {i}: {e.Message}"); }
        }
        return new Module(types, functions, exports, decoded);
    }
    private static Function DecodeBody(Reader body, Signature signature)
    {
        int locals = signature.Parameters;
        for (int i = 0, groups = body.Count(); i < groups; i++)
        {
            int count = body.Count();
            body.I32Type();
            if ((long)locals + count > 100_000) throw new WasmException("Too many locals (limit: 100000).");
            locals += count;
        }
        var instructions = new List<Instruction>();
        int depth = 0;
        while (!body.End)
        {
            int offset = body.Offset;
            byte opcode = body.Byte();
            int operand;
            int[]? targets = null;
            switch (opcode)
            {
                case 0x02: case 0x03: case 0x04:
                    byte blockType = body.Byte();
                    if (blockType != 0x40 && blockType != 0x7f) throw new WasmException("Only empty or i32 block results are supported.");
                    operand = blockType == 0x7f ? 1 : 0; depth++; break;
                case 0x0c: case 0x0d: operand = body.Count(); break;
                case 0x0e:
                    int count = body.Count();
                    if (count >= body.Remaining) throw new WasmException("Branch table extends past body boundary.");
                    targets = new int[count+1];
                    for(int t=0;t<targets.Length;t++) targets[t] = body.Count();
                    operand = 0; break;
                case 0x20: case 0x21: case 0x22: operand = body.Count(); break;
                case 0x41: operand = body.SignedI32(); break;
                case 0x00: case 0x01: case 0x05: case 0x0b: case 0x0f: case 0x1a: case 0x1b:
                case 0x6a: case 0x6b: case 0x6c: operand = 0; break;
                default:
                    if (I32Operations.Arity(opcode) != 0) { operand = 0; break; }
                    throw new WasmException($"Offset 0x{offset:x}: Unsupported WASM opcode 0x{opcode:x2}.");
            }
            instructions.Add(new Instruction(opcode, operand, offset, targets));
            if (opcode == 0x0b)
            {
                if (depth != 0) { depth--; continue; }
                body.RequireEnd();
                return new Function(signature, locals, instructions);
            }
        }
        throw new WasmException("Function body is missing end.");
    }
}
