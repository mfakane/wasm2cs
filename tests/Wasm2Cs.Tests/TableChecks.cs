using System.Reflection;
using Wasm2Cs;
using static ExecutionChecks;

internal static class TableChecks
{
    private static byte[] Type(byte[] parameters, byte[] results) => [0x60, ..U32(parameters.Length), ..parameters, ..U32(results.Length), ..results];

    private static byte[] Module()
    {
        byte[] types = [3, ..Type([0x7f], [0x7f]), ..Type([0x7f], [0x7f]), ..Type([0x7f, 0x7f], [0x7f])];
        byte[] functions = [3, 0, 1, 2];
        byte[] table = [1, 0x70, 1, 4, 4];
        byte[] exports = [2, 1, (byte)'f', 0, 2,
            5, (byte)'t', (byte)'a', (byte)'b', (byte)'l', (byte)'e', 1, 0];
        byte[] elements = [1, 0, 0x41, 0, 0x0b, 3, 0, 1, 2];
        byte[] body0 = [0, 0x20, 0, 0x0b];
        byte[] body1 = [0, 0x20, 0, 0x41, 1, 0x6a, 0x0b];
        byte[] body2 = [0, 0x20, 0, 0x20, 1, 0x11, 1, 0, 0x0b];
        byte[] code = [3, ..U32(body0.Length), ..body0, ..U32(body1.Length), ..body1, ..U32(body2.Length), ..body2];
        return [0, 97, 115, 109, 1, 0, 0, 0, ..Section(1, types), ..Section(3, functions),
            ..Section(4, table), ..Section(7, exports), ..Section(9, elements), ..Section(10, code)];
    }

    private static byte[] PassiveModule()
    {
        byte[] types = [5, ..Type([0x7f], [0x7f]), ..Type([0x7f], [0x7f]), ..Type([], [0x7f]), ..Type([], []),
            ..Type([], [0x7f])];
        byte[] functions = [8, 0, 1, 2, 3, 3, 2, 2, 4];
        byte[] table = [1, 0x70, 1, 2, 4];
        byte[] exports = [6,
            4, (byte)'i', (byte)'n', (byte)'i', (byte)'t', 0, 2,
            4, (byte)'d', (byte)'r', (byte)'o', (byte)'p', 0, 3,
            9, (byte)'a', (byte)'f', (byte)'t', (byte)'e', (byte)'r', (byte)'D', (byte)'r', (byte)'o', (byte)'p', 0, 4,
            4, (byte)'g', (byte)'r', (byte)'o', (byte)'w', 0, 5,
            4, (byte)'s', (byte)'i', (byte)'z', (byte)'e', 0, 6,
            4, (byte)'f', (byte)'i', (byte)'l', (byte)'l', 0, 7];
        byte[] elements = [1, 1, 0, 2, 0, 1];
        byte[][] bodies = [
            [0, 0x20, 0, 0x0b],
            [0, 0x20, 0, 0x41, 1, 0x6a, 0x0b],
            [0, 0x41, 0, 0x41, 0, 0x41, 2, 0xfc, 12, 0, 0, 0x41, 0, 0x25, 0, 0xd1, 0x0b],
            [0, 0xfc, 13, 0, 0x0b],
            [0, 0x41, 0, 0x41, 0, 0x41, 1, 0xfc, 12, 0, 0, 0x0b],
            [0, 0x41, 1, 0xd0, 0x70, 0xfc, 15, 0, 0x0b],
            [0, 0xfc, 16, 0, 0x0b],
            [0, 0x41, 0, 0xd2, 0, 0x41, 2, 0xfc, 17, 0, 0x41, 0, 0x25, 0, 0xd1, 0x0b]
        ];
        var code = new List<byte>([8]);
        foreach (var body in bodies) code.AddRange([..U32(body.Length), ..body]);
        return [0, 97, 115, 109, 1, 0, 0, 0, ..Section(1, types), ..Section(3, functions),
            ..Section(4, table), ..Section(7, exports), ..Section(9, elements), ..Section(10, code.ToArray())];
    }

