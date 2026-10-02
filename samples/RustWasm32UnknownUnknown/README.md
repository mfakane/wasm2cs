# Rust wasm32-unknown-unknown fixture

`rust_wasm32_fixture.wasm` is compiled from `src/lib.rs`; it is not hand-assembled.
Regenerate with `scripts/create-rust-fixture.sh` (Linux/macOS) or
`scripts/create-rust-fixture.ps1` (Windows).

Toolchain: Rust `1.85.0` (pinned by `rust-toolchain.toml`), target
`wasm32-unknown-unknown`, `cargo`/`rustc` 1.85.0.
Binary SHA-256: `11e623dd79b24be745cb2603f4249f050f795e1ad93f1a751060375d8b9907e3`.

The script builds a `cdylib` with `cargo rustc --target wasm32-unknown-unknown
--release --lib`, then passes `-C opt-level=z -C panic=abort`,
`-C link-arg=--no-entry -C link-arg=--export-memory`, and explicit exports for
`add_i64` and `sum_u8`. The crate is `#![no_std]` with a looping panic handler.
The generated binary is committed so normal tests do not depend on a local Rust
toolchain.

`add_i64(a, b)` returns `a.wrapping_add(b)`. `sum_u8(ptr, len)` unsafely walks
`len` bytes and returns their wrapping `u32` sum. Together these exercise i64
arithmetic and linear-memory loads without WASI or host imports.
