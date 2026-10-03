# T07: wasi_snapshot_preview1.fd_write

Measured on 2026-10-03 (JST), after T06 (`docs/t06-start-memory.md`). The one import wired here is `wasi_snapshot_preview1.fd_write` `(i32,i32,i32,i32)->i32`. `proc_exit` is not implemented and is not given a success stub.

## What fd_write does

`HostEnvironment.FdWrite` reads `iovsLength` little-endian `{buf:u32, buf_len:u32}` records from the `WasmMemory` the caller already holds, copies those bytes, and writes them with the existing `Write` path.

- fd 1 is the `Stdout` callback. fd 2 is `Stderr`. Both are explicit constructor arguments (the default callbacks discard bytes).
- Any other fd is a virtual file from `OpenFile`. An unknown fd returns errno 8 (`EBADF`). A read-only file returns errno 68 (`EROFS`). The file bytes are not changed.
- On success, the byte count is stored at `nwritten` and the return value is 0. `nwritten` is left unchanged when the return value is not 0.
- An iovec array, a non-zero payload, or `nwritten` outside the guest memory returns errno 21 (`EFAULT`) and does not write. `iovsLength` 0 does not read the iovec pointer. A zero-length payload does not read guest memory.
- A length sum that does not fit in `uint`, or a payload that does not fit in a single host buffer, returns errno 28 (`EINVAL`) before any write.

No other Preview1 import is handled. This is not a sandbox: there is no fuel, time, or memory limit beyond what the host process already has.

## Fixture

`samples/WasiPreview1/wasi_hello.wasm` exports `_start` and owned `memory`. It has no start section. Its success path calls `fd_write` once and does not call `proc_exit`.

wasmtime 28.0.1 `wasmtime run samples/WasiPreview1/wasi_hello.wasm` wrote the ten bytes `hello wasi` plus a newline and exited 0.

`ExecutionChecks.WasiFdWrite` translates that module, passes `FdWrite` as import 0, and passes a `proc_exit` delegate that fails the test if it is called. The export `_start` is void. After `new`, it returns normally (it calls `proc_exit` only when main is non-zero) and stdout is `hello wasi\n`. That normal return is wasmtime's exit 0. `HostChecks.VerifyFdWrite` covers stdout, stderr, two iovecs, an empty write, `EBADF`, `EROFS`, a virtual-file write, and the fault cases above.

Run:

```sh
dotnet run --project tests/Wasm2Cs.Tests
```

## Packaging

`Wasm2Cs.DotnetHost` is still not in the NuGet package or the Unity tarball. WASI is not claimed as something those packages provide, so `scripts/pack.mjs` and `scripts/test-package.mjs` are unchanged. A consumer that wants this import references `src/Wasm2Cs.DotnetHost/Wasm2Cs.DotnetHost.csproj` and supplies the delegates itself. See `docs/usage.md`.

Later (2026-10-03): the project moved to `tests/Wasm2Cs.DotnetHost` and is test code only. Consumers supply their own delegates; `FdWrite` is a reference to copy.

## Still open

An import that runs from a **start section** against **owned** memory still cannot call `ReadMemory` or `FdWrite`. The generated constructor does not publish that `WasmMemory` until it returns. T06 recorded this, and T07 does not change the constructor. `wasi_hello.wasm` is not blocked: `fd_write` runs from the exported `_start` after `new`.
