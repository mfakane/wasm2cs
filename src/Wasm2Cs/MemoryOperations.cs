namespace Wasm2Cs;

internal static class MemoryOperations
{
    public static int Width(byte op) => op == 0x28 || op == 0x36 ? 4 : op == 0x2e || op == 0x2f || op == 0x3b ? 2 : 1;
    public static bool IsStore(byte op) => op == 0x36 || op == 0x3a || op == 0x3b;
    public const string Helpers = """
    private byte[] __wasm_memory;
    public int MemorySize { get { return __wasm_memory.Length; } }
    private int __wasm_Address(int address, uint offset, int width)
    {
        ulong effective = (ulong)unchecked((uint)address) + offset;
        if (effective + (ulong)width > (ulong)__wasm_memory.Length) throw new TrapException(TrapKind.MemoryOutOfBounds);
        return (int)effective;
    }
    public byte[] ReadMemory(uint offset, int count)
    {
        if (count < 0) throw new global::System.ArgumentOutOfRangeException("count");
        int address = __wasm_Address(unchecked((int)offset),0,count);
        var bytes = new byte[count];
        global::System.Buffer.BlockCopy(__wasm_memory,address,bytes,0,count);
        return bytes;
    }
    public void WriteMemory(uint offset, byte[] bytes)
    {
        if (bytes == null) throw new global::System.ArgumentNullException("bytes");
        int address = __wasm_Address(unchecked((int)offset),0,bytes.Length);
        global::System.Buffer.BlockCopy(bytes,0,__wasm_memory,address,bytes.Length);
    }
    private int __wasm_Load(int address, uint offset, int width, bool signed)
    {
        unchecked
        {
            int index = __wasm_Address(address,offset,width);
            uint value = 0;
            for(int i=0;i<width;i++) value |= (uint)__wasm_memory[index+i] << (i*8);
            if (signed && width == 1) return (sbyte)value;
            if (signed && width == 2) return (short)value;
            return (int)value;
        }
    }
    private void __wasm_Store(int address, int value, uint offset, int width)
    {
        unchecked
        {
            int index = __wasm_Address(address,offset,width);
            for(int i=0;i<width;i++) __wasm_memory[index+i] = (byte)((uint)value >> (i*8));
        }
    }
    private int __wasm_Grow(int delta)
    {
        int previous = __wasm_memory.Length/65536;
        ulong pages = (ulong)previous + unchecked((uint)delta);
        if (pages > __WASM_MAX_PAGES__) return -1;
        if (delta == 0) return previous;
        try
        {
            var grown = new byte[(int)pages*65536];
            global::System.Buffer.BlockCopy(__wasm_memory,0,grown,0,__wasm_memory.Length);
            __wasm_memory = grown;
            return previous;
        }
        catch(global::System.OutOfMemoryException) { return -1; }
    }

""";
}
