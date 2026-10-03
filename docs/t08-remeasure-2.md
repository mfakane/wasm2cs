# T08: re-measurement after the second T02 pass

Measured on 2026-10-03 (JST) at `1c09a27` on `feat/t02-realistic-inputs`. That tree includes T07 `proc_exit` (`docs/t07-proc-exit.md`), T07 `fd_fdstat_get` (`docs/t07-fd-fdstat-get.md`), and the workload differential test. This pass re-runs **every** input from both T02 passes with the same methods. The earlier T08 (`docs/t08-wasi-remeasure.md`) covered only `wasi_hello.wasm`.

Toolchain: .NET SDK 10.0.400, Node.js v22.17.0 (T02 pass 1 used 22.19.0), wasmtime 28.0.1, wasi-sdk-24.0, WABT 1.0.41.

## Method

- **Translation:** `dotnet run --project src/Wasm2Cs.Cli -- <file.wasm>` for every input, on the default `portable-netstandard2.0` profile. The two SIMD inputs ran on all four profiles.
- **Covered by the test suite** (`dotnet run --project tests/Wasm2Cs.Tests`, 43 PASS): Arithmetic, Algorithms, Floating, Exceptions ×4, both workload builds, and the three WASI fixtures. Each is compared with Node.js, wasmtime, or fixed values recorded from them.
- **Not in the test suite**, so re-run in a temporary consumer (not checked in) with the T02 pass 1 cases: the Rust fixture, `Host.wasm`, `FunctionPointers.wasm`, and `Vector.wasm` (`dotnet-vector`). Node.js ran the same cases. wasmtime `--invoke f` ran `Vector.wasm`.
- **WASI:** `wasmtime run` and `node:wasi` preview1 with every import wrapped to log calls, compared with `ExecutionChecks.Wasi*`.
- `node scripts/test-build.mjs`, `node scripts/pack.mjs`, and `node scripts/test-package.mjs` succeeded. No distribution path changed in this range, so these are a sanity check.

## Before and after

| Input | T02 result | Now |
|---|---|---|
| `samples/Smoke/Arithmetic.wasm` | match (Node) | match, test suite |
| `samples/CAlgorithms/Algorithms.wasm` | match (Node) | match, test suite |
| `samples/RustWasm32UnknownUnknown/rust_wasm32_fixture.wasm` | match (Node) | match, harness: `add_i64` 42 / `i64::MIN` / 0, `sum_u8` 508 / 0, bytes unchanged, `__data_end` = `__heap_base` = 1048576, both OOB calls trap |
| `samples/Floating/Floating.wasm` | match (Node) | match, test suite |
| `samples/Host/Host.wasm` | match (Node) | match, harness: `f(3)` 26, log `42,3,99`, memory at 16 `9,8,7` |
| `samples/Exceptions/*.wasm` (4) | match (Node; CLR exception rule for `HostException`) | unchanged, test suite |
| `samples/Tables/FunctionPointers.wasm` | match (Node) | match, harness: `call(41,0)` 42, `call(1,1)` traps |
| `samples/Vectors/Vector.wasm.base64` | `dotnet-vector` lanes `(22,44,66,88)` = wasmtime | same: bits `41b00000 42300000 42840000 42b00000` on both. `portable-netstandard2.0` and `dotnet-netstandard2.1` still reject `v128`; `dotnet-vector` and `unity-mathematics` translate |
| `samples/WasiPreview1/wasi_hello.wasm` | stdout match; host was caller-supplied | match, test suite: `hello wasi\n`, exit 0 |
| `samples/WasiPreview1/exit_code.wasm` | (new in T07) | match, test suite: stderr `exit 3\n`, `proc_exit(3)`, exit 3 |
| `samples/CWorkload/Workload.wasm` | match (T02 pass 2, scratch) | match, now in the test suite: 40 calls and final memory |
| `samples/CWorkload/WorkloadFeatures.wasm` | match (T02 pass 2, scratch) | match, test suite |
| `samples/CSimd/Simd.wasm` | rejected on 3 profiles | rejected on all 4 profiles (`dotnet-netstandard2.1` now tried): `Function 1: Offset 0x73: Unsupported WASM opcode 0xfd/17` |
| `samples/WasiPreview1/wasi_printf.wasm` | blocked at `fd_fdstat_get` | **match**, test suite: same stdout, stderr, and call order as Node; exit 0 as wasmtime |

## What changed in the documentation

- `docs/supported-features.md`: the "arbitrary Rust/C/C++ output" line is replaced with what was measured. SIMD lists the instructions Clang autovectorization needed that are not supported. The WASI section adds `fd_fdstat_get`.
- `docs/usage.md`: the WASI section and its measured scope.
- `README.md`: the limitation sentence about Rust/C/C++ output.

## Still unsupported or unverified

- 19 SIMD instructions from `Simd.wasm` (`docs/t02-gap-report-2.md`). T04 has not selected a group yet.
- WASI: `fd_seek`, `fd_close`, and every other Preview1 import. A start-section import on owned memory. A shipped WASI host (the host is test code).
- Not measured: Rust `std` or rustc newer than 1.85, C++, inputs larger than about 7k instructions.
- Unity Editor and IL2CPP were not run for any of these inputs.
