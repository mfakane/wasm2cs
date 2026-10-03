#!/usr/bin/env bash
# Rebuild samples/CWorkload/{Workload,WorkloadFeatures}.wasm and samples/CSimd/Simd.wasm with wasi-sdk-24.0
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
# Library-style modules: wasi-libc supplies memcpy/memset, no _start, no WASI imports.
common=(--target=wasm32-wasi -g0 -nostartfiles -Wl,--no-entry -Wl,--export-memory -Wl,--strip-all)
workload_exports=()
for name in buffer_ptr fnv64 copy_fill signed_bytes stats interpret sort_bytes fill; do
  workload_exports+=("-Wl,--export=$name")
done
simd_exports=()
for name in bytes_ptr init saxpy dot byte_sum byte_max clamp_scale; do
  simd_exports+=("-Wl,--export=$name")
done

workload="$repo/samples/CWorkload"
"$clang" "${common[@]}" -O2 "${workload_exports[@]}" \
  "$workload/workload.c" -o "$workload/Workload.wasm"
"$clang" "${common[@]}" -O2 -mbulk-memory -msign-ext -mnontrapping-fptoint -mmultivalue -mreference-types \
  "${workload_exports[@]}" "$workload/workload.c" -o "$workload/WorkloadFeatures.wasm"

simd="$repo/samples/CSimd"
"$clang" "${common[@]}" -O3 -msimd128 "${simd_exports[@]}" \
  "$simd/simd.c" -o "$simd/Simd.wasm"

sha256sum "$workload/Workload.wasm" "$workload/WorkloadFeatures.wasm" "$simd/Simd.wasm"
