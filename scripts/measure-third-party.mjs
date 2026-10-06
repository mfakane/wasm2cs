// T02 round 5: replay third-party WebAssembly modules, driven by their own published JS, in generated C#.
// node scripts/measure-third-party.mjs [--inventory]
//
// The npm tarballs are downloaded (or reused from artifacts/t02-third-party/cache) and checked against
// pinned SHA-256 values. Binaries are not checked in. Each package's own JS API runs in Node with the
// WebAssembly exports wrapped: every export call, the arguments, the bytes JS wrote into linear memory
// before the call, JS-side memory.grow, the return value or trap, and a full-memory SHA-256 after the
// call are recorded. The trace is replayed on a fresh Node instance and on the C# translation.
import assert from 'node:assert/strict';
import { createHash, getHashes, pbkdf2Sync, scryptSync } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
assert.ok(process.argv.slice(2).every(arg => arg === '--inventory'), 'unknown option');
const inventory = process.argv.includes('--inventory');
const directory = join(root, 'artifacts/t02-third-party');
const cache = join(directory, 'cache');
mkdirSync(cache, { recursive: true });
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const xml = text => text.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const run = (command, args, options = {}) => execFileSync(command, args, {
  cwd: root, encoding: 'utf8', maxBuffer: 1 << 28, timeout: 600_000, ...options,
});

const packages = [
  {
    name: 'hash-wasm', version: '4.12.0', license: 'MIT',
    url: 'https://registry.npmjs.org/hash-wasm/-/hash-wasm-4.12.0.tgz',
    sha256: '1db32a125fb46177932ec8ac438d3cd8214ebdfaccb5d6611b657d88eb586f92',
    entry: 'package/dist/index.umd.js',
  },
  {
    name: 'xxhash-wasm', version: '1.1.0', license: 'MIT',
    url: 'https://registry.npmjs.org/xxhash-wasm/-/xxhash-wasm-1.1.0.tgz',
    sha256: 'cddd90f792012cd253728e11a643e15251268a7a9588851ccc51a5c8f16340ad',
    entry: 'package/cjs/xxhash-wasm.cjs',
  },
];

for (const pkg of packages) {
  const tarball = join(cache, `${pkg.name}-${pkg.version}.tgz`);
  if (!existsSync(tarball)) {
    const response = await fetch(pkg.url);
    assert.ok(response.ok, `${pkg.url}: HTTP ${response.status}`);
    writeFileSync(tarball, Buffer.from(await response.arrayBuffer()));
  }
  assert.equal(sha256(readFileSync(tarball)), pkg.sha256, `${pkg.name} tarball hash`);
  pkg.dir = join(cache, `${pkg.name}-${pkg.version}`);
  rmSync(pkg.dir, { recursive: true, force: true });
  mkdirSync(pkg.dir, { recursive: true });
  run('tar', ['xzf', tarball, '-C', pkg.dir]);
}

