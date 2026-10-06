# T04: SIMD group for Clang `-O3 -msimd128` (`Simd.wasm`)

Selected on 2026-10-06 (JST) from `master` `4eeba86`, after T02 round 5
([t02-gap-report-5.md](t02-gap-report-5.md)). The only fixed input with a concrete
SIMD gap is `samples/CSimd/Simd.wasm` (Clang 18, `-O3 -msimd128`, autovectorized;
see [samples/CSimd/README.md](../samples/CSimd/README.md) and
[t02-gap-report-2.md](t02-gap-report-2.md)).

## Decision

Implement **all 19** missing SIMD opcodes from that module as **one group**.

They appear together in one autovectorized binary. No smaller subset lets
`Simd.wasm` translate. They share one storage type per profile
(`Vector128<float>` / `float4`), the same memarg and lane-immediate shapes, and
the same portable / `dotnet-netstandard2.1` rejection rule already used for
`v128.const` / `f32x4.add` / `f32x4.mul`.

T03 stays unstarted (no Core gap). Portable profile is **not** extended.

## Opcodes (0xfd prefix)

| # | Name | Immediates | Stack | Notes |
|---:|---|---|---|---|
| 0 | `v128.load` | memarg (align, offset); natural align 4 | `[i32] -> [v128]` | 16-byte load; OOB → `MemoryOutOfBounds` |
| 11 | `v128.store` | memarg; natural align 4 | `[i32, v128] -> []` | 16-byte store; OOB → `MemoryOutOfBounds` |
| 13 | `i8x16.shuffle` | 16 lane bytes in `0..31` | `[v128, v128] -> [v128]` | lanes index `a\|\|b` as 32 bytes |
| 17 | `i32x4.splat` | — | `[i32] -> [v128]` | bit-preserving splat |
| 19 | `f32x4.splat` | — | `[f32] -> [v128]` | |
| 22 | `i8x16.extract_lane_u` | lane `0..15` (raw byte) | `[v128] -> [i32]` | zero-extends |
| 27 | `i32x4.extract_lane` | lane `0..3` (raw byte) | `[v128] -> [i32]` | bit-preserving |
| 67 | `f32x4.lt` | — | `[v128, v128] -> [v128]` | per-lane all-ones / zeros mask |
| 78 | `v128.and` | — | `[v128, v128] -> [v128]` | bitwise |
| 82 | `v128.bitselect` | — | `[v128, v128, v128] -> [v128]` | `(a & c) \| (b & ~c)`; pops `c`, `b`, `a` |
| 92 | `v128.load32_zero` | memarg; natural align 2 | `[i32] -> [v128]` | 4-byte load, high 12 bytes zero |
| 102 | `i8x16.narrow_i16x8_u` | — | `[v128, v128] -> [v128]` | saturating unsigned narrow |
| 121 | `i8x16.max_u` | — | `[v128, v128] -> [v128]` | |
| 134 | `i16x8.narrow_i32x4_u` | — | `[v128, v128] -> [v128]` | saturating unsigned narrow |
| 137 | `i16x8.extend_low_i8x16_u` | — | `[v128] -> [v128]` | zero-extend lanes 0–7 |
| 169 | `i32x4.extend_low_i16x8_u` | — | `[v128] -> [v128]` | zero-extend lanes 0–3 |
| 174 | `i32x4.add` | — | `[v128, v128] -> [v128]` | wrapping |
| 181 | `i32x4.mul` | — | `[v128, v128] -> [v128]` | wrapping |
| 234 | `f32x4.pmin` | — | `[v128, v128] -> [v128]` | `b < a ? b : a` (pseudo-min) |

Already supported (unchanged): `v128.const` (12), `f32x4.add` (228), `f32x4.mul` (230).

`v128` globals stay unsupported.

## Target profiles

| Profile | Expectation |
|---|---|
| `dotnet-vector` | Accept the group; lower to `System.Runtime.Intrinsics.Vector128<float>` (with `As*` / helpers for integer and byte lanes). Lane values, traps, and memory bounds must match wasmtime or Node. |
| `unity-mathematics` | Accept the group; lower to `Unity.Mathematics.float4` (with `math.asint` / `asuint` / `asfloat` and helpers). Generate compilable-looking C#. **Unity Editor and Windows IL2CPP are unverified on this box** — do not claim Unity runtime support for the new instructions. |
| `portable-netstandard2.0` | Keep rejecting any `v128` / SIMD instruction (including the three already supported only on vector profiles). |
| `dotnet-netstandard2.1` | Same rejection as portable for SIMD. |

## Concrete WASM inputs

### Should succeed (`dotnet-vector`; `unity-mathematics` translate)

Hand-built modules in `ExecutionChecks` (and `Simd.wasm` itself):

1. **Binary / unary / splat / extract / shuffle / narrow / extend / max / bitselect / pmin / lt** — modules that push known `v128.const` (or splat) values, run one or a short chain of the new ops, and extract an `i32` digest (or return `v128` where the existing vector fixture does). Expected: each lane / extracted scalar matches Node `WebAssembly` or wasmtime on the same bytes.
2. **`v128.load` / `v128.store` / `v128.load32_zero`** — one-page memory; store then load round-trip; `load32_zero` leaves high bytes zero; address `MemorySize - 15` for 16-byte access and `MemorySize - 3` for 4-byte access trap with `MemoryOutOfBounds` (same as the reference).
3. **`samples/CSimd/Simd.wasm`** — after T05, translate with `-p dotnet-vector`, compile, run export calls against Node (and spot-check traps/memory). Unity profile: translation only on this box.

### Should be rejected

1. Same modules on `portable-netstandard2.0` and `dotnet-netstandard2.1` → `WasmException` (unsupported opcode or cannot lower).
2. `i8x16.shuffle` with a lane byte `> 31` → validation failure.
3. `i8x16.extract_lane_u` lane `> 15` or `i32x4.extract_lane` lane `> 3` → validation failure.
4. `v128.load` / `v128.store` / `v128.load32_zero` with align greater than natural → validation failure.
5. Those memory ops in a module with no memory → validation failure.
6. Any SIMD opcode **not** in this group (and not the three existing ones) → still `Unsupported WASM opcode 0xfd/N`.

## T05 scope

Decode → validate → canonical lower → profile emit for this group only; extend
`ExecutionChecks` vector/profile tests; update `docs/supported-features.md`;
re-measure `Simd.wasm` before/after. Unity Editor / IL2CPP remain unverified.

## T05 results (2026-10-06)

Implemented the full group on `dotnet-vector` and `unity-mathematics`.

| Check | Before | After |
|---|---|---|
| `Simd.wasm` CLI `portable-netstandard2.0` | reject (`0xfd/17`) | reject (cannot lower v128) |
| `Simd.wasm` CLI `dotnet-vector` | reject (`0xfd/17`) | success |
| `Simd.wasm` vs Node (`calls.json`, 17 calls + memory) | not run | match |
| Unity Editor / Windows IL2CPP | — | **unverified** |

Acceptance commands: `dotnet run --project tests/Wasm2Cs.Tests`, `node scripts/test-build.mjs`,
`node scripts/pack.mjs`, `node scripts/test-package.mjs`.
