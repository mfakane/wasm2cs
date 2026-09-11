# Conformance fixtures

Source: [WebAssembly/spec wg-1.0](https://github.com/WebAssembly/spec/tree/977f97014c962f7bd1291fcc6d28b41a924882bf/test/core),
commit `977f97014c962f7bd1291fcc6d28b41a924882bf`. `i32.wast`, `i64.wast`, and `LICENSE`
are unmodified upstream files. `i32.json` is derived using WABT 1.0.39; its
schema was migrated to version 2 without changing any binary, argument, or assertion.

The i32 suite runs 350 expected-value assertions, 9 typed-trap assertions,
and 54 invalid-module assertions. It explicitly skips 29 commands: invalid modules
using non-i32 types, and WAT-text syntax errors. This is **not** the entire official
suite. Control flow, calls, memory, imports, and regeneration are covered separately
by repository differential tests and real .NET/Unity build tests.

Reproduce (network needed only for this step):

```sh
npm ci --prefix scripts/conformance --ignore-scripts
node scripts/conformance/prepare.mjs
```

The adapter supports only the fixed file's command shapes and fails on unknown
commands. WABT assembles module expressions without validating intentionally invalid
modules. Node independently checks all return/trap expectations and invalid binaries.
Ordinary test runs use the checked-in JSON and need neither WABT nor network access.

## Typed IR fixture and schema

`typed-ir.wast` is a repository-owned SH-02 fixture, assembled with WABT 1.0.41
and checked with Node.js v22.17.0 / V8 12.4.254.21-node.26. Its 32 return assertions,
2 typed traps, and 23 invalid modules cover typed locals/calls, block parameters,
loop backedges, branch tables, multiple results, unreachable code, and i64/float
value transport. Its coverage concerns typed control flow; i64 numeric coverage
was added in SH-03 below.

Regenerate with the pinned development shell:

```sh
node scripts/conformance/prepare-typed.mjs tests/Conformance/typed-ir.wast tests/Conformance/typed-ir.json
```

This adapter consumes `wast2json` output, separately from the fixed i32 parser.
It accepts `module`, `assert_return`, `assert_trap`, `assert_invalid`, and
`assert_malformed`; actions are `invoke` or `get` on the current module.
Text-only malformed modules are explicitly skipped. Registration, cross-module
actions, exhaustion assertions, unknown commands/actions/value types/traps, and
unknown module formats fail. It does not claim to execute arbitrary WAST files.
WABT 1.0.41 emits invalid JSON for a trap assertion on a function with multiple
results; the fixture tests that trap through a void wrapper and the adapter
rejects malformed JSON without repairing it.

All JSON files use `SchemaVersion: 2`. `Args` and `Expected` are arrays, including
empty arrays for void calls. Each primitive is `{ "Type": "i64", "Bits": "0020000000000001" }`,
with lowercase hexadecimal strings of 8 digits for i32/f32 and 16 for i64/f64.
Floating-point expectations may instead use `{ "Type": "f32", "NaN": "canonical" }`
or `"arithmetic"`. Exact bits, including signed zero and NaN payloads, are distinct
from these NaN matching rules. NaN patterns are expectations only, never arguments.
`Trap` is one of `Unreachable`, `DivisionByZero`, `IntegerOverflow`, or
`MemoryOutOfBounds`, or `InvalidConversionToInteger`. Reference-type execution is tested separately in C#; the JSON
value codec currently accepts only the four primitive numeric types.

The console executable runs all JSON suites, compiles the typed fixture against
.NET Standard 2.0 with C# 9 and checked arithmetic, and reruns its expectations in
Node. It also tests both value codecs, rejects unknown commands, and checks the WAST
source hash recorded in `typed-ir.json`. Normal runs require neither WABT nor network.
The NuGet consumer test calls the same fixture through statically typed public methods.

Validation follows the [WebAssembly validation algorithm](https://webassembly.github.io/spec/core/appendix/algorithm.html).
In particular, `br_table` checks every concrete operand against every target; an
unreachable polymorphic bottom can satisfy differing target types of equal arity.

## i64 integer and memory coverage

`i64.wast` is the unmodified official wg-1.0 file at the commit above, SHA-256
`36f1d5b1f27495db6ce12dfecc9ed9e5f4a1bbae300357a655901ee866604de5`.
The typed WABT adapter handles all 389 commands: one module, 350 return assertions,
9 traps, and 29 invalid modules. There are no skipped commands in this file.
`i64.json` records its commit, source URL, WABT 1.0.41, Node v22.17.0, and V8
12.4.254.21-node.26. The existing upstream `LICENSE` applies to both official files.

`i64-memory.wast` is a repository-owned fixture with 23 return assertions, 5 traps,
and 6 invalid modules. It exercises i64 global initialization, full/narrow memory
operations, unsigned memory32 bounds, unaligned access, and unchanged bytes after a
trapping store. Both source hashes are checked during the console tests.

```sh
node scripts/conformance/prepare-i64.mjs
node scripts/conformance/prepare-typed.mjs tests/Conformance/i64-memory.wast tests/Conformance/i64-memory.json
```

The first command downloads the pinned official file and verifies its hash before
preparing JSON. Both commands require the pinned Node/WABT development shell.
Normal console tests use the checked-in files and need no WABT or network.

`I64Checks` separately generates modules for every integer operation, tests boundary
pairs and 256 random cases per operation, and compares generated C# to a separate
Node process. Its xorshift64 seed is `0x6a09e667f3bcc909`. Inputs/results and callback
arguments use the shared fixed-width hexadecimal codec, so i64 never passes through
a JSON number. The same run verifies final memory/global contents, callback order,
all invalid tenth-byte LEB payloads, overlong/truncated LEBs, and type mismatches.
Generated sources compile with C# 9, checked arithmetic, and .NET Standard 2.0
reference assemblies. A NuGet-only consumer calls the official i64 and memory
fixtures directly. Floating-point conversions and reinterpret are covered below.


## Floating-point and conversion coverage

The eight SH-04 source files are unmodified files from
[WebAssembly/spec wg-2.0](https://github.com/WebAssembly/spec/tree/fffc6e12fa454e475455a7b58d3b5dc343980c10/test/core),
commit `fffc6e12fa454e475455a7b58d3b5dc343980c10`. The upstream license applies;
`scripts/conformance/floats-sources.json` pins every source SHA-256. Generated JSON
records the source hash, commit, URL, WABT 1.0.41, Node v22.17.0 and V8 version.

| Suite | Return assertions | Traps | Invalid binaries | Explicit WAT-text skips |
|---|---:|---:|---:|---:|
| f32 / f64 (each) | 2,500 | 0 | 11 | 2 |
| f32_cmp / f64_cmp (each) | 2,400 | 0 | 6 | 0 |
| f32_bitwise / f64_bitwise (each) | 360 | 0 | 3 | 0 |
| conversions | 526 | 67 | 25 | 0 |
| float_literals | 99 | 0 | 0 | 78 |

Regenerate all eight with `node scripts/conformance/prepare-floats.mjs`. The adapter
first verifies the original binaries in V8. It then changes only export-section
names that cannot be C# identifiers and matching action names, records that mapping
in `ExportRenames`, and verifies the adapted binaries again. This includes the
binary-literal module in `float_literals.wast`; no instruction bytes are rewritten.
There are no skipped binary or numeric assertions. WAT syntax errors are outside
this binary translator and retain explicit skip records.

`bit-bridge.mjs` imports each reference WASM function into another WASM module.
The wrapper reinterprets float arguments/results inside WASM, so only i32/i64 bits
cross the JavaScript boundary. This preserves signaling NaNs for abs, neg,
copysign, reinterpret, transport and memory. Arithmetic assertions use the spec's
canonical/arithmetic NaN sets instead of requiring one engine's chosen payload.
A global wrapper likewise reads bits inside WASM. The checked-in trap commands
with float inputs include `Results`, supplying the wrapper's result signature.
V8 reports NaN and out-of-range float-to-integer traps with the same message;
reference WAST verification accepts that shared diagnostic for those two expected
kinds, while C# must match the precise expected kind. The random single-instruction
oracle distinguishes them from the input bit pattern.

`FloatChecks` uses xorshift64 seed `0xbb67ae8584caa73b`, boundary pairs and 512 random
inputs per instruction, covering all 70 arithmetic/comparison/conversion opcodes.
Integer-to-float tests include values immediately around f32/f64 rounding
midpoints. Floating inputs include signed zeros, subnormals, infinities, signaling
and quiet NaNs, and adjacent values at truncation limits. Each operation also runs through the generated integer-bit boundary API when its
signature contains floats. Exact comparisons cover constants, memory, globals, locals, multiple results and host callbacks.
Trapping stores are checked against final memory contents. Negative tests reject
incorrect signatures, truncated float immediates and unknown/malformed prefixes.
Normal tests recheck all eight source hashes and V8 expectations without WABT or
network access.

`samples/Floating/Floating.wat` is the shared portable fixture. Regenerate with
`wat2wasm samples/Floating/Floating.wat -o samples/Floating/Floating.wasm` (WABT
1.0.41). `FloatingChecks.cs` runs statically typed assertions in the .NET sample,
the isolated NuGet consumer, Unity Editor and the Windows x64 IL2CPP player. It
includes a rounding-midpoint reproducer, exact signaling-NaN transport, signed
zero, subnormal arithmetic, conversion limits, all eight saturation instructions,
and memory traps. No runtime code generation is used by the player.


Unity Mono exposed two native-runtime differences during SH-04: moving a signaling
NaN through CLR floating values quieted it, and converting `0x8000008000000001` as
u64 to f32 returned `0x5f000000` instead of `0x5f000001`. The generated code now
keeps both float widths in explicit-layout values whose bits are used for
non-arithmetic operations; integer-to-float conversion builds the correctly rounded
bits directly. `tests/Platform/MonoFloatBoundary.cs` reproduces the native NaN
boundary behavior. The original Editor failure log is retained under
`artifacts/self-hosting/sh04/editor-before-fix.log`.

Exact host transport uses generated `__wasm_bits_<export>` methods/properties and
`__wasm_FromBits` import construction. The native float/double API is tested on .NET,
and remains convenient for numeric values on Unity, but native host conversions can
quiet signaling NaNs before the translator receives them. The shared platform
fixture uses the bit API for exact NaN assertions; it does not relax expected bits.
