# WASI Preview1 fixture

`wasi_hello.wasm` is compiled from `hello.c` and `exit_code.wasm` from
`exit_code.c`; neither is hand-assembled.
Regenerate with `scripts/create-wasi-fixture.sh` (Linux/macOS) or
`scripts/create-wasi-fixture.ps1` (Windows). Set `WASI_SDK_PATH` (or `-WasiSdk`)
to a wasi-sdk-24.0 install if it is not under `/workspace/tools` or `/opt`.

Toolchain: wasi-sdk-24.0 (Clang 18.1.2-wasi-sdk), target `wasm32-wasi`.
Binary SHA-256: `wasi_hello.wasm`
`6f0c02dbb07a7c565c5af3c13803bd3acfaa4e80528c86833148f840aa3ddea7`,
`exit_code.wasm`
`f215d3e6f2af88e539479454f42a5333bcffc0e5d6657beb2aa3b194f57eed14`.

The script uses `-O1 -g0 -Wl,--strip-all` and writes each binary next to its
source. The generated binaries are committed so normal tests do not depend on a
local wasi-sdk.

`hello.c` writes `hello wasi\n` to `STDOUT_FILENO` via `write` and returns `0`
on a full write, otherwise `1`. `exit_code.c` writes `exit 3\n` to
`STDERR_FILENO` and returns `3`, so the wasi-libc `_start` calls `proc_exit(3)`.
Both are Preview1 guests (imports `fd_write` / `proc_exit` through the
wasi-libc startup path). Do not treat
`samples/SelfHosting/HostAbi.wat` as WASI coverage.
