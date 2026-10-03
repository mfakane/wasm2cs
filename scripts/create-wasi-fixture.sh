#!/usr/bin/env bash
# Rebuild samples/WasiPreview1/wasi_hello.wasm and exit_code.wasm with wasi-sdk-24.0
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
sample="$repo/samples/WasiPreview1"

# Prefer WASI_SDK_PATH, then common locations under /workspace or /opt.
sdk="${WASI_SDK_PATH:-}"
if [[ -z "$sdk" ]]; then
  for candidate in \
    /workspace/tools/wasi-sdk-24.0 \
    /workspace/tools/wasi-sdk-24.0-x86_64-linux \
    /opt/wasi-sdk-24.0 \
    /opt/wasi-sdk
  do
    if [[ -x "$candidate/bin/clang" ]]; then
      sdk="$candidate"
      break
    fi
  done
fi
if [[ -z "$sdk" || ! -x "$sdk/bin/clang" ]]; then
  echo "wasi-sdk-24.0 clang not found. Set WASI_SDK_PATH or install under /workspace/tools or /opt." >&2
  exit 1
fi

clang="$sdk/bin/clang"
"$clang" --version
for pair in hello.c:wasi_hello.wasm exit_code.c:exit_code.wasm; do
  "$clang" --target=wasm32-wasi -O1 -g0 -Wl,--strip-all \
    "$sample/${pair%%:*}" \
    -o "$sample/${pair##*:}"
  sha256sum "$sample/${pair##*:}"
done
