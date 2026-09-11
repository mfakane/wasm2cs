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
        public void I32Type() { if (Byte() != 0x7f) throw new WasmException("Only i32 values are supported."); }
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