// ---- Tracing: wrap WebAssembly so the packages' own JS drives the modules. ----
const moduleBytes = new WeakMap();
const instances = [];   // { pkg, bytes, ops }
let currentPackage = null;
const encodeArg = value => typeof value === 'bigint' ? { i64: value.toString() } : value === undefined ? null : value;
const encodeResult = value => value === undefined ? 'undefined' : String(value);
function trapClass(error) {
  if (!(error instanceof WebAssembly.RuntimeError)) throw error;
  const m = error.message;
  if (/memory access out of bounds|out of bounds memory/.test(m)) return 'MemoryOutOfBounds';
  if (/unreachable/.test(m)) return 'Unreachable';
  if (/divide by zero|remainder by zero/.test(m)) return 'DivisionByZero';
  if (/divide result unrepresentable|integer overflow/.test(m)) return 'IntegerOverflow';
  if (/float unrepresentable|invalid conversion/.test(m)) return 'InvalidConversionToInteger';
  if (/table index is out of bounds/.test(m)) return 'TableOutOfBounds';
  if (/null function/.test(m)) return 'IndirectCallNull';
  if (/signature mismatch/.test(m)) return 'IndirectCallTypeMismatch';
  return 'RuntimeError: ' + m;
}
function memoryOf(exports) {
  return Object.values(exports).find(value => value instanceof WebAssembly.Memory);
}
// Diff the current memory against the snapshot after the previous call: emit grow + write ops.
function flush(record) {
  const memory = Buffer.from(record.memory.buffer);
  if (memory.length !== record.shadow.length) {
    assert.ok(memory.length > record.shadow.length);
    record.ops.push({ op: 'grow', delta: (memory.length - record.shadow.length) / 65536 });
    record.shadow = Buffer.concat([record.shadow, Buffer.alloc(memory.length - record.shadow.length)]);
  }
  const block = 4096;
  let start = -1, last = -1;
  const emit = () => {
    if (start >= 0) record.ops.push({ op: 'write', offset: start, base64: memory.subarray(start, last + 1).toString('base64') });
    start = last = -1;
  };
  for (let base = 0; base < memory.length; base += block) {
    const end = Math.min(base + block, memory.length);
    if (memory.subarray(base, end).equals(record.shadow.subarray(base, end))) continue;
    for (let i = base; i < end; i++) {
      if (memory[i] === record.shadow[i]) continue;
      if (start >= 0 && i - last > 16) emit();
      if (start < 0) start = i;
      last = i;
    }
  }
  emit();
}
function wrap(instance, bytes) {
  const exports = instance.exports;
  const record = { pkg: currentPackage, bytes, ops: [], memory: memoryOf(exports) };
  record.shadow = Buffer.from(new Uint8Array(record.memory.buffer));
  instances.push(record);
  const wrapped = {};
  for (const [name, value] of Object.entries(exports)) {
    if (typeof value !== 'function') { wrapped[name] = value; continue; }
    wrapped[name] = function (...args) {
      flush(record);
      const op = { op: 'call', name, args: args.map(encodeArg) };
      record.ops.push(op);
      try {
        const result = value(...args);
        op.result = encodeResult(result);
        return result;
      } catch (error) {
        op.trap = trapClass(error);
        throw error;
      } finally {
        const memory = new Uint8Array(record.memory.buffer);
        op.pages = memory.length / 65536;
        op.memorySha256 = sha256(memory);
        record.shadow = Buffer.from(memory);
      }
    };
  }
  return { exports: Object.freeze(wrapped) };
}
const realCompile = WebAssembly.compile, realInstantiate = WebAssembly.instantiate;
WebAssembly.compile = async source => {
  const bytes = Buffer.from(source instanceof ArrayBuffer ? new Uint8Array(source) : source);
  const module = await realCompile(bytes);
  moduleBytes.set(module, bytes);
  return module;
};
WebAssembly.instantiate = async (source, imports) => {
  if (source instanceof WebAssembly.Module) {
    assert.ok(moduleBytes.has(source), 'module compiled outside the tracer');
    return wrap(await realInstantiate(source, imports), moduleBytes.get(source));
  }
  const bytes = Buffer.from(source instanceof ArrayBuffer ? new Uint8Array(source) : source);
  const result = await realInstantiate(bytes, imports);
  moduleBytes.set(result.module, bytes);
  return { module: result.module, instance: wrap(result.instance, bytes) };
};

// ---- Workloads: the packages' public APIs, with independent checks where Node has the algorithm. ----
const require = createRequire(import.meta.url);
function pseudo(length, seed) {
  const out = Buffer.alloc(length);
  let x = seed >>> 0 || 1;
  for (let i = 0; i < length; i++) { x ^= x << 13; x >>>= 0; x ^= x >>> 17; x ^= x << 5; x >>>= 0; out[i] = x & 0xff; }
  return out;
}
const inputs = [Buffer.alloc(0), Buffer.from('abc'), pseudo(1000, 7), pseudo(40_000, 11)];  // 40000 > hash-wasm MAX_HEAP
const checks = [];
const check = (label, actual, expected) => { assert.equal(actual, expected, label); checks.push(label); };
const nodeHashes = new Set(getHashes());

