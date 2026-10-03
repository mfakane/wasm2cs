# Clang workload fixtures

`Workload.wasm` and `WorkloadFeatures.wasm` are compiled from `workload.c`; they
are not hand-assembled. Regenerate with `scripts/create-clang-workload-fixtures.sh`
(Linux/macOS) or `scripts/create-clang-workload-fixtures.ps1` (Windows, not run on
2026-10-03). Set `WASI_SDK_PATH` (or `-WasiSdk`) to a wasi-sdk-24.0 install.

Toolchain: wasi-sdk-24.0 (Clang 18.1.2-wasi-sdk), target `wasm32-wasi`, linked as
a library: `-nostartfiles -Wl,--no-entry -Wl,--export-memory -Wl,--strip-all`,
`-g0`, and an explicit `-Wl,--export=` for each function. wasi-libc supplies
`memcpy` / `memset` / `memmove`; the modules have no imports and no `_start`.

| Binary | Extra flags | SHA-256 |
|---|---|---|
| `Workload.wasm` | `-O2` (Clang 18 default CPU) | `46cf8952aeee366677a9f16f27c5d69ae6841b60eda2ec3426510010707a7b21` |
| `WorkloadFeatures.wasm` | `-O2 -mbulk-memory -msign-ext -mnontrapping-fptoint -mmultivalue -mreference-types` | `b162e51628ed043e86f487b5c238514ebafc1e2d32ebc16bb38c3a533b6d6a3d` |

Exports (all `i32` in and out): `fill` (xorshift32 into a 4 KiB buffer),
`fnv64` (64-bit FNV-1a folded to 32 bits), `copy_fill` (`memcpy` / `memset` /
`memmove`), `signed_bytes` (signed byte arithmetic), `stats` (f64 mean, variance,
`sqrt`, and float-to-int conversions), `interpret` (a dense `switch` bytecode
interpreter), `sort_bytes` (insertion sort through a comparator function
pointer), and `buffer_ptr`.

`stats(n, scale)` with a very large `scale` converts an out-of-range float to
`int`, which is undefined in C. The two builds return different values for that
call (trapping-guarded vs. saturating conversion). Each build matches Node.js on
its own.
