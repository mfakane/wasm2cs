# T06: host memory during start

Measured on 2026-10-03 (JST), after T02 (`docs/t02-gap-report.md`). No Core opcode group was missing, so T03–T05 were not started. This task checks whether an import can see the module memory while the start function runs.

## Modules

Both modules are built in `ExecutionChecks.MemoryDuringStart`. Each has an active data segment `11 22 33 44` at offset 8 and a start function that executes `i32.load8_u` at that address, then calls `env.peek(loaded, 4)`.

- Imported memory: the host passes a `WasmMemory` into the constructor.
- Owned memory: the module allocates the memory. The constructor takes only `env.peek`.

Run:

```sh
dotnet run --project tests/Wasm2Cs.Tests
```

The relevant line is `PASS: host reads imported memory during start; owned memory is initialized but unpublished until the constructor returns.`

## What holds

Imported memory: during start, `peek` read `11 22 33 44` from the same `WasmMemory` the host passed in. The exported `memory` property is that object. Active data is copied before start. No API change was required.

Owned memory: start itself can read the segment (`peek` receives `0x11`). After the constructor returns, `ReadMemory(8, 4)` is `11 22 33 44`. During start, the instance variable the caller is about to assign is still null. The generated constructor does not publish the owned `WasmMemory` to the import, so the host cannot call `ReadMemory` or use the memory export until construction finishes.

## API decision

No generated-constructor or `Wasm2Cs.DotnetHost` change. `HostEnvironment.ReadIovec` / `Write` already take a `WasmMemory` the caller already holds. They are not a WASI import binding.

Publishing an owned `WasmMemory` before start would mean storing a caller-supplied object on every generated constructor. That is larger than this check, and `samples/WasiPreview1/wasi_hello.wasm` does not need it: it has no start section. Its `fd_write` runs from the exported `_start` after `new` returns, when `memory` is already public (see the T02 consumer).

## Still open for start on owned memory

T07 connects `fd_write` only for a call that already has the `WasmMemory` (`docs/t07-fd-write.md`). A Preview1 import that runs from a **start section** against **owned** memory still cannot see that memory. A Preview1 import invoked from an export after construction, which is the `wasi_hello.wasm` shape, is not blocked by this result. `HostEnvironment` is not a finished WASI adapter.
