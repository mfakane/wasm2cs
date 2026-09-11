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
    <Wasm Include="Arithmetic.wasm" /><Wasm Include="Algorithms.wasm" /><Wasm Include="Host.wasm" /></ItemGroup>
</Project>`);
for (const [from,to] of [['samples/Smoke/Arithmetic.wasm','Arithmetic.wasm'],['samples/CAlgorithms/Algorithms.wasm','Algorithms.wasm'],
  ['samples/Host/Host.wasm','Host.wasm'],['samples/Host/HostChecks.cs','HostChecks.cs']]) copyFileSync(join(root,from),join(directory,to));
writeFileSync(join(directory,'Program.cs'),'using System;\n'+readFileSync(join(root,'samples/Smoke/Program.cs'),'utf8'));
try {
  run(['restore','--source',join(root,'artifacts'),'-p:NuGetAudit=false']);
  run(['build','--no-restore','-m:1','-p:UseSharedCompilation=false','--nologo']);
  const output=run(['bin/Debug/net10.0/Consumer.dll']);
  assert.match(output,/Clang CRC32\(123456789\) = cbf43926/);
  assert.match(readFileSync(join(directory,'obj/generated/Wasm2Cs.Generator/Wasm2Cs.WasmGenerator/Host.g.cs'),'utf8'),/delegate int __wasm_Import0/);
  assert.equal(existsSync(join(directory,'bin/Debug/net10.0/Wasm2Cs.Generator.dll')),false,'Generator leaked into runtime output');
  assert.equal(existsSync(join(directory,'bin/Debug/net10.0/Wasm2Cs.dll')),false,'Translator leaked into runtime output');
  console.log(`PASS: fresh NuGet-only C# 9 consumer, isolated package cache, arithmetic/CRC32/host imports.\n${output}Artifacts: ${directory}`);
} catch (error) {
  console.error(error.stdout?.toString(),error.stderr?.toString(),`Consumer: ${directory}`);
  throw error;
}
