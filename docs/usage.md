# Usage

This guide covers setting up wasm2cs for .NET projects (via NuGet package or source reference), Unity, and the CLI. For the supported instruction subset, see [supported-features.md](supported-features.md).

## Package setup

`Wasm2Cs.Generator` is not published to a public registry. Build it locally from the repository root:

```sh
node scripts/pack.mjs
```

This produces `artifacts/Wasm2Cs.Generator.0.1.0-preview.1.nupkg`. Add a local NuGet source before restoring.

**Option 1 — `nuget.config` in your project root:**

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="wasm2cs-local" value="/path/to/wasm2cs/artifacts" />
  </packageSources>
</configuration>
```

**Option 2 — restore flag:**

```sh
dotnet restore --source /path/to/wasm2cs/artifacts
```

Then add to your `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Wasm2Cs.Generator" Version="0.1.0-preview.1" PrivateAssets="all" />
  <Wasm Include="YourModule.wasm" />
</ItemGroup>
```

The package automatically imports the MSBuild target and Roslyn analyzer. No explicit `<Import>` or separate runtime package reference is needed.

`PrivateAssets="all"` marks the package as a build-time tool. For application projects this is correct — the generated code and the bundled `Wasm2Cs.Runtime.dll` are included in the output. If you are building a library that exposes generated types in its public API, downstream consumers will not receive the runtime transitively; handle that dependency separately.

Verify the package against an isolated consumer:

```sh
node scripts/test-package.mjs
```

## Source reference (from repository)

Use this when working on wasm2cs itself. `samples/Smoke/Smoke.csproj` is a complete example. The minimum entries in an SDK-style project:

```xml
<ItemGroup>
  <ProjectReference Include="/path/to/wasm2cs/src/Wasm2Cs.Runtime/Wasm2Cs.Runtime.csproj" />
  <ProjectReference Include="/path/to/wasm2cs/src/Wasm2Cs.Generator/Wasm2Cs.Generator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  <Wasm Include="YourModule.wasm" />
</ItemGroup>
<Import Project="/path/to/wasm2cs/build/Wasm2Cs.targets" />
```

`OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"` register the generator as a Roslyn analyzer without adding a compile-time assembly reference. The `<Import>` pulls in the `Wasm2CsPrepare` MSBuild target that the package would otherwise import automatically.

## MSBuild pipeline

When a project contains `<Wasm>` items, the `Wasm2CsPrepare` target runs before compilation:

1. Each `.wasm` file is Base64-encoded into `$(IntermediateOutputPath)wasm2cs/<Name>.wasm.base64`. Timestamps are preserved when the bytes are unchanged, so the generator is not reinvoked unnecessarily.
2. The encoded files are passed to the Roslyn Source Generator as `AdditionalFiles`. The generator decodes them, translates each module, and emits C# source.

Run a build after editing a `.wasm` file. IDE automatic file-watching behavior for `AdditionalFiles` has not been verified.

### Persisting generated files

To also write generated `.g.cs` files to disk for inspection:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

Generated files appear at `obj/generated/Wasm2Cs.Generator/Wasm2Cs.WasmGenerator/<Name>.g.cs` (large modules also produce split `<Name>.Functions.NNNN.g.cs` files). Keep the output path under `obj` to avoid compiling the files twice.

### Target profile

The `Wasm2CsTargetProfile` property selects the C# lowering backend:

| Profile | Description |
|---|---|
| `portable-netstandard2.0` | Default. Uses only .NET Standard 2.0 APIs. |
| `dotnet-netstandard2.1` | Uses operation-scoped `Span`/`MemoryMarshal` helpers. |
| `dotnet-vector` | Adds `System.Runtime.Intrinsics.Vector128<float>` for the limited SIMD subset. |
| `unity-mathematics` | Uses `Unity.Mathematics` for the limited SIMD subset. |

```xml
<PropertyGroup>
  <Wasm2CsTargetProfile>dotnet-netstandard2.1</Wasm2CsTargetProfile>
</PropertyGroup>
```

A profile never changes WASM semantics. Selecting `dotnet-vector` or `unity-mathematics` does not enable additional SIMD instructions; it only affects how the supported `v128.const`, `f32x4.add`, and `f32x4.mul` subset is lowered.

## Generated API

Each `.wasm` filename (without extension) becomes a sealed class in `Wasm2Cs.Generated`. Each exported WASM function becomes a public instance method. Modules own independent memory and global state.

```csharp
using Wasm2Cs.Generated;

var arith = new Arithmetic();
Console.WriteLine(arith.add(20, 22));  // 42
```

### Naming rules

- Filenames must be valid ASCII C# identifiers. C# keywords are escaped with `@`.
- An export cannot have the same name as its class.
- Module filenames must be unique ignoring case.