currentPackage = 'hash-wasm';
const hw = require(join(packages[0].dir, packages[0].entry));
const nodeDigest = { md5: 'md5', sha1: 'sha1', sha224: 'sha224', sha256: 'sha256', sha384: 'sha384', sha512: 'sha512',
  ripemd160: 'ripemd160', sm3: 'sm3', whirlpool: 'whirlpool', md4: 'md4' };
for (const data of inputs) {
  const tag = `${data.length}B`;
  for (const [fn, algorithm] of Object.entries(nodeDigest)) {
    const value = await hw[fn](data);
    if (nodeHashes.has(algorithm)) check(`hash-wasm ${fn} ${tag} = node:crypto`, value, createHash(algorithm).update(data).digest('hex'));
  }
  for (const bits of [224, 256, 384, 512]) {
    const value = await hw.sha3(data, bits);
    if (nodeHashes.has(`sha3-${bits}`)) check(`hash-wasm sha3-${bits} ${tag} = node:crypto`, value, createHash(`sha3-${bits}`).update(data).digest('hex'));
  }
  await hw.keccak(data, 256);
  const b2b = await hw.blake2b(data, 512);
  if (nodeHashes.has('blake2b512')) check(`hash-wasm blake2b-512 ${tag} = node:crypto`, b2b, createHash('blake2b512').update(data).digest('hex'));
  const b2s = await hw.blake2s(data, 256);
  if (nodeHashes.has('blake2s256')) check(`hash-wasm blake2s-256 ${tag} = node:crypto`, b2s, createHash('blake2s256').update(data).digest('hex'));
  await hw.blake2b(data, 256, 'key');
  await hw.blake3(data); await hw.blake3(data, 256, Buffer.alloc(32, 7));
  await hw.adler32(data); await hw.crc32(data); await hw.crc32(data, 0x82f63b78); await hw.crc64(data);
  await hw.xxhash32(data); await hw.xxhash32(data, 0x12345678);
  await hw.xxhash64(data); await hw.xxhash64(data, 1, 2);
  await hw.xxhash3(data); await hw.xxhash128(data, 3, 4);
}
// Known-answer checks that do not depend on Node's crypto.
check('hash-wasm crc32("123456789")', await hw.crc32('123456789'), 'cbf43926');
check('hash-wasm blake3("abc")', await hw.blake3('abc'), '6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85');
check('hash-wasm xxhash64("abc")', await hw.xxhash64('abc'), '44bc2cf5ad770999');
check('hash-wasm adler32("Wikipedia")', await hw.adler32('Wikipedia'), '11e60398');
// Streaming state save/load, HMAC, PBKDF2, Argon2, bcrypt, scrypt.
const stream = await hw.createSHA256();
stream.init(); stream.update(inputs[2]); const saved = stream.save(); stream.update(inputs[3]);
const streamed = stream.digest('hex'); stream.load(saved); stream.update(inputs[3]);
check('hash-wasm SHA256 save/load', stream.digest('hex'), streamed);
check('hash-wasm SHA256 stream = node:crypto', streamed, createHash('sha256').update(Buffer.concat([inputs[2], inputs[3]])).digest('hex'));
const mac = await hw.createHMAC(hw.createSHA256(), 'key'); mac.init(); mac.update('The quick brown fox jumps over the lazy dog');
check('hash-wasm HMAC-SHA256 RFC example', mac.digest(), 'f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8');
const salt = pseudo(16, 99);
check('hash-wasm PBKDF2-SHA256 = node:crypto',
  await hw.pbkdf2({ password: 'password', salt, iterations: 50, hashLength: 32, hashFunction: hw.createSHA256(), outputType: 'hex' }),
  pbkdf2Sync('password', salt, 50, 32, 'sha256').toString('hex'));
