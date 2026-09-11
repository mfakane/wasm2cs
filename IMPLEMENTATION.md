# Implementation milestones

Baseline: `8818947`. Work branch: `feat/unity-wasm-pipeline`.
Each milestone is committed separately after its checks pass. No registry publishing.

1. Complete (`fd9e980`): separate decoding, module IR, validation, and C# emission.
2. Complete (`a7aad72`): portable netstandard2.0 generator (Roslyn 4.3.0) and instance API.
3. Complete: Unity input bridge, asmdef isolation, create/move/change/delete/restart
   checks, Editor and Windows x64 IL2CPP smoke (Unity 6000.6.0f1).
4. Complete: i32 numeric operations and traps; 3,073 additional differential outcomes.
5. Complete: structured branches, result transfer, branch tables, unreachable
   validation, and 200 differential GCD loop executions.
6. Complete: direct/private/void calls, factorial and mutual recursion, exported wrappers.
7. Complete: memory32 loads/stores/growth, globals, active data and start;
   differential memory/global snapshots and isolated-instance checks.
8. Complete: Clang-produced array/CRC32 fixtures, 35 differential vectors plus
   full memory snapshots; Unity Editor and Windows x64 IL2CPP verified.
9. Complete: typed host function imports, imported start/export, memory exchange,
   reentry and unchanged host exceptions; Unity Editor and Windows x64 IL2CPP verified.
10. Complete: pinned official WebAssembly 1.0 i32 fixtures (350 returns, 9 typed
    traps, 54 invalid modules, 29 explicit skips), plus MSBuild regeneration tests.
11. NuGet and UPM distribution with clean-consumer tests.

## Verification

Every milestone: solution build, the console test executable, Smoke, and
`node scripts/test-build.mjs`. Unity Editor and Windows x64 IL2CPP execution
are required for milestones 3, 8, 9, and 11. A missing tool or unavailable
runtime is not a passing check. Preserve fixed-seed Node.js differential tests.

## Scope

Unity 6.0 and Windows x64 IL2CPP; C# 9 or earlier output using .NET Standard 2.0
APIs. Generation is ahead of time, with no runtime interpreter, reflection, or
dynamic compilation. All generated modules become instances. Initial WASM
scope is i32, direct calls, one memory32 (256 MiB implementation limit), owned
globals, active data, start, and typed function imports. WASI, other value types,
tables/indirect calls, imported memory/globals, SIMD, threads, GC, and UdonSharp
are deferred.

## Environment

The local SDK is .NET 10.0.400 but inherited DOTNET_ROOT points to an older SDK.
Build checks must set DOTNET_ROOT and DOTNET_ROOT_X64 to the root reported by
the selected SDK, without changing the user's global configuration.

Windows has Unity 6000.6.0f1 at `C:/Program Files/Unity/Hub/Editor/6000.6.0f1`.
The Windows IL2CPP support module was added with the official CLI for validation.
Unity 6.0 compatibility is a target; 6000.6.0f1 is the actual tested editor.
The WebGL module also supplies Clang and wasm-ld under
`Editor/Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten/llvm`.
