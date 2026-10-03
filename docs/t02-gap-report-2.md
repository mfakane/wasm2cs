# T02 gap report, second pass

Measured on 2026-10-03 (JST) on branch `feat/t02-realistic-inputs`, based on `4543a6e` (T07 `proc_exit`). The first pass (`docs/t02-gap-report.md`) found no Core gap because its fixed inputs were small: the Rust fixture is `no_std` at `opt-level=z` and uses 12 distinct opcodes. This pass adds compiler output that looks more like real code. It re-runs only the new inputs. The first-pass inputs are not re-measured here.

Toolchain: .NET SDK 10.0.400, Node.js v22.17.0 (dev shell; the first pass used 22.19.0), wasmtime 28.0.1, wasi-sdk-24.0 (Clang 18.1.2-wasi-sdk), WABT 1.0.41. Rust was not used: no Rust toolchain was installed, and bumping the pinned 1.85 is a separate decision.

## New inputs (T01)

| Input | Source | Build |
|---|---|---|
| `samples/CWorkload/Workload.wasm` | `workload.c` | `-O2`, Clang 18 default CPU, library link |
| `samples/CWorkload/WorkloadFeatures.wasm` | same | `-O2 -mbulk-memory -msign-ext -mnontrapping-fptoint -mmultivalue -mreference-types` |
| `samples/CSimd/Simd.wasm` | `simd.c` | `-O3 -msimd128` (autovectorized, no intrinsics) |
| `samples/WasiPreview1/wasi_printf.wasm` | `printf.c` | `-O1`, wasi-libc stdio |

Regenerate with `scripts/create-clang-workload-fixtures.sh` and `scripts/create-wasi-fixture.sh`. Both rebuilt every binary byte-for-byte, including the existing `wasi_hello.wasm` and `exit_code.wasm`. Hashes and flags are in each `samples/*/README.md`. The `.ps1` variants were updated but not run.

## How gaps were counted

`node scripts/wasm-opcodes.mjs <file.wasm>` walks every code body with `wasm-objdump -d` and counts each instruction. It does not stop at the first rejected one. Local declarations are not counted. Sections were listed with `wasm-objdump -h` and imports with `wasm-objdump -x -j Import`.

Translation: `dotnet run --project src/Wasm2Cs.Cli -- <file.wasm>`, default profile `portable-netstandard2.0`. `Simd.wasm` was also tried with `-p dotnet-vector` and `-p unity-mathematics`.

Execution: the generated C# was compiled into a temporary `net10.0` consumer that referenced `src/Wasm2Cs.Runtime` and `tests/Wasm2Cs.DotnetHost`. The same call script then ran, in order, on one instance in Node.js `WebAssembly` and in the C# consumer. Each call's return value or trap was compared, plus the SHA-256 of the whole final memory. The temporary consumer is not checked in. Call script for both workload builds:

```json
[["fill",[1,4096]],["fnv64",[0]],["fnv64",[1]],["fnv64",[4096]],["fnv64",[-5]],["fnv64",[99999]],
 ["copy_fill",[0,0,0]],["copy_fill",[3,100,171]],["copy_fill",[2048,2048,255]],["copy_fill",[-1,5,0]],["copy_fill",[0,3000,0]],
 ["signed_bytes",[0]],["signed_bytes",[1]],["signed_bytes",[4096]],["signed_bytes",[-1]],
 ["stats",[0,1]],["stats",[1,1000]],["stats",[4096,3]],["stats",[333,-77]],["stats",[4096,2147483647]],
 ["interpret",[0,0]],["interpret",[256,7]],["interpret",[100,-1]],["interpret",[300,1]],
 ["fill",[-123456,4096]],["interpret",[256,2147483647]],["stats",[4096,100]],
 ["sort_bytes",[0,0]],["sort_bytes",[512,0]],["sort_bytes",[512,1]],["sort_bytes",[512,2]],["sort_bytes",[10,3]],["sort_bytes",[513,0]],
 ["fill",[2147483647,777]],["fnv64",[777]],["copy_fill",[1,777,1]],["signed_bytes",[777]],["sort_bytes",[300,2]],["fill",[0,-1]],["buffer_ptr",[]]]
```

WASI modules ran under `wasmtime run` and `node:wasi` (`version: 'preview1'`, `returnOnExit: true`). Every Preview1 import was wrapped to log its calls.

## Per-input results

| Input | Instructions (distinct) | CLI | Class | Reference | Result |
|---|---|---|---|---|---|
| `Workload.wasm` | 2709 (74) | success | Core | Node | match: 40 calls, final memory hash |
| `WorkloadFeatures.wasm` | 1486 (65) | success | Core | Node | match: 40 calls, final memory hash |
| `Simd.wasm` | 999 (64) | rejected on the 3 profiles tried | SIMD | not run | first error `Function 1: Offset 0x73: Unsupported WASM opcode 0xfd/17` |
| `wasi_printf.wasm` | 7312 (95) | success | WASI | wasmtime + Node preview1 | blocked at the `fd_fdstat_get` host import; translation is not the gap |

### Clang workload, two builds

Sections: type, function, table, memory, global, export, elem, code, data. No imports, no start section. The CLI file name must be a C# identifier, so the feature build is named `WorkloadFeatures.wasm` (a first attempt named `Workload.features.wasm` was rejected for its name only).

