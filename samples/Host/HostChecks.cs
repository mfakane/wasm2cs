#nullable disable
using System;
using System.Collections.Generic;
using Wasm2Cs.Generated;

public static class HostChecks
{
    public static void Verify()
    {
        Host module = null;
        var log = new List<int>();
        module = new Host((offset, length) => {
            var bytes = module.ReadMemory(unchecked((uint)offset),length);
            int sum = 0;
            foreach (byte value in bytes) sum += value;
            module.WriteMemory(unchecked((uint)offset),new byte[] {9,8,7});
            return module.reenter(sum);
        }, value => log.Add(value));
        if (module.f(3) != 26) throw new Exception("Host return value/reentry differs.");
        module.notify(99);
        if (log.Count != 3 || log[0] != 42 || log[1] != 3 || log[2] != 99)
            throw new Exception("Host callback order differs.");
        var memory = module.ReadMemory(16,3);
        if (memory[0] != 9 || memory[1] != 8 || memory[2] != 7) throw new Exception("Host memory exchange differs.");
        var failure = new InvalidOperationException("host failure");
        var failing = new Host((a,b) => throw failure, value => {});
        try { failing.f(3); throw new Exception("Missing host exception."); }
        catch (InvalidOperationException e) { if (!ReferenceEquals(e,failure)) throw; }
        try { new Host(null, value => {}); throw new Exception("Accepted missing host import."); }
        catch (ArgumentNullException) { }
        try { new Host((a,b) => 0, value => throw failure); throw new Exception("Missing start exception."); }
        catch (InvalidOperationException e) { if (!ReferenceEquals(e,failure)) throw; }
    }
}
