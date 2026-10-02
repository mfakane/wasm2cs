# T02 gap report

Measured on 2026-10-03 (JST) at `master` `b6ade21` (merge of PR #2). Toolchain: .NET SDK 10.0.401, Node.js v22.19.0, wasmtime 28.0.1. Translation command, unless noted:

```sh
dotnet run --project src/Wasm2Cs.Cli -- <file.wasm>
```

The default target profile is `portable-netstandard2.0`. Successes were compiled as a temporary consumer against `src/Wasm2Cs.Runtime` (`net10.0`) and compared with Node.js `WebAssembly` (and Node's experimental WASI Preview1). wasmtime was used for the WASI process exit and for the SIMD lane bit pattern. This is not a claim about inputs outside the set below.

## How gaps were counted

Every code body was walked, including instructions after the first one the CLI would reject, and opcodes were histogrammed. A missing immediate shape would have stopped that walk; none of the non-SIMD modules stopped. The SIMD fixture is 92 bytes and contains only `v128.const`, `f32x4.add`, and `f32x4.mul`. The CLI accepts that whole module on `dotnet-vector`, so there is no further unsupported opcode hiding behind the profile rejection.

Unsupported-opcode counts below are therefore full-module counts, not "first error only". The checked-in conformance files are `.wast`/`.json`, not CLI inputs. They were not reassembled. `dotnet run --project tests/Wasm2Cs.Tests` was run instead (see Conformance).

## Per-input results

| Input | CLI | Class | Reference | Result |
|---|---|---|---|---|
| `samples/Smoke/Arithmetic.wasm` | success | Core | Node | match (returns, wrap) |
| `samples/CAlgorithms/Algorithms.wasm` | success | Core | Node | match (returns, 16-byte buffer, OOB trap) |
| `samples/RustWasm32UnknownUnknown/rust_wasm32_fixture.wasm` | success | Core | Node | match (i64 wrap, memory sum, globals, OOB trap) |
| `samples/WasiPreview1/wasi_hello.wasm` | success | WASI | Node Preview1 + wasmtime | stdout match; see WASI |
| `samples/Floating/Floating.wasm` | success | Core | Node | match (values, bits, traps) |
| `samples/Host/Host.wasm` | success | Core | Node | match (return, callback order, memory) |
| `samples/Exceptions/Exceptions.wasm` | success | Core | Node | match, including `unreachable` not swallowed |
| `samples/Exceptions/HostException.wasm` | success | Core | Node not applicable for CLR exceptions | compiles; CLR exception propagates past `catch_all` (documented host behavior) |
| `samples/Exceptions/HostTagException.wasm` | success | Core | Node | tag catch of a host `WasmThrownException` matches the WAT (`f(3)` → 14) |
| `samples/Exceptions/Imported.wasm` | success | Core | Node | `f(5)` → 6 |
| `samples/Tables/FunctionPointers.wasm` | success | Core | Node | `call(41,0)` → 42; index 1 is `TableOutOfBounds` |
| `samples/Vectors/Vector.wasm.base64` | profile-dependent | SIMD | wasmtime | see SIMD |

`samples/SelfHosting/HostAbi.wat` was not treated as WASI, per T01.

### Arithmetic

Repro: `dotnet run --project src/Wasm2Cs.Cli -- samples/Smoke/Arithmetic.wasm`

No imports, no memory. Compared `add(20,22)=42`, `add(2147483647,1)=-2147483648`, `square(7)=49`, `sub(5,8)=-3`, `mul(-3,7)=-21`, `snapshot(9)=9`, `minimum()=-2147483648`, `negative()=-1`, `tee(4)=8`, `early(0)=0`, `early(1)=1`. All matched Node.

### Clang freestanding (`Algorithms.wasm`)

Repro: `dotnet run --project src/Wasm2Cs.Cli -- samples/CAlgorithms/Algorithms.wasm`

Owned memory, no imports. After writing `123456789` at `buffer_ptr()`: `crc32` = `0xcbf43926`, `sum_bytes` = 477. `f(7,16)` = 395785129 and the 16 buffer bytes were `07 14 21 2e 3b 48 55 62 6f 7c 89 96 a3 b0 bd ca`. `f(1,300)` = 2766843464 (length clamped). `sum_bytes(0x100000,1)` traps `MemoryOutOfBounds` (Node: memory access out of bounds). Opcode walk: only i32 arithmetic, `i32.load8_u` / `i32.store8`, blocks, loops, branches. Unsupported opcodes: 0.

### Rust `wasm32-unknown-unknown`

Repro: `dotnet run --project src/Wasm2Cs.Cli -- samples/RustWasm32UnknownUnknown/rust_wasm32_fixture.wasm`

No imports. Memory minimum 16 pages. Exports `add_i64`, `sum_u8`, `memory`, `__data_end`, `__heap_base`.

| Case | Node and generated C# |
|---|---|
| `add_i64(20,22)` | 42 |
| `add_i64(i64::MAX, 1)` | `i64::MIN` (wrap) |
| `add_i64(-1,1)` | 0 |
| `sum_u8` of `01 02 fa ff 00` | 508 |
| `sum_u8` length 0 | 0 |
| those five bytes after the call | unchanged |
| `__data_end` / `__heap_base` | 1048576 / 1048576 |
| `sum_u8(16*65536, 1)` and `sum_u8(16*65536-1, 2)` | `MemoryOutOfBounds` |

Opcode counts (full bodies): `local.get` 8, `end` 4, `local.set` 4, `i32.const` 3, `i32.add` 3, `i64.add` 1, `loop` 1, `block` 1, `br_if` 1, `return` 1, `i32.load8_u` 1, `br` 1. Unsupported opcodes: 0. Sections: type, function, memory, global, export, code.

### WASI Preview1

Repro: `dotnet run --project src/Wasm2Cs.Cli -- samples/WasiPreview1/wasi_hello.wasm`

Imports (function kind only): `wasi_snapshot_preview1.fd_write (i32,i32,i32,i32)->i32`, `wasi_snapshot_preview1.proc_exit (i32)`. Owned memory, one table, active data `hello wasi\n`, export `_start`. No start section. CLI translation succeeds; the constructor requires both delegates. There is no built-in WASI host.

Node `node:wasi` Preview1 (`returnOnExit: true`) called `fd_write(1, 66584, 1, 66580)` once, returned errno 0, wrote `hello wasi\n`, did **not** call `proc_exit`, and `start` returned 0. wasmtime 28.0.1 `wasmtime run samples/WasiPreview1/wasi_hello.wasm` printed the same line and exited 0. A temporary C# `fd_write` that reads `iovec {ptr,len}` from the generated memory wrote the same bytes; `proc_exit` was not called; `_start` returned. Compared, not left uncompared: Node is a Preview1 runtime for this module.

`proc_exit` is still present (wrapper `call 1` followed by `unreachable`) but is not on the success path. Opcode walk of all eight functions found only MVP integer, memory, call, global, and `unreachable` instructions. Unsupported opcodes: 0. The gap is the missing product host, not decode or codegen, for this input.

### Floating, host, tables, exceptions

These samples are already in the repository tests. T02 still translated each file and ran the calls below against Node.

- Floating: `f32_add(1.5, 2.25)=3.75`; `f32_div(1,0)` bits `0x7f800000`; `f32_min(-0, +0)` bits `0x80000000`; `i32.trunc_f32_s(NaN)` → `InvalidConversionToInteger`; `i32.trunc_f32_s(1e20)` → `IntegerOverflow`; saturating truncations → `i32::MAX` / `i32::MIN`; `store32`/`load32` of 1.5; `host32(7)=1167171584` with an import that does JS `ToInt32(x) ^ 0x1234`; global `g32` bits `0xff812345`.
- Host: same exchange/notify script as `samples/Host/HostChecks.cs`. `f(3)=26`, log `42,3,99`, memory at 16 is `09 08 07`.
- Tables: `call(41,0)=42`. `call(1,1)` is `TableOutOfBounds` (Node: table index out of bounds).
- Exceptions: `direct(5)=6`, `f(5)=6`, `catch_all()=7`, `rethrow(4)=5`, `different()=2`, `trap()` → `Unreachable` (Node `RuntimeError: unreachable`; `catch_all` does not catch the trap), `branch(3)=3`, `try_branch(8)=8`, `catch_branch()=41`, `indirect(6)=7`. wasmtime 28.0.1 rejects this binary (`exceptions proposal not enabled`, and `-W exceptions` is not a known flag), so the exception reference is Node only.
- Imported tag and host-tag callback matched the WAT. A CLR `InvalidOperationException` from `HostException`'s import propagated out of `catch_all`, which is the documented host-exception rule, not a WASM trap.

### SIMD fixture

`samples/Vectors/Vector.wasm.base64` decodes to a 92-byte module exporting `f() -> v128`.

```text
dotnet run --project src/Wasm2Cs.Cli -- Vector.wasm
# Target profile 'portable-netstandard2.0' does not support v128 lowering.

dotnet run --project src/Wasm2Cs.Cli -- -p dotnet-vector Vector.wasm
```

`dotnet-vector` compiles. Lanes are `(22, 44, 66, 88)`, matching `(1,2,3,4)+(10,20,30,40)` then multiplied by `(2,2,2,2)`, and matching wasmtime's returned v128 bits. No other SIMD opcode appears. This is the already documented profile split, not a new SIMD group.

## Conformance (practical)

`dotnet run --project tests/Wasm2Cs.Tests` passed on this tree, including the checked-in differential suites (`i32`, `i64`, `i64-memory`, `f32`, `f64`, comparisons, bitwise, conversions, `float_literals`, `typed-ir`) against Node, plus memory, table, exception, and import checks. Those JSON fixtures are the supported command subset described in `tests/Conformance/README.md` (explicit skips for out-of-subset modules). They do not show an additional Core opcode to implement. Counts from that run include 3649 C#/Node matches, 3073 i32 outcomes, 26684 i64/conversion outcomes, and 267254 float outcomes.

## Confirmed gaps

| Gap | Class | Where it showed up | Priority |
|---|---|---|---|
| None in decode, immediates, validation, imports, C# compile, or runtime for the Core samples | Core | all Core rows above matched | — |
| No WASI host is generated. Callers must supply `fd_write` / `proc_exit` themselves. This module's success path only needs a correct `fd_write` over owned memory | WASI | `wasi_hello.wasm` | next, if WASI is in scope (T06 then one T07 import) |
| `v128` rejected on `portable-netstandard2.0` / `dotnet-netstandard2.1` | SIMD | `Vector.wasm` only | not next: the only SIMD opcodes in the fixed inputs are already implemented on `dotnet-vector` and `unity-mathematics` |

No section id, import kind, or opcode outside the supported subset occurred in these inputs. Threads, shared memory, memory64, GC, and the Component Model were not present and stay out of scope.

## Next Core feature group

**None.** T03 is not started. There is no Core instruction or section group that these fixed inputs need and the translator rejects or executes differently from Node.

Do not start T04/T05 from this measurement: the fixed inputs do not contain an unimplemented SIMD opcode. The conditional WASI tasks are the only remaining product gap on the fixed set, and only if that work is chosen. T06's question (host reads of owned and imported memory, including during `start`) is not answered by `wasi_hello.wasm`, which has owned memory and no start section; its `_start` export already reads memory from a hand-written `fd_write`.
