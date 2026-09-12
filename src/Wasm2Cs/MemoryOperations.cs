namespace Wasm2Cs;

internal static class MemoryOperations
{
    public static int Width(byte op) => op switch
    {
        0x29 or 0x2b or 0x37 or 0x39 => 8,
        0x2a or 0x38 or 0x28 or 0x34 or 0x35 or 0x36 or 0x3e => 4,
        0x2e or 0x2f or 0x32 or 0x33 or 0x3b or 0x3d => 2,
        0x2c or 0x2d or 0x30 or 0x31 or 0x3a or 0x3c => 1,
        _ => throw new InvalidOperationException("Unknown memory instruction.")
    };
    public static bool IsStore(byte op) => op >= 0x36 && op <= 0x3e;
    public static bool IsSigned(byte op) => op == 0x2c || op == 0x2e || op == 0x30 || op == 0x32 || op == 0x34;
    public static ValueType Type(byte op) => op == 0x2a || op == 0x38 ? ValueType.F32 :
        op == 0x2b || op == 0x39 ? ValueType.F64 : op == 0x29 || op == 0x37 || (op >= 0x30 && op <= 0x35) ||
        (op >= 0x3c && op <= 0x3e) ? ValueType.I64 : ValueType.I32;
    public const string Helpers = """
    private global::Wasm2Cs.WasmMemory __wasm_memory;
    public int MemorySize { get { return __wasm_memory.Size; } }
    private int __wasm_Address(int address, uint offset, int width)
    {
        ulong effective = (ulong)unchecked((uint)address) + offset;
        if (width < 0 || effective + (ulong)width > (ulong)__wasm_memory.Size) throw new TrapException(TrapKind.MemoryOutOfBounds);
        return (int)effective;
    }
    private int __wasm_BulkAddress(int address, int length)
    {
        ulong start = unchecked((uint)address), count = unchecked((uint)length);
        if (start + count > (ulong)__wasm_memory.Size) throw new TrapException(TrapKind.MemoryOutOfBounds);
        return checked((int)start);
    }
    public byte[] ReadMemory(uint offset, int count)
    {
        if (count < 0) throw new global::System.ArgumentOutOfRangeException("count");
        int address = __wasm_Address(unchecked((int)offset),0,count);
        return __wasm_memory.ReadMemory(unchecked((uint)address),count);
    }
    public void ReadMemoryInto(uint offset, byte[] destination, int destinationOffset, int count)
    {
        if (destination == null) throw new global::System.ArgumentNullException("destination");
        if (destinationOffset < 0 || count < 0 || (long)destinationOffset + count > destination.Length)
            throw new global::System.ArgumentOutOfRangeException("destinationOffset");
        __wasm_Address(unchecked((int)offset),0,count);
        __wasm_memory.ReadMemory(offset,destination,destinationOffset,count);
    }
    public void WriteMemory(uint offset, byte[] bytes)
    {
        if (bytes == null) throw new global::System.ArgumentNullException("bytes");
        int address = __wasm_Address(unchecked((int)offset),0,bytes.Length);
        __wasm_memory.WriteMemory(unchecked((uint)address),bytes);
    }
    public void WriteMemoryFrom(uint offset, byte[] bytes, int sourceOffset, int count)
    {
        if (bytes == null) throw new global::System.ArgumentNullException("bytes");
        if (sourceOffset < 0 || count < 0 || (long)sourceOffset + count > bytes.Length)
            throw new global::System.ArgumentOutOfRangeException("sourceOffset");
        __wasm_Address(unchecked((int)offset),0,count);
        __wasm_memory.WriteMemory(offset,bytes,sourceOffset,count);
    }
    private int __wasm_Load(int address, uint offset, int width, bool signed)
    {
        unchecked
        {
            int index = __wasm_Address(address,offset,width);
            uint value = 0;
            for(int i=0;i<width;i++) value |= (uint)__wasm_memory.ReadByte(unchecked((uint)(index+i))) << (i*8);
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
            for(int i=0;i<width;i++) __wasm_memory.WriteByte(unchecked((uint)(index+i)),(byte)((uint)value >> (i*8)));
        }
    }
    private long __wasm_Load64(int address, uint offset, int width, bool signed)
    {
        unchecked
        {
            int index = __wasm_Address(address,offset,width);
            ulong value = 0;
            for (int i=0;i<width;i++) value |= (ulong)__wasm_memory.ReadByte(unchecked((uint)(index+i))) << (i*8);
            if (signed && width == 1) return (sbyte)value;
            if (signed && width == 2) return (short)value;
            if (signed && width == 4) return (int)value;
            return (long)value;
        }
    }
    private void __wasm_Store64(int address, long value, uint offset, int width)
    {
        unchecked
        {
            int index = __wasm_Address(address,offset,width);
            for (int i=0;i<width;i++) __wasm_memory.WriteByte(unchecked((uint)(index+i)),(byte)((ulong)value >> (i*8)));
        }
    }
    private int __wasm_Grow(int delta)
    {
        int previous = __wasm_memory.CurrentPages;
        ulong pages = (ulong)previous + unchecked((uint)delta);
        if (pages > __WASM_MAX_PAGES__) return -1;
        return __wasm_memory.Grow(delta);
    }
    private void __wasm_Copy(int destination, int source, int length)
    {
        __wasm_BulkAddress(destination,length);
        __wasm_BulkAddress(source,length);
        __wasm_memory.Copy(unchecked((uint)destination),unchecked((uint)source),unchecked((uint)length));
    }
    private void __wasm_Fill(int destination, int value, int length)
    {
        __wasm_BulkAddress(destination,length);
        __wasm_memory.Fill(unchecked((uint)destination),unchecked((byte)value),unchecked((uint)length));
    }
    private void __wasm_Init(byte[] data, int destination, int source, int length)
    {
        if (data == null) throw new TrapException(TrapKind.DataSegmentOutOfBounds);
        ulong start = unchecked((uint)source), count = unchecked((uint)length);
        if (start + count > (ulong)data.Length) throw new TrapException(TrapKind.DataSegmentOutOfBounds);
        __wasm_BulkAddress(destination,length);
        __wasm_memory.WriteMemory(unchecked((uint)destination),data,checked((int)start),checked((int)count));
    }

""";
}
