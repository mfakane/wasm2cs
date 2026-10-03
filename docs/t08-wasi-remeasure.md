# T08: WASI fixture remeasurement

Measured on 2026-10-03 (JST), on master `f18b3ab` (merge of T07, PR #5). Only `samples/WasiPreview1/wasi_hello.wasm` was compared again. SHA-256 `6f0c02dbb07a7c565c5af3c13803bd3acfaa4e80528c86833148f840aa3ddea7`. Core and SIMD inputs were not re-run. T03–T05 stay unstarted. No codegen profile was added.

## Commands

```sh
wasmtime run samples/WasiPreview1/wasi_hello.wasm   # wasmtime 28.0.1
# stdout bytes 68 65 6c 6c 6f 20 77 61 73 69 0a, process exit 0

node --experimental-wasi-unstable-preview1
# node:wasi preview1, returnOnExit: true, Node.js v22.19.0
# fd_write(1, 66584, 1, 66580) once, errno 0, proc_exit not called, start code 0
# stdout bytes 68 65 6c 6c 6f 20 77 61 73 69 0a

dotnet run --project tests/Wasm2Cs.Tests
# ExecutionChecks.WasiFdWrite: same stdout bytes, _start returns normally, proc_exit not called
```

`_start` is a void export. A normal return is the wasmtime/Node exit code 0. Non-zero main would call `proc_exit`, which this host does not implement.

## Before and after

| | T02 (before a product host) | This measurement |
|---|---|---|
| CLI translation | succeeds; constructor needs both delegates | unchanged |
| Reference stdout / exit | wasmtime 28.0.1 and Node preview1: `hello wasi\n`, exit 0 | same bytes and exit 0, re-run above |
| `fd_write` | caller-supplied; a temporary reader matched the bytes | `HostEnvironment.FdWrite` matches those bytes. Errno 0, one write of 11 bytes |
| `proc_exit` | present, not on the success path | still not called, still not implemented, not a success stub |
| Other Preview1 imports | none in this module | still none; not implemented |
| Owned memory during start | not required by this module (no start section) | still unpublished until the constructor returns. Not fixed |
| Package | `Wasm2Cs.DotnetHost` not in NuGet or Unity | unchanged. Packaging scripts were not modified, so the package consumer was not re-tested for this host |
| Unity / IL2CPP | not a WASI claim | not verified. The host is not in the Unity tarball |

## Host settings this scope needs

Reference `src/Wasm2Cs.DotnetHost/Wasm2Cs.DotnetHost.csproj` (on 2026-10-03, later moved to `tests/Wasm2Cs.DotnetHost` as test-only code). Pass an explicit stdout callback (fd 1) and stderr callback (fd 2). Other fds are `OpenFile` virtual files, not the real filesystem. Call `FdWrite` with the exported `memory` only after `new`. Do not treat this as a sandbox.

`docs/supported-features.md` and `docs/usage.md` describe this measured slice and nothing wider.