    private static byte[] ImportedModule()
    {
        byte[] imports = [1, 3, (byte)'e', (byte)'n', (byte)'v', 5, (byte)'t', (byte)'a', (byte)'b', (byte)'l', (byte)'e', 1, 0x70, 1, 1, 2];
        byte[] body = [0, 0x20, 0, 0x20, 1, 0x11, 0, 0, 0x0b];
        return [0, 97, 115, 109, 1, 0, 0, 0, ..Section(1, [2, ..Type([0x7f], [0x7f]), ..Type([0x7f, 0x7f], [0x7f])]), ..Section(2, imports),
            ..Section(3, [1, 1]), ..Section(7, [2, 1, (byte)'f', 0, 0, 5, (byte)'t', (byte)'a', (byte)'b', (byte)'l', (byte)'e', 1, 0]),
            ..Section(10, [1, ..U32(body.Length), ..body])];
    }

    private static byte[] ExternRefModule()
    {
        byte[] body = [0, 0x41, 0, 0xd0, 0x6f, 0x26, 0, 0x41, 0, 0x25, 0, 0xd1, 0x0b];
        return [0, 97, 115, 109, 1, 0, 0, 0, ..Section(1, [1, ..Type([], [0x7f])]),
            ..Section(3, [1, 0]), ..Section(4, [1, 0x6f, 0, 1]), ..Section(7, [1, 1, (byte)'f', 0, 0]),
            ..Section(10, [1, ..U32(body.Length), ..body])];
    }

    private static byte[] CopyModule()
    {
        byte[] body = [0, 0x41, 0, 0xd2, 0, 0x41, 1, 0xfc, 17, 0,
            0x41, 0, 0x41, 0, 0x41, 1, 0xfc, 14, 1, 0,
            0x41, 0, 0x25, 1, 0xd1, 0x0b];
        return [0, 97, 115, 109, 1, 0, 0, 0, ..Section(1, [1, ..Type([], [0x7f])]),
            ..Section(3, [1, 0]), ..Section(4, [2, 0x70, 1, 2, 2, 0x70, 1, 2, 2]),
            ..Section(7, [1, 1, (byte)'f', 0, 0]), ..Section(10, [1, ..U32(body.Length), ..body])];
    }

    private static byte[] ImportedFunctionModule()
    {
        byte[] imports = [1, 3, (byte)'e', (byte)'n', (byte)'v', 3, (byte)'i', (byte)'n', (byte)'c', 0, 0];
        byte[] body = [0, 0x20, 0, 0x20, 1, 0x11, 0, 0, 0x0b];
        return [0, 97, 115, 109, 1, 0, 0, 0, ..Section(1, [2, ..Type([0x7f], [0x7f]), ..Type([0x7f, 0x7f], [0x7f])]),
            ..Section(2, imports), ..Section(3, [1, 1]), ..Section(4, [1, 0x70, 0, 1]),
            ..Section(7, [1, 1, (byte)'f', 0, 1]), ..Section(9, [1, 0, 0x41, 0, 0x0b, 1, 0]),
            ..Section(10, [1, ..U32(body.Length), ..body])];
    }