check('hash-wasm scrypt = node:crypto',
  await hw.scrypt({ password: 'password', salt, costFactor: 16, blockSize: 8, parallelism: 1, hashLength: 32, outputType: 'hex' }),
  scryptSync('password', salt, 32, { N: 16, r: 8, p: 1 }).toString('hex'));
for (const kind of ['argon2i', 'argon2d', 'argon2id']) {
  const encoded = await hw[kind]({ password: 'password', salt, parallelism: 1, iterations: 2, memorySize: 64, hashLength: 32, outputType: 'encoded' });
  check(`hash-wasm ${kind} verify`, await hw.argon2Verify({ password: 'password', hash: encoded }), true);
}
// RFC 9106 section 5.3 Argon2id test vector (secret and associated data omitted by hash-wasm's API): use a self-check instead.
const bcryptHash = await hw.bcrypt({ password: 'password', salt, costFactor: 4, outputType: 'encoded' });
check('hash-wasm bcrypt verify', await hw.bcryptVerify({ password: 'password', hash: bcryptHash }), true);
check('hash-wasm bcrypt wrong password', await hw.bcryptVerify({ password: 'Password', hash: bcryptHash }), false);

currentPackage = 'xxhash-wasm';
const xx = await require(join(packages[1].dir, packages[1].entry))();
for (const data of [...inputs, pseudo(200_000, 5)]) {  // 200000 bytes makes the package grow memory from JS
  const tag = `${data.length}B`;
  check(`xxhash-wasm h32 ${tag} = hash-wasm xxhash32`, xx.h32Raw(data).toString(16).padStart(8, '0'), await hw.xxhash32(data));
  check(`xxhash-wasm h64 ${tag} = hash-wasm xxhash64`, xx.h64Raw(data, 5n).toString(16).padStart(16, '0'), await hw.xxhash64(data, 5, 0));
}
currentPackage = 'xxhash-wasm';
const h = xx.create64(123n); h.update('hello '); h.update(pseudo(5000, 3));
const h64 = h.digest();
check('xxhash-wasm create64 streaming = one-shot', h64, xx.h64Raw(Buffer.concat([Buffer.from('hello '), pseudo(5000, 3)]), 123n));
xx.create32(9).update('abc').digest(); xx.h32('unicode \u00e9\u4e2d'); xx.h64ToString('abc');

// ---- Group traces by module bytes. Add synthetic out-of-bounds calls. ----
WebAssembly.compile = realCompile; WebAssembly.instantiate = realInstantiate;
const modules = new Map();
for (const record of instances) {
  const key = sha256(record.bytes);
  if (!modules.has(key)) {
    const exports = WebAssembly.Module.exports(new WebAssembly.Module(record.bytes)).map(e => e.name);
    const algorithm = record.pkg === 'xxhash-wasm' ? 'xxhash_wasm'
      : exports.includes('bcrypt') ? 'bcrypt' : exports.includes('scrypt') ? 'scrypt' : null;
    modules.set(key, { key, pkg: record.pkg, bytes: record.bytes, exports, algorithm, traces: [] });
  }
  modules.get(key).traces.push(record.ops);
}
// Name hash-wasm modules by the embedded name in the bundle.
const bundle = readFileSync(join(packages[0].dir, 'package/dist/index.umd.min.js'), 'utf8');
for (const match of bundle.matchAll(/name:"([a-z0-9]+)",data:"([A-Za-z0-9+/=]+)"/g)) {
  const key = sha256(Buffer.from(match[2], 'base64'));
  if (modules.has(key)) modules.get(key).algorithm = match[1];
  else modules.set(key, { key, pkg: 'hash-wasm', bytes: Buffer.from(match[2], 'base64'), algorithm: match[1], traces: [], unused: true });
}
for (const module of modules.values()) {
  assert.ok(module.algorithm, `unnamed module ${module.key}`);
  if (module.traces.length === 0) continue;
  const last = module.traces.at(-1);
  if (module.exports.includes('Hash_Update')) last.push({ op: 'call', name: 'Hash_Update', args: [1 << 24], synthetic: true });
  if (module.exports.includes('xxh32')) last.push({ op: 'call', name: 'xxh32', args: [0x7ffffff0, 64, 0], synthetic: true });
  if (module.exports.includes('Hash_GetBuffer')) last.push({ op: 'call', name: 'Hash_GetBuffer', args: [], synthetic: true });
}

