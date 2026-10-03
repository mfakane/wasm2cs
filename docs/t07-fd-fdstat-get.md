# T07: wasi_snapshot_preview1.fd_fdstat_get

Measured on 2026-10-03 (JST), after the second T02 pass (`docs/t02-gap-report-2.md`). The import wired here is `wasi_snapshot_preview1.fd_fdstat_get` `(i32 fd, i32 buf)->i32`. It was the only import that stopped `samples/WasiPreview1/wasi_printf.wasm`. The host stays test code in `tests/Wasm2Cs.DotnetHost` and is not shipped.

## What fd_fdstat_get does

`HostEnvironment.FdFdstatGet(memory, fd, buf)` writes a 24-byte Preview1 `fdstat` at `buf`, little-endian: `u8 filetype` at 0, `u16 flags` at 2, `u64 rights_base` at 8, `u64 rights_inheriting` at 16. Padding is zero.

| fd | filetype | flags | rights_base | rights_inheriting |
|---|---|---|---|---|
| 1, 2 (Stdout / Stderr callbacks) | 2 `character_device` | 0 | `fd_write` (bit 6) | 0 |
| `OpenFile` virtual file, writable | 4 `regular_file` | 0 | `fd_write` | 0 |
| `OpenFile` virtual file, read-only | 4 `regular_file` | 0 | 0 | 0 |
| anything else, including 0 | — | — | — | — (errno 8 `EBADF`, memory unchanged) |

A `buf` whose 24 bytes do not fit in memory returns errno 21 (`EFAULT`) without writing. These records are a design choice. Rights list only what this host implements: `fd_read` and `fd_seek` are not imports here, so they are not advertised. The records do not copy a reference runtime, because the reference depends on where the process's stdout goes (below).

`fd_seek` and `fd_close` are still not implemented. `ExecutionChecks.WasiPrintf` passes delegates for them that throw `UnsupportedImportException`, and checks that they were not called.

## Fixture and references

`samples/WasiPreview1/wasi_printf.wasm` (SHA-256 `8f4874ed…6e2d8`). wasi-libc calls `fd_fdstat_get(1, …)` once before the first write, to choose stdout buffering.

Node.js v22.17.0 `node:wasi` filled that record differently depending on the real stdout. Bytes 0–23:

| Node stdout | record |
|---|---|
| `/dev/null` | `02 00 01 80 00 00 00 00 ff ff ff 3f 00 00 00 00 ff ff ff 3f 00 00 00 00` |
| pipe | `06 00 01 00 00 00 00 00 4a 00 20 38 00 00 00 00 ff ff ff 3f 00 00 00 00` |
| regular file | `04 00 01 80 00 00 00 00 ff 01 e0 08 00 00 00 00 00 00 00 00 00 00 00 00` |

The program's output did not depend on the record on this input. Node's stdout bytes were identical for the pipe and the regular file (the `/dev/null` run cannot be read back), and the C# host wrote the same bytes with its own record. One line goes out in one `fd_write` whichever buffering mode is chosen.

| Runtime | calls | stdout | stderr | exit |
|---|---|---|---|---|
| wasmtime 28.0.1 | (not traced) | `count=4 total=1028 mean=257.000 hex=0x404 name=wasi\n` | `warn:  12.3%\n` | 0 |
| Node v22.17.0 preview1 | `fd_fdstat_get(1)`, `fd_write(1)`, `fd_write(2)` | same | same | `start()` 0 |
| `ExecutionChecks.WasiPrintf` | same three, same order | same | same | `_start` returns, `proc_exit` not called |

`HostChecks.VerifyFdstatGet` covers fd 1 and fd 2, read-only and writable virtual files, `EBADF` on fd 0 and an unknown fd (memory unchanged), `EFAULT` for a partial or out-of-range buffer, and a record that ends exactly at the end of memory.

```sh
dotnet run --project tests/Wasm2Cs.Tests
```

## Still open

`fd_seek`, `fd_close`, and every other Preview1 import, a start-section import on owned memory, and a shipped WASI host. Reference behavior for a stdout that reports `fd_seek` rights (wasi-libc's non-tty path) was not compared on a program whose output depends on buffering.
