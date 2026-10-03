# Rebuild samples/CWorkload/{Workload,WorkloadFeatures}.wasm and samples/CSimd/Simd.wasm with wasi-sdk-24.0
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
        if (Test-Path (Join-Path $c "bin\clang.exe")) { $WasiSdk = $c; break }
    }
}
if (-not $WasiSdk) { throw "wasi-sdk-24.0 not found. Pass -WasiSdk or set WASI_SDK_PATH." }

$clang = Join-Path $WasiSdk "bin\clang.exe"
& $clang --version
# Library-style modules: wasi-libc supplies memcpy/memset, no _start, no WASI imports.
$common = @("--target=wasm32-wasi", "-g0", "-nostartfiles", "-Wl,--no-entry", "-Wl,--export-memory", "-Wl,--strip-all")
$workloadExports = @("buffer_ptr", "fnv64", "copy_fill", "signed_bytes", "stats", "interpret", "sort_bytes", "fill") | ForEach-Object { "-Wl,--export=$_" }
$simdExports = @("bytes_ptr", "init", "saxpy", "dot", "byte_sum", "byte_max", "clamp_scale") | ForEach-Object { "-Wl,--export=$_" }

$workload = Join-Path $repo "samples/CWorkload"
$simd = Join-Path $repo "samples/CSimd"
$builds = @(
    @{ Flags = @("-O2"); Exports = $workloadExports; Source = (Join-Path $workload "workload.c"); Output = (Join-Path $workload "Workload.wasm") },
    @{ Flags = @("-O2", "-mbulk-memory", "-msign-ext", "-mnontrapping-fptoint", "-mmultivalue", "-mreference-types"); Exports = $workloadExports; Source = (Join-Path $workload "workload.c"); Output = (Join-Path $workload "WorkloadFeatures.wasm") },
    @{ Flags = @("-O3", "-msimd128"); Exports = $simdExports; Source = (Join-Path $simd "simd.c"); Output = (Join-Path $simd "Simd.wasm") }
)
foreach ($build in $builds) {
    & $clang @common @($build.Flags) @($build.Exports) $build.Source -o $build.Output
    if ($LASTEXITCODE -ne 0) { throw "Clang workload fixture compilation failed" }
    Get-FileHash $build.Output -Algorithm SHA256
}