    public static async Task Verify()
    {
        var bytes = Module();
        await ExecutionChecks.Compare(bytes, [[41, 0], [41, 1], [41, 2], [41, 3], [41, 4]]);
        ExecutionChecks.Compile(bytes, "PortableTables", portable: true);
        var type = ExecutionChecks.Compile(bytes, "Tables").GetType("Wasm2Cs.Generated.Tables")!;
        var instance = Activator.CreateInstance(type)!;
        var call = type.GetMethod("f")!;
        if ((int)call.Invoke(instance, [41, 0])! != 41 || (int)call.Invoke(instance, [41, 1])! != 42)
            throw new Exception("Indirect call or active element initialization failed.");

        ExpectTrap(() => call.Invoke(instance, [41, 2]), "IndirectCallTypeMismatch");
        var table = (WasmTable)type.GetProperty("table")!.GetValue(instance)!;
        if (table.CurrentSize != 4 || table.ElementType != WasmValueType.FuncRef)
            throw new Exception("Generated table export is incorrect.");
        ExpectTrap(() => call.Invoke(instance, [41, 4]), "TableOutOfBounds");
        ExpectTrap(() => call.Invoke(instance, [41, 3]), "IndirectCallNull");

        var passiveType = ExecutionChecks.Compile(PassiveModule(), "PassiveTables").GetType("Wasm2Cs.Generated.PassiveTables")!;
        var passive = Activator.CreateInstance(passiveType)!;
        if ((int)passiveType.GetMethod("init")!.Invoke(passive, null)! != 0 ||
            (int)passiveType.GetMethod("fill")!.Invoke(passive, null)! != 0 ||
            (int)passiveType.GetMethod("grow")!.Invoke(passive, null)! != 2 ||
            (int)passiveType.GetMethod("size")!.Invoke(passive, null)! != 3 ||
            (int)passiveType.GetMethod("grow")!.Invoke(passive, null)! != 3 ||
            (int)passiveType.GetMethod("grow")!.Invoke(passive, null)! != -1 ||
            (int)passiveType.GetMethod("size")!.Invoke(passive, null)! != 4)
            throw new Exception("Passive table operations changed state incorrectly.");
        var dropped = Activator.CreateInstance(passiveType)!;
        passiveType.GetMethod("drop")!.Invoke(dropped, null);
        ExpectTrap(() => passiveType.GetMethod("afterDrop")!.Invoke(dropped, null), "ElementSegmentOutOfBounds");

        var importedType = ExecutionChecks.Compile(ImportedModule(), "ImportedTable").GetType("Wasm2Cs.Generated.ImportedTable")!;
        var importedTable = new WasmTable(WasmValueType.FuncRef, 1, 2);
        Func<int, int> tableCallback = value => value + 1;
        importedTable.Set(0, tableCallback);
        var imported = Activator.CreateInstance(importedType, [importedTable])!;
        if ((int)importedType.GetMethod("f")!.Invoke(imported, [41, 0])! != 42 ||
            !ReferenceEquals(importedType.GetProperty("table")!.GetValue(imported), importedTable))
            throw new Exception("Imported table was not shared.");
        try { Activator.CreateInstance(importedType, [new WasmTable(WasmValueType.ExternRef, 1, 2)]); throw new Exception("Accepted an incompatible table import."); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }

        var externType = ExecutionChecks.Compile(ExternRefModule(), "ExternTable").GetType("Wasm2Cs.Generated.ExternTable")!;
        if ((int)externType.GetMethod("f")!.Invoke(Activator.CreateInstance(externType), null)! != 1)
            throw new Exception("Externref table operation failed.");

        var copyType = ExecutionChecks.Compile(CopyModule(), "CopyTables").GetType("Wasm2Cs.Generated.CopyTables")!;
        if ((int)copyType.GetMethod("f")!.Invoke(Activator.CreateInstance(copyType), null)! != 0)
            throw new Exception("table.copy did not preserve the function reference.");

        var importedFunctionType = ExecutionChecks.Compile(ImportedFunctionModule(), "ImportedFunctionTable")
            .GetType("Wasm2Cs.Generated.ImportedFunctionTable")!;
        Func<int, int> callback = value => value + 1;
        var import = Delegate.CreateDelegate(importedFunctionType.GetNestedType("__wasm_Import0")!, callback.Target, callback.Method);
        var importedFunction = Activator.CreateInstance(importedFunctionType, [import])!;
        if ((int)importedFunctionType.GetMethod("f")!.Invoke(importedFunction, [41, 0])! != 42)
            throw new Exception("Imported host delegate did not survive indirect dispatch.");
        Console.WriteLine("PASS: tables, active elements, structural indirect-call signatures, and table traps.");
    }

    private static void ExpectTrap(Action action, string kind)
    {
        try { action(); throw new Exception("Expected table trap: " + kind); }
        catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "TrapException")
        {
            if (e.InnerException!.GetType().GetProperty("Kind")?.GetValue(e.InnerException)?.ToString() != kind)
                throw new Exception("Incorrect table trap kind.");
        }
    }
}
