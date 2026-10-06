# Clang++ workload fixture

`CppWorkload.wasm` is compiled from `workload.cpp`; it is not hand-assembled.
Regenerate with `scripts/create-cpp-fixture.sh` (Linux/macOS) or
`scripts/create-cpp-fixture.ps1`. Set `WASI_SDK_PATH` (or `-WasiSdk`) to a
wasi-sdk-24.0 install. On 2026-10-06 both scripts rebuilt the binary
byte-for-byte on Linux (the `.ps1` under PowerShell 7.6 on Linux, not on Windows),
and the `.sh` script also did so from a copy of the sources in another directory.

Toolchain: wasi-sdk-24.0 (`clang version 18.1.2-wasi-sdk`, libc++/libc++abi and
wasi-libc from the same SDK), target `wasm32-wasi`, built as a reactor library:

```text
clang++ --target=wasm32-wasi -mexec-model=reactor -O2 -g0 -std=c++17 -fno-exceptions -fno-rtti
        -Wall -Wextra -Werror -Wl,--export-memory -Wl,--strip-all workload.cpp -o CppWorkload.wasm
```

| Binary | Bytes | SHA-256 |
|---|---:|---|
| `CppWorkload.wasm` | 26364 | `e8add49c18f985a3ddc2a6dcc160ea6c7c766aa03f98f867cdd74fdbacfa3807` |

The module has no imports and no start section. Clang 18 default CPU features
are used; no instruction or function was removed after linking. The two
`memory.copy` / one `memory.fill` instructions come from wasi-libc's prebuilt
`memcpy` / `memset` (as in `samples/CWorkload`). `workload.cpp` replaces
`operator new` / `operator delete` (malloc, then trap on failure) and
`__cxa_pure_virtual` (trap). Without those replacements libc++abi's
`abort_message` pulls in stdio and three WASI imports (`fd_close`, `fd_seek`,
`fd_write`) that this library never calls.

C++ features exercised: a dynamically initialized global (`volatile` seed, so
the constructor runs from `_initialize` → `__wasm_call_ctors`), virtual dispatch
through a vtable (`call_indirect`), `std::make_unique` and arrays of
`std::unique_ptr` (heap growth via dlmalloc → `memory.grow`), `std::sort` (the
`int` specialization is the prebuilt one in `libc++.a`), `std::stable_sort` with a
lambda (temporary buffer through `operator new(nothrow)`), `std::reverse`,
`std::partial_sum`, `std::accumulate`, and a templated 4×4 `uint64_t` matrix
product. Exports (`i32` arguments; `prefix` and `shapes` return `i64`):
`_initialize`, `buffer_ptr`, `pages`, `registry_next`, `live`, `fill`,
`sort_values`, `prefix`, `shapes`, `matrix`, `checked_at`, `divide`, `memory`.

`calls.json` is the call sequence used by `ExecutionChecks.CppWorkload`. It calls
`registry_next` once before `_initialize` (the constructor has not run, so it
returns 1) and then after it (1005, 1006, …). The test replays the sequence on one
instance in the generated C# and in Node.js (`tests/Wasm2Cs.Tests/sequence-oracle.mjs`)
and compares every return value or trap and the SHA-256 of the final memory. It
also checks the C# trap kinds: `checked_at` out of range → `Unreachable`,
`divide(INT_MIN, -1)` → `IntegerOverflow`, `divide(1, 0)` → `DivisionByZero`,
the same classes wasmtime 28.0.1 reports. Memory grows from 2 to 93 pages.

See [the T02 round 5 report](../../docs/t02-gap-report-5.md). This fixture does
not cover C++ exceptions, RTTI, iostreams, or threads.
