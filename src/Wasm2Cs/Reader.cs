using System.Text;
namespace Wasm2Cs;
internal sealed class Reader(byte[] bytes, int start = 0, int? end = null)
    {
        private int position = start;
        private readonly int limit = end ?? bytes.Length;
        public int Offset => position;
        public int Remaining => limit - position;
        public bool End => position == limit;
        public byte Byte() => position < limit ? bytes[position++] : throw new WasmException("Unexpected end of WASM binary.");
        public void RequireEnd() { if (!End) throw new WasmException("Trailing bytes in section or function."); }
        private static bool IsValueType(byte value) => value == 0x7f || value == 0x7e || value == 0x7d ||
            value == 0x7c || value == 0x70 || value == 0x6f;
        public ValueType ValueType()
        {
            byte value = Byte();
            if (!IsValueType(value)) throw new WasmException($"Unsupported WASM value type 0x{value:x2}.");
            return (ValueType)value;
        }
        public ValueType[] ValueTypes()
        {
            int count = Count();
            if (count > Remaining) throw new WasmException("Value type vector extends past binary boundary.");
            var types = new ValueType[count];
            for (int i = 0; i < count; i++) types[i] = ValueType();
            return types;
        }
        public Signature BlockType(List<Signature> types)
        {
            byte first = Byte();
            if (first == 0x40) return Signature.Empty;
            if (IsValueType(first)) return new Signature(Array.Empty<ValueType>(), new[] { (ValueType)first });
            // Type indices use s33, not u32: bit 6 of the terminal byte is a sign bit.
            long value = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = i == 0 ? first : Byte();
                if (i == 4 && (b & 0x70) != 0 && (b & 0x70) != 0x70)
                    throw new WasmException("Block type LEB128 integer exceeds 33 bits.");
                value |= (long)(b & 0x7f) << (7 * i);
                if ((b & 0x80) == 0)
                {
                    if ((b & 0x40) != 0) value |= -1L << (7 * (i + 1));
                    if (value < 0 || value >= types.Count) throw new WasmException("Invalid block type index.");
                    return types[(int)value];
                }
            }
            throw new WasmException("Block type LEB128 integer is too long.");
        }
        private uint Leb(bool signed)
        {
            uint value = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = Byte();
                if (i == 4 && (signed ? (b & 0x78) != 0 && (b & 0x78) != 0x78 : (b & 0x70) != 0))
                    throw new WasmException("LEB128 integer exceeds 32 bits.");
                value |= (uint)(b & 0x7f) << (7 * i);
                if ((b & 0x80) == 0)
                {
                    if (signed && i < 4 && (b & 0x40) != 0) value |= uint.MaxValue << (7 * (i + 1));
                    return value;
                }
            }
            throw new WasmException("LEB128 integer is too long.");
        }
        public int SignedI32() => unchecked((int)Leb(true));
        public uint UnsignedI32() => Leb(false);
        public byte[] Bytes(int length)
        {
            var slice = Slice(length);
            var result = new byte[length];
            Buffer.BlockCopy(bytes, slice.position, result, 0, length);
            return result;
        }
        public int Count()
        {
            uint value = Leb(false);
            if (value > int.MaxValue) throw new WasmException("Length or index exceeds implementation limit.");
            return (int)value;
        }
        public Reader Slice(int length)
        {
            if (length > limit - position) throw new WasmException("Section or body extends past binary boundary.");
            var result = new Reader(bytes, position, position + length);
            position += length;
            return result;
        }
        public string Name()
        {
            var text = Slice(Count());
            try { return new UTF8Encoding(false, true).GetString(bytes, text.position, text.limit - text.position); }
            catch (DecoderFallbackException) { throw new WasmException("Invalid UTF-8 name."); }
        }
    }
