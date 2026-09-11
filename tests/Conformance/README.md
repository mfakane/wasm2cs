# Conformance fixtures

Source: [WebAssembly/spec wg-1.0](https://github.com/WebAssembly/spec/tree/977f97014c962f7bd1291fcc6d28b41a924882bf/test/core),
commit `977f97014c962f7bd1291fcc6d28b41a924882bf`. `i32.wast` and `LICENSE`
are unmodified upstream files. `i32.json` is derived using WABT 1.0.39; its
schema was migrated to version 2 without changing any binary, argument, or assertion.

The test executable runs 350 expected-value assertions, 9 typed-trap assertions,
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
value transport. Numeric instructions beyond i32 remain unsupported.

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

Both JSON files use `SchemaVersion: 2`. `Args` and `Expected` are arrays, including
empty arrays for void calls. Each primitive is `{ "Type": "i64", "Bits": "0020000000000001" }`,
with lowercase hexadecimal strings of 8 digits for i32/f32 and 16 for i64/f64.
Floating-point expectations may instead use `{ "Type": "f32", "NaN": "canonical" }`
or `"arithmetic"`. Exact bits, including signed zero and NaN payloads, are distinct
from these NaN matching rules. NaN patterns are expectations only, never arguments.
`Trap` is one of `Unreachable`, `DivisionByZero`, `IntegerOverflow`, or
`MemoryOutOfBounds`. Reference-type execution is tested separately in C#; the JSON
value codec currently accepts only the four primitive numeric types.

The console executable runs both JSON suites, compiles the typed fixture against
.NET Standard 2.0 with C# 9 and checked arithmetic, and reruns its expectations in
Node. It also tests both value codecs, rejects unknown commands, and checks the WAST
source hash recorded in `typed-ir.json`. Normal runs require neither WABT nor network.
The NuGet consumer test calls the same fixture through statically typed public methods.

Validation follows the [WebAssembly validation algorithm](https://webassembly.github.io/spec/core/appendix/algorithm.html).
In particular, `br_table` checks every concrete operand against every target; an
unreachable polymorphic bottom can satisfy differing target types of equal arity.
