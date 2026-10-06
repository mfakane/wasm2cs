# Rebuild samples/CppWorkload/CppWorkload.wasm with wasi-sdk-24.0 clang++ (T02 C++ input)
param(
    [string]$WasiSdk = $env:WASI_SDK_PATH
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

if (-not $WasiSdk) {
    $candidates = @(
        "C:\wasi-sdk-24.0",
        "C:\wasi-sdk",
        "$env:USERPROFILE\wasi-sdk-24.0",
        "$env:USERPROFILE\tools\wasi-sdk-24.0"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "bin\clang++.exe")) { $WasiSdk = $c; break }
    }
}
if (-not $WasiSdk) { throw "wasi-sdk-24.0 not found. Pass -WasiSdk or set WASI_SDK_PATH." }

$clangxx = Join-Path (Join-Path $WasiSdk "bin") $(if ($IsLinux -or $IsMacOS) { "clang++" } else { "clang++.exe" })
& $clangxx --version
# Reactor library: crt1-reactor.o provides _initialize (static constructors).
# wasi-libc supplies malloc/memcpy/memset, libc++ supplies std::sort; the module has no imports.
$dir = Join-Path $repo "samples/CppWorkload"
$flags = @("--target=wasm32-wasi", "-mexec-model=reactor", "-O2", "-g0", "-std=c++17", "-fno-exceptions", "-fno-rtti",
    "-Wall", "-Wextra", "-Werror", "-Wl,--export-memory", "-Wl,--strip-all")
& $clangxx @flags (Join-Path $dir "workload.cpp") -o (Join-Path $dir "CppWorkload.wasm")
if ($LASTEXITCODE -ne 0) { throw "C++ fixture compilation failed" }
Get-FileHash (Join-Path $dir "CppWorkload.wasm") -Algorithm SHA256
