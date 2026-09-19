using System;
using System.Collections.Generic;
using System.Text;
using Wasm2Cs;

namespace Wasm2Cs.DotnetHost;

public enum MonoBootState
{
    Created,
    Running,
    Exited,
    Failed
}

public sealed class MonoAssembly
{
    private readonly byte[] data;

    public MonoAssembly(string name, byte[] data)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('\0') >= 0)
            throw new ArgumentException("Assembly name must be non-empty and NUL-free.", nameof(name));
        if (data == null || data.Length == 0) throw new ArgumentException("Assembly data is required.", nameof(data));
        Name = name;
        this.data = (byte[])data.Clone();
    }

    public string Name { get; }
    public byte[] Data => (byte[])data.Clone();
}

public sealed class MonoBootRequest
{
    public MonoBootRequest(IEnumerable<MonoAssembly> assemblies, IEnumerable<string>? arguments = null,
        string mainAssemblyName = "SelfHosting.dll", int debugLevel = 0,
        IEnumerable<KeyValuePair<string, string>>? runtimeProperties = null)
    {
        if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));
        if (string.IsNullOrEmpty(mainAssemblyName) || mainAssemblyName.IndexOf('\0') >= 0)
            throw new ArgumentException("Main assembly name must be non-empty and NUL-free.", nameof(mainAssemblyName));
        var assemblyList = new List<MonoAssembly>();
        foreach (var assembly in assemblies)
            assemblyList.Add(assembly ?? throw new ArgumentException("Assembly cannot be null.", nameof(assemblies)));

        var argumentList = new List<string>();
        if (arguments != null)
            foreach (var argument in arguments)
                argumentList.Add(argument ?? throw new ArgumentException("Argument cannot be null.", nameof(arguments)));

        Assemblies = assemblyList.AsReadOnly();
        Arguments = argumentList.AsReadOnly();
        MainAssemblyName = mainAssemblyName;
        DebugLevel = debugLevel;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (runtimeProperties != null)
            foreach (var property in runtimeProperties)
            {
                if (string.IsNullOrEmpty(property.Key) || property.Key.IndexOf('\0') >= 0 || property.Value == null || property.Value.IndexOf('\0') >= 0)
                    throw new ArgumentException("Runtime property keys and values must be NUL-free.", nameof(runtimeProperties));
                properties[property.Key] = property.Value;
            }
        RuntimeProperties = properties;
    }

    public IReadOnlyList<MonoAssembly> Assemblies { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string MainAssemblyName { get; }
    public int DebugLevel { get; }
    public IReadOnlyDictionary<string, string> RuntimeProperties { get; }
}

public sealed class MonoBoot
{
    public sealed class NativeExports
    {
        public Func<int> CallConstructors { get; set; } = null!;
        public Func<int, uint> Malloc { get; set; } = null!;
        public Action<uint> Free { get; set; } = null!;
        public Func<uint, uint, int, int> AddAssembly { get; set; } = null!;
        public Action<int, int, uint, uint> LoadRuntime { get; set; } = null!;
        public Action<int, uint>? ConfigureArgs { get; set; }
        public Func<int> InvokeMain { get; set; } = null!;
        public Action<int> Exit { get; set; } = null!;

        internal void Validate()
        {
            if (CallConstructors == null) throw new ArgumentNullException(nameof(CallConstructors));
            if (Malloc == null) throw new ArgumentNullException(nameof(Malloc));
            if (Free == null) throw new ArgumentNullException(nameof(Free));
            if (AddAssembly == null) throw new ArgumentNullException(nameof(AddAssembly));
            if (LoadRuntime == null) throw new ArgumentNullException(nameof(LoadRuntime));
            if (InvokeMain == null) throw new ArgumentNullException(nameof(InvokeMain));
            if (Exit == null) throw new ArgumentNullException(nameof(Exit));
        }
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly WasmMemory memory;
    private readonly NativeExports exports;
    private readonly MonoBootRequest request;
    private readonly List<string> phaseLog = new List<string>();
    private MonoBootState state;

    public MonoBoot(WasmMemory memory, NativeExports exports, MonoBootRequest request)
    {
        this.memory = memory ?? throw new ArgumentNullException(nameof(memory));
        this.exports = exports ?? throw new ArgumentNullException(nameof(exports));
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        exports.Validate();
        state = MonoBootState.Created;
    }

    public MonoBootState State => state;
    public IReadOnlyList<string> PhaseLog => phaseLog.AsReadOnly();
    public int? ManagedReturnCode { get; private set; }
    public int? ExitCode { get; private set; }
    public Exception? Error { get; private set; }

