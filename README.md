# wasm2cs

A proof of concept that translates a typed WebAssembly subset into ordinary C# methods at build time, driven by a Roslyn Source Generator. No interpreter runs at runtime; the output is plain compiled C#.

## Add a WASM file to a project

Declare a `.wasm` file as a build item and the generator produces a sealed C# class with one instance method per export:

```xml
<ItemGroup>
  <PackageReference Include="Wasm2Cs.Generator" Version="0.1.0-preview.1" PrivateAssets="all" />
  <Wasm Include="Arithmetic.wasm" />
</ItemGroup>
```

```csharp
var result = new Wasm2Cs.Generated.Arithmetic().add(20, 22); // 42
```

The package bundles the Source Generator, the MSBuild target that encodes `.wasm` files into text `AdditionalFiles`, and the small `Wasm2Cs.Runtime` ABI assembly that backs memory, globals, and tables. No separate runtime package or manual target import is needed.

`PrivateAssets="all"` is appropriate for application projects. Library projects that expose generated types in their public API need to handle the runtime dependency separately.

**The package is not published to NuGet.** Build it locally from the repository first:

```sh
node scripts/pack.mjs
```

Then point restore at `./artifacts`, for example with `dotnet restore --source ./artifacts` or a `nuget.config`. See [Usage](https://github.com/mfakane/wasm2cs/blob/master/docs/usage.md) for complete setup instructions including source-reference, Unity, CLI, and MSBuild pipeline details.

## Run the proof

Requires the **.NET 10 SDK** and Node.js 22+.

```sh
dotnet build Wasm2Cs.slnx
dotnet run --project tests/Wasm2Cs.Tests --no-build
dotnet run --project samples/Smoke --no-build
node scripts/test-build.mjs
```

`samples/Smoke` prints:

```text
add(20, 22) = 42
square(7) = 49
add(int.MaxValue, 1) = -2147483648
Clang CRC32(123456789) = cbf43926
```

## CLI

Translate a single module and print the generated C#:

```sh
dotnet run --project src/Wasm2Cs.Cli -- samples/Smoke/Arithmetic.wasm
```

Use `--target-profile` (`-p`) to select a lowering profile. The default is `portable-netstandard2.0`.

## Unity

Install `artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz` via **Package Manager → Add package from tarball**. Add `.wasm` files under `Assets`; generated public classes belong to the `Wasm2Cs.Modules` assembly. Custom asmdefs must reference that assembly. The source-only `unity/` directory is not a ready-to-install distribution. Build the tarball with `node scripts/pack.mjs`.

On Windows, `scripts/test-unity.ps1 -PackagePath <tarball>` creates a temporary project and runs an IL2CPP smoke test. Unity 6.0 is the compatibility baseline.

## Limitations

This is **not** a general-purpose WASM compiler; ordinary Rust/C/C++ outputs typically need more instructions than the current subset. This is **not** an execution sandbox: no fuel or time limit, and recursive calls use the host stack. Unsupported instructions and sections are rejected at build time with `WASM001`.

See the [supported feature list](https://github.com/mfakane/wasm2cs/blob/master/docs/supported-features.md) for the exact instruction and type subset.

Project-owned code is licensed under 0BSD. The WebAssembly conformance fixtures retain their own licenses; see `tests/Conformance/LICENSE`.

## Self-hosting

`Wasm2Cs.dll` runs inside a translated .NET WebAssembly runtime and translates separate modules without an outer WASM engine. The [self-hosting roadmap](https://github.com/mfakane/wasm2cs/blob/master/docs/README.md) (Japanese) records the implementation milestones through SH-14.
