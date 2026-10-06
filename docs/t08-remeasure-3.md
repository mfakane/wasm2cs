# T08: re-measurement after T05 SIMD

Measured on 2026-10-06 (JST) at `f7fbe28` (merge of PR #8, T04/T05 SIMD) on branch `feat/t08-remeasure-after-t05`. This pass re-runs **every** fixed input from T02 rounds 1–5 and the earlier T08 passes with the same methods. The previous full remasure (`docs/t08-remeasure-2.md`) predated T05; `Simd.wasm` was rejected on all four profiles there.

Toolchain: .NET SDK 10.0.401, Node.js v22.19.0, wasmtime 28.0.1, wasi-sdk-24.0, WABT 1.0.41.

## Method

- **Translation:** `dotnet run --project src/Wasm2Cs.Cli -- <file.wasm>` (default `portable-netstandard2.0`). SIMD inputs also ran on `dotnet-netstandard2.1`, `dotnet-vector`, and `unity-mathematics`.
- **Covered by the test suite** (`dotnet run --project tests/Wasm2Cs.Tests`, 45 PASS): Arithmetic, Algorithms, Floating, Exceptions, both CWorkload builds, CppWorkload, CSimd (`dotnet-vector` vs Node; portable profiles reject; Unity translates), and the three WASI fixtures. Each is compared with Node.js, wasmtime, or fixed values recorded from them.
- **Not only in the suite**, so also re-run with the T02 scripts or a temporary consumer (not checked in):
  - Rust `no_std` fixture, `Host.wasm`, `FunctionPointers.wasm`, `Vector.wasm` (`dotnet-vector`): temporary harness vs Node (and wasmtime for Vector lanes).
  - RustStd 1.85.0 / 1.90.0: `node scripts/measure-rust-std.mjs`.
  - Third-party (hash-wasm 4.12.0, xxhash-wasm 1.1.0): `node scripts/measure-third-party.mjs` (network; SHA-256 pinned tarballs).
- **WASI:** covered by `ExecutionChecks.Wasi*` against the recorded wasmtime / `node:wasi` behavior.
- `node scripts/test-build.mjs`, `node scripts/pack.mjs`, and `node scripts/test-package.mjs` are run as the distribution sanity check for this T08 (no distribution path code change).

## Before and after

| Input | Before (prior T02/T08) | Now |
|---|---|---|
| `samples/Smoke/Arithmetic.wasm` | match (Node), test suite | match, test suite |
| `samples/CAlgorithms/Algorithms.wasm` | match (Node), test suite | match, test suite |
| `samples/RustWasm32UnknownUnknown/rust_wasm32_fixture.wasm` | match (Node) | match, harness: `add_i64` 42 / `i64::MIN` / 0, `sum_u8` of `01 02 fa ff 00` → 508 / length 0 → 0, bytes unchanged, `__data_end` = `__heap_base` = 1048576, both OOB `sum_u8` trap `MemoryOutOfBounds` (Node same) |
| `samples/Floating/Floating.wasm` | match (Node), test suite | match, test suite |
| `samples/Host/Host.wasm` | match (Node) | match, harness (`HostChecks` script): `f(3)` 26, log `42,3,99`, memory at 16 `9,8,7` (Node same) |
| `samples/Exceptions/*.wasm` (4) | match (Node; CLR exception rule for `HostException`) | unchanged, test suite |
| `samples/Tables/FunctionPointers.wasm` | match (Node) | match, harness: `call(41,0)` 42, `call(1,1)` → `TableOutOfBounds` (Node: table index out of bounds) |
| `samples/Vectors/Vector.wasm.base64` | `dotnet-vector` lanes `(22,44,66,88)` = wasmtime | same lanes and bits on `dotnet-vector`; `unity-mathematics` translates; portable profiles reject `v128` |
| `samples/WasiPreview1/wasi_hello.wasm` | match, test suite | match, test suite: `hello wasi\n`, exit 0 |
| `samples/WasiPreview1/exit_code.wasm` | match, test suite | match, test suite: stderr `exit 3\n`, `proc_exit(3)`, exit 3 |
| `samples/WasiPreview1/wasi_printf.wasm` | match (after T07 `fd_fdstat_get`) | match, test suite |
| `samples/CWorkload/Workload.wasm` | match, test suite | match, test suite: 40 calls and final memory |
| `samples/CWorkload/WorkloadFeatures.wasm` | match, test suite | match, test suite |
| `samples/CSimd/Simd.wasm` | **rejected on all 4 profiles** (`docs/t08-remeasure-2.md`) | **`dotnet-vector`: match Node** over 17 calls and final memory (`ExecutionChecks.CSimd`); `unity-mathematics`: translates (Editor / Windows IL2CPP **not** run); `portable-netstandard2.0` and `dotnet-netstandard2.1`: still reject (`does not support v128 lowering`) |
| `samples/RustStd/RustStd185.wasm` | match Node (T02 round 3); Unity unverified | match Node (`measure-rust-std.mjs`); Unity Editor / IL2CPP still **unverified** (no Windows/Unity here) |
| `samples/RustStd/RustStd190.wasm` | match Node; Unity Editor Mono + Windows x64 IL2CPP matched (T02 round 4) | match Node (`measure-rust-std.mjs`); Unity not re-run in this environment (prior round 4 result unchanged) |
| `samples/CppWorkload/CppWorkload.wasm` | match Node (T02 round 5), test suite | match, test suite: 47 calls and final memory; trap kinds match wasmtime |
| Third-party (hash-wasm 4.12.0 ×22, xxhash-wasm 1.1.0) | match Node (T02 round 5) | match Node again (`measure-third-party.mjs`): 2,396 export calls, results/traps/full memory; bcrypt/scrypt need a non-default class name (`--class-name` / `ClassName`; default name collides with export; no auto-rename) |

## What changed in the documentation

- `docs/supported-features.md`: SIMD / measured-output wording points at this remasure; Unity runtime for the new SIMD group stays unverified; no claim of Unity SIMD support.
- `docs/usage.md`: target-profile note lists the T05 SIMD group (not only `v128.const` / `f32x4.add` / `f32x4.mul`); measured scope cites this remasure.
- `README.md`: limitation sentence no longer says SIMD autovectorized code does not translate; it states the profile requirement and that Unity runtime for SIMD is unverified.
- `docs/wasm-support-plan.md`: T08 records this third remasure.

## Still unsupported or unverified

- SIMD opcodes beyond the T05 group in [t04-simd-selection.md](t04-simd-selection.md). Portable / `dotnet-netstandard2.1` still reject all `v128`.
- **Unity Editor and Windows x64 IL2CPP were not run for `Simd.wasm`** (or any new SIMD path). Do not claim Unity support for the T05 instructions. Rust 1.85.0 on Unity remains unverified (steps in [t02-gap-report-5.md](t02-gap-report-5.md)).
- WASI: `fd_seek`, `fd_close`, and every other Preview1 import; start-section `fd_write` on owned memory; shipped WASI host (host stays test code in `tests/Wasm2Cs.DotnetHost`).
- Export name colliding with the generated class name (bcrypt, scrypt): clearer error by default; rename the class with `--class-name`, MSBuild `ClassName`, or Unity `WasmImporter.ClassName` (do not rename exports; Unity Editor/IL2CPP for ClassName unverified here).
- threads, shared memory, memory64, GC, Component Model, WASI Preview2, execution sandbox: out of scope.
