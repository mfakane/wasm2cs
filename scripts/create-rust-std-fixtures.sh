#!/usr/bin/env bash
# Build the same std workload with two pinned Rust toolchains; keep their defaults.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
sample="$repo/samples/RustStd"
target_root="${RUST_STD_TARGET_DIR:-$sample/target}"
for pair in 1.85.0:RustStd185 1.90.0:RustStd190; do
  version="${pair%%:*}"
  name="${pair#*:}"
  rustup run "$version" rustc --version
  rustup run "$version" cargo --version
  CARGO_TARGET_DIR="$target_root/$version" rustup run "$version" cargo rustc \
    --manifest-path "$sample/Cargo.toml" --locked --offline \
    --target wasm32-unknown-unknown --release --lib -- \
    --remap-path-prefix="$repo=/wasm2cs" \
    -C link-arg=--no-entry -C link-arg=--export-memory
  cp "$target_root/$version/wasm32-unknown-unknown/release/rust_std_fixture.wasm" \
    "$sample/$name.wasm"
done
sha256sum "$sample"/RustStd*.wasm
