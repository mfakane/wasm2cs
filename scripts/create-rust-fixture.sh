#!/usr/bin/env bash
# Rebuild samples/RustWasm32UnknownUnknown/rust_wasm32_fixture.wasm
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
sample="$repo/samples/RustWasm32UnknownUnknown"
cd "$sample"

rustc --version
cargo --version
rustup show active-toolchain || true

cargo rustc --target wasm32-unknown-unknown --release --lib -- \
  -C opt-level=z \
  -C panic=abort \
  -C link-arg=--no-entry \
  -C link-arg=--export-memory \
  -C link-arg=--export=add_i64 \
  -C link-arg=--export=sum_u8

artifact="$sample/target/wasm32-unknown-unknown/release/rust_wasm32_fixture.wasm"
cp "$artifact" "$sample/rust_wasm32_fixture.wasm"
sha256sum "$sample/rust_wasm32_fixture.wasm"
