using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasm2Cs;
using Wasm2Cs.DotnetHost;

public static class MonoBootChecks
{
    public static void Verify()
    {
        VerifySuccess(0);
        VerifySuccess(17);
        VerifyFailures();
        VerifyStateRejections();
        VerifyRuntimeOptions();
    }

    private static void VerifySuccess(int returnCode)
    {
        var fake = new FakeNativeExports { ManagedReturnCode = returnCode };
        var boot = CreateBoot(fake);

        boot.Start();
        Check(boot.State == MonoBootState.Running, "successful start did not run");
        int result = boot.Run();
        Check(result == returnCode && boot.ManagedReturnCode == returnCode, "managed return code was not preserved");
        boot.Exit(returnCode);

        Check(boot.State == MonoBootState.Exited && boot.ExitCode == returnCode, "exit code was not preserved");
        Check(string.Join(",", boot.PhaseLog) == "constructors,add-assembly:Main.dll,add-assembly:Support.dll,load-runtime,configure-args,invoke-main,exit",
            "boot phase order differs");
        Check(fake.Calls.SequenceEqual(new[] { "constructors", "add:Main.dll", "add:Support.dll", "load", "args", "invoke", "exit" }),
            "native call order differs");
        Check(fake.DebugLevel == 3 && fake.MainAssemblyName == "Main.dll", "runtime configuration was not forwarded");
        Check(fake.Arguments.SequenceEqual(new[] { "Main.dll", "--one", "two" }), "arguments were not forwarded");
        Check(fake.Properties.SequenceEqual(new[] { "A=one", "B=two" }), "runtime properties were not forwarded");
        Check(fake.Assemblies.SequenceEqual(new[] { "Main.dll:3", "Support.dll:2" }), "assemblies were not forwarded");
        Check(fake.ExitCode == returnCode, "exit export received the wrong code");
        Check(fake.Outstanding.Count == 0, "successful boot leaked an allocation");
    }

    private static void VerifyFailures()
    {
        VerifyFailure("constructors", fake => fake.ConstructorResult = 1);
        VerifyFailure("add-assembly", fake => fake.AddAssemblyResult = 0);
        VerifyFailure("load-runtime", fake => fake.LoadFailure = true);
        VerifyFailure("configure-args", fake => fake.ConfigureFailure = true, startAndRun: true);
        VerifyFailure("invoke-main", fake => fake.InvokeFailure = true, startAndRun: true);
        VerifyFailure("exit", fake => fake.ExitFailure = true, startAndRun: true);
    }

    private static void VerifyFailure(string phase, Action<FakeNativeExports> configure, bool startAndRun = false)
    {
        var fake = new FakeNativeExports();
        configure(fake);
        var boot = CreateBoot(fake);
        try
        {
            boot.Start();
            if (startAndRun)
            {
                boot.Run();
                boot.Exit(0);
            }
            else throw new Exception("expected " + phase + " failure");
        }
        catch (Exception) when (boot.State == MonoBootState.Failed)
        {
        }

        Check(boot.State == MonoBootState.Failed, phase + " failure did not transition to Failed");
        Check(boot.Error != null, phase + " failure was not recorded");
        Check(fake.Outstanding.Count == 0, phase + " failure leaked an allocation");
    }

    private static void VerifyStateRejections()
    {
        VerifyRejected(CreateBoot(new FakeNativeExports()), _ => { }, "run before start", boot => boot.Run());
        VerifyRejected(CreateBoot(new FakeNativeExports()), _ => { }, "exit before start", boot => boot.Exit(0));
        VerifyRejected(CreateBoot(new FakeNativeExports()), boot => boot.Start(), "duplicate start", boot => boot.Start());
        VerifyRejected(CreateBoot(new FakeNativeExports()), boot => { boot.Start(); boot.Run(); }, "duplicate run", boot => boot.Run());
        VerifyRejected(CreateBoot(new FakeNativeExports()), boot => { boot.Start(); boot.Run(); boot.Exit(0); }, "duplicate exit", boot => boot.Exit(0));
    }

    private static void VerifyRuntimeOptions()
    {
        var fake = new FakeNativeExports();
        var boot = CreateBoot(fake, new[] { "--no-jiterpreter-traces-enabled", "--second" });
        boot.Start();
        Check(fake.RuntimeOptions.SequenceEqual(new[] { "--no-jiterpreter-traces-enabled", "--second" }),
            "runtime options were not forwarded");
        Check(fake.Outstanding.Count == 0, "runtime option forwarding leaked an allocation");
    }

