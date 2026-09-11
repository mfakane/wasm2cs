# wasm2cs

A proof of concept that translates WebAssembly binaries into ordinary C# methods,
then runs the same translator from a Roslyn incremental Source Generator.
The generated application does not need a WebAssembly runtime.

The translator separates binary decoding, a module/instruction representation,
validation, and C# emission. Invalid instructions and operand stacks include
function indices and byte offsets in diagnostics. See `IMPLEMENTATION.md` for
the completed Unity/IL2CPP implementation milestones.

The next goal is to execute `Wasm2Cs.dll` inside a .NET WebAssembly runtime
translated to C#, first on .NET and then on Unity IL2CPP. The Japanese
[self-hosting roadmap](docs/README.md) defines the implementation order and
acceptance criteria. This goal is planned, not yet implemented.

Requires the **.NET 10 SDK** to build the tools and tests; Node.js 22+ supplies the
independent WebAssembly oracle. The core and generator target `netstandard2.0`;
the generator uses Roslyn 4.3.0 from NuGet. Generated code targets C# 9 and .NET
Standard 2.0 APIs, verified against reference assemblies. The Unity bridge lives
under `unity/Packages/com.mfakane.wasm2cs`; local NuGet and UPM packages can be built
with `node scripts/pack.mjs` (requires `dotnet`, Node.js, and `tar` on PATH).

The Unity bridge watches `.wasm` assets and maintains Base64 `.additionalfile`
inputs in `Assets/Wasm2CsGeneratedInputs`. Generated public classes belong only to
the `Wasm2Cs.Modules` assembly. Reference that asmdef from custom consumer assemblies.
The compiler plugin is excluded from runtime platforms. Use the Editor menu
`Tools/Wasm2Cs/Regenerate Inputs` to explicitly reconcile inputs after file changes.
On Windows, `scripts/test-unity.ps1 -Editor <Unity.exe>` creates a temporary consumer,
installs the local package with Unity's package API, and builds/runs an IL2CPP smoke.
It preserves logs and the temporary project for investigation. Unity 6.0 is the
compatibility baseline; this machine's available Editor is 6000.6.0f1.

## Run the proof

```sh
dotnet build Wasm2Cs.slnx
dotnet run --project tests/Wasm2Cs.Tests --no-build
dotnet run --project samples/Smoke --no-build
node scripts/test-build.mjs
```

The 201-byte `samples/Smoke/Arithmetic.wasm` is checked in. Its readable counterpart
is `Arithmetic.wat`; regenerate the binary with `node scripts/create-fixture.mjs`.
The assembler script validates its output with Node's WebAssembly engine.

The test executable compiles translated C# with overflow checks enabled and compares
3,649 calls against Node's WebAssembly engine, including random inputs and integer
boundaries. It also checks malformed/unsupported modules, generator diagnostics,
and changes to generator inputs. `test-build.mjs` exercises actual MSBuild builds,
including a same-size binary edit with its timestamp preserved and removal of a WASM item.

The checked-in official WebAssembly 1.0 i32 subset adds 350 expected-value checks,
9 typed traps, and 54 invalid modules. 29 non-i32/WAT-text cases are explicitly
skipped. See `tests/Conformance/README.md` for pinned provenance, licensing, and
reproduction. This does not imply conformance to the entire WebAssembly specification.

The sample prints:

```text
add(20, 22) = 42
square(7) = 49
add(int.MaxValue, 1) = -2147483648
Clang CRC32(123456789) = cbf43926
```

To inspect standalone translation:

```sh
dotnet run --project src/Wasm2Cs.Cli -- samples/Smoke/Arithmetic.wasm
```

`samples/CAlgorithms/Algorithms.wasm` is a real Clang-produced freestanding C
fixture (array sums and CRC32). The tests compare outputs and memory against
Node.js. Unity consumes the same 35 Node-generated oracle vectors and checks
them in the Editor and the IL2CPP Player. Toolchain versions and reproduction
commands are documented alongside the C source.

## Add a WASM file to a project

For a packaged consumer, put the generated `.nupkg` in a local NuGet feed and add:

```xml
<ItemGroup>
  <PackageReference Include="Wasm2Cs.Generator" Version="0.1.0-preview.1" PrivateAssets="all" />
  <Wasm Include="Arithmetic.wasm" />
</ItemGroup>
```

The package automatically imports the MSBuild target and analyzer. No runtime
package dependency or manual target import is needed. Enable
`EmitCompilerGeneratedFiles` as below to also persist `.g.cs` files on disk.

For Unity, install `artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz` using Package
Manager's **Add package from tarball**. Then add `.wasm` files under `Assets` and
call `Wasm2Cs.Generated` instance methods. Custom asmdefs must reference
`Wasm2Cs.Modules`. The archive includes the generator DLL and its analyzer metadata;
the source-only UPM directory is not a ready-to-install distribution.

Build and verify packages locally:

```sh
node scripts/pack.mjs
node scripts/test-package.mjs
```

On Windows, verify the archive in a fresh Unity project with:

