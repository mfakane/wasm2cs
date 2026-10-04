# Supported features

wasm2cs translates a specific typed subset of WebAssembly 1.0. This is not whole-spec conformance. Unsupported instructions, import kinds, and sections are rejected at build time with `WASM001`.

## Types

- Value types: `i32`, `i64`, `f32`, `f64`, `v128` (limited), `funcref`, `externref`.
- Multiple function results use statically typed C# tuples.
- The numeric host API uses C# `int`, `long`, `float`, and `double`. Internal float values retain raw bits.

## Sections

Supported: type, function import, function, table, memory, global, export, start, code, element, active/passive data. Custom sections are skipped. All other sections are rejected.

## Instructions

### Locals and constants

`local.get`, `local.set`, `local.tee`; i32/i64/f32/f64 constants. Locals are zero-initialized. Stack values are materialized into temporaries so later local assignments cannot change them.

### Integer arithmetic (i32/i64)

MVP arithmetic, comparisons, bitwise operations, shifts, rotates, and bit counts (`clz`, `ctz`, `popcnt`). Wrap/extend conversions and the five sign-extension instructions (`i32.extend8_s`, etc.). Integer arithmetic wraps at 32/64 bits.

Trapping operations: signed division overflow and division/remainder by zero. Signed remainder of `INT_MIN` by `-1` returns zero per the spec (no trap).

### Floating-point (f32/f64)

MVP arithmetic, comparisons, rounding (`floor`, `ceil`, `trunc`, `nearest`), square root, `min`/`max`, and sign operations (`abs`, `neg`, `copysign`). Numeric conversions, reinterpret, and all eight saturating truncations (`i32.trunc_sat_f32_s`, etc.).

Bit operations preserve NaN payloads. Rounding uses ties-to-even and preserves signed zero. Truncation checks NaN and range before casting: `InvalidConversionToInteger` for NaN input, `IntegerOverflow` for out-of-range.

### Control flow

`nop`, `drop`, `select` (typed and untyped), `unreachable`, `return`. Structured `block`, `loop`, `if`/`else`, `br`, `br_if`, `br_table`. Block type indices, block/loop parameters, and multiple results are supported. Unreachable instructions are decoded and type-checked.

### Calls

`call`: direct calls including non-exported functions and recursion. Each WASM function is emitted once; exports are public wrappers over private instance methods.

`call_indirect`: typed indirect calls with structural signature comparison (not type-index comparison). Requires table support (see below).

### Reference types

`ref.null`, `ref.is_null`, `ref.func`. Tables with `funcref` or `externref` elements. Active, passive, and declarative element segments. `table.get/set/grow/size/fill/copy/init`, `elem.drop`. Function references use statically typed delegates.

### Memory

One memory32 per module (owned or imported). Load and store for `i32`, `i64`, `f32`, `f64`, including signed and unsigned narrow loads (8/16-bit for i32; 8/16/32-bit for i64). `memory.size`, `memory.grow`, `memory.copy`, `memory.fill`, `memory.init`, `data.drop`. Active and passive data segments. Start functions run once at instantiation after memory and globals are initialized.

Memory growth replaces private backing storage without invalidating the shared `Wasm2Cs.WasmMemory` object. Declared and host memory limits are tracked separately; exceeding either returns `-1` without changing state. Implementation limit: 256 MiB initial size.

### Globals

Numeric globals (`i32`, `i64`, `f32`, `f64`). Owned and imported. Mutable and immutable. Global exports become properties, writable only for mutable globals.

### Exception handling

`throw`, `rethrow`, `try`/`catch`/`catch_all`. Exception tags (import kind `tag`). The runtime uses `Wasm2Cs.WasmTag` and `Wasm2Cs.WasmThrownException`.

### SIMD (limited)

Only `v128.const`, `f32x4.add`, and `f32x4.mul` are supported. All other SIMD instructions are rejected. `v128` globals are not supported. The `dotnet-vector` and `unity-mathematics` target profiles provide the lowering for these three instructions; `portable-netstandard2.0` and `dotnet-netstandard2.1` reject them.

