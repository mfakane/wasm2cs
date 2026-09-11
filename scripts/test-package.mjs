import { mkdtempSync, writeFileSync, readFileSync, copyFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
const root=fileURLToPath(new URL('../',import.meta.url));
const directory=mkdtempSync(join(tmpdir(),'wasm2cs-nuget-consumer-'));
const env={...process.env,NUGET_PACKAGES:join(directory,'packages')};
const run=args=>execFileSync('dotnet',args,{cwd:directory,env,encoding:'utf8'});
writeFileSync(join(directory,'Consumer.csproj'),`<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
    <LangVersion>9.0</LangVersion><CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors><EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>obj/generated</CompilerGeneratedFilesOutputPath></PropertyGroup>
  <ItemGroup><PackageReference Include="Wasm2Cs.Generator" Version="0.1.0-preview.1" PrivateAssets="all" />
    <Wasm Include="Arithmetic.wasm" /><Wasm Include="Algorithms.wasm" /><Wasm Include="Host.wasm" /><Wasm Include="Typed.wasm" /><Wasm Include="I64.wasm" /><Wasm Include="I64Memory.wasm" /><Wasm Include="Floating.wasm" /></ItemGroup>
</Project>`);
for (const [from,to] of [['samples/Smoke/Arithmetic.wasm','Arithmetic.wasm'],['samples/CAlgorithms/Algorithms.wasm','Algorithms.wasm'],
  ['samples/Floating/Floating.wasm','Floating.wasm'],['samples/Floating/FloatingChecks.cs','FloatingChecks.cs'],
  ['samples/Host/Host.wasm','Host.wasm'],['samples/Host/HostChecks.cs','HostChecks.cs']]) copyFileSync(join(root,from),join(directory,to));
const typed = JSON.parse(readFileSync(join(root, 'tests/Conformance/typed-ir.json'), 'utf8'));
writeFileSync(join(directory, 'Typed.wasm'), Buffer.from(typed.Cases.find(test => test.Kind === 'module').Binary, 'base64'));
for (const [fixture, name] of [['i64.json', 'I64'], ['i64-memory.json', 'I64Memory']]) {
  const data = JSON.parse(readFileSync(join(root, 'tests/Conformance', fixture), 'utf8'));
  writeFileSync(join(directory, name + '.wasm'), Buffer.from(data.Cases.find(test => test.Kind === 'module').Binary, 'base64'));
}
writeFileSync(join(directory,'Program.cs'),'using System;\n'+readFileSync(join(root,'samples/Smoke/Program.cs'),'utf8') + `
var typedModule = new Wasm2Cs.Generated.Typed();
var pair = typedModule.swap(17, 29, 3);
var mixed = typedModule.mixed(9007199254740993L, -0.0f, double.NegativeInfinity);
if (pair != (29, 17) || mixed.Item1 != 9007199254740993L ||
    BitConverter.SingleToInt32Bits(mixed.Item2) != int.MinValue || mixed.Item3 != double.NegativeInfinity)
    throw new Exception("Packaged typed module failed.");
Console.WriteLine("Typed block/loop and multi-result calls passed.");
var integers = new Wasm2Cs.Generated.I64();
if (integers.add(long.MaxValue, 1L) != long.MinValue || integers.div_u(-1L, 2L) != long.MaxValue || integers.rem_s(long.MinValue, -1L) != 0L)
    throw new Exception("Packaged i64 arithmetic failed.");
try { integers.div_s(long.MinValue, -1L); throw new Exception("Missing i64 overflow trap."); }
catch (Wasm2Cs.Generated.I64.TrapException trap) when (trap.Kind == Wasm2Cs.Generated.I64.TrapKind.IntegerOverflow) { }
var memory64Values = new Wasm2Cs.Generated.I64Memory();
if (memory64Values.g != long.MinValue + 1L || memory64Values.roundtrip(9007199254740993L) != 9007199254740993L || memory64Values.g != 9007199254740993L)
    throw new Exception("Packaged i64 memory/global roundtrip failed.");
Console.WriteLine("I64 arithmetic, traps, memory and globals passed.");
`);
try {
  run(['restore','--source',join(root,'artifacts'),'-p:NuGetAudit=false']);
  run(['build','--no-restore','-m:1','-p:UseSharedCompilation=false','--nologo']);
  const output=run(['bin/Debug/net10.0/Consumer.dll']);
  assert.match(output,/Floating-point semantics passed/);
  assert.match(output,/I64 arithmetic, traps, memory and globals passed/);
  assert.match(output,/Typed block\/loop and multi-result calls passed/);
  assert.match(output,/Clang CRC32\(123456789\) = cbf43926/);
  assert.match(readFileSync(join(directory,'obj/generated/Wasm2Cs.Generator/Wasm2Cs.WasmGenerator/Host.g.cs'),'utf8'),/delegate int __wasm_Import0/);
  assert.equal(existsSync(join(directory,'bin/Debug/net10.0/Wasm2Cs.Generator.dll')),false,'Generator leaked into runtime output');
  assert.equal(existsSync(join(directory,'bin/Debug/net10.0/Wasm2Cs.dll')),false,'Translator leaked into runtime output');
  console.log(`PASS: fresh NuGet-only C# 9 consumer, isolated package cache, arithmetic/CRC32/host imports/typed multi-results/i64/floating-point.\n${output}Artifacts: ${directory}`);
} catch (error) {
  console.error(error.stdout?.toString(),error.stderr?.toString(),`Consumer: ${directory}`);
  throw error;
}
