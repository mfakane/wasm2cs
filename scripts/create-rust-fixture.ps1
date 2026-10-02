# Rebuild samples/RustWasm32UnknownUnknown/rust_wasm32_fixture.wasm
param()
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$sample = Join-Path $repo "samples/RustWasm32UnknownUnknown"
Push-Location $sample
try {
    rustc --version
    cargo --version
    cargo rustc --target wasm32-unknown-unknown --release --lib -- `
        -C opt-level=z `
        -C panic=abort `
        -C link-arg=--no-entry `
        -C link-arg=--export-memory `
        -C link-arg=--export=add_i64 `
        -C link-arg=--export=sum_u8
    if ($LASTEXITCODE -ne 0) { throw "Rust fixture compilation failed" }
    $artifact = Join-Path $sample "target/wasm32-unknown-unknown/release/rust_wasm32_fixture.wasm"
    $out = Join-Path $sample "rust_wasm32_fixture.wasm"
    Copy-Item -Force $artifact $out
    Get-FileHash $out -Algorithm SHA256
}
finally {
    Pop-Location
}
