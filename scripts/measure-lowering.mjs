import { mkdtempSync, writeFileSync, readFileSync, statSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../', import.meta.url));
const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-lowering-'));
const xml = text => text.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const vectorFixture = join(directory, 'Vector.wasm');
const cases = [
  { name: 'ArithmeticPortable', fixture: resolve(root, 'samples/Smoke/Arithmetic.wasm'), profile: 'portable-netstandard2.0', compile: true },
  { name: 'ArithmeticNet21', fixture: resolve(root, 'samples/Smoke/Arithmetic.wasm'), profile: 'dotnet-netstandard2.1', compile: true },
  { name: 'VectorDotNet', fixture: vectorFixture, fixtureLabel: 'samples/Vectors/Vector.wasm', profile: 'dotnet-vector', compile: true },
  { name: 'VectorUnity', fixture: vectorFixture, fixtureLabel: 'samples/Vectors/Vector.wasm', profile: 'unity-mathematics', compile: false }
];

writeFileSync(vectorFixture, Buffer.from(
  readFileSync(join(root, 'samples/Vectors/Vector.wasm.base64'), 'utf8').trim(), 'base64'));

function runTimed(cwd, args) {
  return new Promise((resolveResult, reject) => {
    const start = process.hrtime.bigint();
    const child = spawn('dotnet', args, { cwd, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    let maxRssKb = null;
    const poll = setInterval(() => {
      try {
        const status = readFileSync(`/proc/${child.pid}/status`, 'utf8');
        const rss = status.match(/^VmRSS:\s+(\d+) kB$/m);
        if (rss) maxRssKb = Math.max(maxRssKb ?? 0, Number(rss[1]));
      } catch { /* /proc is unavailable on non-Linux hosts. */ }
    }, 10);
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', status => {
      clearInterval(poll);
      const output = stdout + stderr;
      assert.equal(status, 0, output);
      resolveResult({
        output,
        elapsedMs: Number((Number(process.hrtime.bigint() - start) / 1e6).toFixed(1)),
        maxRssKb
      });
    });
  });
}

function writeProject(caseDirectory, fixture, className) {
  writeFileSync(join(caseDirectory, 'Consumer.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>obj/generated</CompilerGeneratedFilesOutputPath></PropertyGroup>
  <ItemGroup><ProjectReference Include="${xml(resolve(root, 'src/Wasm2Cs.Generator/Wasm2Cs.Generator.csproj'))}"
      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <Wasm Include="${xml(fixture)}" /></ItemGroup>
  <Import Project="${xml(resolve(root, 'build/Wasm2Cs.targets'))}" />
</Project>`);
  writeFileSync(join(caseDirectory, 'Program.cs'), `public static class Program { public static void Main() { } }`);
  return join(caseDirectory, 'obj/generated/Wasm2Cs.Generator/Wasm2Cs.WasmGenerator', `${className}.g.cs`);
}

try {
  const results = [];
  for (const entry of cases) {
    const className = basename(entry.fixture, '.wasm');
    const generatedFromCli = await runTimed(root, [
      'run', '--project', resolve(root, 'src/Wasm2Cs.Cli'), '--no-build', '--',
      entry.fixture, '--target-profile', entry.profile
    ]);
    const measurement = {
      name: entry.name,
      fixture: entry.fixtureLabel ?? entry.fixture.slice(root.length).replace(/^[/\\]/, ''),
      profile: entry.profile,
      sourceBytes: Buffer.byteLength(generatedFromCli.output),
      generationMs: generatedFromCli.elapsedMs,
      generationMaxRssKb: generatedFromCli.maxRssKb,
      compileMs: null,
      compileMaxRssKb: null
    };
    if (entry.compile) {
      const caseDirectory = mkdtempSync(join(directory, `${entry.name.toLowerCase()}-`));
      const generatedPath = writeProject(caseDirectory, entry.fixture, className);
      await runTimed(caseDirectory, ['restore', '-p:NuGetAudit=false']);
      const compiled = await runTimed(caseDirectory, [
        'build', '--no-restore', '-m:1', '-p:UseSharedCompilation=false',
        `-p:Wasm2CsTargetProfile=${entry.profile}`, '--nologo'
      ]);
      measurement.compileMs = compiled.elapsedMs;
      measurement.compileMaxRssKb = compiled.maxRssKb;
      measurement.generatedSourceBytes = statSync(generatedPath).size;
      rmSync(caseDirectory, { recursive: true, force: true });
    }
    results.push(measurement);
  }
  console.log(JSON.stringify({ schemaVersion: 1, command: 'node scripts/measure-lowering.mjs', results }, null, 2));
} finally {
  rmSync(directory, { recursive: true, force: true });
}