    private static void VerifyRejected(MonoBoot boot, Action<MonoBoot> setup, string message, Action<MonoBoot> rejected)
    {
        setup(boot);
        ExpectInvalid(() => rejected(boot), message);
        Check(boot.State == MonoBootState.Failed, message + " did not reject into Failed");
    }

    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); throw new Exception(message + " was accepted"); }
        catch (InvalidOperationException) { }
    }

    private static MonoBoot CreateBoot(FakeNativeExports fake, IEnumerable<string>? runtimeOptions = null)
    {
        return new MonoBoot(fake.memory, fake.Exports, new MonoBootRequest(
            new[] { new MonoAssembly("Main.dll", new byte[] { 1, 2, 3 }), new MonoAssembly("Support.dll", new byte[] { 4, 5 }) },
            new[] { "--one", "two" }, "Main.dll", 3,
            new[] { new KeyValuePair<string, string>("A", "one"), new KeyValuePair<string, string>("B", "two") },
            runtimeOptions));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FakeNativeExports
    {
        private uint nextAddress = 32;
        private readonly Dictionary<uint, int> allocations = new Dictionary<uint, int>();

        public readonly List<string> Calls = new List<string>();
        public readonly List<string> Assemblies = new List<string>();
        public readonly List<string> Arguments = new List<string>();
        public readonly List<string> Properties = new List<string>();
        public readonly List<string> RuntimeOptions = new List<string>();
        public int ConstructorResult;
        public int AddAssemblyResult = 1;
        public int ManagedReturnCode;
        public bool LoadFailure;
        public bool ConfigureFailure;
        public bool InvokeFailure;
        public bool ExitFailure;
        public int DebugLevel;
        public string MainAssemblyName = "";
        public int ExitCode;
        public Dictionary<uint, int> Outstanding => allocations;

        public MonoBoot.NativeExports Exports => new MonoBoot.NativeExports
        {
            CallConstructors = () =>
            {
                Calls.Add("constructors");
                return ConstructorResult;
            },
            Malloc = length =>
            {
                uint address = nextAddress;
                nextAddress += (uint)Math.Max(length, 1) + 8;
                allocations[address] = length;
                return address;
            },
            Free = address =>
            {
                if (!allocations.Remove(address)) throw new Exception("fake freed an unknown allocation");
            },
            AddAssembly = (name, data, size) =>
            {
                Calls.Add("add:" + ReadString(name));
                Assemblies.Add(ReadString(name) + ":" + size);
                if (AddAssemblyResult != 0 && !allocations.Remove(data)) throw new Exception("fake freed an unknown allocation");
                return AddAssemblyResult;
            },
            LoadRuntime = (debug, count, keys, values) =>
            {
                Calls.Add("load");
                DebugLevel = debug;
                for (int i = 0; i < count; i++) Properties.Add(ReadString(ReadUInt32(keys, i)) + "=" + ReadString(ReadUInt32(values, i)));
                if (LoadFailure) throw new Exception("load failed");
            },
            ParseRuntimeOptions = (count, vector) =>
            {
                for (int i = 0; i < count; i++) RuntimeOptions.Add(ReadString(HostEnvironment.ReadUInt32(memory, vector + (uint)(i * 4))));
            },
            ConfigureArgs = (count, vector) =>
            {
                Calls.Add("args");
                for (int i = 0; i < count; i++) Arguments.Add(ReadString(ReadUInt32(vector, i)));
                MainAssemblyName = Arguments[0];
                if (ConfigureFailure) throw new Exception("args failed");
            },
            InvokeMain = () =>
            {
                Calls.Add("invoke");
                if (InvokeFailure) throw new Exception("invoke failed");
                return ManagedReturnCode;
            },
            Exit = code =>
            {
                Calls.Add("exit");
                ExitCode = code;
                if (ExitFailure) throw new Exception("exit failed");
            }
        };

        string ReadString(uint address)
        {
            var bytes = new List<byte>();
            while (true)
            {
                byte value = memory.ReadByte(address++);
                if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(value);
            }
        }

        uint ReadUInt32(uint address, int index)
        {
            return HostEnvironment.ReadUInt32(memory, address + (uint)(index * 4));
        }

        internal WasmMemory memory = new WasmMemory(1);
    }
}
