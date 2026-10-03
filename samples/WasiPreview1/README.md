# WASI Preview1 fixture

`wasi_hello.wasm` is compiled from `hello.c`, `exit_code.wasm` from
`exit_code.c`, and `wasi_printf.wasm` from `printf.c`; none is hand-assembled.
Regenerate with `scripts/create-wasi-fixture.sh` (Linux/macOS) or
`scripts/create-wasi-fixture.ps1` (Windows). Set `WASI_SDK_PATH` (or `-WasiSdk`)
to a wasi-sdk-24.0 install if it is not under `/workspace/tools` or `/opt`.

Toolchain: wasi-sdk-24.0 (Clang 18.1.2-wasi-sdk), target `wasm32-wasi`.
Binary SHA-256: `wasi_hello.wasm`
`6f0c02dbb07a7c565c5af3c13803bd3acfaa4e80528c86833148f840aa3ddea7`,
`exit_code.wasm`
`f215d3e6f2af88e539479454f42a5333bcffc0e5d6657beb2aa3b194f57eed14`,
`wasi_printf.wasm`
`8f4874ed3d04750c5520adb46960c6ecf11610a56e2e70e37a54c30e7600e2d8`.

The script uses `-O1 -g0 -Wl,--strip-all` and writes each binary next to its
source. The generated binaries are committed so normal tests do not depend on a
local wasi-sdk.

`hello.c` writes `hello wasi\n` to `STDOUT_FILENO` via `write` and returns `0`
on a full write, otherwise `1`. `exit_code.c` writes `exit 3\n` to
`STDERR_FILENO` and returns `3`, so the wasi-libc `_start` calls `proc_exit(3)`.
`printf.c` formats integers, a float, hex, and a string with `printf` to stdout
and `fprintf` to stderr, and returns `0`. It imports `fd_close`,
`fd_fdstat_get`, `fd_seek`, `fd_write`, and `proc_exit` through wasi-libc stdio.
All three are Preview1 guests. Do not treat
`samples/SelfHosting/HostAbi.wat` as WASI coverage.
