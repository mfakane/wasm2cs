// End-to-end MSBuild tests use an isolated temporary consumer project.
import { mkdtempSync, writeFileSync, readFileSync, statSync, utimesSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../', import.meta.url));
const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-build-'));
const xml = text => text.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const binary = value => Buffer.from([0,97,115,109,1,0,0,0, 1,5,1,0x60,0,1,0x7f,
  3,2,1,0, 7,5,1,1,102,0,0, 10,6,1,4,0,0x41,value,0x0b]);
function project(items) {
  writeFileSync(join(directory, 'Consumer.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>obj/generated</CompilerGeneratedFilesOutputPath></PropertyGroup>
    <ItemGroup><ProjectReference Include="${xml(resolve(root, 'src/Wasm2Cs.Generator/Wasm2Cs.Generator.csproj'))}"
      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />${items}</ItemGroup>
    <Import Project="${xml(resolve(root, 'build/Wasm2Cs.targets'))}" /></Project>`);
}
function dotnet(args, success = true) {
  const result = spawnSync('dotnet', args, { cwd: directory, encoding: 'utf8' });
  if (result.error) throw result.error;
  const text = result.stdout + result.stderr;
  if (success) assert.equal(result.status, 0, text);
  else assert.notEqual(result.status, 0, text);
  return text;
}
const build = (success = true) => dotnet(['build', '-m:1', '-p:UseSharedCompilation=false', '--nologo'], success);
const run = () => dotnet(['bin/Debug/net10.0/Consumer.dll']).trim();
try {
  project('<Wasm Include="Counter.wasm" />');
  writeFileSync(join(directory, 'Program.cs'), 'System.Console.WriteLine(typeof(Program).Assembly.GetType("Wasm2Cs.Generated.Counter")?.GetMethod("f")?.Invoke(null, null) ?? "missing");');
  const path = join(directory, 'Counter.wasm');
  writeFileSync(path, binary(42));
  build();
  assert.equal(run(), '42');
  const generated = join(directory, 'obj/generated/Wasm2Cs.Generator/Wasm2Cs.WasmGenerator/Counter.g.cs');
  assert.match(readFileSync(generated, 'utf8'), /int s0 = 42;/);

  const encoded = join(directory, 'obj/Debug/net10.0/wasm2cs/Counter.wasm.base64');
  const encodedTime = statSync(encoded).mtimeMs;
  build();
  assert.equal(statSync(encoded).mtimeMs, encodedTime, 'Unchanged input was rewritten');

  const original = statSync(path);
  writeFileSync(path, binary(43));
  utimesSync(path, original.atime, original.mtime);
  build();
  assert.equal(run(), '43', 'Same-length binary change with preserved timestamp was ignored');
  assert.match(readFileSync(generated, 'utf8'), /int s0 = 43;/);

  const invalid = binary(42);
  invalid[invalid.length - 3] = 0xff;
  writeFileSync(path, invalid);
  assert.match(build(false), /WASM001.*opcode 0xff/);

  writeFileSync(path, binary(42));
  project('<Wasm Include="Counter.wasm" /><Wasm Include="Counter.wasm" />');
  assert.match(build(false), /WASM002/);

  project('');
  build();
  assert.equal(run(), 'missing', 'Removing a WASM item retained the generated type');
  console.log('PASS: MSBuild generation, unchanged inputs, binary edits, invalid/duplicate inputs, and item removal.');
} finally {
  rmSync(directory, { recursive: true, force: true });
}