```powershell
./scripts/test-unity.ps1 -PackagePath ./artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz
```

Package tests use isolated consumers; Unity builds and runs a Windows x64 IL2CPP
Player. Packages and verification logs are local artifacts only: these scripts do
not publish to NuGet or a UPM registry. Package licensing has not been designated;
choose it before public redistribution.

Use the following entries in an SDK-style .NET 10 project, adjusting the repository
paths. The complete example is `samples/Smoke/Smoke.csproj`.

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="../../src/Wasm2Cs.Generator/Wasm2Cs.Generator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  <Wasm Include="Arithmetic.wasm" />
</ItemGroup>
<Import Project="../../build/Wasm2Cs.targets" />
```

After `dotnet build`, call `new Wasm2Cs.Generated.Arithmetic().add(20, 22)`.
The file is written to
`obj/generated/Wasm2Cs.Generator/Wasm2Cs.WasmGenerator/Arithmetic.g.cs`.
Keep generated files under `obj` to avoid compiling them twice.

MSBuild encodes each binary into `obj/<configuration>/<framework>/wasm2cs/*.wasm.base64`
before compilation. The generator consumes these as text `AdditionalFiles`, so
content changes are tracked without reading hidden binary dependencies inside the
generator. Unchanged inputs retain their timestamps. Run a build after editing WASM;
automatic IDE file-watching behavior has not been verified.

Each filename becomes a sealed class; each function export becomes an instance method.
Create a module with `new Arithmetic()`. This intentionally replaces the prototype's
static API so modules own independent memory and global state.
Names must be ASCII C# identifiers, with keywords escaped using `@`. An export cannot
have the same name as its class, and module filenames must be unique (ignoring case).
Translation errors fail the build with `WASM001`; duplicate class names use `WASM002`.

## Supported subset

- WASM version 1, function types with `i32` parameters and zero or one `i32` result.
- Type, function import, function, memory, global, export, start, code, and active
  data sections; custom sections are skipped.
- `local.get`, `local.set`, `local.tee`, `i32.const`, all MVP i32 arithmetic,
  comparisons, bitwise operations, shifts, rotates, and bit counts.
- `nop`, `drop`, `select`, `unreachable`, `return`, and structured `block`, `loop`,
  `if`/`else`, `br`, `br_if`, and `br_table`. Blocks have no parameters and zero
  or one i32 result. Unreachable instructions are still decoded and validated.
- Runtime traps use each generated module's nested `TrapException` and `TrapKind`.
  Signed division overflow and division/remainder by zero trap; signed remainder
  of `int.MinValue` by `-1` returns zero.
- Zero-initialized locals and wrapping 32-bit integer arithmetic. Stack values are
  materialized into temporary variables so later local assignments cannot change them.
- Direct function calls, including non-exported functions and recursion. Each WASM
  function is emitted once; exports are public wrappers over private instance methods.

- One owned memory32, i32 loads/stores (including signed/unsigned 8/16-bit loads),
  `memory.size/grow`, owned i32 globals, active data segments, and start functions.
  Instantiation initializes memory/globals/data before calling start exactly once.
  Memory is limited to 256 MiB; exceeding growth limits returns -1 without changing state.
  Memory exports use `MemorySize` (bytes), `ReadMemory(uint offset, int count)` and
  `WriteMemory(uint offset, byte[] bytes)`. Reads copy data rather than expose backing
  arrays. Global exports become properties, writable only for mutable globals.

- Function imports with typed i32/void signatures. Constructor arguments follow
  import-section order (`import0`, `import1`, ...), using generated nested delegate
  types `__wasm_Import0`, etc. Lambdas convert directly; no runtime reflection is used.
  Missing delegates fail before start. Callbacks may use the copy-based memory API
  and reenter exported functions; host exceptions propagate unchanged, including
  during start. Import module/name pairs are recorded as UTF-8 Base64 in generated
  comments, allowing arbitrary names without injecting C# syntax.

For example, `samples/Host/HostChecks.cs` constructs `new Host(exchange, notify)`
and verifies callback order, memory exchange, reentry, and exception identity in
both .NET and Unity. Run `node scripts/create-host-fixture.mjs` to reproduce its
binary and verify the same callback scenario with WebAssembly.

Imported memories/globals, indirect calls, floating point,
and other instructions are rejected. This is not yet a general-purpose WASM compiler;
ordinary Rust/C/C++ outputs will typically need more instructions and sections.
All function bodies, including unexported ones, are checked. Parsing validates section
boundaries/order, LEB128 encodings, indices, and operand/result stack heights within
the supported subset. There is an implementation limit of 100,000 locals per function.
This is not an execution sandbox: there is no fuel/time limit, and recursive calls
use the host stack. Host resource exhaustion is not normalized to a WASM trap.

Design references: [WASM binary modules](https://webassembly.github.io/spec/core/binary/modules.html),
[integer semantics](https://webassembly.github.io/spec/core/exec/numerics.html), and
[Roslyn incremental generators](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md).
