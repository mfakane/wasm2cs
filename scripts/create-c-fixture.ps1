param([string]$Tools = "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Data\PlaybackEngines\WebGLSupport\BuildTools\Emscripten\llvm")
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
& (Join-Path $Tools "clang.exe") --version
& (Join-Path $Tools "wasm-ld.exe") --version
& (Join-Path $Tools "clang.exe") --target=wasm32-unknown-unknown -mcpu=mvp -O1 -nostdlib -fno-builtin -fno-vectorize -fno-slp-vectorize `
    "-Wl,--no-entry" "-Wl,--export=sum_bytes" "-Wl,--export=crc32" "-Wl,--export=buffer_ptr" "-Wl,--export=f" "-Wl,--export-memory" `
    "-Wl,--initial-memory=131072" "-Wl,--max-memory=262144" `
    (Join-Path $repo "samples/CAlgorithms/algorithms.c") -o (Join-Path $repo "samples/CAlgorithms/Algorithms.wasm")
if ($LASTEXITCODE -ne 0) { throw "C fixture compilation failed" }
Get-FileHash (Join-Path $repo "samples/CAlgorithms/Algorithms.wasm") -Algorithm SHA256