// Replay on a fresh Node instance: must reproduce the recorded trace, and supplies expectations for synthetic calls.
function replayNode(bytes, ops) {
  const exports = new WebAssembly.Instance(new WebAssembly.Module(bytes), {}).exports;
  const memory = memoryOf(exports);
  const rows = [];
  for (const op of ops) {
    if (op.op === 'grow') { memory.grow(op.delta); continue; }
    if (op.op === 'write') { new Uint8Array(memory.buffer).set(Buffer.from(op.base64, 'base64'), op.offset); continue; }
    const fn = exports[op.name];
    const args = op.args.map(a => a !== null && typeof a === 'object' ? BigInt(a.i64) : a);
    const row = { call: `${op.name}(${op.args.map(a => a !== null && typeof a === 'object' ? a.i64 : String(a)).join(',')})` };
    try { row.result = encodeResult(fn(...args)); } catch (error) { row.trap = trapClass(error); }
    const view = new Uint8Array(memory.buffer);
    row.pages = view.length / 65536; row.memorySha256 = sha256(view);
    rows.push(row);
  }
  return rows;
}

const program = `using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static int ToInt32(JsonElement value) {
    if (value.ValueKind == JsonValueKind.Null) return 0;
    if (value.ValueKind == JsonValueKind.True) return 1;
    if (value.ValueKind == JsonValueKind.False) return 0;
    double number = value.GetDouble();
    if (double.IsNaN(number) || double.IsInfinity(number)) return 0;
    return unchecked((int)(uint)(ulong)(long)Math.Truncate(number % 4294967296.0));
}
var output = new Dictionary<string, List<List<Dictionary<string, object>>>>();
using var manifest = JsonDocument.Parse(File.ReadAllText(args[0]));
foreach (var entry in manifest.RootElement.EnumerateArray()) {
    string className = entry.GetProperty("className").GetString()!;
    var type = typeof(Program).Assembly.GetType("Wasm2Cs.Generated." + className)!;
    var traces = new List<List<Dictionary<string, object>>>();
    foreach (var trace in entry.GetProperty("traces").EnumerateArray()) {
        var instance = Activator.CreateInstance(type)!;
        var memory = (Wasm2Cs.WasmMemory)type.GetProperties().First(p => p.PropertyType == typeof(Wasm2Cs.WasmMemory)).GetValue(instance)!;
        var write = type.GetMethod("WriteMemory")!;
        var read = type.GetMethod("ReadMemory")!;
        var rows = new List<Dictionary<string, object>>();
        foreach (var op in trace.EnumerateArray()) {
            string kind = op.GetProperty("op").GetString()!;
            if (kind == "grow") { if (memory.Grow(op.GetProperty("delta").GetInt32()) < 0) throw new Exception("grow failed"); continue; }
            if (kind == "write") {
                write.Invoke(instance, new object[] { (uint)op.GetProperty("offset").GetInt64(), Convert.FromBase64String(op.GetProperty("base64").GetString()!) });
                continue;
            }
            string name = op.GetProperty("name").GetString()!;
            var method = type.GetMethod(name)!;
            var parameters = method.GetParameters();
            var raw = op.GetProperty("args").EnumerateArray().ToArray();
            var values = new object[parameters.Length];
            var text = new List<string>();
            for (int i = 0; i < raw.Length; i++)
                text.Add(raw[i].ValueKind == JsonValueKind.Object ? raw[i].GetProperty("i64").GetString()! : raw[i].ValueKind == JsonValueKind.Null ? "null" : raw[i].GetRawText());
            for (int i = 0; i < parameters.Length; i++) {
                var element = i < raw.Length ? raw[i] : default;
                var p = parameters[i].ParameterType;
                if (p == typeof(int)) values[i] = i < raw.Length ? ToInt32(element) : 0;
                else if (p == typeof(long)) values[i] = i < raw.Length && element.ValueKind == JsonValueKind.Object ? long.Parse(element.GetProperty("i64").GetString()!) : throw new Exception(name + ": i64 argument is not a BigInt");
                else if (p == typeof(float)) values[i] = i < raw.Length && element.ValueKind == JsonValueKind.Number ? (float)element.GetDouble() : float.NaN;
                else if (p == typeof(double)) values[i] = i < raw.Length && element.ValueKind == JsonValueKind.Number ? element.GetDouble() : double.NaN;
                else throw new Exception(name + ": unsupported parameter type " + p);
            }
            var row = new Dictionary<string, object> { ["call"] = name + "(" + string.Join(",", text) + ")" };
            try {
                object? result = method.Invoke(instance, values);
                row["result"] = result switch { null => "undefined", int v => v.ToString(), long v => v.ToString(), float v => v.ToString("R"), double v => v.ToString("R"), _ => result.ToString()! };
            } catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "TrapException") {
                row["trap"] = e.InnerException.GetType().GetProperty("Kind")!.GetValue(e.InnerException)!.ToString()!;
            }
            row["pages"] = memory.Size / 65536;
            row["memorySha256"] = Hash(memory.ReadMemory(0, memory.Size));
            rows.Add(row);
        }
        traces.Add(rows);
    }
    output[className] = traces;
}
Console.WriteLine(JsonSerializer.Serialize(output));
`;

