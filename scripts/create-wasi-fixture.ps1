# Rebuild samples/WasiPreview1/wasi_hello.wasm with wasi-sdk-24.0
param(
    [string]$WasiSdk = $env:WASI_SDK_PATH
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$sample = Join-Path $repo "samples/WasiPreview1"

if (-not $WasiSdk) {
    $candidates = @(
        "C:\wasi-sdk-24.0",
        "C:\wasi-sdk",
        "$env:USERPROFILE\wasi-sdk-24.0",
        "$env:USERPROFILE\tools\wasi-sdk-24.0"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "bin\clang.exe")) { $WasiSdk = $c; break }
    }
}
if (-not $WasiSdk) { throw "wasi-sdk-24.0 not found. Pass -WasiSdk or set WASI_SDK_PATH." }

$clang = Join-Path $WasiSdk "bin\clang.exe"
& $clang --version
& $clang --target=wasm32-wasi -O1 -g0 "-Wl,--strip-all" `
    (Join-Path $sample "hello.c") `
    -o (Join-Path $sample "wasi_hello.wasm")
if ($LASTEXITCODE -ne 0) { throw "WASI fixture compilation failed" }
Get-FileHash (Join-Path $sample "wasi_hello.wasm") -Algorithm SHA256
