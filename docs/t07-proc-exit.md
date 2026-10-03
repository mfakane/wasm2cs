# T07: wasi_snapshot_preview1.proc_exit

Measured on 2026-10-03 (JST), after the `fd_write` iteration (`docs/t07-fd-write.md`). The import wired here is `wasi_snapshot_preview1.proc_exit` `(i32)->()`. It is required by a new fixture whose `main` returns non-zero. `wasi_hello.wasm` never reaches it. The host stays test code in `tests/Wasm2Cs.DotnetHost` and is not shipped.

## What proc_exit does

`HostEnvironment.ProcExit(code)` calls the existing `Exit(code)`, which records `HasExited` and `ExitCode`, then throws `WasiProcExitException` carrying the same code.

- Preview1 `proc_exit` does not return. In the wasi-libc output, `call proc_exit` is followed by `unreachable`. A delegate that returns would trap `Unreachable`, so the host throws instead. Host exceptions pass through WASM frames unchanged, so the caller catches `WasiProcExitException` around the export it invoked (`_start`).
- The i32 is stored as given: no clamping, masking, or mapping to a process exit status. `HostChecks.VerifyProcExit` covers 0, 3, -1, and `int.MaxValue`.
- A second `ProcExit` on the same host throws `InvalidOperationException` (the existing double-exit guard) and leaves the first code.

The host does not terminate the .NET process. The caller decides what an exit code means.

## Fixture

`samples/WasiPreview1/exit_code.c`, built with wasi-sdk-24.0 (Clang 18.1.2-wasi-sdk), `--target=wasm32-wasi -O1 -g0 -Wl,--strip-all`, by `scripts/create-wasi-fixture.sh`. SHA-256 `f215d3e6f2af88e539479454f42a5333bcffc0e5d6657beb2aa3b194f57eed14`. The same script rebuilt `wasi_hello.wasm` byte-for-byte (`6f0c02db…`). `scripts/create-wasi-fixture.ps1` was updated the same way but not run.

`main` writes `exit 3\n` to fd 2 and returns 3. `wasm-objdump`: imports are only `fd_write` and `proc_exit`, exports `memory` and `_start`, no start section. CLI translation succeeds.

## Reference results

| Runtime | stdout | stderr | exit | calls |
|---|---|---|---|---|
| wasmtime 28.0.1 `wasmtime run` | empty | `65 78 69 74 20 33 0a` | 3 | — |
| Node.js v22.17.0 `node:wasi` preview1, `returnOnExit: true` | empty | same bytes | `start()` returned 3 | `fd_write(2, 66568, 1, 66564) -> 0`, then `proc_exit(3)` |
| `ExecutionChecks.WasiProcExit` | empty | same bytes | `WasiProcExitException.ExitCode` 3, `host.ExitCode` 3 | — |

T02 and T08 used Node.js 22.19.0. This run used the dev shell's 22.17.0. Reference behavior for exit codes outside the fixture (for example 126 and above, or negative codes) was not measured.

```sh
dotnet run --project tests/Wasm2Cs.Tests
```

## Before and after

| | Before (T08) | Now |
|---|---|---|
| `proc_exit` | not called by `wasi_hello.wasm`, not implemented | implemented in the test host; `exit_code.wasm` matches wasmtime and Node |
| `fd_write` to fd 2 from a real module | unit test only | `exit_code.wasm` writes stderr through `FdWrite` |
| Other Preview1 imports | not implemented | unchanged |
| Owned memory during start | unpublished until the constructor returns | unchanged |
| Package | host not shipped | unchanged; host is test code |
| Unity / IL2CPP | not verified | not verified |

A full T08 re-measure of every input is left for after the new inputs from the next T01/T02 pass.

## Still open

Any other Preview1 import, a start-section import on owned memory, and a shipped WASI host.