Both builds use `br_table` (dense `switch`), `call_indirect` (comparator table), `memory.copy` (2 each), i64 multiply/shift/xor, f64 arithmetic and `sqrt`, and float-to-int conversions. The default build uses `i32.trunc_f32_s`, `i32.trunc_f64_s`, `i64.trunc_f64_s`. The feature build uses the `trunc_sat` forms of the same three. No sign-extension instruction (`i32.extend8_s` etc.) was emitted in either build; Clang used `i32.load8_s` instead. `-mmultivalue` and `-mreference-types` produced no multi-result function or reference-typed instruction that the walk could see.

The call script ran no traps on either build: the C source checks its bounds.

`stats(4096, 2147483647)` converts an out-of-range float to `int`, which is undefined in C. The default build returns 112937946 and the feature build returns -112937766. In each build the C# output equals Node for that build. This is a difference in the C, not in the translator. The final memory SHA-256 was `d8443f23…3018de` for both builds in both engines.

Unsupported opcodes: 0 in both builds.

### Autovectorized SIMD

No imports. Translation stops at the first SIMD instruction outside `v128.const` / `f32x4.add` / `f32x4.mul` on `portable-netstandard2.0`, `dotnet-vector`, and `unity-mathematics` (`dotnet-netstandard2.1` was not tried), so it was not executed.

Full-module SIMD counts (WABT names): `i32x4.add` 23, `v128.and` 16, `v128.const` 14, `i16x8.narrow_i32x4_u` 8, `i8x16.shuffle` 6, `v128.store` 6, `i8x16.max_u` 5, `i32x4.mul` 4, `i8x16.narrow_i16x8_u` 4, `v128.load` 4, `f32x4.add` 2, `f32x4.lt` 1, `f32x4.mul` 1, `f32x4.pmin` 1, `f32x4.splat` 1, `i16x8.extend_low_i8x16_u` 1, `i32x4.extend_low_i16x8_u` 1, `i32x4.extract_lane` 1, `i32x4.splat` 1, `i8x16.extract_lane_u` 1, `v128.bitselect` 1, `v128.load32_zero` 1.

That is 22 distinct SIMD instructions, of which 19 are unsupported (all but `v128.const`, `f32x4.add`, `f32x4.mul`). The non-SIMD instructions in this module (f32 arithmetic, f32 load/store, int-to-float conversions, MVP integer and control flow) all appear in inputs that already translate and match a reference (the first-pass Floating sample and the two workload builds).

### WASI `printf`

Imports: `fd_close (i32)->i32`, `fd_fdstat_get (i32,i32)->i32`, `fd_seek (i32,i64,i32,i32)->i32`, `fd_write`, `proc_exit`. Export `_start`, owned memory, no start section. Uses `call_indirect` 13, `br_table` 4, `memory.copy`, `memory.fill`, `i64.div_u`, and f64 formatting arithmetic. CLI translation succeeds.

| Runtime | Import calls | stdout | stderr | exit |
|---|---|---|---|---|
| wasmtime 28.0.1 | (not traced) | `count=4 total=1028 mean=257.000 hex=0x404 name=wasi\n` | `warn:  12.3%\n` | 0 |
| Node v22.17.0 preview1 | `fd_fdstat_get(1, 69144) -> 0`, `fd_write(1, 69152, 2, 69148) -> 0`, `fd_write(2, 70032, 2, 70028) -> 0` | same bytes | same bytes | `start()` 0 |
| C# test host (`FdWrite`, `ProcExit`; others throw `UnsupportedImportException`) | `fd_fdstat_get(1, 69144)` → unsupported | none | none | did not finish |

wasi-libc calls `fd_fdstat_get` on stdout before the first write to choose stdout buffering. `fd_seek` and `fd_close` are imported but were not called in this run.

Probe, scratch only (not a product change): with a temporary `fd_fdstat_get` that wrote a character-device record and returned 0, the C# run made the same three calls with the same pointers as Node, wrote the same stdout and stderr bytes, and returned from `_start`. So once that import is connected, nothing after it differs on this input.

## Confirmed gaps

| Gap | Class | Where it showed up | Priority |
|---|---|---|---|
| None in decode, immediates, validation, C# compile, or runtime for Clang `-O2` output, with or without bulk-memory / sign-ext / nontrapping-fptoint / multivalue / reference-types flags | Core | both workload builds matched | — |
| 19 SIMD instructions (list above), needed by Clang autovectorization at `-O3 -msimd128` | SIMD | `Simd.wasm` only | **next candidate for T04**: a concrete input now exists |
| `wasi_snapshot_preview1.fd_fdstat_get` not in the test host | WASI | `wasi_printf.wasm`; the only blocker on that input | **next T07 iteration**, if WASI work continues |
| `fd_seek`, `fd_close` imported but not called | WASI | `wasi_printf.wasm` | not needed by the measured path |

## Next Core feature group

**None.** T03 is still not started. Clang `-O2` output translated and matched Node with each feature set tried.

The plan leaves the choice between SIMD (T04/T05) and WASI (T07) to measurement. This pass gives each one a concrete input. T07 `fd_fdstat_get` is one import that unblocks `wasi_printf.wasm`. T04 needs a narrower first group cut from the 19 SIMD instructions. Not measured: Rust `std` / newer rustc output, C++, inputs larger than about 7k instructions, and Unity/IL2CPP.