Clang 18 autovectorization (`-O3 -msimd128`, `samples/CSimd`) emitted these unsupported instructions, so such modules are rejected on every profile: `i32x4.add`, `v128.and`, `i16x8.narrow_i32x4_u`, `i8x16.shuffle`, `v128.store`, `i8x16.max_u`, `i32x4.mul`, `i8x16.narrow_i16x8_u`, `v128.load`, `f32x4.lt`, `f32x4.pmin`, `f32x4.splat`, `i16x8.extend_low_i8x16_u`, `i32x4.extend_low_i16x8_u`, `i32x4.extract_lane`, `i32x4.splat`, `i8x16.extract_lane_u`, `v128.bitselect`, `v128.load32_zero` (`docs/t02-gap-report-2.md`).

## Traps

Runtime traps use each generated module's nested `TrapException` and `TrapKind`:

| Kind | Cause |
|---|---|
| `Unreachable` | `unreachable` instruction executed |
| `DivisionByZero` | integer division or remainder by zero |
| `IntegerOverflow` | signed division `INT_MIN / -1` |
| `InvalidConversionToInteger` | float-to-integer truncation with NaN input |
| `MemoryOutOfBounds` | out-of-range memory access |

## Function imports

Constructor arguments follow import-section order (`import0`, `import1`, …), using generated nested delegate types `__wasm_Import0`, etc. Lambdas convert directly; no runtime reflection is used. A missing delegate fails before `start` runs. Host exceptions propagate unchanged through WASM frames, including during `start`. Import `module`/`name` pairs are recorded as UTF-8 Base64 in generated comments.

## Memory and global export API

Memory exports surface the shared `Wasm2Cs.WasmMemory` object plus compatibility methods: `MemorySize`, `ReadMemory(uint offset, int count)`, `ReadMemoryInto`, `WriteMemory(uint offset, byte[] bytes)`, `WriteMemoryFrom`. Reads copy data; no backing array is exposed.

## Floating-point bit API

For exact float payloads across the host boundary, each float-bearing export also has a `__wasm_bits_<name>` method or global property: f32 uses `int` bits, f64 uses `long` bits. Modules with float imports provide `__wasm_FromBits(…)` and `__wasm_BitsImportN` delegates.

Use these when signaling-NaN payloads must cross the host boundary unchanged. The ordinary `float`/`double` API is available and convenient for numeric values, but native CLR float handling can quiet signaling NaNs (observed on Unity Mono).

## Validation

Parsing validates section boundaries and order, LEB128 encodings, indices, and operand/result stack types and heights within the supported subset. All function bodies including unexported ones are checked. Implementation limit: 100,000 locals per function.

## What is not supported

- WASM threads, shared memory, memory64.
- WASM GC proposal.
- WASI Preview1. The packages do not provide a WASI host; supply the imports yourself (see below).
- SIMD instructions beyond `v128.const`, `f32x4.add`, `f32x4.mul`.
- `v128` globals.
- Arbitrary Rust/C/C++ output is not guaranteed. Measured on 2026-10-03 (`docs/t08-remeasure-2.md`): Clang 18 (wasi-sdk-24.0) `-O2` library output, linked with wasi-libc `memcpy`/`memset`, translated and matched Node.js, with or without `-mbulk-memory -msign-ext -mnontrapping-fptoint -mmultivalue -mreference-types`. A `no_std` Rust 1.85 `wasm32-unknown-unknown` library matched too. `-O3 -msimd128` output does not translate (see SIMD).
- The Rust `std` data-summary library in `samples/RustStd` was measured with Rust 1.85.0 and 1.90.0 on 2026-10-03 ([T02 round 3](t02-gap-report-3.md)). It uses integer parsing, `Vec`, sorting, `BTreeMap`, string formatting, and the standard allocator, with no imports. Both modules (about 12–13k instructions) compiled as C# 9 on `portable-netstandard2.0` and matched Node.js in 18 cases, full memory snapshots, memory growth, a bounds trap, and fresh instance memory. Rust 1.90.0 was also tested through the packaged Unity UPM package on Unity 6000.6.0f1 Editor Mono and Windows x64 IL2CPP ([T02 round 4](t02-gap-report-4.md)); both matched Node.js in the same 18 cases and full memory checks. This does not cover other std APIs, other Rust versions on Unity, C++ output, or third-party modules.

