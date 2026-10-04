# Rust std telemetry-summary fixtures

These library-style modules compile the same `src/lib.rs` with Rust 1.85.0 and
1.90.0, target `wasm32-unknown-unknown`. They use `std` (`Vec`, integer parsing,
`sort_unstable`, `BTreeMap`, and `String` formatting) and the standard allocator.
They have no imports, WASI entry point, or start section. This is a project-owned
representative workload, not a third-party application. The source uses the
project's 0BSD license.

## Regeneration

Install both exact toolchains with the WASM target, then run from the repository:

```sh
rustup toolchain install 1.85.0 --profile minimal --target wasm32-unknown-unknown
rustup toolchain install 1.90.0 --profile minimal --target wasm32-unknown-unknown
bash scripts/create-rust-std-fixtures.sh
```

The manifest pins release `opt-level=2`, `panic=abort`, `lto=true`,
`codegen-units=1`, and `strip=true`. The script uses `--locked --offline`,
`--remap-path-prefix=<repository>=/wasm2cs`, `-C link-arg=--no-entry`, and
`-C link-arg=--export-memory`. Compiler target features retain their defaults;
no instruction or function is removed after compilation. There are no Cargo
dependencies. `rust-toolchain.toml` pins 1.90.0 for direct Cargo use; the script
explicitly selects each version. `RUST_STD_TARGET_DIR` can select a separate
build directory for checking reproducibility.

| File | Compiler | Bytes | SHA-256 |
|---|---|---:|---|
| `RustStd185.wasm` | `rustc 1.85.0 (4d91de4e4 2025-02-17)` | 29997 | `a4d566a5e4a27c4313190f040715db3f1a938dc556c2c902f1bcaff44b421f95` |
| `RustStd190.wasm` | `rustc 1.90.0 (1159e78c4 2025-09-14)` | 27413 | `02a2e53a0694d8176207ec0758589efe9e8b4ca45ad986ce7cd448dd30eb943a` |

## Host API

Write UTF-8 signed integer readings, separated by ASCII whitespace, at
`buffer_ptr()`. `buffer_capacity()` is 131072 bytes. Invoke `analyze(length)`;
on success `result_sum()` gives the wrapping i64 sum, `result_unique()` the
distinct-value count, and `result_median()` the sorted element at `n / 2`,
where `n` is the number of readings. Read `output_len()` bytes
from `output_ptr()` for the ascending `value:count\n` histogram.

`analyze` returns 0 on success, -1 for invalid UTF-8, -2 for an invalid or
overflowing i64 token, -3 for an input length above capacity, -4 for no readings,
and -5 when the formatted output exceeds capacity. Every call resets result
metadata first; on error `output_len()` is zero. Output storage beyond that
length may retain previous bytes. Input bytes remain unchanged. `input_byte`
uses Rust's bounds check; an index at capacity aborts with WASM `unreachable`.

## Measurement

```sh
node scripts/measure-rust-std.mjs
# Include all-body opcode counts and section inventory (requires WABT):
node scripts/measure-rust-std.mjs --inventory
```

The checked-in binaries need .NET SDK 10 and Node.js 22 for comparison; Rust is
only needed to regenerate them. The script compiles generated C# as C# 9 with
the netstandard2.0 runtime ABI, runs on .NET 10, and compares 18 `analyze` calls
per module with Node and independently calculated expected values. Full memory
hashes after every call, memory growth, a bounds trap, and a fresh instance are
also compared. Artifacts are retained in `artifacts/t02-rust-std/`.
The regression workflow runs the comparison without requiring Rust or WABT.

See [the T02 round 3 report](../../docs/t02-gap-report-3.md). This measurement
does not cover host I/O, all Rust std APIs, arbitrary Rust versions, or every
Unity platform and backend.

## Unity measurement

The Rust 1.90.0 fixture was also checked with the packaged Unity UPM package
on Unity 6000.6.0f1 Editor Mono and Windows x64 IL2CPP. From the repository root:

```sh
node scripts/measure-rust-std.mjs --unity-oracle
node scripts/pack.mjs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-unity-rust-std.ps1 -PackagePath artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz
```

The Windows PowerShell harness requires Unity 6 with Windows Build Support
(IL2CPP). It creates a temporary project and checks 18 calls, complete memory
snapshots, growth, a bounds trap, and a fresh instance against the Node oracle.
See the [T02 round 4 report](../../docs/t02-gap-report-4.md) for the measured
versions, hashes, and scope limits.