    public void Start()
    {
        RequireState(MonoBootState.Created, "start");
        try
        {
            Phase("constructors");
            RequireSuccess(exports.CallConstructors(), "constructors");

            foreach (var assembly in request.Assemblies) AddAssembly(assembly);

            Phase("load-runtime");
            LoadRuntime();

            state = MonoBootState.Running;
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
    }

    public int Run()
    {
        RequireState(MonoBootState.Running, "run");
        if (ManagedReturnCode.HasValue)
        {
            var exception = new InvalidOperationException("Cannot run managed entry point more than once.");
            Fail(exception);
            throw exception;
        }
        try
        {
            if (exports.ConfigureArgs != null)
            {
                Phase("configure-args");
                ConfigureArgs();
            }
            Phase("invoke-main");
            ManagedReturnCode = exports.InvokeMain();
            return ManagedReturnCode.Value;
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
    }

    public void Exit(int exitCode)
    {
        RequireState(MonoBootState.Running, "exit");
        if (!ManagedReturnCode.HasValue)
        {
            var exception = new InvalidOperationException("Cannot exit before the managed entry point returns.");
            Fail(exception);
            throw exception;
        }
        try
        {
            Phase("exit");
            exports.Exit(exitCode);
            ExitCode = exitCode;
            state = MonoBootState.Exited;
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
    }

    private void AddAssembly(MonoAssembly assembly)
    {
        Phase("add-assembly:" + assembly.Name);
        byte[] name = Utf8.GetBytes(assembly.Name + "\0");
        uint nameAddress = Allocate(name.Length);
        uint dataAddress = 0;
        bool dataOwnedByRuntime = false;
        try
        {
            memory.WriteMemory(nameAddress, name);
            dataAddress = Allocate(assembly.Data.Length);
            memory.WriteMemory(dataAddress, assembly.Data);
            int result = exports.AddAssembly(nameAddress, dataAddress, assembly.Data.Length);
            if (result == 0) throw new InvalidOperationException("assembly " + assembly.Name + " was rejected.");
            // mono_wasm_add_assembly retains the bytes as a bundled resource and
            // frees them when the runtime releases that resource.
            dataOwnedByRuntime = true;
        }
        finally
        {
            if (dataAddress != 0 && !dataOwnedByRuntime) exports.Free(dataAddress);
            exports.Free(nameAddress);
        }
    }

    private void LoadRuntime()
    {
        var keys = new List<uint>();
        var values = new List<uint>();
        uint keyVector = 0;
        uint valueVector = 0;
        try
        {
            foreach (var property in request.RuntimeProperties)
            {
                keys.Add(AllocateUtf8(property.Key));
                values.Add(AllocateUtf8(property.Value));
            }
            if (keys.Count != 0)
            {
                keyVector = Allocate(checked(keys.Count * 4));
                valueVector = Allocate(checked(values.Count * 4));
                for (int i = 0; i < keys.Count; i++)
                {
                    HostEnvironment.WriteUInt32(memory, checked(keyVector + (uint)(i * 4)), keys[i]);
                    HostEnvironment.WriteUInt32(memory, checked(valueVector + (uint)(i * 4)), values[i]);
                }
            }
            exports.LoadRuntime(request.DebugLevel, keys.Count, keyVector, valueVector);
        }
        finally
        {
            if (valueVector != 0) exports.Free(valueVector);
            if (keyVector != 0) exports.Free(keyVector);
            for (int i = values.Count - 1; i >= 0; i--) exports.Free(values[i]);
            for (int i = keys.Count - 1; i >= 0; i--) exports.Free(keys[i]);
        }
    }

    private void ConfigureArgs()
    {
        var addresses = new uint[request.Arguments.Count + 1];
        var allocated = new List<uint>();
        uint vectorAddress = 0;
        try
        {
            addresses[0] = AllocateUtf8(request.MainAssemblyName);
            allocated.Add(addresses[0]);
            for (int i = 0; i < request.Arguments.Count; i++)
            {
                addresses[i + 1] = AllocateUtf8(request.Arguments[i]);
                allocated.Add(addresses[i + 1]);
            }

            vectorAddress = Allocate(checked(addresses.Length * 4));
            for (int i = 0; i < addresses.Length; i++)
                HostEnvironment.WriteUInt32(memory, checked(vectorAddress + (uint)(i * 4)), addresses[i]);

            exports.ConfigureArgs!(addresses.Length, vectorAddress);
        }
        finally
        {
            if (vectorAddress != 0) exports.Free(vectorAddress);
            for (int i = allocated.Count - 1; i >= 0; i--) exports.Free(allocated[i]);
        }
    }

    private uint Allocate(int length)
    {
        uint address = exports.Malloc(length);
        if (address == 0) throw new InvalidOperationException("Runtime malloc returned a null address.");
        if ((ulong)address + (ulong)Math.Max(length, 1) > (ulong)memory.Size)
            throw new InvalidOperationException("Runtime malloc returned an address outside linear memory: " + address + ".");
        return address;
    }

    private uint AllocateUtf8(string value)
    {
        byte[] bytes = Utf8.GetBytes(value + "\0");
        uint address = Allocate(bytes.Length);
        memory.WriteMemory(address, bytes);
        return address;
    }

    private void RequireState(MonoBootState expected, string operation)
    {
        if (state != expected)
        {
            var exception = new InvalidOperationException("Cannot " + operation + " while boot is " + state + ".");
            if (state != MonoBootState.Failed) Fail(exception);
            throw exception;
        }
    }

    private void RequireSuccess(int result, string phase)
    {
        if (result != 0) throw new InvalidOperationException(phase + " returned " + result + ".");
    }

    private void Phase(string name) { phaseLog.Add(name); }

    private void Fail(Exception exception)
    {
        Error = exception;
        state = MonoBootState.Failed;
    }
}
