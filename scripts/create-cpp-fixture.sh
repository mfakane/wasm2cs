#!/usr/bin/env bash
# Rebuild samples/CppWorkload/CppWorkload.wasm with wasi-sdk-24.0 clang++ (T02 C++ input).
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"

# Prefer WASI_SDK_PATH, then common locations under /workspace or /opt.
sdk="${WASI_SDK_PATH:-}"
if [[ -z "$sdk" ]]; then
  for candidate in \
    /workspace/tools/wasi-sdk-24.0 \
    /workspace/tools/wasi-sdk-24.0-x86_64-linux \
    /opt/wasi-sdk-24.0 \
    /opt/wasi-sdk
  do
    if [[ -x "$candidate/bin/clang++" ]]; then
      sdk="$candidate"
      break
    fi
  done
fi
if [[ -z "$sdk" || ! -x "$sdk/bin/clang++" ]]; then
  echo "wasi-sdk-24.0 clang++ not found. Set WASI_SDK_PATH or install under /workspace/tools or /opt." >&2
  exit 1
fi

clangxx="$sdk/bin/clang++"
"$clangxx" --version
# Reactor library: crt1-reactor.o provides _initialize (static constructors).
# wasi-libc supplies malloc/memcpy/memset, libc++ supplies std::sort; the module has no imports.
dir="$repo/samples/CppWorkload"
"$clangxx" --target=wasm32-wasi -mexec-model=reactor -O2 -g0 -std=c++17 -fno-exceptions -fno-rtti \
  -Wall -Wextra -Werror -Wl,--export-memory -Wl,--strip-all \
  "$dir/workload.cpp" -o "$dir/CppWorkload.wasm"
sha256sum "$dir/CppWorkload.wasm"
