# WASI Preview1 fixture

`wasi_hello.wasm` is compiled from `hello.c`; it is not hand-assembled.
Regenerate with `scripts/create-wasi-fixture.sh` (Linux/macOS) or
`scripts/create-wasi-fixture.ps1` (Windows). Set `WASI_SDK_PATH` (or `-WasiSdk`)
to a wasi-sdk-24.0 install if it is not under `/workspace/tools` or `/opt`.

Toolchain: wasi-sdk-24.0 (Clang 18.1.2-wasi-sdk), target `wasm32-wasi`.
Binary SHA-256: `6f0c02dbb07a7c565c5af3c13803bd3acfaa4e80528c86833148f840aa3ddea7`.

The script uses `-O1 -g0 -Wl,--strip-all` and writes the binary next to the
source. The generated binary is committed so normal tests do not depend on a
local wasi-sdk.

`hello.c` writes `hello wasi\n` to `STDOUT_FILENO` via `write` and returns `0`
on a full write, otherwise `1`. This is a Preview1 guest (imports such as
`fd_write` / `proc_exit` through the wasi-libc startup path). Do not treat
`samples/SelfHosting/HostAbi.wat` as WASI coverage.
