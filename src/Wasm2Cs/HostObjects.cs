using System.Runtime.InteropServices;

namespace Wasm2Cs;

// Public objects used by generated modules when a memory or global crosses a
// module/host boundary. The backing storage is deliberately private so a grow
// can replace it without leaving callers with a stale array.
public enum WasmValueType : byte
{
    V128 = 0x7b,
    I32 = 0x7f,
    I64 = 0x7e,
    F32 = 0x7d,
    F64 = 0x7c,
    FuncRef = 0x70,
    ExternRef = 0x6f
}

public sealed class WasmMemory
{
    private const int PageSize = 65536;
    private byte[] bytes;

    public int Size => bytes.Length;
    public int CurrentPages => bytes.Length / PageSize;
    public int? DeclaredMaximumPages { get; }
    public int HostMaximumPages { get; }

    public WasmMemory(int minimumPages, int? maximumPages = null, int hostMaximumPages = 4096)
    {
        if (minimumPages < 0 || maximumPages.HasValue && (maximumPages.Value < minimumPages || maximumPages.Value > 65536))
            throw new ArgumentOutOfRangeException(nameof(minimumPages));
        if (hostMaximumPages < 0 || hostMaximumPages > 65536 || minimumPages > hostMaximumPages)
            throw new ArgumentOutOfRangeException(nameof(hostMaximumPages));
        DeclaredMaximumPages = maximumPages;
        HostMaximumPages = hostMaximumPages;
        bytes = new byte[checked(minimumPages * PageSize)];
    }

    public void ValidateImport(int minimumPages, int? maximumPages)
    {
        if (CurrentPages < minimumPages)
            throw new ArgumentException("Imported memory is smaller than the required minimum.", nameof(minimumPages));
        if (maximumPages.HasValue && (!DeclaredMaximumPages.HasValue || DeclaredMaximumPages.Value > maximumPages.Value))
            throw new ArgumentException("Imported memory maximum does not match the required limit.", nameof(maximumPages));
    }

    public int Grow(int delta)
    {
        int previous = CurrentPages;
        ulong requested = (ulong)previous + unchecked((uint)delta);
        int maximum = HostMaximumPages;
        if (DeclaredMaximumPages.HasValue && DeclaredMaximumPages.Value < maximum) maximum = DeclaredMaximumPages.Value;
        if (requested > (ulong)maximum) return -1;
        if (delta == 0) return previous;
        try
        {
            var grown = new byte[checked((int)requested * PageSize)];
            Buffer.BlockCopy(bytes, 0, grown, 0, bytes.Length);
            bytes = grown;
            return previous;
        }
        catch (OutOfMemoryException) { return -1; }
        catch (OverflowException) { return -1; }
    }

    public byte ReadByte(uint offset)
    {
        ValidateRange(offset, 1);
        return bytes[(int)offset];
    }

    public void WriteByte(uint offset, byte value)
    {
        ValidateRange(offset, 1);
        bytes[(int)offset] = value;
    }

#if NETSTANDARD2_1_OR_GREATER
    // These methods keep Span and MemoryMarshal inside one operation. A caller
    // cannot retain a view while Grow replaces the private backing array.
    public ushort ReadUInt16(uint offset)
    {
        ValidateRange(offset, 2);
        ushort value = MemoryMarshal.Read<ushort>(bytes.AsSpan(checked((int)offset), 2));
        return BitConverter.IsLittleEndian ? value : Reverse(value);
    }

    public uint ReadUInt32(uint offset)
    {
        ValidateRange(offset, 4);
        uint value = MemoryMarshal.Read<uint>(bytes.AsSpan(checked((int)offset), 4));
        return BitConverter.IsLittleEndian ? value : Reverse(value);
    }

    public ulong ReadUInt64(uint offset)
    {
        ValidateRange(offset, 8);
        ulong value = MemoryMarshal.Read<ulong>(bytes.AsSpan(checked((int)offset), 8));
        return BitConverter.IsLittleEndian ? value : Reverse(value);
    }

    public void WriteUInt16(uint offset, ushort value)
    {
        ValidateRange(offset, 2);
        if (!BitConverter.IsLittleEndian) value = Reverse(value);
        MemoryMarshal.Write(bytes.AsSpan(checked((int)offset), 2), ref value);
    }

    public void WriteUInt32(uint offset, uint value)
    {
        ValidateRange(offset, 4);
        if (!BitConverter.IsLittleEndian) value = Reverse(value);
        MemoryMarshal.Write(bytes.AsSpan(checked((int)offset), 4), ref value);
    }

    public void WriteUInt64(uint offset, ulong value)
    {
        ValidateRange(offset, 8);
        if (!BitConverter.IsLittleEndian) value = Reverse(value);
        MemoryMarshal.Write(bytes.AsSpan(checked((int)offset), 8), ref value);
    }

