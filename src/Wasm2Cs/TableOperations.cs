namespace Wasm2Cs;

internal static class TableOperations
{
    public const string Helpers = """
    private object __wasm_TableGet(global::Wasm2Cs.WasmTable table, int index)
    {
        if ((uint)index >= (uint)table.CurrentSize) throw new TrapException(TrapKind.TableOutOfBounds);
        return table.Get(unchecked((uint)index));
    }
    private void __wasm_TableSet(global::Wasm2Cs.WasmTable table, int index, object value)
    {
        if ((uint)index >= (uint)table.CurrentSize) throw new TrapException(TrapKind.TableOutOfBounds);
        table.Set(unchecked((uint)index), value);
    }
    private int __wasm_TableGrow(global::Wasm2Cs.WasmTable table, int delta, object value)
    {
        return table.Grow(delta, value);
    }
    private void __wasm_TableRange(global::Wasm2Cs.WasmTable table, int index, int length)
    {
        ulong start = unchecked((uint)index), count = unchecked((uint)length);
        if (start + count > (ulong)table.CurrentSize) throw new TrapException(TrapKind.TableOutOfBounds);
    }
    private void __wasm_TableInit(global::Wasm2Cs.WasmTable table, object[] source, int destination, int sourceIndex, int length)
    {
        if (source == null) throw new TrapException(TrapKind.ElementSegmentOutOfBounds);
        ulong start = unchecked((uint)sourceIndex), count = unchecked((uint)length);
        if (start + count > (ulong)source.Length) throw new TrapException(TrapKind.ElementSegmentOutOfBounds);
        __wasm_TableRange(table, destination, length);
        table.Init(source, unchecked((uint)destination), unchecked((uint)sourceIndex), unchecked((uint)length));
    }
    private void __wasm_TableCopy(global::Wasm2Cs.WasmTable destination, global::Wasm2Cs.WasmTable source,
        int destinationIndex, int sourceIndex, int length)
    {
        __wasm_TableRange(destination, destinationIndex, length);
        __wasm_TableRange(source, sourceIndex, length);
        destination.Copy(source, unchecked((uint)destinationIndex), unchecked((uint)sourceIndex), unchecked((uint)length));
    }
    private void __wasm_TableFill(global::Wasm2Cs.WasmTable table, int destination, object value, int length)
    {
        __wasm_TableRange(table, destination, length);
        table.Fill(unchecked((uint)destination), value, unchecked((uint)length));
    }

""";
}
