using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

// SH-11 deliberately keeps the probe in the managed guest. The outer runner
// may compare its result, but it must not calculate the result on its behalf.
public static class ManagedProbes
{
    private const int Seed = 0x5eed;
    private static int invocationCount;
    private static int reentryCount;

    public static int InvocationCount => invocationCount;
    public static int ReentryCount => reentryCount;

    public static string Run()
    {
        invocationCount++;
        var result = new Result
        {
            Invocation = invocationCount,
            Utf8 = Utf8Probe(),
            Collections = CollectionsProbe(),
            Dictionary = DictionaryProbe(),
            Generics = GenericProbe(),
            Linq = LinqProbe(),
            Record = RecordProbe(),
            Numeric = NumericProbe(),
            Exceptions = ExceptionProbe(),
            Recursion = RecursiveProbe(6),
            Delegate = DelegateProbe(),
            Json = JsonProbe(),
            GarbageCollection = GarbageCollectionProbe(),
            GuestHeap = GC.GetTotalMemory(false)
        };
        result.Passed = result.Utf8 == "Wasm2Cs-日本語-😀|22" &&
            result.Collections == "3,1,4,1,5|4|14|3" &&
            result.Dictionary == "alpha=4;beta=2|2" &&
            result.Generics == "generic:7|generic:3" &&
            result.Linq == "2,4,6|12" &&
            result.Record == "True|True|True" &&
            result.Numeric == "1,234,567.5|000042" &&
            result.Exceptions == "caught|finally" &&
            result.Recursion == "720" &&
            result.Delegate == "15" &&
            result.Json == "Wasm2Cs|3|True" &&
            result.GarbageCollection == "bytes:survivor-content|string:survivor-string|record:survivor:18|checksum:2016|seed:24301";
        result.Fingerprint = string.Join(";", new[]
        {
            result.Utf8, result.Collections, result.Dictionary, result.Generics,
            result.Linq, result.Record, result.Numeric, result.Exceptions,
            result.Recursion, result.Delegate, result.Json, result.GarbageCollection
        });
        return JsonSerializer.Serialize(result);
    }

    public static int Reenter()
    {
        reentryCount++;
        return reentryCount;
    }

    private static string Utf8Probe()
    {
        const string value = "Wasm2Cs-日本語-😀";
        var bytes = Encoding.UTF8.GetBytes(value);
        var base64 = Convert.ToBase64String(bytes);
        var roundTrip = Convert.FromBase64String(base64);
        var builder = new StringBuilder();
        builder.Append(Encoding.UTF8.GetString(roundTrip)).Append('|').Append(roundTrip.Length);
        return builder.ToString();
    }

    private static string CollectionsProbe()
    {
        var source = new[] { 3, 1, 4, 1, 5 };
        var copy = new int[source.Length];
        Array.Copy(source, copy, source.Length);
        var bytes = new byte[copy.Length * sizeof(int)];
        Buffer.BlockCopy(copy, 0, bytes, 0, bytes.Length);
        var roundTrip = new int[copy.Length];
        Buffer.BlockCopy(bytes, 0, roundTrip, 0, bytes.Length);
        return string.Join(",", roundTrip) + "|" + roundTrip.Distinct().Count() + "|" + roundTrip.Sum() +
            "|" + BitConverter.ToInt32(bytes, 0);
    }

    private static string DictionaryProbe()
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["alpha"] = 1,
            ["beta"] = 2
        };
        values["alpha"] += 3;
        return $"alpha={values["alpha"]};beta={values["beta"]}|{values.Count}";
    }

    private static string GenericProbe()
    {
        return $"{Identity("generic", 7)}|{Identity("generic", 3)}";
    }

    private static string Identity<T>(string prefix, T value) => prefix + ":" + value;

    private static string RecursiveProbe(int value)
    {
        static int Factorial(int current) => current < 2 ? 1 : current * Factorial(current - 1);
        return Factorial(value).ToString(CultureInfo.InvariantCulture);
    }

    private static string LinqProbe()
    {
        var values = Enumerable.Range(1, 6)
            .Where(value => value % 2 == 0)
            .Select(value => value)
            .OrderByDescending(value => value)
            .OrderBy(value => value)
            .ToArray();
        return $"{string.Join(",", values)}|{values.Sum()}";
    }

    private static string RecordProbe()
    {
        var first = new ProbeRecord("probe", 7);
        var same = new ProbeRecord("probe", 7);
        var different = new ProbeRecord("probe", 8);
        return $"{first == same}|{first != different}|{first.GetHashCode() == same.GetHashCode()}";
    }

    private static string NumericProbe()
    {
        return 1234567.5.ToString("N1", CultureInfo.InvariantCulture) + "|" +
            42.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string ExceptionProbe()
    {
        var caught = false;
        var finallyRan = false;
        try
        {
            throw new InvalidOperationException("SH-11 probe");
        }
        catch (InvalidOperationException exception) when (exception.Message == "SH-11 probe")
        {
            caught = true;
        }
        finally
        {
            finallyRan = true;
        }
        return (caught ? "caught" : "missed") + "|" + (finallyRan ? "finally" : "no-finally");
    }

    private static string DelegateProbe()
    {
        Func<int, int> doubleValue = value => value * 2;
        Func<int, int> composed = value => doubleValue(value) + 1;
        return composed(7).ToString(CultureInfo.InvariantCulture);
    }

    private static string JsonProbe()
    {
        var json = JsonSerializer.Serialize(new JsonRecord("Wasm2Cs", new[] { 1, 2, 3 }));
        var value = JsonSerializer.Deserialize<JsonRecord>(json) ?? throw new InvalidOperationException("JSON probe returned null.");
        return $"{value.Name}|{value.Values.Length}|{value.Values.SequenceEqual(new[] { 1, 2, 3 })}";
    }

    private static string GarbageCollectionProbe()
    {
        var survivorBytes = Encoding.UTF8.GetBytes("survivor-content");
        var survivorString = new string("survivor-string".ToCharArray());
        var survivorRecord = new ProbeRecord("survivor", survivorBytes.Length + 2);
        var checksum = 0;
        var state = Seed;
        for (var i = 0; i < 64; i++)
        {
            state = unchecked(state * 1103515245 + 12345);
            var garbage = new byte[1024 + ((unchecked((uint)state) >> 24) & 0x7ff)];
            garbage[0] = (byte)i;
            checksum += garbage[0];
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var result = $"bytes:{Encoding.UTF8.GetString(survivorBytes)}|string:{survivorString}|" +
            $"record:{survivorRecord.Name}:{survivorRecord.Value}|checksum:{checksum}|seed:{Seed}";
        GC.KeepAlive(survivorBytes);
        GC.KeepAlive(survivorString);
        GC.KeepAlive(survivorRecord);
        return result;
    }

    private sealed record ProbeRecord(string Name, int Value);
    private sealed record JsonRecord(string Name, int[] Values);

    private sealed class Result
    {
        public int Invocation { get; set; }
        public bool Passed { get; set; }
        public string Utf8 { get; set; } = "";
        public string Collections { get; set; } = "";
        public string Dictionary { get; set; } = "";
        public string Generics { get; set; } = "";
        public string Linq { get; set; } = "";
        public string Record { get; set; } = "";
        public string Numeric { get; set; } = "";
        public string Exceptions { get; set; } = "";
        public string Recursion { get; set; } = "";
        public string Delegate { get; set; } = "";
        public string Json { get; set; } = "";
        public string GarbageCollection { get; set; } = "";
        public long GuestHeap { get; set; }
        public string Fingerprint { get; set; } = "";
    }
}
