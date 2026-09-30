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
- WASI.
- SIMD instructions beyond `v128.const`, `f32x4.add`, `f32x4.mul`.
- `v128` globals.
- Arbitrary Rust/C/C++ output: most real-world outputs require instructions or sections not yet in the subset.

This is **not** an execution sandbox. There is no fuel or time limit, and recursive calls use the host stack. Host resource exhaustion is not normalized to a WASM trap.