run('dotnet', ['build', join(root, 'src/Wasm2Cs.Cli'), '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
const cli = join(root, 'src/Wasm2Cs.Cli/bin/Debug/net10.0/Wasm2Cs.Cli.dll');
const consumer = join(directory, 'consumer');
rmSync(consumer, { recursive: true, force: true });
mkdirSync(join(consumer, 'wasm'), { recursive: true });
const results = [];
const manifest = [];
const expected = {};
for (const module of [...modules.values()].sort((a, b) => a.algorithm.localeCompare(b.algorithm))) {
  const file = join(consumer, 'wasm', `${module.algorithm}.wasm`);
  writeFileSync(file, module.bytes);
  const result = {
    package: module.pkg, module: module.algorithm, sha256: module.key, wasmBytes: module.bytes.length,
    imports: WebAssembly.Module.imports(new WebAssembly.Module(module.bytes)).length,
    traces: module.traces.length, calls: module.traces.reduce((n, t) => n + t.filter(op => op.op === 'call').length, 0),
  };
  if (inventory) {
    const opcodes = run('node', [join(root, 'scripts/wasm-opcodes.mjs'), file]);
    result.instructions = Number(opcodes.match(/: (\d+) instructions/)[1]);
    result.distinctInstructions = Number(opcodes.match(/instructions, (\d+) distinct/)[1]);
    result.sections = run('wasm-objdump', ['-h', file]).split('\n').filter(l => /start=/.test(l)).map(l => l.trim().split(/\s+/)[0]);
  }
  // T02 command with the default class name (the file name).
  const plain = spawnSync('dotnet', [cli, file, '-o', join(directory, 'default-name')], { cwd: root, encoding: 'utf8', timeout: 300_000 });
  result.defaultNameTranslation = plain.status === 0 ? 'success' : plain.stderr.trim();
  const className = 'ThirdParty_' + module.algorithm;
  const translation = spawnSync('dotnet', [cli, file, '-n', className, '-o', consumer], { cwd: root, encoding: 'utf8', timeout: 300_000 });
  result.translation = translation.status === 0 ? 'success' : translation.stderr.trim();
  results.push(result);
  if (translation.status !== 0) { process.exitCode = 1; continue; }
  if (module.traces.length === 0) { result.execution = 'not called by the package workload'; continue; }
  const traceRows = module.traces.map(ops => replayNode(module.bytes, ops));
  // The fresh Node replay must reproduce what the package's own run recorded.
  module.traces.forEach((ops, t) => ops.filter(op => op.op === 'call').forEach((op, i) => {
    if (op.synthetic) return;
    const row = traceRows[t][i];
    assert.deepEqual({ result: row.result, trap: row.trap, pages: row.pages, memorySha256: row.memorySha256 },
      { result: op.result, trap: op.trap, pages: op.pages, memorySha256: op.memorySha256 }, `${module.algorithm}: Node replay of ${row.call}`);
  }));
  expected[className] = traceRows;
  result.syntheticTraps = traceRows.flat().filter(row => row.trap).map(row => `${row.call}: ${row.trap}`);
  manifest.push({ className, traces: module.traces });
}
rmSync(join(directory, 'default-name'), { recursive: true, force: true });
writeFileSync(join(consumer, 'manifest.json'), JSON.stringify(manifest));
writeFileSync(join(consumer, 'node.json'), JSON.stringify(expected));
writeFileSync(join(consumer, 'Program.cs'), program);
writeFileSync(join(consumer, 'Consumer.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <LangVersion>9.0</LangVersion><ImplicitUsings>disable</ImplicitUsings><Nullable>annotations</Nullable></PropertyGroup>
  <ItemGroup><Compile Remove="wasm/**" /><ProjectReference Include="${xml(join(root, 'src/Wasm2Cs.Runtime/Wasm2Cs.Runtime.csproj'))}"
    SetTargetFramework="TargetFramework=netstandard2.0" /></ItemGroup>
</Project>`);
writeFileSync(join(directory, 'build.txt'), run('dotnet', ['build', join(consumer, 'Consumer.csproj'), '-m:1', '-p:UseSharedCompilation=false', '--nologo']));
const actual = JSON.parse(run('dotnet', [join(consumer, 'bin/Debug/net10.0/Consumer.dll'), join(consumer, 'manifest.json')]));
writeFileSync(join(consumer, 'csharp.json'), JSON.stringify(actual));
for (const result of results) {
  const className = 'ThirdParty_' + result.module;
  if (!expected[className]) continue;
  assert.deepEqual(actual[className], expected[className], `${result.module}: C# vs Node (every call result, trap, and full memory)`);
  result.execution = 'match';
  console.log(`${result.package} ${result.module}: ${result.calls} calls in ${result.traces} instance(s) match Node (results, traps, full memory after each call)`);
}
for (const result of results.filter(r => r.execution !== 'match')) console.log(`${result.package} ${result.module}: ${result.translation}; ${result.execution ?? 'not run'}`);
for (const result of results.filter(r => r.defaultNameTranslation !== 'success')) console.log(`default class name: ${result.module}: ${result.defaultNameTranslation}`);
console.log(`${checks.length} independent checks of the packages' outputs passed (node:crypto, known answers, cross-package).`);
writeFileSync(join(directory, 'results.json'), JSON.stringify({
  schemaVersion: 1, command: `node scripts/measure-third-party.mjs${inventory ? ' --inventory' : ''}`,
  nodeVersion: process.version, dotnetVersion: run('dotnet', ['--version']).trim(),
  wabtVersion: inventory ? run('wasm-objdump', ['--version']).trim() : null,
  packages: packages.map(({ name, version, license, url, sha256 }) => ({ name, version, license, url, sha256 })),
  checks, results,
}, null, 2));