    private static ushort Reverse(ushort value) => (ushort)((value >> 8) | (value << 8));
    private static uint Reverse(uint value) => (value >> 24) | ((value >> 8) & 0xff00) |
        ((value << 8) & 0xff0000) | (value << 24);
    private static ulong Reverse(ulong value) => (value >> 56) | ((value >> 40) & 0xff00UL) |
        ((value >> 24) & 0xff0000UL) | ((value >> 8) & 0xff000000UL) |
        ((value << 8) & 0xff00000000UL) | ((value << 24) & 0xff0000000000UL) |
        ((value << 40) & 0xff000000000000UL) | (value << 56);
#endif

    public byte[] ReadMemory(uint offset, int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        ValidateRange(offset, unchecked((uint)count));
        var result = new byte[count];
        ReadMemory(offset, result, 0, count);
        return result;
    }

    public void ReadMemory(uint offset, byte[] destination, int destinationOffset, int count)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        ValidateBuffer(destination.Length, destinationOffset, count);
        ValidateRange(offset, unchecked((uint)count));
        Buffer.BlockCopy(bytes, checked((int)offset), destination, destinationOffset, count);
    }

    public void WriteMemory(uint offset, byte[] source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        WriteMemory(offset, source, 0, source.Length);
    }

    public void WriteMemory(uint offset, byte[] source, int sourceOffset, int count)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        ValidateBuffer(source.Length, sourceOffset, count);
        ValidateRange(offset, unchecked((uint)count));
        Buffer.BlockCopy(source, sourceOffset, bytes, checked((int)offset), count);
    }

    public void Copy(uint destination, uint source, uint count)
    {
        ValidateRange(destination, count);
        ValidateRange(source, count);
        int length = checked((int)count);
        Array.Copy(bytes, checked((int)source), bytes, checked((int)destination), length);
    }

    public void Fill(uint destination, byte value, uint count)
    {
        ValidateRange(destination, count);
        int start = checked((int)destination), length = checked((int)count);
#if NETSTANDARD2_1_OR_GREATER
        bytes.AsSpan(start, length).Fill(value);
#else
        for (int i = 0; i < length; i++) bytes[start + i] = value;
#endif
    }

    private void ValidateRange(uint offset, uint count)
    {
        if ((ulong)offset + count > (ulong)bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private static void ValidateBuffer(int length, int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
}

public sealed class WasmGlobal
{
    [StructLayout(LayoutKind.Explicit)]
    private struct Float32
    {
        [FieldOffset(0)] public int Bits;
        [FieldOffset(0)] public float Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Float64
    {
        [FieldOffset(0)] public long Bits;
        [FieldOffset(0)] public double Value;
    }

    private ulong bits;
    private object? referenceValue;

    public WasmValueType Type { get; }
    public bool Mutable { get; }

    // Numeric globals use this property so generated code can preserve raw
    // f32/f64 bits across an import/export boundary.
    public ulong Bits
    {
        get => bits;
        set
        {
            EnsureMutable();
            bits = value;
        }
    }

    public object? ReferenceValue
    {
        get => referenceValue;
        set
        {
            EnsureMutable();
            referenceValue = value;
        }
    }

    public object? Value
    {
        get => Type switch
        {
            WasmValueType.I32 => unchecked((int)(uint)bits),
            WasmValueType.I64 => unchecked((long)bits),
            WasmValueType.F32 => ToFloat32((int)(uint)bits),
            WasmValueType.F64 => ToFloat64(unchecked((long)bits)),
            _ => referenceValue
        };
        set
        {
            EnsureMutable();
            SetValue(value);
        }
    }

    public WasmGlobal(WasmValueType type, object? value, bool mutable = false)
    {
        Type = type;
        Mutable = mutable;
        SetValue(value);
    }

    public WasmGlobal(WasmValueType type, ulong bits, bool mutable = false)
    {
        Type = type;
        Mutable = mutable;
        this.bits = bits;
    }

    private void SetValue(object? value)
    {
        switch (Type)
        {
            case WasmValueType.I32 when value is int i32: bits = unchecked((uint)i32); break;
            case WasmValueType.I64 when value is long i64: bits = unchecked((ulong)i64); break;
            case WasmValueType.F32 when value is float f32: bits = unchecked((uint)FromFloat32(f32)); break;
            case WasmValueType.F64 when value is double f64: bits = unchecked((ulong)FromFloat64(f64)); break;
            case WasmValueType.FuncRef:
            case WasmValueType.ExternRef:
                referenceValue = value;
                break;
            default:
                throw new ArgumentException("Global value does not match its WebAssembly type.", nameof(value));
        }
    }

    public void Validate(WasmValueType expectedType, bool expectedMutable)
    {
        if (Type != expectedType || Mutable != expectedMutable)
            throw new ArgumentException("Imported global type or mutability does not match.");
    }

    private void EnsureMutable()
    {
        if (!Mutable) throw new InvalidOperationException("Cannot modify an immutable WebAssembly global.");
    }

    private static int FromFloat32(float value) { var bits = new Float32 { Value = value }; return bits.Bits; }
    private static float ToFloat32(int value) { var bits = new Float32 { Bits = value }; return bits.Value; }
    private static long FromFloat64(double value) { var bits = new Float64 { Value = value }; return bits.Bits; }
    private static double ToFloat64(long value) { var bits = new Float64 { Bits = value }; return bits.Value; }
}
