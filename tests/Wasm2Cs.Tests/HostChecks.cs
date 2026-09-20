using System;
using System.Collections.Generic;
using Wasm2Cs;
using Wasm2Cs.DotnetHost;

public static class HostChecks
{
    public static void Verify()
    {
        var output = new List<byte>();
        var errorOutput = new List<byte>();
        var random = new byte[] { 7, 8, 9, 10 };
        var host = new HostEnvironment(
            wallClock: () => DateTimeOffset.FromUnixTimeMilliseconds(1234),
            monotonicClock: () => 10,
            entropy: bytes => Array.Copy(random, bytes, Math.Min(random.Length, bytes.Length)),
            stdout: bytes => output.AddRange(bytes),
            stderr: bytes => errorOutput.AddRange(bytes),
            virtualFiles: new Dictionary<string, byte[]> { ["/input"] = new byte[] { 1, 2, 3 } });
        var memory = new WasmMemory(1);

        int fd = host.OpenFile("input");
        Check(fd >= 3, "virtual file was not opened");
        Check(host.Read(fd, memory, 32, 3) == 3, "file read length differs");
        Check(host.Read(fd, memory, 32, 3) == 0, "EOF was not reported");
        Check(memory.ReadMemory(32, 3).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "file bytes differ");
        Check(host.OpenFile("/missing") == -1, "missing file did not fail");

        HostEnvironment.WriteUInt32(memory, 64, 32);
        HostEnvironment.WriteUInt32(memory, 68, 3);
        var iov = new byte[3];
        Check(HostEnvironment.ReadIovecs(memory, 64, 1, iov) == 3, "iovec read length differs");
        Check(iov.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "iovec bytes differ");
        host.Write(1, new byte[] { (byte)'o', (byte)'k' }, 0, 2);
        Check(System.Text.Encoding.UTF8.GetString(output.ToArray()) == "ok", "stdout differs");
        host.Write(2, new byte[] { (byte)'e', (byte)'r', (byte)'r' }, 0, 3);
        Check(System.Text.Encoding.UTF8.GetString(errorOutput.ToArray()) == "err", "stderr differs");

        try { host.SetFile("../escape", Array.Empty<byte>()); throw new Exception("path escape accepted"); }
        catch (ArgumentException) { }
        try { HostEnvironment.ReadString(memory, uint.MaxValue, 1); throw new Exception("invalid pointer accepted"); }
        catch (ArgumentOutOfRangeException) { }

        var bytes = new byte[4];
        host.Entropy(bytes);
        Check(bytes.AsSpan().SequenceEqual(random), "entropy adapter differs");
        Check(host.WallClock().ToUnixTimeMilliseconds() == 1234, "wall clock units differ");
        Check(host.MonotonicClock() == 10, "monotonic clock differs");

        var order = new List<int>();
        host.Enqueue(() => order.Add(1));
        host.ScheduleTimer(5, () => order.Add(2));
        Check(host.Pump(0) == 1 && host.Pump(15) == 1, "callback queue order differs");
        Check(order[0] == 1 && order[1] == 2, "callback order differs");
        host.Exit(7);
        Check(host.HasExited && host.ExitCode == 7, "exit state differs");
        try { host.Exit(8); throw new Exception("double exit accepted"); }
        catch (InvalidOperationException) { }

        var unsupported = new UnsupportedImportException("env", "missing", 1, 2);
        Check(unsupported.Message.Contains("env.missing", StringComparison.Ordinal), "unsupported import name missing");
        Check(unsupported.Arguments.Count == 2, "unsupported import arguments missing");
        Console.WriteLine("PASS: portable host memory, stdout/stderr, virtual files, clocks, entropy, callbacks, and rejection state.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
