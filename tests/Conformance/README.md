# Official i32 conformance subset

Source: [WebAssembly/spec wg-1.0](https://github.com/WebAssembly/spec/tree/977f97014c962f7bd1291fcc6d28b41a924882bf/test/core),
commit `977f97014c962f7bd1291fcc6d28b41a924882bf`. `i32.wast` and `LICENSE`
are unmodified upstream files. `i32.json` is derived using WABT 1.0.39.

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