## WASI Preview1 `fd_write`, `proc_exit`, and `fd_fdstat_get` (test host only)

WASI imports are ordinary function imports: the caller passes the delegates. The test-only project `tests/Wasm2Cs.DotnetHost` (self-hosting experiment host, not shipped) implements three imports as a reference: `wasi_snapshot_preview1.fd_write` `(i32 fd, i32 iovs, i32 iovs_len, i32 nwritten) -> i32` on `HostEnvironment.FdWrite`, `wasi_snapshot_preview1.proc_exit` `(i32 code)` on `HostEnvironment.ProcExit`, and `wasi_snapshot_preview1.fd_fdstat_get` `(i32 fd, i32 buf) -> i32` on `HostEnvironment.FdFdstatGet`. The translator does not emit a host, and other imports are not stubbed as success. Stdout is fd 1 and stderr is fd 2, using the callbacks passed to `HostEnvironment`. Other descriptors are virtual files from `OpenFile`.

The caller passes the guest `WasmMemory`. Owned memory is not visible until the generated constructor returns, so `fd_write` from a start section on owned memory still cannot read that memory. `samples/WasiPreview1/wasi_hello.wasm` has no start section; call the exported `_start` after `new`. Checked on 2026-10-03 against wasmtime 28.0.1 and Node.js 22.19.0 `node:wasi` preview1 (`docs/t08-wasi-remeasure.md`). Both write `hello wasi` plus a newline (`68 65 6c 6c 6f 20 77 61 73 69 0a`) and exit 0. Node calls `fd_write(1, 66584, 1, 66580)` once, returns errno 0, and does not call `proc_exit`. `ExecutionChecks.WasiFdWrite` matches that stdout. The export `_start` is void; a normal return is that exit 0.

`ProcExit` records the code as given and throws `WasiProcExitException`, because `proc_exit` does not return (wasi-libc follows the call with `unreachable`). Catch it around `_start`. `samples/WasiPreview1/exit_code.wasm` writes `exit 3` plus a newline to stderr and calls `proc_exit(3)`. On 2026-10-03, wasmtime 28.0.1 exited 3 with those stderr bytes, Node.js 22.17.0 `node:wasi` returned 3 from `start()`, and `ExecutionChecks.WasiProcExit` matched both (`docs/t07-proc-exit.md`).

`FdFdstatGet` reports fd 1 and fd 2 as a character device with only the `fd_write` right, and `OpenFile` fds as regular files (`fd_write` only when writable). Unknown fds return `EBADF`. This record is a host choice; Node's depends on where its stdout goes. wasi-libc stdio calls it once on stdout before the first write. `samples/WasiPreview1/wasi_printf.wasm` (`printf` and `fprintf`) then matched wasmtime 28.0.1 and Node.js 22.17.0 in stdout, stderr, exit 0, and import call order (`docs/t07-fd-fdstat-get.md`). It also imports `fd_seek` and `fd_close`, which it does not call and the host does not implement.

Not in this measured slice: any other Preview1 import (including `fd_seek` and `fd_close`), exit codes other than 3 against a reference runtime, `fd_write` from a start section on owned memory, a sandbox, and shipping the host in NuGet or Unity (the host stays test code). Unity and IL2CPP were not run. No target profile changed.

This is **not** an execution sandbox. There is no fuel or time limit, and recursive calls use the host stack. Host resource exhaustion is not normalized to a WASM trap. `fd_write` does not add one.
