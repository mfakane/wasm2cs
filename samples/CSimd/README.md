# Clang SIMD fixture

`Simd.wasm` is compiled from `simd.c`; it is not hand-assembled. The loops are
plain C. The SIMD instructions come from Clang's autovectorizer, not intrinsics.
Regenerate with `scripts/create-clang-workload-fixtures.sh` (Linux/macOS) or
`scripts/create-clang-workload-fixtures.ps1` (Windows, not run on 2026-10-03).

Toolchain: wasi-sdk-24.0 (Clang 18.1.2-wasi-sdk), target `wasm32-wasi`,
`-O3 -msimd128 -g0`, linked as a library like `samples/CWorkload`
(`-nostartfiles -Wl,--no-entry -Wl,--export-memory -Wl,--strip-all`).
Binary SHA-256: `25fa5682559a4e4c3b5d767f838482d6b4d4e49abd3260105d528d1eb502ef29`.

Exports (all `i32` in and out): `init`, `saxpy`, `dot`, `byte_sum`, `byte_max`,
`clamp_scale`, `bytes_ptr`. No imports.

After T05 (2026-10-06), `dotnet-vector` translates and matches Node on
`calls.json` (`ExecutionChecks.CSimd`, [t04-simd-selection.md](../../docs/t04-simd-selection.md)).
`unity-mathematics` translates; Editor/IL2CPP unverified.
`portable-netstandard2.0` and `dotnet-netstandard2.1` still reject SIMD.