### Diagnostics

| Code | Source | Meaning |
|---|---|---|
| `WASM001` | Source Generator | Translation failed: unsupported instruction, malformed binary, or validation error. Includes function index and byte offset. |
| `WASM002` | MSBuild task | Two `<Wasm>` filenames produce the same class name (case-insensitive). |

Both fail the build.

## Unity

Build the UPM tarball from the repository root:

```sh
node scripts/pack.mjs
```

This produces `artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz`. In the Unity Editor:

1. Open **Package Manager → + → Add package from tarball** and select the `.tgz`.
2. Add `.wasm` files anywhere under `Assets`.
3. The bridge watches assets and maintains Base64 inputs in `Assets/Wasm2CsGeneratedInputs`. Use **Tools/Wasm2Cs/Regenerate Inputs** to reconcile after file changes outside the Editor.
4. Generated public classes belong to the `Wasm2Cs.Modules` assembly. Custom asmdefs must reference `Wasm2Cs.Modules`.
5. The compiler plugin is excluded from runtime platforms.

On Windows, run a full IL2CPP smoke test:

```powershell
./scripts/test-unity.ps1 -PackagePath ./artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz
```

The script creates a temporary consumer project, installs the package, and builds and runs a Windows x64 IL2CPP Player. Logs and the temporary project are preserved for investigation.

Unity 6.0 is the compatibility baseline; `6000.6.0f1` is the tested editor. The source-only `unity/Packages/com.mfakane.wasm2cs` directory is not a ready-to-install distribution.

## WASI Preview1 imports

No WASI host is generated or shipped in the `Wasm2Cs.Generator` NuGet package or the Unity tarball. Pass delegates for the module's `wasi_snapshot_preview1` imports yourself. `tests/Wasm2Cs.DotnetHost` is a test-only host from the self-hosting experiments; do not reference it from an application. Its `HostEnvironment.FdWrite` is a tested reference implementation of `wasi_snapshot_preview1.fd_write` that you can copy, and the example below is how the tests use it. Do not pass a delegate that returns success for imports you have not implemented. `proc_exit` is not provided.

`FdWrite` needs the module memory. Owned memory is unpublished until `new` returns, so this does not work for a start-section import on owned memory. `samples/WasiPreview1/wasi_hello.wasm` exports `_start` and has no start section. The export is void. wasmtime 28.0.1 prints `hello wasi` and a newline and exits 0. A normal return from `_start`, with `proc_exit` not called, is that exit 0. `ExecutionChecks.WasiFdWrite` checks the same stdout.

```csharp
// Test-side usage (tests/Wasm2Cs.Tests). Not for applications: copy FdWrite instead.
using Wasm2Cs.DotnetHost;
using Wasm2Cs.Generated;

var stdout = new List<byte>();
var host = new HostEnvironment(stdout: stdout.AddRange);
wasi_hello? guest = null;
guest = new wasi_hello(
    (int fd, int iovs, int iovsLength, int nwritten) =>
        host.FdWrite(guest!.memory, fd, iovs, iovsLength, nwritten),
    (int code) => throw new InvalidOperationException("proc_exit is not connected: " + code));
guest._start();
```

fd 1 and fd 2 are the stdout and stderr callbacks. Any other fd has to be opened with `OpenFile` on a virtual file. Bytes are not written to the real filesystem. This is not a sandbox.

Measured scope, 2026-10-03, fixture `samples/WasiPreview1/wasi_hello.wasm` only (`docs/t08-wasi-remeasure.md`): wasmtime 28.0.1 and Node.js 22.19.0 preview1 both print `hello wasi` plus a newline and exit 0, and the generated host matches that stdout without calling `proc_exit`. That is the whole WASI surface. The host is test code, not a NuGet or Unity package. Unity and IL2CPP were not verified.

## CLI

Translate a single module to stdout:

```sh
dotnet run --project src/Wasm2Cs.Cli -- <module.wasm>
```

| Flag | Short | Description |
|---|---|---|
| `--target-profile` | `-p` | Lowering profile. Default: `portable-netstandard2.0`. |
| `--class-name` | `-n` | Override the generated class name. Default: filename without extension. |
| `--output-directory` | `-o` | Write files to a directory instead of stdout. Removes existing `.g.cs` files for the class first. |

## Pinned development environment

The self-hosting toolchain is pinned in `flake.lock`: .NET SDK `10.0.400`, Node.js `22.17.0`, WABT `1.0.41`. Enter with Nix:

```sh
nix develop
node scripts/self-hosting.mjs prepare
```

The shell sets `DOTNET_ROOT`, `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, and `MSBuildUserExtensionsPath` locally without changing the user's global SDK state.
