// T02 round 3: compare the complete Rust std call sequence and linear memory.
// node scripts/measure-rust-std.mjs [--inventory] (WABT required for inventory).
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, readdirSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
assert.ok(process.argv.slice(2).every(arg => arg === '--inventory'), 'unknown option');
const inventory = process.argv.includes('--inventory');
const directory = join(root, 'artifacts/t02-rust-std');
mkdirSync(directory, { recursive: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const xml = text => text.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const run = (command, args) => execFileSync(command, args, {
  cwd: root, encoding: 'utf8', maxBuffer: 1 << 27, timeout: 300_000,
});
const valuesCase = (name, values, text = values.join(' ')) => ({
  name, bytes: Buffer.from(text), values: values.map(BigInt), status: 0,
});
const capacity = 131_072;
const cases = [
  valuesCase('basic', [4, -2, 4, 17, 0]),
  { name: 'empty', bytes: Buffer.alloc(0), status: -4 },
  { name: 'whitespace', bytes: Buffer.from(' \t\r\n\f'), status: -4 },
  { name: 'invalid-utf8', bytes: Buffer.from([0xff]), status: -1 },
  { name: 'invalid-number', bytes: Buffer.from('1 2 nope 4'), status: -2 },
  { name: 'integer-overflow', bytes: Buffer.from('9223372036854775808'), status: -2 },
  { name: 'negative-integer-overflow', bytes: Buffer.from('-9223372036854775809'), status: -2 },
  valuesCase('extrema-wrap', ['9223372036854775807', '9223372036854775807', '-9223372036854775808', '-1']),
  valuesCase('ascii-whitespace-plus', [7, -2, 7], '\t+7\r\n-2\f7 '),
  valuesCase('sorted', Array.from({ length: 4096 }, (_, i) => i - 2048)),
  valuesCase('reverse', Array.from({ length: 4096 }, (_, i) => 2047 - i)),
  valuesCase('duplicates', Array(16_000).fill(-31)),
  valuesCase('large-mixed', Array.from({ length: 16_000 }, (_, i) => (i * 7919) % 5003 - 2501)),
  { name: 'output-overflow', bytes: Buffer.from(Array.from({ length: 20_000 }, (_, i) => i).join(' ')), status: -5 },
  { ...valuesCase('exact-input-capacity', [7]), bytes: Buffer.concat([Buffer.alloc(capacity - 1, 32), Buffer.from('7')]) },
  { name: 'too-long', bytes: Buffer.alloc(0), length: capacity + 1, status: -3 },
  { name: 'unsigned-length', bytes: Buffer.alloc(0), length: -1, status: -3 },
  valuesCase('recovery', [42]),
];
for (const entry of cases) assert.ok(entry.bytes.length <= capacity, entry.name);
const caseFile = join(directory, 'cases.json');
writeFileSync(caseFile, JSON.stringify(cases.map(entry => ({
  name: entry.name, input: entry.bytes.toString('base64'), length: entry.length ?? entry.bytes.length,
}))));

function expected(entry) {
  if (entry.status !== 0) return { status: entry.status, sum: '0', unique: 0, median: '0', outputBase64: '' };
  const sorted = [...entry.values].sort((a, b) => a < b ? -1 : a > b ? 1 : 0);
  const counts = new Map();
  for (const value of sorted) counts.set(value, (counts.get(value) ?? 0) + 1);
  const output = [...counts].map(([value, count]) => `${value}:${count}\n`).join('');
  return {
    status: 0,
    sum: BigInt.asIntN(64, sorted.reduce((sum, value) => sum + value, 0n)).toString(),
    unique: counts.size, median: sorted[Math.floor(sorted.length / 2)].toString(),
    outputBase64: Buffer.from(output).toString('base64'),
  };
}

function reference(module) {
  const e = new WebAssembly.Instance(module).exports;
  const initial = {
    inputPointer: e.buffer_ptr(), outputPointer: e.output_ptr(), capacity: e.buffer_capacity(),
    dataEnd: e.__data_end.value, heapBase: e.__heap_base.value,
    pages: e.memory.buffer.byteLength / 65536, memorySha256: hash(Buffer.from(e.memory.buffer)),
  };
  const rows = [];
  for (const entry of cases) {
    new Uint8Array(e.memory.buffer).set(entry.bytes, e.buffer_ptr());
    const status = e.analyze(entry.length ?? entry.bytes.length);
    // Reacquire the buffer after every call: Rust's allocator may grow memory.
    const memory = Buffer.from(e.memory.buffer);
    const row = {
      name: entry.name, status, sum: e.result_sum().toString(), unique: e.result_unique(),
      median: e.result_median().toString(),
      outputBase64: memory.subarray(e.output_ptr(), e.output_ptr() + e.output_len()).toString('base64'),
      inputSha256: hash(memory.subarray(e.buffer_ptr(), e.buffer_ptr() + entry.bytes.length)),
      pages: memory.length / 65536, memorySha256: hash(memory),
    };
    assert.deepEqual({ status, sum: row.sum, unique: row.unique, median: row.median, outputBase64: row.outputBase64 },
      expected(entry), `Node vs independent expected result: ${entry.name}`);
    assert.equal(row.inputSha256, hash(entry.bytes), `Node changed input: ${entry.name}`);
    rows.push(row);
  }
  assert.equal(e.input_byte(0), Buffer.from(e.memory.buffer)[e.buffer_ptr()]);
  assert.throws(() => e.input_byte(capacity), error => error instanceof WebAssembly.RuntimeError && /unreachable/.test(error.message));
  const fresh = new WebAssembly.Instance(module).exports;
  assert.equal(hash(Buffer.from(fresh.memory.buffer)), initial.memorySha256, 'fresh instance memory');
  return { initial, rows, trap: 'Unreachable', freshMemorySha256: hash(Buffer.from(fresh.memory.buffer)) };
}

const program = `using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Wasm2Cs.Generated;

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var module = new Probe();
var initial = new {
    inputPointer = module.buffer_ptr(), outputPointer = module.output_ptr(), capacity = module.buffer_capacity(),
    dataEnd = module.__data_end, heapBase = module.__heap_base, pages = module.MemorySize / 65536,
    memorySha256 = Hash(module.ReadMemory(0, module.MemorySize))
};
var rows = new List<object>();
using var inputs = JsonDocument.Parse(File.ReadAllText(args[0]));
foreach (var entry in inputs.RootElement.EnumerateArray()) {
    var bytes = Convert.FromBase64String(entry.GetProperty("input").GetString()!);
    module.WriteMemory(unchecked((uint)module.buffer_ptr()), bytes);
    var status = module.analyze(entry.GetProperty("length").GetInt32());
    rows.Add(new {
        name = entry.GetProperty("name").GetString(), status,
        sum = module.result_sum().ToString(CultureInfo.InvariantCulture), unique = module.result_unique(),
        median = module.result_median().ToString(CultureInfo.InvariantCulture),
        outputBase64 = Convert.ToBase64String(module.ReadMemory(unchecked((uint)module.output_ptr()), module.output_len())),
        inputSha256 = Hash(module.ReadMemory(unchecked((uint)module.buffer_ptr()), bytes.Length)),
        pages = module.MemorySize / 65536, memorySha256 = Hash(module.ReadMemory(0, module.MemorySize))
    });
}
if (module.input_byte(0) != module.ReadMemory(unchecked((uint)module.buffer_ptr()), 1)[0])
    throw new Exception("in-bounds read differs");
string trap;
try { module.input_byte(module.buffer_capacity()); throw new Exception("bounds check did not trap"); }
catch (Probe.TrapException error) { trap = error.Kind.ToString(); }
var fresh = new Probe();
Console.WriteLine(JsonSerializer.Serialize(new { initial, rows, trap,
    freshMemorySha256 = Hash(fresh.ReadMemory(0, fresh.MemorySize)) }));
`;

run('dotnet', ['build', join(root, 'src/Wasm2Cs.Cli'), '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
const cli = join(root, 'src/Wasm2Cs.Cli/bin/Debug/net10.0/Wasm2Cs.Cli.dll');
const results = [];
for (const name of ['RustStd185', 'RustStd190']) {
  const fixture = join(root, `samples/RustStd/${name}.wasm`);
  const bytes = readFileSync(fixture);
  const module = new WebAssembly.Module(bytes);
  assert.deepEqual(WebAssembly.Module.imports(module), [], 'library must need no host stubs');
  const expectedResult = reference(module);
  const consumer = join(directory, name);
  mkdirSync(consumer, { recursive: true });
  for (const file of readdirSync(consumer).filter(file => file.endsWith('.g.cs')))
    unlinkSync(join(consumer, file));
  writeFileSync(join(consumer, 'node.json'), JSON.stringify(expectedResult, null, 2));
  let opcodeText = null;
  if (inventory) {
    opcodeText = run('node', [join(root, 'scripts/wasm-opcodes.mjs'), fixture]);
    writeFileSync(join(consumer, 'opcodes.txt'), opcodeText);
    writeFileSync(join(consumer, 'sections.txt'), run('wasm-objdump', ['-h', fixture]));
    writeFileSync(join(consumer, 'module.txt'), run('wasm-objdump', ['-x', fixture]));
  }
  const start = performance.now();
  const translation = spawnSync('dotnet', [cli, fixture, '-n', 'Probe', '-o', consumer], {
    cwd: root, encoding: 'utf8', timeout: 300_000,
  });
  if (translation.error) throw translation.error;
  const result = {
    name, sha256: hash(bytes), wasmBytes: bytes.length,
    instructions: opcodeText === null ? null : Number(opcodeText.match(/: (\d+) instructions/)[1]),
    distinctInstructions: opcodeText === null ? null : Number(opcodeText.match(/instructions, (\d+) distinct/)[1]),
    profile: 'portable-netstandard2.0', translationExit: translation.status,
    translationMs: Math.round(performance.now() - start),
  };
  results.push(result);
  if (translation.status !== 0) {
    result.failure = translation.stderr;
    process.exitCode = 1;
    continue;
  }
  result.sourceBytes = readdirSync(consumer).filter(file => file.endsWith('.g.cs'))
    .reduce((total, file) => total + statSync(join(consumer, file)).size, 0);
  writeFileSync(join(consumer, 'Program.cs'), program);
  writeFileSync(join(consumer, 'Consumer.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <LangVersion>9.0</LangVersion><ImplicitUsings>disable</ImplicitUsings></PropertyGroup>
  <ItemGroup><ProjectReference Include="${xml(join(root, 'src/Wasm2Cs.Runtime/Wasm2Cs.Runtime.csproj'))}"
    SetTargetFramework="TargetFramework=netstandard2.0" /></ItemGroup>
</Project>`);
  const compileStart = performance.now();
  writeFileSync(join(consumer, 'build.txt'), run('dotnet', ['build', join(consumer, 'Consumer.csproj'),
    '-m:1', '-p:UseSharedCompilation=false', '--nologo']));
  result.compileMs = Math.round(performance.now() - compileStart);
  const actualResult = JSON.parse(run('dotnet', [join(consumer, 'bin/Debug/net10.0/Consumer.dll'), caseFile]));
  writeFileSync(join(consumer, 'csharp.json'), JSON.stringify(actualResult, null, 2));
  assert.deepEqual(actualResult, expectedResult, `${name}: C# vs Node (including every full memory snapshot)`);
  result.matchedCases = cases.length;
  result.trap = actualResult.trap;
  result.initialPages = actualResult.initial.pages;
  result.maximumPages = Math.max(...actualResult.rows.map(row => row.pages));
  assert.ok(result.maximumPages > result.initialPages, `${name}: exercise memory growth`);
  console.log(`${name}: ${cases.length} calls, full memory snapshots, bounds trap, and fresh instance match Node`);
}
writeFileSync(join(directory, 'results.json'), JSON.stringify({
  schemaVersion: 1, command: `node scripts/measure-rust-std.mjs${inventory ? ' --inventory' : ''}`,
  nodeVersion: process.version, dotnetVersion: run('dotnet', ['--version']).trim(),
  wabtVersion: inventory ? run('wasm-objdump', ['--version']).trim() : null, results,
}, null, 2));
