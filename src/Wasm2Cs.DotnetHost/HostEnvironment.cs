using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Wasm2Cs;

namespace Wasm2Cs.DotnetHost;

public readonly struct WasmIovec
{
    public WasmIovec(uint address, uint length)
    {
        Address = address;
        Length = length;
    }

    public uint Address { get; }
    public uint Length { get; }
}

public sealed class UnsupportedImportException : Exception
{
    public UnsupportedImportException(string module, string name, IEnumerable<object>? arguments)
        : base("Unsupported import " + module + "." + name + ". Arguments: [" +
            string.Join(", ", (arguments ?? Enumerable.Empty<object>()).Select(value => value?.ToString() ?? "null")) + "].")
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Arguments = (arguments ?? Enumerable.Empty<object>()).ToArray();
    }

    public UnsupportedImportException(string module, string name, params object[] arguments)
        : this(module, name, (IEnumerable<object>)arguments)
    {
    }

    public string Module { get; }
    public string Name { get; }
    public IReadOnlyList<object> Arguments { get; }
}

public sealed class HostEnvironment
{
    private sealed class FileEntry
    {
        public readonly string Path;
        public byte[] Bytes;
        public int Position;
        public readonly bool Writable;

        public FileEntry(string path, byte[] bytes, bool writable)
        {
            Path = path;
            Bytes = bytes;
            Writable = writable;
        }
    }

    private sealed class TimerEntry
    {
        public readonly long Due;
        public readonly long Order;
        public readonly Action Callback;

        public TimerEntry(long due, long order, Action callback)
        {
            Due = due;
            Order = order;
            Callback = callback;
        }
    }

    private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private readonly Dictionary<int, FileEntry> descriptors = new Dictionary<int, FileEntry>();
    private readonly Queue<Action> callbacks = new Queue<Action>();
    private readonly List<TimerEntry> timers = new List<TimerEntry>();
    private readonly Func<long> monotonicClock;
    private long timerOrder;
    private int nextFd = 3;

    public HostEnvironment(
        Func<DateTimeOffset>? wallClock = null,
        Func<long>? monotonicClock = null,
        Action<byte[]>? entropy = null,
        Action<byte[]>? stdout = null,
        Action<byte[]>? stderr = null,
        IEnumerable<string>? arguments = null,
        IEnumerable<KeyValuePair<string, string>>? environment = null,
        IDictionary<string, byte[]>? virtualFiles = null)
    {
        WallClock = wallClock ?? (() => DateTimeOffset.UtcNow);
        this.monotonicClock = monotonicClock ?? new Func<long>(() => System.Environment.TickCount);
        Entropy = entropy ?? FillEntropy;
        Stdout = stdout ?? (_ => { });
        Stderr = stderr ?? (_ => { });
        Arguments = (arguments ?? Enumerable.Empty<string>()).ToArray();
        var environmentValues = new Dictionary<string, string>(StringComparer.Ordinal);
        if (environment != null)
            foreach (var pair in environment) environmentValues[pair.Key] = pair.Value;
        Environment = environmentValues;
        if (virtualFiles != null)
            foreach (var pair in virtualFiles) files[NormalizePath(pair.Key)] = (byte[])pair.Value.Clone();
    }

    public Func<DateTimeOffset> WallClock { get; }
    public Func<long> MonotonicClock => monotonicClock;
    public Action<byte[]> Entropy { get; }
    public Action<byte[]> Stdout { get; }
    public Action<byte[]> Stderr { get; }
    public IReadOnlyList<string> Arguments { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }

    public void SetFile(string path, byte[] content)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        files[NormalizePath(path)] = (byte[])content.Clone();
    }

    public int OpenFile(string path, bool writable = false)
    {
        byte[] content;
        string normalized = NormalizePath(path);
        if (!files.TryGetValue(normalized, out content)) return -1;
        int fd = nextFd++;
        descriptors[fd] = new FileEntry(normalized, content, writable);
        return fd;
    }

    public bool Close(int fd) => descriptors.Remove(fd);

    public int Read(int fd, byte[] destination, int offset, int count)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (offset < 0 || count < 0 || (long)offset + count > destination.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        FileEntry file;
        try { file = GetFile(fd); }
        catch (InvalidOperationException) { return -9; }
        int available = file.Bytes.Length - file.Position;
        int amount = Math.Min(count, Math.Max(0, available));
        Buffer.BlockCopy(file.Bytes, file.Position, destination, offset, amount);
        file.Position += amount;
        return amount;
    }

    public int Write(int fd, byte[] source, int offset, int count)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (offset < 0 || count < 0 || (long)offset + count > source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (fd == 1) { Stdout(Copy(source, offset, count)); return count; }
        if (fd == 2) { Stderr(Copy(source, offset, count)); return count; }
        FileEntry file;
        try { file = GetFile(fd); }
        catch (InvalidOperationException) { return -9; }
        if (!file.Writable) return -1;
        if (file.Position + count > file.Bytes.Length)
        {
            Array.Resize(ref file.Bytes, checked(file.Position + count));
            files[file.Path] = file.Bytes;
            foreach (var descriptor in descriptors.Values)
                if (descriptor.Path == file.Path) descriptor.Bytes = file.Bytes;
        }
        Buffer.BlockCopy(source, offset, file.Bytes, file.Position, count);
        file.Position += count;
        return count;
    }

    public int Read(int fd, WasmMemory memory, uint address, int count)
    {
        if (memory == null) throw new ArgumentNullException(nameof(memory));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var buffer = new byte[count];
        int read = Read(fd, buffer, 0, count);
        if (read > 0) memory.WriteMemory(address, buffer, 0, read);
        return read;
    }

    public int Write(int fd, WasmMemory memory, uint address, int count)
    {
        if (memory == null) throw new ArgumentNullException(nameof(memory));
        return Write(fd, memory.ReadMemory(address, count), 0, count);
    }

    public const int WasiErrnoSuccess = 0;
    public const int WasiErrnoBadf = 8;
    public const int WasiErrnoFault = 21;
    public const int WasiErrnoInval = 28;
    public const int WasiErrnoIo = 29;
    public const int WasiErrnoRofs = 68;

    // wasi_snapshot_preview1.fd_write (i32,i32,i32,i32)->i32.
    // iovs is an array of {buf:u32, buf_len:u32}. nwritten receives the byte count on success.
    // fd 1 and fd 2 use Stdout and Stderr. Other fds use OpenFile. No other Preview1 import is implemented.
    // The caller must already hold the WasmMemory. Owned memory is not published until the generated
    // constructor returns, so this cannot serve an import that runs from a start section.
    public int FdWrite(WasmMemory memory, int fd, int iovs, int iovsLength, int nwritten)
    {
        if (memory == null) throw new ArgumentNullException(nameof(memory));
        uint resultAddress = unchecked((uint)nwritten);
        if (!InRange(memory, resultAddress, 4)) return WasiErrnoFault;

        uint vectorCount = unchecked((uint)iovsLength);
        uint vectorAddress = unchecked((uint)iovs);
        if (vectorCount > uint.MaxValue / 8) return WasiErrnoFault;
        if (vectorCount != 0 && !InRange(memory, vectorAddress, vectorCount * 8)) return WasiErrnoFault;

        var vectors = new List<WasmIovec>();
        uint total = 0;
        for (uint i = 0; i < vectorCount; i++)
        {
            WasmIovec vector = ReadIovec(memory, vectorAddress + (i * 8u));
            if (vector.Length != 0 && !InRange(memory, vector.Address, vector.Length)) return WasiErrnoFault;
            if (vector.Length > uint.MaxValue - total) return WasiErrnoInval;
            total += vector.Length;
            vectors.Add(vector);
        }
        if (total > int.MaxValue) return WasiErrnoInval;

        int writable = RequireWritable(fd);
        if (writable != WasiErrnoSuccess) return writable;

        int written;
        if (total == 0) written = 0;
        else
        {
            var payload = new byte[(int)total];
            int filled = 0;
            foreach (WasmIovec vector in vectors)
            {
                if (vector.Length == 0) continue;
                int amount = (int)vector.Length;
                memory.ReadMemory(vector.Address, payload, filled, amount);
                filled += amount;
            }
            written = Write(fd, payload, 0, payload.Length);
            if (written < 0) return WriteErrno(written);
        }
        WriteUInt32(memory, resultAddress, (uint)written);
        return WasiErrnoSuccess;
    }

    private int RequireWritable(int fd)
    {
        if (fd == 1 || fd == 2) return WasiErrnoSuccess;
        int written = Write(fd, Array.Empty<byte>(), 0, 0);
        return written < 0 ? WriteErrno(written) : WasiErrnoSuccess;
    }

    private static int WriteErrno(int written)
    {
        if (written == -9) return WasiErrnoBadf;
        if (written == -1) return WasiErrnoRofs;
        return WasiErrnoIo;
    }

    private static bool InRange(WasmMemory memory, uint address, uint length)
    {
        ulong end = (ulong)address + length;
        return end >= address && end <= (ulong)memory.Size;
    }

    public void Exit(int code)
    {
        if (HasExited) throw new InvalidOperationException("The host has already exited.");
        HasExited = true;
        ExitCode = code;
    }

    public void Enqueue(Action callback) { callbacks.Enqueue(callback ?? throw new ArgumentNullException(nameof(callback))); }

    public void ScheduleTimer(long delay, Action callback)
    {
        if (delay < 0) throw new ArgumentOutOfRangeException(nameof(delay));
        timers.Add(new TimerEntry(checked(monotonicClock() + delay), timerOrder++, callback ?? throw new ArgumentNullException(nameof(callback))));
    }

    public int Pump() => Pump(monotonicClock());

    public int Pump(long now)
    {
        int count = 0;
        while (callbacks.Count != 0) { callbacks.Dequeue()(); count++; }
        timers.Sort((x, y) => { int result = x.Due.CompareTo(y.Due); return result != 0 ? result : x.Order.CompareTo(y.Order); });
        while (timers.Count != 0 && timers[0].Due <= now)
        {
            Action callback = timers[0].Callback;
            timers.RemoveAt(0);
            callback();
            count++;
            while (callbacks.Count != 0) { callbacks.Dequeue()(); count++; }
        }
        return count;
    }

    public static string ReadString(WasmMemory memory, uint address, int maxBytes = int.MaxValue)
    {
        if (memory == null) throw new ArgumentNullException(nameof(memory));
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var bytes = new List<byte>();
        for (int i = 0; i < maxBytes; i++)
        {
            byte value = memory.ReadByte(checked(address + (uint)i));
            if (value == 0) break;
            bytes.Add(value);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static int WriteString(WasmMemory memory, uint address, string value, bool nulTerminate = true)
    {
        if (memory == null) throw new ArgumentNullException(nameof(memory));
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value)));
        memory.WriteMemory(address, bytes);
        if (nulTerminate) memory.WriteByte(checked(address + (uint)bytes.Length), 0);
        return bytes.Length + (nulTerminate ? 1 : 0);
    }

    public static WasmIovec ReadIovec(WasmMemory memory, uint address)
    {
        return new WasmIovec(ReadUInt32(memory, address), ReadUInt32(memory, checked(address + 4)));
    }

    public static int ReadIovecs(WasmMemory memory, uint address, int count, byte[] destination)
    {
        if (count < 0 || destination == null) throw new ArgumentOutOfRangeException(nameof(count));
        int written = 0;
        for (int i = 0; i < count && written < destination.Length; i++)
        {
            WasmIovec vector = ReadIovec(memory, checked(address + (uint)(i * 8)));
            int amount = Math.Min(checked((int)vector.Length), destination.Length - written);
            memory.ReadMemory(vector.Address, destination, written, amount);
            written += amount;
        }
        return written;
    }

    private FileEntry GetFile(int fd)
    {
        FileEntry file;
        if (!descriptors.TryGetValue(fd, out file)) throw new InvalidOperationException("Unknown file descriptor.");
        return file;
    }

    public static uint ReadUInt32(WasmMemory memory, uint address)
    {
        // Shift as uint. A checked cast of the int expression throws when bit 31 is set.
        return memory.ReadByte(address)
            | ((uint)memory.ReadByte(checked(address + 1)) << 8)
            | ((uint)memory.ReadByte(checked(address + 2)) << 16)
            | ((uint)memory.ReadByte(checked(address + 3)) << 24);
    }

    public static void WriteUInt32(WasmMemory memory, uint address, uint value)
    {
        memory.WriteByte(address, unchecked((byte)value));
        memory.WriteByte(checked(address + 1), unchecked((byte)(value >> 8)));
        memory.WriteByte(checked(address + 2), unchecked((byte)(value >> 16)));
        memory.WriteByte(checked(address + 3), unchecked((byte)(value >> 24)));
    }

    private static byte[] Copy(byte[] source, int offset, int count)
    {
        var result = new byte[count];
        Buffer.BlockCopy(source, offset, result, 0, count);
        return result;
    }

    private static string NormalizePath(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        var parts = path.Replace('\\', '/').Split('/');
        var normalized = new List<string>();
        foreach (string part in parts)
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (normalized.Count == 0) throw new ArgumentException("Path escapes the virtual root.", nameof(path)); normalized.RemoveAt(normalized.Count - 1); }
            else normalized.Add(part);
        }
        return "/" + string.Join("/", normalized.ToArray());
    }

    private static void FillEntropy(byte[] bytes)
    {
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
    }
}
