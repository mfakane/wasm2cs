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
            virtualFiles: new Dictionary<string, byte[]>
            {
                ["/input"] = new byte[] { 1, 2, 3 },
                ["/rw"] = Array.Empty<byte>(),
                ["/ro"] = new byte[] { 1, 2, 3 }
            });
        var memory = new WasmMemory(1);

        int fd = host.OpenFile("input");
        Check(fd >= 3, "virtual file was not opened");
        Check(host.Read(fd, memory, 32, 3) == 3, "file read length differs");
        Check(host.Read(fd, memory, 32, 3) == 0, "EOF was not reported");
        Check(memory.ReadMemory(32, 3).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "file bytes differ");
        Check(host.OpenFile("/missing") == -1, "missing file did not fail");

        int writeFd = host.OpenFile("/rw", writable: true);
        Check(host.Write(writeFd, new byte[] { 7, 8, 9 }, 0, 3) == 3, "writable file write length differs");
        int readBackFd = host.OpenFile("/rw");
        var readBack = new byte[3];
        Check(host.Read(readBackFd, readBack, 0, 3) == 3 && readBack.AsSpan().SequenceEqual(new byte[] { 7, 8, 9 }), "writable file read-back differs");
        int readOnlyFd = host.OpenFile("/ro");
        Check(host.Write(readOnlyFd, new byte[] { 9 }, 0, 1) == -1, "read-only file write did not fail");
        Check(host.Read(999, new byte[1], 0, 1) == -9 && host.Write(999, new byte[1], 0, 1) == -9, "invalid file descriptor did not fail");

        HostEnvironment.WriteUInt32(memory, 64, 32);
        HostEnvironment.WriteUInt32(memory, 68, 3);
        var iov = new byte[3];
        Check(HostEnvironment.ReadIovecs(memory, 64, 1, iov) == 3, "iovec read length differs");
        Check(iov.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "iovec bytes differ");
        try { HostEnvironment.ReadIovecs(memory, 65535, 1, new byte[1]); throw new Exception("out-of-range iovec pointer accepted"); }
        catch (ArgumentOutOfRangeException) { }
        HostEnvironment.WriteUInt32(memory, 64, 65535);
        HostEnvironment.WriteUInt32(memory, 68, 2);
        try { HostEnvironment.ReadIovecs(memory, 64, 1, new byte[2]); throw new Exception("out-of-range iovec payload accepted"); }
        catch (ArgumentOutOfRangeException) { }
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
        VerifyFdWrite();
        VerifyProcExit();
        Console.WriteLine("PASS: portable host memory, stdout/stderr, virtual files, clocks, entropy, callbacks, rejection state, wasi fd_write, and wasi proc_exit.");
    }

    private static void VerifyProcExit()
    {
        foreach (int code in new[] { 0, 3, -1, int.MaxValue })
        {
            var host = new HostEnvironment();
            try { host.ProcExit(code); throw new Exception("proc_exit returned"); }
            catch (WasiProcExitException exception) { Check(exception.ExitCode == code, "proc_exit exception code differs"); }
            Check(host.HasExited && host.ExitCode == code, "proc_exit did not record the raw code");
            try { host.ProcExit(code); throw new Exception("second proc_exit accepted"); }
            catch (InvalidOperationException) { }
            Check(host.ExitCode == code, "second proc_exit changed the code");
        }
    }

    private static void VerifyFdWrite()
    {
        var stdout = new List<byte>();
        var stderr = new List<byte>();
        var host = new HostEnvironment(
            stdout: stdout.AddRange,
            stderr: stderr.AddRange,
            virtualFiles: new Dictionary<string, byte[]>
            {
                ["/out"] = Array.Empty<byte>(),
                ["/ro"] = new byte[] { 1, 2, 3 }
            });
        var memory = new WasmMemory(1);
        byte[] hello = System.Text.Encoding.ASCII.GetBytes("hello wasi\n");
        memory.WriteMemory(32, hello);
        PutIovec(memory, 64, 32, (uint)hello.Length);
        HostEnvironment.WriteUInt32(memory, 80, 0xffffffff);
        Check(host.FdWrite(memory, 1, 64, 1, 80) == HostEnvironment.WasiErrnoSuccess, "fd_write stdout errno differs");
        Check(HostEnvironment.ReadUInt32(memory, 80) == (uint)hello.Length, "fd_write stdout nwritten differs");
        Check(System.Text.Encoding.ASCII.GetString(stdout.ToArray()) == "hello wasi\n", "fd_write stdout bytes differ");

        memory.WriteMemory(96, System.Text.Encoding.ASCII.GetBytes("err"));
        PutIovec(memory, 112, 96, 3);
        Check(host.FdWrite(memory, 2, 112, 1, 80) == HostEnvironment.WasiErrnoSuccess, "fd_write stderr errno differs");
        Check(System.Text.Encoding.ASCII.GetString(stderr.ToArray()) == "err", "fd_write stderr bytes differ");

        int before = stdout.Count;
        PutIovec(memory, 120, 32, 5);
        PutIovec(memory, 128, 37, (uint)hello.Length - 5);
        Check(host.FdWrite(memory, 1, 120, 2, 80) == HostEnvironment.WasiErrnoSuccess, "fd_write multi-iovec errno differs");
        Check(HostEnvironment.ReadUInt32(memory, 80) == (uint)hello.Length, "fd_write multi-iovec nwritten differs");
        Check(System.Text.Encoding.ASCII.GetString(stdout.ToArray()) == "hello wasi\nhello wasi\n", "fd_write multi-iovec bytes differ");
        Check(stdout.Count == before + hello.Length, "fd_write multi-iovec byte count differs");

        HostEnvironment.WriteUInt32(memory, 80, 0xffffffff);
        Check(host.FdWrite(memory, 1, 64, 0, 80) == HostEnvironment.WasiErrnoSuccess, "empty fd_write errno differs");
        Check(HostEnvironment.ReadUInt32(memory, 80) == 0, "empty fd_write nwritten differs");
        Check(stdout.Count == before + hello.Length, "empty fd_write wrote stdout");

        HostEnvironment.WriteUInt32(memory, 80, 0xffffffff);
        Check(host.FdWrite(memory, 0, 64, 1, 80) == HostEnvironment.WasiErrnoBadf, "stdin fd_write did not return EBADF");
        Check(host.FdWrite(memory, 99, 64, 1, 80) == HostEnvironment.WasiErrnoBadf, "unknown fd_write did not return EBADF");
        Check(HostEnvironment.ReadUInt32(memory, 80) == 0xffffffff, "failed fd_write changed nwritten");

        int readOnly = host.OpenFile("/ro");
        Check(host.FdWrite(memory, readOnly, 64, 1, 80) == HostEnvironment.WasiErrnoRofs, "read-only fd_write did not return EROFS");
        var unchanged = new byte[3];
        int readBack = host.OpenFile("/ro");
        Check(host.Read(readBack, unchanged, 0, 3) == 3 && unchanged.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "read-only file changed");

        int writeFd = host.OpenFile("/out", writable: true);
        Check(host.FdWrite(memory, writeFd, 64, 1, 80) == HostEnvironment.WasiErrnoSuccess, "file fd_write errno differs");
        Check(HostEnvironment.ReadUInt32(memory, 80) == (uint)hello.Length, "file fd_write nwritten differs");
        var fileBytes = new byte[hello.Length];
        int writtenBack = host.OpenFile("/out");
        Check(host.Read(writtenBack, fileBytes, 0, fileBytes.Length) == fileBytes.Length && fileBytes.AsSpan().SequenceEqual(hello), "file fd_write bytes differ");

        int stdoutBeforeFault = stdout.Count;
        HostEnvironment.WriteUInt32(memory, 80, 0xffffffff);
        Check(host.FdWrite(memory, 1, 65536, 1, 80) == HostEnvironment.WasiErrnoFault, "out-of-range iovec did not return EFAULT");
        PutIovec(memory, 64, 65536, 1);
        Check(host.FdWrite(memory, 1, 64, 1, 80) == HostEnvironment.WasiErrnoFault, "out-of-range payload did not return EFAULT");
        PutIovec(memory, 64, 32, (uint)hello.Length);
        Check(host.FdWrite(memory, 1, 64, 1, 65536) == HostEnvironment.WasiErrnoFault, "out-of-range nwritten did not return EFAULT");
        Check(host.FdWrite(memory, 1, 64, -1, 80) == HostEnvironment.WasiErrnoFault, "negative iovs length did not return EFAULT");
        Check(HostEnvironment.ReadUInt32(memory, 80) == 0xffffffff, "faulting fd_write changed nwritten");
        Check(stdout.Count == stdoutBeforeFault, "faulting fd_write wrote stdout");
        PutIovec(memory, 64, (uint)memory.Size, 0);
        Check(host.FdWrite(memory, 1, 64, 1, 80) == HostEnvironment.WasiErrnoSuccess, "zero-length one-past-end iovec failed");
        Check(HostEnvironment.ReadUInt32(memory, 80) == 0, "zero-length iovec nwritten differs");

        PutIovec(memory, 64, 0x80000000, 1);
        HostEnvironment.WriteUInt32(memory, 80, 0xffffffff);
        Check(host.FdWrite(memory, 1, 64, 1, 80) == HostEnvironment.WasiErrnoFault, "high iovec pointer did not return EFAULT");
        Check(HostEnvironment.ReadUInt32(memory, 80) == 0xffffffff, "high iovec pointer changed nwritten");
        Check(HostEnvironment.ReadUInt32(memory, 64) == 0x80000000, "high iovec pointer was not read");

        try { host.FdWrite(null!, 1, 0, 0, 0); throw new Exception("null memory accepted"); }
        catch (ArgumentNullException) { }
    }

    private static void PutIovec(WasmMemory memory, uint address, uint pointer, uint length)
    {
        HostEnvironment.WriteUInt32(memory, address, pointer);
        HostEnvironment.WriteUInt32(memory, address + 4, length);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
