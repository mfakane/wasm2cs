// SH-01: build one pinned browser-wasm bundle, inspect it, and collect the
// official Node.js reference result. No generated artifact is source controlled.
import {
  cpSync,
  existsSync,
  mkdtempSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
  renameSync
} from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { arch, cpus, platform, release, tmpdir } from 'node:os';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { acceptCacheStamp, cacheKey, extractMeasurement, missingTools, runStages, verifyStages } from './self-hosting-verify.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const artifactRoot = resolve(process.env.SELF_HOSTING_ARTIFACTS ?? join(root, 'artifacts', 'self-hosting'));
const environmentRoot = join(artifactRoot, 'environment');
const bundleRoot = join(artifactRoot, 'bundle');
const manifestPath = join(artifactRoot, 'bundle-manifest.json');
const profilePath = join(root, 'docs', 'self-hosting', 'SH-01-profile.json');
const startupProfilePath = join(root, 'docs', 'self-hosting', 'SH-10-startup.json');
const managedProfilePath = join(root, 'docs', 'self-hosting', 'SH-11-managed.json');
const profile = JSON.parse(readFileSync(profilePath, 'utf8'));
const args = process.argv.slice(2);
const command = args[0];

const help = `Usage: node scripts/self-hosting.mjs <prepare|inventory|reference|generate|compile|host|hello|managed|translate|audit|verify|unity-prepare|unity-check>

prepare    install/check the isolated workload and publish the guest bundle
inventory  validate the bundle and record a full WABT inventory
reference  run the bundle through the official Node.js browser-wasm host
generate   translate dotnet.native.wasm into deterministic C# source files
compile    compile the generated C# files against the runtime ABI
host       run the C# SH-09 ABI fixture and construct the full generated runtime
hello      start the generated Mono runtime and run managed Hello World
managed    compare managed probes across .NET, official WASM, and translated Mono
translate  run wasm2cs inside translated Mono and verify generated C#
audit      verify canonical bundle provenance and SH-01 through SH-13 records
verify     rerun inventory, generation, compile, ABI, managed startup, and self-hosting; does not install workloads
unity-prepare  write the Unity self-host runner and guest hash manifest
unity-check   compare Unity Editor/IL2CPP output with SH-12 and compile it outside Unity

prepare flags:
  --skip-workload-install  report a missing workload without attempting install
`;

function json(path, value) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(value, null, 2) + '\n');
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function sha256File(path) {
  return sha256(readFileSync(path));
}

function run(program, programArgs, options = {}) {
  const result = spawnSync(program, programArgs, {
    cwd: options.cwd ?? root,
    env: options.env ?? process.env,
    encoding: 'utf8',
    maxBuffer: 512 * 1024 * 1024,
    timeout: options.timeoutMs
  });
  const output = (result.stdout ?? '') + (result.stderr ?? '');
  if (result.error) {
    if (options.allowFailure) return { status: null, output, error: result.error };
    throw new Error(`${program} could not be started: ${result.error.message}`);
  }
  if (result.status !== 0 && !options.allowFailure) {
    throw new Error(`${program} ${programArgs.join(' ')} failed (exit ${result.status})\n${output}`);
  }
  return { status: result.status, output };
}

function commandPath(name) {
  const suffixes = process.platform === 'win32' ? ['', '.exe', '.cmd'] : [''];
  for (const directory of (process.env.PATH ?? '').split(process.platform === 'win32' ? ';' : ':')) {
    for (const suffix of suffixes) {
      const candidate = join(directory, name + suffix);
      if (existsSync(candidate)) return candidate;
    }
  }
  return null;
}

function tool(name, versionArgs = ['--version']) {
  const path = name === 'node'
    ? process.execPath
    : (name.includes('/') || name.includes('\\') ? name : commandPath(name));
  if (!path) return { command: name, available: false, version: null };
  const result = run(path, versionArgs, { allowFailure: true });
  return {
    command: path,
    available: result.status === 0,
    version: result.output.trim() || null
  };
}

function dotnetInfo() {
  const result = run('dotnet', ['--info']);
  const version = /^\s*Version:\s*(\S+)\s*$/m.exec(result.output)?.[1] ?? null;
  const basePath = /^\s*Base Path:\s*(.+?)\s*$/m.exec(result.output)?.[1] ?? null;
  if (!version || !basePath) throw new Error('dotnet --info did not contain an SDK version and Base Path.');
  return { version, basePath, text: result.output };
}

function isolatedEnvironment(basePath) {
  const sdkRoot = resolve(basePath, '..', '..');
  return {
    ...process.env,
    DOTNET_ROOT: sdkRoot,
    DOTNET_ROOT_X64: sdkRoot,
    DOTNET_CLI_HOME: join(environmentRoot, 'dotnet-home'),
    NUGET_PACKAGES: join(environmentRoot, 'nuget-packages'),
    MSBuildUserExtensionsPath: join(environmentRoot, 'msbuild'),
    DOTNET_NOLOGO: '1'
  };
}

function recordCommand(log, program, programArgs, options = {}) {
  const result = run(program, programArgs, options);
  log.push(`$ ${program} ${programArgs.join(' ')}\n${result.output}`);
  return result;
}

function savedWabtTool(name) {
  const toolchainPath = join(artifactRoot, 'toolchain.json');
  if (!existsSync(toolchainPath)) return null;
  const toolchain = JSON.parse(readFileSync(toolchainPath, 'utf8'));
  return toolchain.tools?.wabt?.find(item => item.command?.endsWith(`/${name}`) || item.command?.endsWith(`\\${name}`))?.command ?? null;
}

function walkFiles(directory) {
  const files = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walkFiles(path));
    else if (entry.isFile()) files.push(path);
  }
  return files;
}

function posixPath(path) {
  return path.replaceAll('\\', '/');
}

function classifyArtifact(path) {
  const name = path.split('/').pop();
  const extension = extname(name).toLowerCase();
  if (extension === '.dll' || extension === '.pdb') return 'managed';
  if (path === 'main.mjs' || extension === '.mjs' || extension === '.js') return 'javascript';
  if (extension === '.wasm') return name === 'dotnet.native.wasm' ? 'runtime' : 'managed';
  if (path.startsWith('_framework/')) return 'runtime';
  return 'supporting';
}

function bundleEntries(directory) {
  return walkFiles(directory)
    .map(path => posixPath(relative(directory, path)))
    .sort()
    .map(path => {
      const absolute = join(directory, ...path.split('/'));
      return {
        path,
        category: classifyArtifact(path),
        bytes: statSync(absolute).size,
        sha256: sha256File(absolute)
      };
    });
}

function manifestBody(entries) {
  return {
    schemaVersion: 1,
    milestone: 'SH-01',
    profile: profile.profile,
    entries
  };
}

function writeManifest() {
  const entries = bundleEntries(bundleRoot);
  const body = manifestBody(entries);
  json(manifestPath, { ...body, bundleSha256: sha256(JSON.stringify(body)) });
  return JSON.parse(readFileSync(manifestPath, 'utf8'));
}

function acceptRebuild() {
  return process.env.SELF_HOSTING_ACCEPT_REBUILD === '1';
}

function verifyCanonicalBundle(manifest) {
  const canonical = profile.canonicalBundle;
  if (!canonical?.sha256 || !canonical.runtimeSha256)
    throw new Error(`Canonical bundle hashes are missing from ${profilePath}.`);
  if (manifest.bundleSha256 !== canonical.sha256) {
    const message = `Bundle ${manifest.bundleSha256} differs from canonical bundle ${canonical.sha256}.`;
    if (!acceptRebuild()) throw new Error(message);
    const runtime = manifest.entries?.find(item => item.path === '_framework/dotnet.native.wasm');
    json(join(artifactRoot, 'rebuild-mismatch.json'), {
      schemaVersion: 1,
      status: 'not-reproduced',
      bundleSha256: manifest.bundleSha256,
      canonicalBundleSha256: canonical.sha256,
      runtimeSha256: runtime?.sha256 ?? null,
      canonicalRuntimeSha256: canonical.runtimeSha256,
      reason: 'Publish embeds absolute workload paths, so another work directory cannot reproduce the canonical bytes.'
    });
    console.error(`self-hosting: ${message} Continuing without claiming reproduction.`);
    return;
  }
  const runtime = runtimeEntry(manifest);
  if (runtime.sha256 !== canonical.runtimeSha256)
    throw new Error(`Runtime ${runtime.sha256} differs from canonical runtime ${canonical.runtimeSha256}.`);
}

function verifyBundle() {
  if (!existsSync(manifestPath) || !existsSync(bundleRoot)) {
    throw new Error(`Bundle is missing. Run prepare first: ${manifestPath}`);
  }
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const actualEntries = bundleEntries(bundleRoot);
  const expectedBody = { ...manifest };
  delete expectedBody.bundleSha256;
  if (sha256(JSON.stringify(expectedBody)) !== manifest.bundleSha256) {
    throw new Error('Bundle manifest hash does not match its entries.');
  }
  if (JSON.stringify(actualEntries) !== JSON.stringify(manifest.entries)) {
    throw new Error('Bundle contents differ from bundle-manifest.json; refusing mixed artifacts.');
  }
  verifyCanonicalBundle(manifest);
  const effectivePath = join(bundleRoot, 'effective-build.json');
  if (!existsSync(effectivePath)) throw new Error('Bundle is missing effective-build.json.');
  const effective = JSON.parse(readFileSync(effectivePath, 'utf8'));
  if (effective.runtimePack !== profile.runtimePack) {
    throw new Error(`Bundle runtime pack differs from the pinned profile: ${effective.runtimePack}`);
  }
  for (const [key, value] of Object.entries(profile.buildProperties)) {
    if (String(effective.buildProperties[key]) !== String(value)) {
      throw new Error(`Bundle build property ${key} differs from the pinned profile.`);
    }
  }
  return manifest;
}

function bootConfig(manifest) {
  const profile = JSON.parse(readFileSync(startupProfilePath, 'utf8'));
  const bootPath = join(bundleRoot, ...profile.bootConfigPath.split('/'));
  if (!existsSync(bootPath)) throw new Error(`Boot config is missing: ${bootPath}`);
  const text = readFileSync(bootPath, 'utf8');
  const match = /\/\*json-start\*\/([\s\S]*?)\/\*json-end\*\//.exec(text);
  if (!match) throw new Error(`Boot config is not a pinned JSON module: ${bootPath}`);
  const config = JSON.parse(match[1]);
  const runtime = runtimeEntry(manifest);
  const runtimePath = join(bundleRoot, ...runtime.path.split('/'));
  if (runtime.path !== profile.runtimePath) throw new Error(`SH-10 runtime path differs: ${runtime.path}`);
  if (sha256File(runtimePath) !== profile.runtimeWasmSha256)
    throw new Error(`SH-10 runtime hash differs from ${startupProfilePath}.`);
  if (config.mainAssemblyName !== profile.mainAssemblyName)
    throw new Error(`SH-10 main assembly differs: ${config.mainAssemblyName}`);
  if (Number(config.debugLevel ?? 0) !== Number(profile.debugLevel))
    throw new Error(`SH-10 debug level differs: ${config.debugLevel}`);
  if (config.globalizationMode !== profile.globalizationMode)
    throw new Error(`SH-10 globalization mode differs: ${config.globalizationMode}`);
  const assemblies = [...(config.resources?.coreAssembly ?? []), ...(config.resources?.assembly ?? [])];
  if (assemblies.length === 0) throw new Error('SH-10 boot config contains no managed assemblies.');
  const paths = new Set(manifest.entries.map(entry => entry.path));
  for (const assembly of assemblies) {
    const path = assembly.virtualPath ?? assembly.name;
    if (!path || !paths.has(`_framework/${path}`)) throw new Error(`SH-10 managed asset is missing: ${path}`);
  }
  const properties = config.runtimeConfig?.runtimeOptions?.configProperties ?? {};
  if (JSON.stringify(sortedProperties(properties)) !== JSON.stringify(sortedProperties(propertiesFromProfile())))
    throw new Error('SH-10 runtime properties differ from the boot config.');
  const inventoryPath = join(artifactRoot, 'inventory.json');
  if (!existsSync(inventoryPath)) throw new Error(`Runtime inventory is missing: ${inventoryPath}`);
  const inventory = JSON.parse(readFileSync(inventoryPath, 'utf8'));
  const runtimeInventory = inventory.wasm?.find(item => item.path === runtime.path);
  if (!runtimeInventory) throw new Error(`Inventory does not contain ${runtime.path}.`);
  const exportNames = new Set((runtimeInventory.exports ?? []).map(item => /-> "([^"]+)"$/.exec(item)?.[1]).filter(Boolean));
  for (const name of Object.values(profile.requiredExports))
    if (!exportNames.has(name)) throw new Error(`SH-10 required export is missing: ${name}`);
  if (!profile.managedEntryWrapper) throw new Error('SH-10 managed entry wrapper is missing from the startup profile.');
  return { profile, config, assemblies, properties, managedEntryWrapper: profile.managedEntryWrapper, runtimeSha256: sha256File(runtimePath) };
}

function propertiesFromProfile() {
  return JSON.parse(readFileSync(startupProfilePath, 'utf8')).runtimeProperties;
}

function sortedProperties(properties) {
  return Object.entries(properties).sort(([left], [right]) => left.localeCompare(right));
}

function sectionItems(text) {
  const sections = {};
  let section = null;
  for (const line of text.split(/\r?\n/)) {
    const header = /^([A-Za-z][A-Za-z ]*)\[(\d+)\]:/.exec(line);
    if (header) {
      section = header[1].trim();
      sections[section] = { count: Number(header[2]), items: [] };
      continue;
    }
    if (section && /^\s*-\s*/.test(line)) sections[section].items.push(line.trim().slice(1).trim());
  }
  return sections;
}

function parseFunctions(disassembly, objectDump) {
  const functions = [];
  let current = null;
  for (const line of disassembly.split(/\r?\n/)) {
    const marker = /^\s*(?:[0-9a-f]+\s+)?(?:-\s*)?func\[(\d+)\](?:\s+size=(\d+))?(?:\s+<([^>]+)>)?:/i.exec(line);
    if (marker) {
      current = {
        index: Number(marker[1]),
        size: marker[2] ? Number(marker[2]) : null,
        name: marker[3] ?? null,
        instructions: []
      };
      functions.push(current);
      continue;
    }
    const instruction = /^\s*([0-9a-f]+):\s+(.+?)\s+\|\s*(.*)$/i.exec(line);
    if (current && instruction) {
      current.instructions.push({
        offset: Number.parseInt(instruction[1], 16),
        bytes: instruction[2].trim(),
        operation: instruction[3].trim()
      });
    }
  }
  const typeByFunction = new Map();
  for (const match of objectDump.matchAll(/^\s*-\s*func\[(\d+)\]\s+sig=(\d+)(?:\s+<([^>]+)>)?/gm)) {
    typeByFunction.set(Number(match[1]), {
      typeIndex: Number(match[2]),
      name: match[3] ?? null
    });
  }
  for (const functionInfo of functions) Object.assign(functionInfo, typeByFunction.get(functionInfo.index));
  return functions;
}

function featureInventory(nativeFile, objectDump, disassembly, headerDump) {
  const sections = sectionItems(objectDump);
  const functions = parseFunctions(disassembly, objectDump);
  const declaredFunctions = sections.Code?.count;
  if (declaredFunctions !== undefined && declaredFunctions !== functions.length) {
    throw new Error(`${nativeFile}: WABT disassembly listed ${functions.length} of ${declaredFunctions} defined functions.`);
  }
  const allText = `${objectDump}\n${disassembly}`;
  return {
    path: nativeFile,
    sections,
    types: sections.Type?.items ?? [],
    imports: sections.Import?.items ?? [],
    exports: sections.Export?.items ?? [],
    tables: sections.Table?.items ?? [],
    memories: sections.Memory?.items ?? [],
    globals: sections.Global?.items ?? [],
    elements: sections.Element?.items ?? [],
    data: sections.Data?.items ?? [],
    start: sections.Start?.items ?? [],
    functions,
    functionCount: functions.length,
    targetFeatures: allText.split(/\r?\n/).filter(line => /target feature|features:/i.test(line)),
    wasmHeader: headerDump.trim(),
    evidence: {
      objectDump: `${nativeFile}.objdump.txt`,
      disassembly: `${nativeFile}.disasm.txt`,
      sections: `${nativeFile}.sections.txt`
    }
  };
}

function featureRows(inventories, artifacts) {
  const text = JSON.stringify({ inventories, artifacts }).toLowerCase();
  const found = pattern => pattern.test(text);
  return [
    ['i32 instructions and control flow', 'SH-02', /i32|block|loop|br_if/],
    ['i64 instructions and values', 'SH-03', /i64/],
    ['floating-point instructions', 'SH-04', /f32|f64/],
    ['memory, globals, data, and start', 'SH-05', /memory|global|data|start/],
    ['tables, elements, and indirect calls', 'SH-06', /table|element|call_indirect/],
    ['WASM exception handling', 'SH-07', /try|catch|throw|tag/],
    ['large-module code generation', 'SH-08', /functioncount/],
    ['runtime import ABI', 'SH-09', /imports/],
    ['Mono startup and managed entry point', 'SH-10', /dotnet\.native\.wasm/],
    ['managed BCL, GC, and exceptions', 'SH-11', /system\.private\.corelib/],
    ['guest Wasm2Cs translation', 'SH-12', /wasm2cs\.dll/],
    ['Unity IL2CPP guest execution', 'SH-13', /unity/],
    ['reproducible bundle verification', 'SH-14', /sha256/]
  ].map(([feature, owner, pattern]) => ({
    feature,
    owner,
    status: found(pattern) ? 'observed; implementation status is assigned to owner' : 'not observed in scanned text'
  }));
}

function inventory() {
  const manifest = verifyBundle();
  const wabtNames = ['wasm-validate', 'wasm-objdump', 'wasm2wat'];
  const wabt = wabtNames.map(name => tool(savedWabtTool(name) ?? name));
  const missing = wabt.filter(item => !item.available);
  if (missing.length) {
    throw new Error(`Pinned WABT tools are required for a complete inventory: ${missing.map(item => item.command).join(', ')}`);
  }
  const wrongVersion = wabt.filter(item => !item.version || !item.version.includes(profile.wabt));
  if (wrongVersion.length) {
    throw new Error(`WABT ${profile.wabt} is required; got ${wrongVersion.map(item => `${item.command}: ${item.version}`).join(', ')}`);
  }
  const wabtCommands = Object.fromEntries(wabt.map((item, index) => [wabtNames[index], item.command]));
  const wasmEntries = manifest.entries.filter(entry => entry.path.endsWith('.wasm'));
  const inventories = [];
  for (const entry of wasmEntries) {
    const path = entry.path;
    const absolute = join(bundleRoot, ...path.split('/'));
    const validation = run(wabtCommands['wasm-validate'], ['--enable-all', absolute], { allowFailure: true });
    if (validation.status !== 0) throw new Error(`wasm-validate failed for ${path}\n${validation.output}`);
    const header = run(wabtCommands['wasm-objdump'], ['-h', absolute]);
    const objectDump = run(wabtCommands['wasm-objdump'], ['-x', absolute]);
    const disassembly = run(wabtCommands['wasm-objdump'], ['-d', absolute]);
    const sections = run(wabtCommands['wasm-objdump'], ['-s', absolute]);
    for (const outputPath of [`${path}.objdump.txt`, `${path}.disasm.txt`, `${path}.sections.txt`]) {
      mkdirSync(dirname(join(artifactRoot, outputPath)), { recursive: true });
    }
    writeFileSync(join(artifactRoot, `${path}.objdump.txt`), objectDump.output);
    writeFileSync(join(artifactRoot, `${path}.disasm.txt`), disassembly.output);
    writeFileSync(join(artifactRoot, `${path}.sections.txt`), `${header.output}\n${sections.output}`);
    inventories.push(featureInventory(path, objectDump.output, disassembly.output, header.output));
  }
  const artifacts = manifest.entries.map(entry => ({ ...entry }));
  const managedDependencies = artifacts
    .filter(entry => entry.category === 'managed' && entry.path.endsWith('.dll'))
    .map(entry => entry.path)
    .sort();
  const result = {
    schemaVersion: 1,
    milestone: 'SH-01',
    profile: profile.profile,
    bundleSha256: manifest.bundleSha256,
    tools: Object.fromEntries(wabt.map((item, index) => [wabtNames[index], item.version])),
    artifacts,
    managedDependencies,
    wasm: inventories,
    requiredFeatures: featureRows(inventories, artifacts)
  };
  json(join(artifactRoot, 'inventory.json'), result);
  const summary = [
    '# SH-01 runtime inventory',
    '',
    `- Profile: \`${profile.profile}\``,
    `- Bundle SHA-256: \`${manifest.bundleSha256}\``,
    `- WABT: ${wabt.map(item => item.version).join(', ')}`,
    `- Managed artifacts: ${manifest.entries.filter(item => item.category === 'managed').length}`,
    `- Runtime artifacts: ${manifest.entries.filter(item => item.category === 'runtime').length}`,
    '',
    '## WASM modules',
    ...inventories.flatMap(item => [
      `### \`${item.path}\``,
      `- Functions: ${item.functionCount}`,
      `- Sections: ${Object.entries(item.sections).map(([name, value]) => `${name}=${value.count}`).join(', ')}`,
      `- Imports: ${item.sections.Import?.count ?? 0}; exports: ${item.sections.Export?.count ?? 0}; tables: ${item.sections.Table?.count ?? 0}; memories: ${item.sections.Memory?.count ?? 0}; data: ${item.sections.Data?.count ?? 0}; start: ${item.sections.Start?.count ?? 0}`,
      `- Full function instruction list: \`inventory.json\` and ${item.evidence.disassembly}`,
      ''
    ]),
    '## Required features',
    '| Feature | Owner | State |',
    '|---|---|---|',
    ...result.requiredFeatures.map(item => `| ${item.feature} | ${item.owner} | ${item.status} |`),
    '',
    'This inventory is evidence only; it does not claim that wasm2cs can translate the runtime yet.',
    ''
  ].join('\n');
  writeFileSync(join(artifactRoot, 'inventory.md'), summary);
  console.log(`Wrote inventory for ${inventories.length} runtime WASM module(s), bundle ${manifest.bundleSha256}.`);
}

function requestScenarios() {
  const arithmetic = readFileSync(join(root, 'samples', 'Smoke', 'Arithmetic.wasm'));
  const clang = readFileSync(join(root, 'samples', 'CAlgorithms', 'Algorithms.wasm'));
  const invalid = Buffer.from([0, 97, 115, 109, 1, 0, 0, 0, 255]);
  return [
    { id: 'hello-world', operation: 'hello' },
    { id: 'arithmetic', operation: 'translate', className: 'Arithmetic', wasmBase64: arithmetic.toString('base64'), inputBytes: arithmetic.length, inputSha256: sha256(arithmetic) },
    { id: 'clang', operation: 'translate', className: 'Algorithms', wasmBase64: clang.toString('base64'), inputBytes: clang.length, inputSha256: sha256(clang) },
    { id: 'invalid-input', operation: 'translate', className: 'Invalid', wasmBase64: invalid.toString('base64'), inputBytes: invalid.length, inputSha256: sha256(invalid) }
  ];
}

function translateScenarios() {
  const arithmetic = readFileSync(join(root, 'samples', 'Smoke', 'Arithmetic.wasm'));
  const changedArithmetic = Buffer.from(arithmetic);
  const constant = Buffer.from([0x41, 0x80, 0x80, 0x80, 0x80, 0x78]);
  const constantOffset = changedArithmetic.indexOf(constant);
  if (constantOffset < 0 || changedArithmetic.indexOf(constant, constantOffset + 1) >= 0)
    throw new Error('Arithmetic fixture does not contain one unique i32.const -2147483648 encoding.');
  Buffer.from([0x41, 0xab, 0x80, 0x80, 0x80, 0x00]).copy(changedArithmetic, constantOffset);
  const clang = readFileSync(join(root, 'samples', 'CAlgorithms', 'Algorithms.wasm'));
  const host = readFileSync(hostAbiWasm());
  const invalid = Buffer.from([0, 97, 115, 109, 1, 0, 0, 0, 255]);
  const scenario = (id, bytes, className) => ({ id, operation: 'translate-sources', className,
    wasmBase64: bytes.toString('base64'), inputBytes: bytes.length, inputSha256: sha256(bytes) });
  return [
    scenario('arithmetic-repeat-1', arithmetic, 'Arithmetic'),
    scenario('arithmetic-repeat-2', arithmetic, 'Arithmetic'),
    scenario('arithmetic-changed-class', arithmetic, 'ArithmeticChanged'),
    scenario('arithmetic-changed-bytes', changedArithmetic, 'ArithmeticChangedBytes'),
    scenario('clang', clang, 'Algorithms'),
    scenario('host', host, 'HostAbi'),
    scenario('invalid', invalid, 'Invalid')
  ];
}

function parseReferenceOutput(output) {
  for (const line of output.trim().split(/\r?\n/).reverse()) {
    try {
      const value = JSON.parse(line);
      if (value?.protocol === 1 && !value.stream) return value;
    } catch {
      // Runtime diagnostics are retained in the log; keep looking for the protocol line.
    }
  }
  throw new Error('The guest did not emit a reference protocol result.');
}

function parseBundleOutput(output, streamId) {
  const sources = [];
  const sourceByName = new Map();
  let response = null;
  let streamError = null;
  for (const line of output.trim().split(/\r?\n/)) {
    try {
      const value = JSON.parse(line);
      if (value?.protocol === 1 && value.stream && value.id === streamId) {
        if (value.error) streamError = value.error;
        if (value.source) {
          const existing = sourceByName.get(value.source.Name);
          if (existing) existing.Text += value.source.Text;
          else {
            sourceByName.set(value.source.Name, value.source);
            sources.push(value.source);
          }
        }
      } else if (value?.protocol === 1) response = value;
    } catch {
      // Runtime diagnostics are retained in the log; keep looking for protocol lines.
    }
  }
  if (!response) throw new Error('The guest did not emit a reference protocol result.');
  if (streamError) throw new Error(streamError);
  return { response, sources };
}

function reference() {
  const manifest = verifyBundle();
  const main = join(bundleRoot, 'main.mjs');
  if (!existsSync(main)) throw new Error('Bundle is missing main.mjs.');
  const host = tool('node');
  if (host.version !== `v${profile.node}`) throw new Error(`Node ${profile.node} is required; selected Node is ${host.version ?? 'missing'}.`);
  const request = { protocol: 1, scenarios: requestScenarios() };
  const result = run(process.execPath, [main], {
    cwd: bundleRoot,
    env: { ...process.env, SELF_HOSTING_REQUEST: Buffer.from(JSON.stringify(request)).toString('base64url') }
  });
  const response = parseReferenceOutput(result.output);
  const byId = new Map(response.results?.map(item => [item.id, item]) ?? []);
  const hello = byId.get('hello-world');
  if (hello?.output !== 'Hello, World!') throw new Error('Hello World reference result differed.');
  for (const [id, className] of [['arithmetic', 'Arithmetic'], ['clang', 'Algorithms']]) {
    const item = byId.get(id);
    if (!item?.output?.startsWith('OK\n') || !item.output.includes(`class @${className}`)) {
      throw new Error(`${id} guest translation did not return the expected C# text.`);
    }
  }
  if (!byId.get('invalid-input')?.output?.startsWith('ERROR:')) {
    throw new Error('Invalid input did not return a conversion diagnostic.');
  }
  if (!result.output.includes('Hello, World!\n42'))
    throw new Error('Managed Main did not print the SH-10 Hello World probe.');
  const record = {
    schemaVersion: 1,
    milestone: 'SH-01',
    profile: profile.profile,
    bundleSha256: manifest.bundleSha256,
    host: { node: host, command: 'official browser-wasm AppBundle/main.mjs' },
    request: { protocol: request.protocol, scenarios: request.scenarios.map(({ id, operation, className, inputBytes, inputSha256 }) => ({ id, operation, className, inputBytes, inputSha256 })) },
    results: response.results,
    managedMain: { stdout: 'Hello, World!\n42\n', exitCode: 0 }
  };
  json(join(artifactRoot, 'reference-results.json'), record);
  writeFileSync(join(artifactRoot, 'reference.log'), result.output);
  abiReference();
  console.log(`Reference passed for Hello World, Arithmetic, Clang, and invalid input; bundle ${manifest.bundleSha256}.`);
}

function loadSh09Contract(manifest) {
  const contractPath = join(root, 'docs', 'self-hosting', 'SH-09-imports.json');
  if (!existsSync(contractPath)) throw new Error(`SH-09 import contract is missing: ${contractPath}`);
  const contract = JSON.parse(readFileSync(contractPath, 'utf8'));
  const runtime = runtimeEntry(manifest);
  const runtimePath = join(bundleRoot, ...runtime.path.split('/'));
  if (runtime.path !== contract.runtimePath) throw new Error(`SH-09 contract targets ${contract.runtimePath}, not ${runtime.path}.`);
  const runtimeSha256 = sha256File(runtimePath);
  if (runtimeSha256 !== contract.runtimeSha256)
    throw new Error(`SH-09 runtime hash differs: contract ${contract.runtimeSha256}, bundle ${runtimeSha256}.`);
  const inventoryPath = join(artifactRoot, 'inventory.json');
  if (!existsSync(inventoryPath)) throw new Error(`Runtime inventory is missing. Run inventory first: ${inventoryPath}`);
  const inventory = JSON.parse(readFileSync(inventoryPath, 'utf8'));
  const moduleInventory = inventory.wasm?.find(item => item.path === runtime.path);
  if (!moduleInventory) throw new Error(`Inventory does not contain ${runtime.path}.`);
  const supported = new Map((contract.supportedImports ?? []).map(item => [`${item.module}\0${item.name}`, item]));
  if (contract.defaultDisposition !== 'reject') throw new Error('SH-09 contract must use reject as its default disposition.');
  const inventoryKeys = new Set();
  const imports = moduleInventory.imports.map((entry, index) => {
    const match = /^func\[(\d+)\] sig=(\d+) <(.+)> <- (.+)$/.exec(entry);
    if (!match) throw new Error(`Cannot parse runtime import ${index}: ${entry}`);
    const separator = match[3].lastIndexOf('.');
    if (separator <= 0) throw new Error(`Cannot split runtime import ${index}: ${entry}`);
    const module = match[3].slice(0, separator);
    const name = match[3].slice(separator + 1);
    inventoryKeys.add(`${module}\0${name}`);
    const item = supported.get(`${module}\0${name}`);
    if (item && Number(item.signatureId) !== Number(match[2]))
      throw new Error(`SH-09 signature mismatch for ${module}.${name}: contract ${item.signatureId}, inventory ${match[2]}.`);
    return { index: Number(match[1]), signatureId: Number(match[2]), module, name,
      disposition: item ? 'supported' : contract.defaultDisposition, scenario: item?.scenario ?? null };
  });
  if (imports.some(item => item.disposition !== 'supported' && item.disposition !== 'reject'))
    throw new Error('SH-09 import contract contains an unknown disposition.');
  for (const key of supported.keys()) if (!inventoryKeys.has(key))
    throw new Error(`SH-09 contract contains an import absent from the runtime inventory: ${key.replace('\0', '.')}`);
  if (contract.bundleSha256 && contract.bundleSha256 !== manifest.bundleSha256)
    throw new Error(`SH-09 bundle hash differs: contract ${contract.bundleSha256}, bundle ${manifest.bundleSha256}.`);
  return { contract, imports, bundleSha256: manifest.bundleSha256, runtimeSha256 };
}

function hostAbiWasm() {
  const output = join(artifactRoot, 'host-abi.wasm');
  const wat = join(root, 'samples', 'SelfHosting', 'HostAbi.wat');
  const wat2wasm = savedWabtTool('wat2wasm') ?? commandPath('wat2wasm');
  if (!wat2wasm) {
    if (existsSync(output)) return output;
    throw new Error('Pinned wat2wasm is required for the SH-09 fixture.');
  }
  run(wat2wasm, [wat, '-o', output]);
  return output;
}

function readU32(bytes, offset) {
  return (bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24) >>> 0;
}

function writeU32(bytes, offset, value) {
  bytes[offset] = value & 0xff;
  bytes[offset + 1] = (value >>> 8) & 0xff;
  bytes[offset + 2] = (value >>> 16) & 0xff;
  bytes[offset + 3] = (value >>> 24) & 0xff;
}

function abiReference() {
  const fixture = hostAbiWasm();
  const bytes = readFileSync(fixture);
  let instance;
  const output = [];
  const imports = {
    env: {
      emscripten_get_now: () => 1234.5,
      mono_wasm_browser_entropy: (address, length) => {
        const memory = new Uint8Array(instance.exports.memory.buffer);
        for (let i = 0; i < length; i++) memory[address + i] = i + 1;
        return 0;
      }
    },
    wasi_snapshot_preview1: {
      fd_write: (fd, iovs, count, result) => {
        const memory = new Uint8Array(instance.exports.memory.buffer);
        let written = 0;
        for (let i = 0; i < count; i++) {
          const vector = iovs + i * 8;
          const address = readU32(memory, vector);
          const length = readU32(memory, vector + 4);
          output.push(...memory.slice(address, address + length));
          written += length;
        }
        writeU32(memory, result, written);
        return 0;
      }
    }
  };
  instance = new WebAssembly.Instance(new WebAssembly.Module(bytes), imports);
  const status = instance.exports.run();
  const memory = new Uint8Array(instance.exports.memory.buffer);
  const record = {
    schemaVersion: 1,
    milestone: 'SH-09',
    fixture: { path: 'samples/SelfHosting/HostAbi.wat', bytes: bytes.length, sha256: sha256(bytes) },
    results: {
      status,
      nowMs: instance.exports.now_ms(),
      entropy: Array.from(memory.slice(32, 36)),
      grownEntropy: Array.from(memory.slice(65536, 65540)),
      stdout: Buffer.from(output).toString('utf8'),
      written: readU32(memory, 12)
    }
  };
  json(join(artifactRoot, 'host-reference.json'), record);
  return record;
}

function csharpIdentifier(module, name) {
  const candidate = `import_${module}_${name}`;
  if (/^[A-Za-z_][A-Za-z0-9_]*$/.test(candidate) && !candidate.startsWith('__wasm_')) return candidate;
  return `wasm_export_${Buffer.from(candidate, 'utf8').toString('hex')}`;
}

function hostRunnerSource(includeFullRuntime = true) {
  const now = csharpIdentifier('env', 'emscripten_get_now');
  const entropy = csharpIdentifier('env', 'mono_wasm_browser_entropy');
  const write = csharpIdentifier('wasi_snapshot_preview1', 'fd_write');
  const fullRuntimeSource = includeFullRuntime
    ? 'var fullRuntime = new DotnetRuntime(new DotnetRuntime.Bindings());\n_ = fullRuntime.memory;\n_ = fullRuntime.__indirect_function_table;'
    : '';
  return `using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Wasm2Cs;
using Wasm2Cs.DotnetHost;
using Wasm2Cs.Generated;

var stdout = new List<byte>();
var environment = new HostEnvironment(
    wallClock: () => DateTimeOffset.FromUnixTimeMilliseconds(1234),
    entropy: bytes => { for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1); },
    stdout: bytes => stdout.AddRange(bytes));
WasmMemory? attachedMemory = null;
WasmMemory RequireMemory() => attachedMemory ?? throw new InvalidOperationException("WASM memory was not attached.");
var bindings = new HostAbi.Bindings
{
    ${now} = () => environment.WallClock().ToUnixTimeMilliseconds() + 0.5,
    ${entropy} = (address, length) =>
    {
        var memory = RequireMemory();
        var bytes = new byte[length];
        environment.Entropy(bytes);
        memory.WriteMemory(unchecked((uint)address), bytes);
        return 0;
    },
    ${write} = (fd, iovs, count, result) =>
    {
        var memory = RequireMemory();
        var bytes = new byte[1024];
        var length = HostEnvironment.ReadIovecs(memory, unchecked((uint)iovs), count, bytes);
        var written = environment.Write(fd, bytes, 0, length);
        HostEnvironment.WriteUInt32(memory, unchecked((uint)result), unchecked((uint)written));
        return 0;
    }
};
var module = new HostAbi(bindings);
attachedMemory = module.memory;
var status = module.run();
var memory = RequireMemory();
var actual = new
{
    status,
    nowMs = module.now_ms(),
    entropy = memory.ReadMemory(32, 4).Select(value => (int)value).ToArray(),
    grownEntropy = memory.ReadMemory(65536, 4).Select(value => (int)value).ToArray(),
    stdout = Encoding.UTF8.GetString(stdout.ToArray()),
    written = (int)HostEnvironment.ReadUInt32(memory, 12)
};
${fullRuntimeSource}
Console.WriteLine("HOST_ABI_RESULT:" + JsonSerializer.Serialize(actual));
`;
}

function helloRunnerSource(boot, managed = null, translate = null, options = null) {
  const unity = options?.host === 'unity';
  const managedProbeWrapper = JSON.stringify(managed?.probeWrapper ?? '');
  const managedReentryWrapper = JSON.stringify(managed?.reentryWrapper ?? '');
  const managedThrowWrapper = JSON.stringify(managed?.throwWrapper ?? '');
  const translateWrapper = JSON.stringify(translate?.wrapper ?? '');
  const translationRequests = translate?.requests ?? [{ id: '', operation: '', wasmBase64: '', className: '' }];
  const translationRequestSource = `var translationRequests = new[] { ${translationRequests.map(request =>
    `new { id = ${JSON.stringify(request.id)}, wasmBase64 = ${JSON.stringify(request.wasmBase64)}, className = ${JSON.stringify(request.className)} }`).join(', ')} };`;
  const assemblyLines = boot.assemblies.map(assembly => {
    const name = assembly.virtualPath ?? assembly.name;
    const path = JSON.stringify(join(bundleRoot, '_framework', name));
    const bytes = unity
      ? `UnitySelfHostAdapter.LoadAssembly(${JSON.stringify(name)})`
      : `File.ReadAllBytes(${path})`;
    const condition = name === 'System.Private.CoreLib.dll'
      ? 'scenario != "missing-corelib"'
      : name === boot.profile.mainAssemblyName
        ? 'scenario != "missing-selfhosting" && scenario != "translate-missing-selfhosting"'
        : name === 'Wasm2Cs.dll'
          ? 'scenario != "missing-wasm2cs" && scenario != "translate-missing-wasm2cs"'
          : 'true';
    if (name === boot.profile.mainAssemblyName) {
      return `var selfHostingData = ${bytes};
 if (scenario == "corrupt-selfhosting") selfHostingData[0] ^= 0xff;
 if (${condition}) assemblies.Add(new MonoAssembly(${JSON.stringify(name)}, selfHostingData));`;
    }
    if (name === 'Wasm2Cs.dll') {
      return `var wasm2csData = ${bytes};
 if (scenario == "corrupt-wasm2cs" || scenario == "translate-corrupt-wasm2cs") wasm2csData[0] ^= 0xff;
 if (${condition}) assemblies.Add(new MonoAssembly(${JSON.stringify(name)}, wasm2csData));`;
    }
    return `if (${condition}) assemblies.Add(new MonoAssembly(${JSON.stringify(name)}, ${bytes}));`;
  }).join('\n');
  const scenarioLine = unity
    ? 'var scenario = UnitySelfHostAdapter.Scenario();'
    : 'var scenario = Environment.GetEnvironmentVariable("SH10_SCENARIO") ?? "positive";';
  const readGlobals = unity
    ? 'object[] ReadGlobals() => Array.Empty<object>();'
    : `object[] ReadGlobals() => typeof(DotnetRuntime).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
      .Where(field => field.Name.StartsWith("__wasm_G", StringComparison.Ordinal))
      .OrderBy(field => field.Name)
      .Select(field => field.GetValue(RequireRuntime()) ?? "null")
      .ToArray();`;
  const unityRecord = unity ? 'UnitySelfHostAdapter.WriteTranslation(translationRequest.id, output);' : '';
  const exitCode = `scenario == "positive" || scenario == "managed" || scenario == "translate"
       ? failure == null && managedReturn == 0 && boot?.ExitCode == 0 ? 0 : 1
       : failure == null ? 1 : 0`;
  const resultEmit = unity
    ? `UnitySelfHostAdapter.WriteOutcome(scenario, managedReturn, boot?.ExitCode, boot?.State.ToString() ?? (failure == null ? null : "Failed"), failure == null ? null : failure.GetType().FullName, failure == null ? null : failure.Message, startupDurationMs, executionDurationMs, runtime == null ? 0 : runtime.memory.CurrentPages, GC.GetTotalMemory(false));
   UnitySelfHostAdapter.Exit(${exitCode});`
    : `Console.WriteLine("SH10_RESULT:" + JsonSerializer.Serialize(new
  {
       scenario,
       stdout = Encoding.UTF8.GetString(stdout.ToArray()),
       stderr = Encoding.UTF8.GetString(stderr.ToArray()),
       managedReturn,
      exitCode = boot?.ExitCode,
       requestedExit,
       requestedAbort,
       managedProbeResults,
        managedThrow,
        translatedResults,
        reentryStatus,
       memoryIdentity = runtime == null ? 0 : RuntimeHelpers.GetHashCode(runtime.memory),
       tableIdentity = runtime == null ? 0 : RuntimeHelpers.GetHashCode(runtime.__indirect_function_table),
       runtimeIdentity = runtime == null ? 0 : RuntimeHelpers.GetHashCode(runtime),
       globalInitial,
       globalFinal,
       hostState = new { fileMarker = hostFileMarker, callbacks = hostCallbacks, exited = environment.HasExited, exitCode = environment.ExitCode },
       runtimeTrace,
       linearPages = runtime?.memory.CurrentPages ?? 0,
       linearMaximumBytes = runtime == null ? 0 : runtime.memory.HostMaximumPages * 65536,
       declaredMaximumPages = runtime?.memory.DeclaredMaximumPages,
       outerGcHeap = GC.GetTotalMemory(false),
       outerGcAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
       outerWorkingSet = Process.GetCurrentProcess().WorkingSet64,
       outerPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
       startupDurationMs,
       executionDurationMs,
        state = boot?.State.ToString() ?? (failure == null ? null : "Failed"),
      failure = failure == null ? null : new { type = failure.GetType().FullName, message = failure.Message },
      phases = boot?.PhaseLog.ToArray() ?? Array.Empty<string>()
  }));
   Environment.ExitCode = ${exitCode};`;
  const propertyLines = Object.entries(boot.properties).map(([key, value]) =>
    `    new KeyValuePair<string, string>(${JSON.stringify(key)}, ${JSON.stringify(String(value).toLowerCase())})`).join(',\n');
  const now = csharpIdentifier('env', 'emscripten_get_now');
  const dateNow = csharpIdentifier('env', 'emscripten_date_now');
  const nowRes = csharpIdentifier('env', 'emscripten_get_now_res');
  const monotonic = csharpIdentifier('env', '_emscripten_get_now_is_monotonic');
  const entropy = csharpIdentifier('env', 'mono_wasm_browser_entropy');
  const pid = csharpIdentifier('env', 'mono_wasm_process_current_pid');
  const heapMax = csharpIdentifier('env', 'emscripten_get_heap_max');
  const resizeHeap = csharpIdentifier('env', 'emscripten_resize_heap');
  const assertFail = csharpIdentifier('env', '__assert_fail');
  const abort = csharpIdentifier('env', 'abort');
  const exit = csharpIdentifier('env', 'exit');
  const forceExit = csharpIdentifier('env', 'emscripten_force_exit');
  const fdWrite = csharpIdentifier('wasi_snapshot_preview1', 'fd_write');
  const fdRead = csharpIdentifier('wasi_snapshot_preview1', 'fd_read');
  const fdClose = csharpIdentifier('wasi_snapshot_preview1', 'fd_close');
  const fdStat = csharpIdentifier('wasi_snapshot_preview1', 'fd_fdstat_get');
  const fdSync = csharpIdentifier('wasi_snapshot_preview1', 'fd_sync');
  const envSizes = csharpIdentifier('wasi_snapshot_preview1', 'environ_sizes_get');
  const envGet = csharpIdentifier('wasi_snapshot_preview1', 'environ_get');
  const getcwd = csharpIdentifier('env', '__syscall_getcwd');
  const fcntl64 = csharpIdentifier('env', '__syscall_fcntl64');
  const openat = csharpIdentifier('env', '__syscall_openat');
  const scheduleBackground = csharpIdentifier('env', 'schedule_background_exec');
  const traceLogger = csharpIdentifier('env', 'mono_wasm_trace_logger');
  const assignments = `    ${now} = () => environment.WallClock().ToUnixTimeMilliseconds(),
    ${dateNow} = () => environment.WallClock().ToUnixTimeMilliseconds(),
    ${nowRes} = () => 1.0,
    ${monotonic} = () => 1,
    ${pid} = () => 1,
    ${heapMax} = () => RequireMemory().Size,
    ${resizeHeap} = bytes =>
    {
        var memory = RequireMemory();
        var pages = (int)(((long)bytes + 65535L) / 65536L);
        var delta = pages - memory.CurrentPages;
        return delta < 0 ? 0 : (memory.Grow(delta) >= 0 ? 1 : 0);
    },
    ${entropy} = (address, length) =>
    {
        var bytes = new byte[length];
        environment.Entropy(bytes);
        RequireMemory().WriteMemory(unchecked((uint)address), bytes);
        return 0;
    },
     ${assertFail} = (message, file, line, functionName) => throw new InvalidOperationException($"WASM assertion failed: {HostEnvironment.ReadString(RequireMemory(), unchecked((uint)message))} at {HostEnvironment.ReadString(RequireMemory(), unchecked((uint)file))}:{line}"),
     ${abort} = () => { requestedAbort = true; throw new InvalidOperationException("WASM abort requested."); },
    ${exit} = code => requestedExit = code,
     ${forceExit} = code => requestedExit = code,
     ${scheduleBackground} = () => { },
     ${traceLogger} = (domain, level, message, fatal, userData) => runtimeTrace.Add(new
     {
         domain = HostEnvironment.ReadString(RequireMemory(), unchecked((uint)domain)),
         level = HostEnvironment.ReadString(RequireMemory(), unchecked((uint)level)),
         message = HostEnvironment.ReadString(RequireMemory(), unchecked((uint)message)),
         fatal
     }),
    ${envSizes} = (count, bufferSize) =>
    {
        var memory = RequireMemory();
        var entries = environment.Environment.Select(pair => pair.Key + "=" + pair.Value).ToArray();
        HostEnvironment.WriteUInt32(memory, unchecked((uint)count), unchecked((uint)entries.Length));
        HostEnvironment.WriteUInt32(memory, unchecked((uint)bufferSize), unchecked((uint)entries.Sum(value => Encoding.UTF8.GetByteCount(value) + 1)));
        return 0;
    },
    ${envGet} = (environmentPointers, buffer) =>
    {
        var memory = RequireMemory();
        var offset = unchecked((uint)buffer);
        var index = 0;
        foreach (var pair in environment.Environment)
        {
            var bytes = Encoding.UTF8.GetBytes(pair.Key + "=" + pair.Value + "\\0");
            HostEnvironment.WriteUInt32(memory, unchecked((uint)(environmentPointers + index * 4)), offset);
            memory.WriteMemory(offset, bytes);
            offset += unchecked((uint)bytes.Length);
            index++;
        }
        HostEnvironment.WriteUInt32(memory, unchecked((uint)(environmentPointers + index * 4)), 0);
        return 0;
    },
    ${getcwd} = (buffer, size) =>
    {
        if (size == 0) return -28;
        var bytes = Encoding.UTF8.GetBytes("/\\0");
        if (size < bytes.Length) return -68;
        RequireMemory().WriteMemory(unchecked((uint)buffer), bytes);
        return bytes.Length;
    },
     ${fcntl64} = (fd, command, varargs) => command == 0 || command == 1030 ? fd : command >= 1 && command <= 4 ? 0 : -28,
    ${openat} = (directory, path, flags, mode) =>
    {
        var value = HostEnvironment.ReadString(RequireMemory(), unchecked((uint)path));
        if (value.EndsWith("stdout", StringComparison.Ordinal)) return 1;
        if (value.EndsWith("stderr", StringComparison.Ordinal)) return 2;
        return -2;
    },
     ${fdWrite} = (fd, iovs, count, result) =>
     {
         var memory = RequireMemory();
         var bytes = new byte[4096];
         var length = HostEnvironment.ReadIovecs(memory, unchecked((uint)iovs), count, bytes);
         var written = environment.Write(fd, bytes, 0, length);
         HostEnvironment.WriteUInt32(memory, unchecked((uint)result), unchecked((uint)written));
         if (reentryArmed && !reentryTriggered && reentry != null)
         {
             reentryTriggered = true;
             reentryStatus = reentry();
         }
         return 0;
     },
    ${fdRead} = (fd, iovs, count, result) =>
    {
        var memory = RequireMemory();
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            var vector = HostEnvironment.ReadIovec(memory, unchecked((uint)(iovs + i * 8)));
            var amount = environment.Read(fd, memory, vector.Address, checked((int)vector.Length));
            total += amount;
            if (amount != vector.Length) break;
        }
        HostEnvironment.WriteUInt32(memory, unchecked((uint)result), unchecked((uint)total));
        return 0;
    },
     ${fdStat} = (fd, result) =>
     {
         if (fd >= 3) return 8;
         RequireMemory().WriteByte(unchecked((uint)result), 2);
         RequireMemory().WriteByte(unchecked((uint)(result + 1)), 0);
         RequireMemory().WriteByte(unchecked((uint)(result + 2)), 0);
         RequireMemory().WriteByte(unchecked((uint)(result + 3)), 0);
         return 0;
     },
     ${fdSync} = fd => fd < 3 ? 0 : 8,
      ${fdClose} = fd => fd < 3 || environment.Close(fd) ? 0 : 8`;
   let source = `using System;
using System.Collections.Generic;
 using System.IO;
 using System.Diagnostics;
 using System.Linq;
 using System.Reflection;
 using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Wasm2Cs;
using Wasm2Cs.DotnetHost;
using Wasm2Cs.Generated;

 var instanceNumber = 1;
 var stdout = new List<byte>();
 var stderr = new List<byte>();
 var environment = new HostEnvironment(
     wallClock: () => DateTimeOffset.FromUnixTimeMilliseconds(1234),
     entropy: bytes => { for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + 1); },
     stdout: bytes => stdout.AddRange(bytes),
     stderr: bytes => stderr.AddRange(bytes));
  ${scenarioLine}
 var hostFileMarker = false;
 var hostCallbacks = 0;
 if (scenario == "managed")
 {
     if (instanceNumber == 1)
     {
         environment.SetFile("/sh11-instance-one", new byte[] { 1, 2, 3 });
         environment.Enqueue(() => { });
     }
     hostFileMarker = environment.OpenFile("/sh11-instance-one") >= 3;
     hostCallbacks = environment.Pump();
 }
 var requestedExit = (int?)null;
 var requestedAbort = false;
 var reentryArmed = false;
 var reentryTriggered = false;
 var reentryStatus = 0;
 Func<int>? reentry = null;
  var managedProbeResults = new List<object>();
  var runtimeTrace = new List<object>();
  var translatedResults = new List<object>();
  object? managedThrow = null;
 WasmMemory? attachedMemory = null;
 DotnetRuntime? runtime = null;
 object[] globalInitial = Array.Empty<object>();
 object[] globalFinal = Array.Empty<object>();
 WasmMemory RequireMemory() => attachedMemory ?? throw new InvalidOperationException("WASM memory was not attached.");
  DotnetRuntime RequireRuntime() => runtime ?? throw new InvalidOperationException("runtime was not constructed");
   ${readGlobals}
 // Unassigned imports stay null so the generated runtime rejects unexpected calls.
 var bindings = new DotnetRuntime.Bindings
 {
 ${assignments}
 };
 if (scenario == "missing-import") bindings.${fdWrite} = null!;
 runtime = new DotnetRuntime(bindings);
 attachedMemory = runtime.memory;
 globalInitial = ReadGlobals();
 reentry = () => InvokeManagedWrapper(${managedReentryWrapper});
 var exports = new MonoBoot.NativeExports
{
    CallConstructors = () => { RequireRuntime().wasm_export___wasm_call_ctors(); return 0; },
    Malloc = length => unchecked((uint)RequireRuntime().malloc(length)),
    Free = address => RequireRuntime().free(unchecked((int)address)),
     AddAssembly = (name, data, size) => RequireRuntime().mono_wasm_add_assembly(unchecked((int)name), unchecked((int)data), size),
     ParseRuntimeOptions = (count, options) => RequireRuntime().mono_wasm_parse_runtime_options(count, unchecked((int)options)),
     LoadRuntime = (debug, count, keys, values) => RequireRuntime().mono_wasm_load_runtime(debug, count, unchecked((int)keys), unchecked((int)values)),
    ConfigureArgs = (argc, argv) => RequireRuntime().mono_wasm_set_main_args(argc, unchecked((int)argv)),
     InvokeMain = () =>
     {
         if (scenario == "native-exit")
         {
             RequireRuntime().mono_wasm_exit(9);
             return 0;
         }
         uint assemblyName = 0;
         uint namespaceName = 0;
         uint className = 0;
         uint methodName = 0;
         uint arguments = 0;
         try
         {
             assemblyName = MonoPtr("${boot.profile.mainAssemblyName}");
             namespaceName = MonoPtr("");
             className = MonoPtr("SelfHostingDriver");
             methodName = MonoPtr("${boot.managedEntryWrapper}");
             var assembly = RequireRuntime().mono_wasm_assembly_load(unchecked((int)assemblyName));
             if (assembly == 0) throw new InvalidOperationException("managed startup assembly was not found.");
             var klass = RequireRuntime().mono_wasm_assembly_find_class(assembly, unchecked((int)namespaceName), unchecked((int)className));
             if (klass == 0) throw new InvalidOperationException("managed startup class was not found.");
             var method = RequireRuntime().mono_wasm_assembly_find_method(klass, unchecked((int)methodName), -1);
             if (method == 0) throw new InvalidOperationException("managed startup method was not found.");
             arguments = AllocateNative(64);
             RequireMemory().WriteMemory(arguments, new byte[64]);
             RequireRuntime().mono_wasm_invoke_jsexport(method, unchecked((int)arguments));
             var exceptionType = ReadUInt32Unchecked(unchecked(arguments + 12));
             if (exceptionType != 0) throw new InvalidOperationException($"managed startup raised marshaled exception type {exceptionType}");
             var resultType = ReadUInt32Unchecked(unchecked(arguments + 44));
             if (resultType != 7) throw new InvalidOperationException($"managed startup returned marshaled type {resultType}, expected Int32");
             return unchecked((int)ReadUInt32Unchecked(unchecked(arguments + 32)));
         }
         finally
         {
             if (arguments != 0) RequireRuntime().free(unchecked((int)arguments));
             if (methodName != 0) RequireRuntime().free(unchecked((int)methodName));
             if (className != 0) RequireRuntime().free(unchecked((int)className));
             if (namespaceName != 0) RequireRuntime().free(unchecked((int)namespaceName));
             if (assemblyName != 0) RequireRuntime().free(unchecked((int)assemblyName));
         }
     },
     // mono_wasm_exit records the native exit and deliberately traps after
     // calling emscripten_force_exit. Only that expected boundary is normal.
     Exit = code =>
     {
         try
         {
             _ = RequireRuntime().mono_wasm_exit(code);
             throw new InvalidOperationException("mono_wasm_exit returned without its native exit trap.");
         }
         catch (DotnetRuntime.TrapException exception) when (requestedExit == code && exception.Kind == DotnetRuntime.TrapKind.Unreachable)
         {
         }
     }
 };
 uint AllocateNative(int length)
 {
     var address = unchecked((uint)RequireRuntime().malloc(length));
     if (address == 0 || (ulong)address + (ulong)length > (ulong)RequireMemory().Size)
         throw new InvalidOperationException($"Runtime malloc returned an invalid address for {length} bytes: {address}.");
     return address;
 }
 uint MonoPtr(string value)
 {
     var bytes = Encoding.UTF8.GetBytes(value + "\\0");
     var address = AllocateNative(bytes.Length);
    RequireMemory().WriteMemory(address, bytes);
    return address;
  }
  uint ReadUInt32Unchecked(uint address) => BitConverter.ToUInt32(RequireMemory().ReadMemory(address, 4), 0);
  int InvokeManagedWrapper(string wrapper)
  {
      uint assemblyName = 0;
      uint namespaceName = 0;
      uint className = 0;
      uint methodName = 0;
      uint arguments = 0;
      try
      {
          assemblyName = MonoPtr("${boot.profile.mainAssemblyName}");
          namespaceName = MonoPtr("");
          className = MonoPtr("SelfHostingDriver");
          methodName = MonoPtr(wrapper);
          var assembly = RequireRuntime().mono_wasm_assembly_load(unchecked((int)assemblyName));
          if (assembly == 0) throw new InvalidOperationException("managed export assembly was not found.");
          var klass = RequireRuntime().mono_wasm_assembly_find_class(assembly, unchecked((int)namespaceName), unchecked((int)className));
          if (klass == 0) throw new InvalidOperationException("managed export class was not found.");
          var method = RequireRuntime().mono_wasm_assembly_find_method(klass, unchecked((int)methodName), -1);
          if (method == 0) throw new InvalidOperationException("managed export method was not found: " + wrapper);
          arguments = AllocateNative(64);
          RequireMemory().WriteMemory(arguments, new byte[64]);
          RequireRuntime().mono_wasm_invoke_jsexport(method, unchecked((int)arguments));
          var exceptionType = ReadUInt32Unchecked(unchecked(arguments + 12));
          if (exceptionType != 0) throw new InvalidOperationException($"managed export '{wrapper}' raised marshaled exception type {exceptionType}");
          var resultType = ReadUInt32Unchecked(unchecked(arguments + 44));
          if (resultType != 7) throw new InvalidOperationException($"managed export '{wrapper}' returned marshaled type {resultType}, expected Int32");
          return unchecked((int)ReadUInt32Unchecked(unchecked(arguments + 32)));
      }
      finally
      {
          if (arguments != 0) RequireRuntime().free(unchecked((int)arguments));
          if (methodName != 0) RequireRuntime().free(unchecked((int)methodName));
          if (className != 0) RequireRuntime().free(unchecked((int)className));
          if (namespaceName != 0) RequireRuntime().free(unchecked((int)namespaceName));
          if (assemblyName != 0) RequireRuntime().free(unchecked((int)assemblyName));
      }
  }
  uint WriteUtf16(string value)
  {
      var bytes = Encoding.Unicode.GetBytes(value);
      var address = AllocateNative(checked(bytes.Length + 2));
      RequireMemory().WriteMemory(address, bytes);
      return address;
  }
  string ReadManagedString(uint rootAddress)
  {
      var scratch = AllocateNative(12);
      try
      {
          RequireMemory().WriteMemory(scratch, new byte[12]);
          RequireRuntime().mono_wasm_string_get_data_ref(unchecked((int)rootAddress), unchecked((int)scratch), unchecked((int)(scratch + 4u)), unchecked((int)(scratch + 8u)));
          var chars = ReadUInt32Unchecked(scratch);
          var byteLength = ReadUInt32Unchecked(scratch + 4u);
          if (byteLength == 0) return string.Empty;
          return Encoding.Unicode.GetString(RequireMemory().ReadMemory(chars, checked((int)byteLength)));
      }
      finally
      {
          RequireRuntime().free(unchecked((int)scratch));
      }
  }
  string InvokeStringWrapper(string wrapper, string[] values)
  {
      var arguments = AllocateNative(checked((values.Length + 2) * 32));
      var buffers = new List<uint>();
      uint inputRootName = 0;
      var inputRootRegistered = false;
      var inputRootAddress = checked(arguments + 64u);
      try
      {
          RequireMemory().WriteMemory(arguments, new byte[checked((values.Length + 2) * 32)]);
          inputRootName = MonoPtr("SH-12 string arguments");
          if (RequireRuntime().mono_wasm_register_root(unchecked((int)inputRootAddress), checked(values.Length * 32), unchecked((int)inputRootName)) == 0)
              throw new InvalidOperationException("Could not register SH-12 string argument roots.");
          inputRootRegistered = true;
          for (var index = 0; index < values.Length; index++)
          {
              var buffer = WriteUtf16(values[index]);
              buffers.Add(buffer);
              var slot = checked(arguments + 64u + (uint)(index * 32));
              RequireMemory().WriteByte(slot + 12, 15);
              RequireRuntime().mono_wasm_string_from_utf16_ref(unchecked((int)buffer), values[index].Length, unchecked((int)slot));
          }
          var assemblyName = MonoPtr("${boot.profile.mainAssemblyName}");
          var namespaceName = MonoPtr("");
          var className = MonoPtr("SelfHostingDriver");
          var methodName = MonoPtr(wrapper);
          try
          {
              var assembly = RequireRuntime().mono_wasm_assembly_load(unchecked((int)assemblyName));
              if (assembly == 0) throw new InvalidOperationException("managed export assembly was not found.");
              var klass = RequireRuntime().mono_wasm_assembly_find_class(assembly, unchecked((int)namespaceName), unchecked((int)className));
              if (klass == 0) throw new InvalidOperationException("managed export class was not found.");
              var method = RequireRuntime().mono_wasm_assembly_find_method(klass, unchecked((int)methodName), -1);
              if (method == 0) throw new InvalidOperationException("managed export method was not found: " + wrapper);
              RequireRuntime().mono_wasm_invoke_jsexport(method, unchecked((int)arguments));
              var exceptionType = ReadUInt32Unchecked(arguments + 12);
              if (exceptionType != 0) throw new InvalidOperationException($"managed export '{wrapper}' raised marshaled exception type {exceptionType}");
              var result = checked(arguments + 32u);
              var resultType = RequireMemory().ReadByte(result + 12);
              if (resultType != 15) throw new InvalidOperationException($"managed export '{wrapper}' returned marshaled type {resultType}, expected String");
              return ReadManagedString(result);
          }
          finally
          {
              RequireRuntime().free(unchecked((int)methodName));
              RequireRuntime().free(unchecked((int)className));
              RequireRuntime().free(unchecked((int)namespaceName));
              RequireRuntime().free(unchecked((int)assemblyName));
          }
      }
      finally
      {
          if (inputRootRegistered) RequireRuntime().mono_wasm_deregister_root(unchecked((int)inputRootAddress));
          if (inputRootName != 0) RequireRuntime().free(unchecked((int)inputRootName));
          for (var index = buffers.Count - 1; index >= 0; index--) RequireRuntime().free(unchecked((int)buffers[index]));
          RequireRuntime().free(unchecked((int)arguments));
      }
  }
  string? ReadProbePayload()
  {
      var lines = Encoding.UTF8.GetString(stdout.ToArray()).Split(new[] { "\\r\\n", "\\n" }, StringSplitOptions.None);
      for (var index = lines.Length - 1; index >= 0; index--)
          if (lines[index].StartsWith("SH11_PROBE:", StringComparison.Ordinal)) return lines[index].Substring("SH11_PROBE:".Length);
      return null;
  }
  var assemblies = new List<MonoAssembly>();
 ${assemblyLines}
 ${translationRequestSource}
 MonoBoot? boot = null;
 int? managedReturn = null;
 Exception? failure = null;
 double startupDurationMs = 0;
 double executionDurationMs = 0;
 try
 {
     if (assemblies.Count != ${boot.assemblies.length})
         throw new InvalidOperationException($"Managed assembly supply is incomplete: expected ${boot.assemblies.length}, received {assemblies.Count}.");
     var request = new MonoBootRequest(
         assemblies,
         Array.Empty<string>(),
          ${JSON.stringify(boot.profile.mainAssemblyName)},
          ${Number(boot.profile.debugLevel)},
          new KeyValuePair<string, string>[]
          {
  ${propertyLines}
          },
          new[] { "--no-jiterpreter-traces-enabled" });
      var startupStarted = Stopwatch.GetTimestamp();
      boot = new MonoBoot(RequireMemory(), exports, request);
      boot.Start();
      startupDurationMs = (Stopwatch.GetTimestamp() - startupStarted) * 1000.0 / Stopwatch.Frequency;
      var executionStarted = Stopwatch.GetTimestamp();
      managedReturn = boot.Run();
      if (scenario == "managed")
      {
          for (var invocation = 0; invocation < 3; invocation++)
          {
              reentryArmed = true;
              reentryTriggered = false;
              var status = InvokeManagedWrapper(${managedProbeWrapper});
              reentryArmed = false;
              managedProbeResults.Add(new { status, payload = ReadProbePayload() });
          }
          try
          {
              InvokeManagedWrapper(${managedThrowWrapper});
              managedThrow = new { thrown = false, type = "", message = "" };
          }
          catch (Exception exception)
          {
              managedThrow = new { thrown = true, type = exception.GetType().FullName, message = exception.Message };
          }
      }
      if (scenario == "translate")
      {
          foreach (var translationRequest in translationRequests)
          {
              var output = InvokeStringWrapper(${translateWrapper}, new[] { translationRequest.wasmBase64, translationRequest.className });
              translatedResults.Add(new { id = translationRequest.id, output });
              ${unityRecord}
          }
      }
      executionDurationMs = (Stopwatch.GetTimestamp() - executionStarted) * 1000.0 / Stopwatch.Frequency;
      globalFinal = ReadGlobals();
      boot.Exit(managedReturn.Value);
      if (scenario == "managed" && instanceNumber == 1) environment.Exit(17);
 }
 catch (Exception exception)
 {
     failure = exception;
 }
  ${resultEmit}
   `;
  if (!unity) return source;
  source = source
    .replace('using System.Reflection;\n', '')
    .replace('using System.Runtime.CompilerServices;\n', '')
    .replace('using System.Text.Json;\n', '');
  const bodyStart = source.indexOf(' var instanceNumber = 1;');
  if (bodyStart < 0) throw new Error('Unity runner source did not contain its instance body.');
  source = '#nullable enable\n' + source.slice(0, bodyStart)
    + '\npublic static class UnitySelfHostRunner\n{\n    public static void Run()\n    {'
    + source.slice(bodyStart)
    + '\n    }\n}\n';
  for (const forbidden of ['Transpiler', 'DynamicInvoke', 'WebAssembly', 'JsonSerializer', 'GetGCMemoryInfo', 'File.ReadAllBytes'])
    if (source.includes(forbidden)) throw new Error(`Unity runner must not reference ${forbidden}.`);
  if (!source.includes('new MonoBoot(') || !source.includes('UnitySelfHostAdapter.LoadAssembly'))
    throw new Error('Unity runner does not reuse MonoBoot and the file adapter.');
  return source;
}

function managedRunnerSource(boot, managed) {
  const source = helloRunnerSource(boot, managed);
  const marker = ' var instanceNumber = 1;';
  const bodyStart = source.indexOf(marker);
  if (bodyStart < 0) throw new Error('Managed runner source did not contain its instance body.');
  const prefix = source.slice(0, bodyStart);
  const body = source.slice(bodyStart).replace(marker, ' var instanceNumber = ++sh11Instance;');
  return `${prefix}var sh11Instance = 0;\nRunInstance();\nGC.Collect();\nGC.WaitForPendingFinalizers();\nGC.Collect();\nRunInstance();\nvoid RunInstance()\n{\n${body}\n}`;
}

function translateRunnerSource(boot, translate) {
  return helloRunnerSource(boot, null, translate);
}

function taggedJsonLines(output, tag) {
  return output.split(/\r?\n/).filter(line => line.startsWith(tag)).map(line => JSON.parse(line.slice(tag.length)));
}

function wrapperName(exports, method) {
  const key = exports.find(value => value.startsWith(method + '.'));
  const suffix = key?.slice(method.length + 1);
  if (!suffix) throw new Error(`Official WASM exports did not contain ${method}.<signature>.`);
  return `__Wrapper_${method}_${suffix}`;
}

function normalizeProbe(result) {
  if (!result || typeof result !== 'object') throw new Error('Managed probe returned a non-object result.');
  const normalized = { ...result };
  delete normalized.GuestHeap;
  return normalized;
}

function verifyProbeRuns(label, runs) {
  if (!Array.isArray(runs) || runs.length !== 3) throw new Error(`${label} returned ${runs?.length ?? 0} probe runs; expected 3.`);
  for (let index = 0; index < runs.length; index++) {
    const run = runs[index];
    if (run.status !== 0) throw new Error(`${label} probe ${index + 1} returned status ${run.status}.`);
    if (typeof run.result === 'string') run.result = JSON.parse(run.result);
    if (!run.result?.Passed) throw new Error(`${label} probe ${index + 1} reported failure: ${JSON.stringify(run.result)}.`);
    if (run.result.Invocation !== index + 1) throw new Error(`${label} probe invocation counter is ${run.result.Invocation}; expected ${index + 1}.`);
  }
  return runs.map(run => normalizeProbe(run.result));
}

function compareProbeRuns(normal, official, translated) {
  const expected = JSON.stringify(normal);
  if (JSON.stringify(official) !== expected) throw new Error('Official browser-WASM managed probes differ from normal .NET.');
  if (JSON.stringify(translated) !== expected) throw new Error('Translated Mono managed probes differ from normal .NET.');
}

function normalManagedResults(timeoutMs) {
  const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-sh11-dotnet-'));
  try {
    writeFileSync(join(directory, 'ManagedProbes.cs'), readFileSync(join(root, 'samples', 'SelfHosting', 'ManagedProbes.cs')));
    writeFileSync(join(directory, 'Program.cs'), `using System;\n\nfor (var i = 0; i < 3; i++) Console.WriteLine("SH11_PROBE:" + ManagedProbes.Run());\n`);
    const project = join(directory, 'ManagedReference.csproj');
    writeFileSync(project, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <InvariantGlobalization>true</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup><Compile Include="ManagedProbes.cs" /><Compile Include="Program.cs" /></ItemGroup>
</Project>
`);
    run('dotnet', ['build', project, '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
    const assembly = join(directory, 'bin', 'Release', 'net10.0', 'ManagedReference.dll');
    const started = process.hrtime.bigint();
    const execution = run('dotnet', [assembly], { allowFailure: true, timeoutMs });
    if (execution.status === null) throw new Error(`Normal .NET managed probe timed out: ${execution.error?.message ?? 'unknown timeout'}`);
    if (execution.status !== 0) throw new Error(`Normal .NET managed probe failed (exit ${execution.status}).\n${execution.output}`);
    const probes = taggedJsonLines(execution.output, 'SH11_PROBE:').map(result => ({ status: 0, result }));
    return { probes, durationMs: Number(process.hrtime.bigint() - started) / 1e6, output: execution.output };
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

function officialManagedResults(timeoutMs) {
  const invoked = invokeBundle({
    protocol: 1,
    scenarios: [
      { id: 'exports', operation: 'managed-exports' },
      { id: 'probe', operation: 'managed-probe', invocations: 3 },
      { id: 'throw', operation: 'managed-throw' }
    ]
  }, { timeoutMs });
  const exports = invoked.response.results?.find(item => item.id === 'exports')?.exports ?? [];
  const probe = invoked.response.results?.find(item => item.id === 'probe');
  const throwing = invoked.response.results?.find(item => item.id === 'throw');
  if (!probe || !throwing) throw new Error('Official browser-WASM managed probe response is incomplete.');
  return {
    exports,
    wrappers: {
      probe: wrapperName(exports, 'RunManagedProbe'),
      reentry: wrapperName(exports, 'ReenterManagedProbe'),
      throw: wrapperName(exports, 'ThrowManagedProbe')
    },
    probes: probe.runs,
    throwing,
    output: invoked.response
  };
}

function translatedManagedResults(boot, generated, wrappers, timeoutMs, gcHeapLimit) {
  const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-sh11-managed-'));
  try {
    for (const entry of generated.manifest.entries)
      writeFileSync(join(directory, entry.path), readFileSync(join(generated.generatedRoot, entry.path)));
    writeFileSync(join(directory, 'Program.cs'), managedRunnerSource(boot, {
      probeWrapper: wrappers.probe ?? wrappers.probeWrapper,
      reentryWrapper: wrappers.reentry ?? wrappers.reentryWrapper,
      throwWrapper: wrappers.throw ?? wrappers.throwWrapper
    }));
    const project = join(directory, 'Managed.csproj');
    writeFileSync(project, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="*.g.cs" />
    <Compile Include="Program.cs" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.Runtime', 'Wasm2Cs.Runtime.csproj'))}" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.DotnetHost', 'Wasm2Cs.DotnetHost.csproj'))}" />
  </ItemGroup>
</Project>
`);
    run('dotnet', ['build', project, '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
    const assembly = join(directory, 'bin', 'Release', 'net10.0', 'Managed.dll');
    const started = process.hrtime.bigint();
    const execution = run('dotnet', [assembly], {
      allowFailure: true,
      timeoutMs,
      env: { ...process.env, SH10_SCENARIO: 'managed', DOTNET_GCHeapHardLimit: `0x${gcHeapLimit.toString(16)}` }
    });
    if (execution.status === null) throw new Error(`Translated Mono managed probe timed out: ${execution.error?.message ?? 'unknown timeout'}`);
    const records = taggedJsonLines(execution.output, 'SH10_RESULT:');
    if (records.length !== 2) throw new Error(`Translated Mono runner emitted ${records.length} instance record(s); expected 2.\n${execution.output}`);
    if (execution.status !== 0) throw new Error(`Translated Mono managed probe failed (exit ${execution.status}).\n${execution.output}`);
    return { instances: records, durationMs: Number(process.hrtime.bigint() - started) / 1e6, output: execution.output };
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

function parseTranslatedSources(output) {
  if (typeof output !== 'string') throw new Error('Translation output was not a string.');
  if (output.startsWith('ERROR:')) return { error: output };
  let sources;
  try { sources = JSON.parse(output); } catch (error) { throw new Error(`Translation output was not valid JSON: ${error.message}`); }
  if (!Array.isArray(sources) || sources.length === 0) throw new Error('Translation returned no generated sources.');
  for (const source of sources) {
    if (!source || typeof source.Name !== 'string' || typeof source.Text !== 'string')
      throw new Error('Translation returned an invalid generated source.');
  }
  return { sources };
}

function sourceSummary(sources) {
  const text = Buffer.from(sources.map(source => source.Text).join(''), 'utf8');
  return { files: sources.length, bytes: text.length, sha256: sha256(text) };
}

function normalTranslationResults(scenarios, timeoutMs) {
  const cliProject = join(root, 'src', 'Wasm2Cs.Cli', 'Wasm2Cs.Cli.csproj');
  run('dotnet', ['build', cliProject, '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
  const cli = join(root, 'src', 'Wasm2Cs.Cli', 'bin', 'Release', 'net10.0', 'Wasm2Cs.Cli.dll');
  const results = {};
  for (const scenario of scenarios) {
    const input = Buffer.from(scenario.wasmBase64, 'base64');
    const path = join(tmpdir(), `wasm2cs-sh12-${process.pid}-${scenario.id}.wasm`);
    writeFileSync(path, input);
    try {
      const execution = run('dotnet', [cli, path, '--class-name', scenario.className, '--target-profile', 'portable-netstandard2.0'], {
        allowFailure: true,
        timeoutMs
      });
      results[scenario.id] = execution.status === 0 ? { text: execution.output, status: 0 } : { error: execution.output, status: execution.status };
    } finally {
      rmSync(path, { force: true });
    }
  }
  return results;
}

function officialTranslationResults(scenarios, timeoutMs) {
  const invoked = invokeBundle({ protocol: 1, scenarios: [{ id: 'exports', operation: 'managed-exports' }, ...scenarios] }, { timeoutMs });
  const exports = invoked.response.results?.find(item => item.id === 'exports')?.exports ?? [];
  const results = Object.fromEntries((invoked.response.results ?? []).filter(item => item.id !== 'exports').map(item => [item.id, item]));
  return { wrappers: { translate: wrapperName(exports, 'TranslateSourcesBase64') }, results, output: invoked.response };
}

function translatedTranslateResults(boot, generated, wrappers, scenarios, timeoutMs) {
  const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-sh12-translate-'));
  try {
    for (const entry of generated.manifest.entries)
      writeFileSync(join(directory, entry.path), readFileSync(join(generated.generatedRoot, entry.path)));
    const runnerSource = translateRunnerSource(boot, {
      wrapper: wrappers.translate,
      requests: scenarios
    });
    writeFileSync(join(directory, 'Program.cs'), runnerSource);
    const project = join(directory, 'Translate.csproj');
    const projectSource = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="*.g.cs" />
    <Compile Include="Program.cs" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.Runtime', 'Wasm2Cs.Runtime.csproj'))}" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.DotnetHost', 'Wasm2Cs.DotnetHost.csproj'))}" />
  </ItemGroup>
</Project>
`;
    const provenance = {
      outerEngine: /\bWebAssembly\b/.test(runnerSource) || projectSource.includes('Wasm2Cs.Cli'),
      outerTranslator: /\bTranspiler\b/.test(runnerSource) || projectSource.includes('Wasm2Cs.Cli')
    };
    if (provenance.outerEngine || provenance.outerTranslator)
      throw new Error('Translated SH-12 runner references an outer WASM engine or translator.');
    writeFileSync(project, projectSource);
    run('dotnet', ['build', project, '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
    const assembly = join(directory, 'bin', 'Release', 'net10.0', 'Translate.dll');
    const execute = scenario => {
      const result = run('dotnet', [assembly], {
        allowFailure: true,
        timeoutMs,
        env: { ...process.env, SH10_SCENARIO: scenario }
      });
      const records = taggedJsonLines(result.output, 'SH10_RESULT:');
      if (records.length !== 1) throw new Error(`Translated SH-12 runner emitted ${records.length} result records for ${scenario}.`);
      return { processExit: result.status, output: result.output, result: records[0] };
    };
    return {
      positive: execute('translate'),
      missingWasm2cs: execute('translate-missing-wasm2cs'),
      corruptWasm2cs: execute('translate-corrupt-wasm2cs'),
      provenance
    };
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

function compileGeneratedModule(id, sources, reference) {
  const directory = mkdtempSync(join(tmpdir(), `wasm2cs-sh12-compile-${id}-`));
  try {
    for (const source of sources) writeFileSync(join(directory, source.Name), source.Text);
    if (id === 'arithmetic-changed-bytes') {
      const sourceText = sources.map(source => source.Text).join('');
      if (!sourceText.includes('= 43;')) throw new Error('Changed arithmetic source did not contain the changed constant.');
    }
    const program = id === 'arithmetic'
      ? 'using Wasm2Cs.Generated; var module = new Arithmetic(); if (module.add(20, 22) != 42 || module.square(7) != 49) throw new System.Exception("Arithmetic mismatch."); System.Console.WriteLine("GENERATED_OK");'
        : id === 'arithmetic-changed-bytes'
        ? 'using Wasm2Cs.Generated; var module = new ArithmeticChangedBytes(); var actual = module.minimum(); if (actual != 43) throw new System.Exception("Changed arithmetic mismatch: " + actual); System.Console.WriteLine("GENERATED_OK");'
        : id === 'clang'
        ? 'using Wasm2Cs.Generated; var module = new Algorithms(); var buffer = unchecked((uint)module.buffer_ptr()); module.WriteMemory(buffer, System.Text.Encoding.ASCII.GetBytes("123456789")); if (unchecked((uint)module.crc32(unchecked((int)buffer), 9)) != 0xcbf43926u) throw new System.Exception("CRC mismatch."); System.Console.WriteLine("GENERATED_OK");'
        : id === 'host' ? hostRunnerSource(false) : null;
    if (!program) throw new Error(`No SH-12 generated-module runner exists for ${id}.`);
    writeFileSync(join(directory, 'Program.cs'), program + '\n');
    const hostProject = id === 'host';
    const project = join(directory, 'Generated.csproj');
    writeFileSync(project, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><LangVersion>9.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><Compile Include="*.g.cs" /><Compile Include="Program.cs" /><ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.Runtime', 'Wasm2Cs.Runtime.csproj'))}" />${hostProject ? `
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.DotnetHost', 'Wasm2Cs.DotnetHost.csproj'))}" />` : ''}</ItemGroup>
</Project>
`);
    const execution = run('dotnet', ['run', '--project', project, '--configuration', 'Release', '-p:UseSharedCompilation=false', '--nologo']);
    if (hostProject) {
      const line = execution.output.trim().split(/\r?\n/).reverse().find(value => value.startsWith('HOST_ABI_RESULT:'));
      if (!line) throw new Error(`Generated host runner did not emit HOST_ABI_RESULT.\n${execution.output}`);
      const actual = JSON.parse(line.slice('HOST_ABI_RESULT:'.length));
      const expected = JSON.parse(readFileSync(join(artifactRoot, 'host-reference.json'), 'utf8')).results;
      if (JSON.stringify(actual) !== JSON.stringify(expected))
        throw new Error(`Generated host ABI result differs for ${reference}.`);
      return { passed: true, output: 'HOST_ABI_OK', reference };
    }
    return { passed: true, output: 'GENERATED_OK', reference };
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

function host() {
  const failurePath = join(artifactRoot, 'host-failure.json');
  try {
    const manifest = verifyBundle();
    const sh09 = loadSh09Contract(manifest);
    for (const [module, name] of [
      ['env', 'emscripten_get_now'],
      ['env', 'mono_wasm_browser_entropy'],
      ['wasi_snapshot_preview1', 'fd_write']
    ]) {
      const item = sh09.imports.find(value => value.module === module && value.name === name);
      if (item?.disposition !== 'supported') throw new Error(`SH-09 runner requires supported import ${module}.${name}.`);
    }
    const referencePath = join(artifactRoot, 'host-reference.json');
    if (!existsSync(referencePath)) throw new Error(`SH-09 reference result is missing. Run reference first: ${referencePath}`);
    const reference = JSON.parse(readFileSync(referencePath, 'utf8'));
    const fixture = hostAbiWasm();
    if (reference.fixture?.sha256 !== sha256File(fixture)) throw new Error('SH-09 fixture differs from its reference result.');
    const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-sh09-host-'));
    try {
      const cliProject = join(root, 'src', 'Wasm2Cs.Cli', 'Wasm2Cs.Cli.csproj');
      run('dotnet', ['build', cliProject, '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
      const cli = join(root, 'src', 'Wasm2Cs.Cli', 'bin', 'Release', 'net10.0', 'Wasm2Cs.Cli.dll');
      const fixtureSources = join(directory, 'fixture');
      mkdirSync(fixtureSources, { recursive: true });
      run('dotnet', [cli, fixture, '--class-name', 'HostAbi', '--output-directory', fixtureSources, '--target-profile', 'portable-netstandard2.0']);
      const generated = generatedManifest();
      for (const entry of generated.manifest.entries)
        writeFileSync(join(directory, entry.path), readFileSync(join(generated.generatedRoot, entry.path)));
      if (generated.manifest.bundleSha256 !== sh09.bundleSha256 || generated.manifest.input?.sha256 !== sh09.runtimeSha256)
        throw new Error('Generated sources do not match the verified SH-09 runtime bundle.');
      for (const path of walkFiles(fixtureSources))
        writeFileSync(join(directory, `HostAbi.${path.split(/[\\/]/).pop()}`), readFileSync(path));
      writeFileSync(join(directory, 'Program.cs'), hostRunnerSource());
      writeFileSync(join(directory, 'Host.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="*.g.cs" />
    <Compile Include="Program.cs" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.Runtime', 'Wasm2Cs.Runtime.csproj'))}" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.DotnetHost', 'Wasm2Cs.DotnetHost.csproj'))}" />
  </ItemGroup>
</Project>
`);
      const build = run('dotnet', ['run', '--project', join(directory, 'Host.csproj'), '--configuration', 'Release', '-p:UseSharedCompilation=false', '--nologo']);
      const line = build.output.trim().split(/\r?\n/).reverse().find(value => value.startsWith('HOST_ABI_RESULT:'));
      if (!line) throw new Error(`Host runner did not emit HOST_ABI_RESULT.\n${build.output}`);
      const actual = JSON.parse(line.slice('HOST_ABI_RESULT:'.length));
      const expected = reference.results;
      if (JSON.stringify(actual) !== JSON.stringify(expected))
        throw new Error(`SH-09 ABI result differs.\nExpected: ${JSON.stringify(expected)}\nActual: ${JSON.stringify(actual)}`);
      const record = {
        schemaVersion: 1,
        milestone: 'SH-09',
        bundleSha256: sh09.bundleSha256,
        runtimeSha256: sh09.runtimeSha256,
        fixture: reference.fixture,
        imports: sh09.imports,
        results: actual,
        fullRuntime: { constructed: true, monoStarted: false }
      };
      json(join(artifactRoot, 'host-results.json'), record);
      rmSync(failurePath, { force: true });
      console.log(`SH-09 host passed: ${sh09.imports.length} imports classified, ABI fixture matched, full runtime constructed.`);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  } catch (error) {
    json(failurePath, { schemaVersion: 1, milestone: 'SH-09', status: 'blocked', reason: error instanceof Error ? error.message : String(error) });
    throw error;
  }
}

function hello() {
  const failurePath = join(artifactRoot, 'hello-failure.json');
  try {
    const manifest = verifyBundle();
    const startup = bootConfig(manifest);
    const referencePath = join(artifactRoot, 'reference-results.json');
    if (!existsSync(referencePath)) throw new Error(`Reference result is missing. Run reference first: ${referencePath}`);
    const reference = JSON.parse(readFileSync(referencePath, 'utf8'));
    if (reference.managedMain?.stdout !== 'Hello, World!\n42\n' || reference.managedMain?.exitCode !== 0)
      throw new Error('Reference result does not contain the SH-10 managed Main probe.');
    const generated = generatedManifest();
    if (generated.manifest.bundleSha256 !== manifest.bundleSha256 || generated.manifest.input?.sha256 !== startup.runtimeSha256)
      throw new Error('Generated sources do not match the verified SH-10 runtime bundle.');
    const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-sh10-hello-'));
    try {
      for (const entry of generated.manifest.entries)
        writeFileSync(join(directory, entry.path), readFileSync(join(generated.generatedRoot, entry.path)));
      writeFileSync(join(directory, 'Program.cs'), helloRunnerSource(startup));
      writeFileSync(join(directory, 'Hello.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="*.g.cs" />
    <Compile Include="Program.cs" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.Runtime', 'Wasm2Cs.Runtime.csproj'))}" />
    <ProjectReference Include="${xml(join(root, 'src', 'Wasm2Cs.DotnetHost', 'Wasm2Cs.DotnetHost.csproj'))}" />
  </ItemGroup>
</Project>
`);
      const project = join(directory, 'Hello.csproj');
      run('dotnet', ['build', project, '--configuration', 'Release', '-p:UseSharedCompilation=false', '--nologo']);
      const runner = join(directory, 'bin', 'Release', 'net10.0', 'Hello.dll');
      const execute = scenario => {
        const result = run('dotnet', [runner], {
          env: { ...process.env, SH10_SCENARIO: scenario },
          allowFailure: true
        });
        const line = result.output.trim().split(/\r?\n/).reverse().find(value => value.startsWith('SH10_RESULT:'));
        if (!line) throw new Error(`SH-10 ${scenario} runner did not emit SH10_RESULT.\n${result.output}`);
        return { processExit: result.status, result: JSON.parse(line.slice('SH10_RESULT:'.length)) };
      };
      const positiveRun = execute('positive');
      const actual = positiveRun.result;
      if (positiveRun.processExit !== 0 || actual.failure !== null || actual.stdout !== reference.managedMain.stdout ||
          actual.managedReturn !== 0 || actual.exitCode !== 0 || actual.state !== 'Exited')
        throw new Error(`SH-10 managed result differs. Expected ${JSON.stringify(reference.managedMain)}, actual ${JSON.stringify(actual)}`);
      const assemblyPhases = actual.phases.filter(value => value.startsWith('add-assembly:'));
      const tail = ['load-runtime', 'configure-args', 'invoke-main', 'exit'];
      if (actual.phases[0] !== 'constructors' || assemblyPhases.length !== startup.assemblies.length ||
          actual.phases.slice(actual.phases.length - tail.length).join(',') !== tail.join(','))
        throw new Error(`SH-10 startup phases differ: ${JSON.stringify(actual.phases)}`);
      const negativeScenarios = {};
      for (const scenario of ['missing-corelib', 'missing-selfhosting', 'corrupt-selfhosting', 'missing-import', 'native-exit']) {
        const execution = execute(scenario);
        const failure = execution.result;
        if (execution.processExit !== 0 || failure.failure === null || failure.state !== 'Failed')
          throw new Error(`SH-10 negative scenario ${scenario} was not detected: ${JSON.stringify(execution)}`);
        if (scenario === 'native-exit' && failure.requestedExit !== 9)
          throw new Error(`SH-10 native exit scenario lost its exit code: ${JSON.stringify(failure)}`);
        negativeScenarios[scenario] = execution;
      }
      const record = {
        schemaVersion: 1,
        milestone: 'SH-10',
        bundleSha256: manifest.bundleSha256,
        runtimeSha256: startup.runtimeSha256,
        mainAssemblyName: startup.profile.mainAssemblyName,
        assemblyCount: startup.assemblies.length,
        results: actual,
        negativeScenarios,
        reference: reference.managedMain,
        outerEngine: false
      };
      json(join(artifactRoot, 'hello-results.json'), record);
      rmSync(failurePath, { force: true });
      console.log(`SH-10 hello passed: ${startup.assemblies.length} assemblies registered, managed Main matched, exit 0.`);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  } catch (error) {
    json(failurePath, { schemaVersion: 1, milestone: 'SH-10', status: 'blocked', reason: error instanceof Error ? error.message : String(error) });
    throw error;
  }
}

function translate() {
  const failurePath = join(artifactRoot, 'translate-failure.json');
  const timeoutMs = 15 * 60 * 1000;
  const maxOutputBytes = 64 * 1024 * 1024;
  try {
    const manifest = verifyBundle();
    const startup = bootConfig(manifest);
    const generated = generatedManifest();
    if (generated.manifest.bundleSha256 !== manifest.bundleSha256)
      throw new Error('Generated sources do not match the verified SH-12 bundle; run generate first.');
    const evidencePath = join(root, 'docs', 'self-hosting', 'SH-12-translate.json');
    if (!existsSync(evidencePath)) throw new Error(`SH-12 evidence is missing: ${evidencePath}`);
    const evidence = JSON.parse(readFileSync(evidencePath, 'utf8'));
    if (evidence.schemaVersion !== 1 || evidence.milestone !== 'SH-12' || evidence.bundleSha256 !== manifest.bundleSha256 || evidence.runtimeSha256 !== startup.runtimeSha256)
      throw new Error('SH-12 evidence does not match the verified bundle or runtime.');
    if (evidence.guestAssemblyCount !== startup.assemblies.length)
      throw new Error('SH-12 evidence has a different guest assembly count.');
    for (const [name, expectedHash] of Object.entries(evidence.guestAssemblyHashes ?? {})) {
      const assembly = startup.assemblies.find(value => value.name === name);
      const actualHash = assembly && manifest.entries.find(entry => entry.path === `_framework/${assembly.virtualPath ?? assembly.name}`)?.sha256;
      if (actualHash !== expectedHash) throw new Error(`SH-12 evidence hash differs for ${name}.`);
    }
    const scenarios = translateScenarios();
    const evidenceScenarios = Object.fromEntries((evidence.inputScenarios ?? []).map(scenario => [scenario.id, scenario]));
    for (const scenario of scenarios) {
      const recorded = evidenceScenarios[scenario.id];
      if (!recorded || recorded.className !== scenario.className || recorded.bytes !== scenario.inputBytes || recorded.sha256 !== scenario.inputSha256)
        throw new Error(`SH-12 evidence input differs for ${scenario.id}.`);
    }
    const official = officialTranslationResults(scenarios, timeoutMs);
    if (evidence.wrapper !== official.wrappers.translate) throw new Error('SH-12 evidence wrapper differs from the guest export.');
    const normal = normalTranslationResults(scenarios, timeoutMs);
    const translated = translatedTranslateResults(startup, generated, official.wrappers, scenarios, timeoutMs);
    const positive = translated.positive.result;
    if (translated.positive.processExit !== 0 || positive.failure !== null || positive.state !== 'Exited' || positive.managedReturn !== 0)
      throw new Error(`Translated SH-12 runner failed: ${JSON.stringify(positive)}.`);
    if (translated.missingWasm2cs.processExit !== 0 || translated.missingWasm2cs.result.failure?.message !== 'Managed assembly supply is incomplete: expected 174, received 173.' || translated.missingWasm2cs.result.state !== 'Failed')
      throw new Error('Missing Wasm2Cs.dll did not fail translated startup/translation.');
    if (translated.corruptWasm2cs.processExit !== 0 || translated.corruptWasm2cs.result.failure?.message !== 'assembly Wasm2Cs.dll was rejected.' || translated.corruptWasm2cs.result.state !== 'Failed')
      throw new Error('Corrupt Wasm2Cs.dll did not fail translated startup/translation.');
    const translatedById = Object.fromEntries(positive.translatedResults.map(item => [item.id, item.output]));
    const sourceRecords = {};
    const generatedSourceSummaries = {};
    for (const scenario of scenarios) {
      const expected = official.results[scenario.id]?.output;
      const actual = translatedById[scenario.id];
      if (typeof expected !== 'string' || typeof actual !== 'string') throw new Error(`Missing translated result for ${scenario.id}.`);
      if (expected !== actual) throw new Error(`Guest translated output differs from official browser-WASM for ${scenario.id}.`);
      if (Buffer.byteLength(actual, 'utf8') > maxOutputBytes) throw new Error(`Translation output exceeds ${maxOutputBytes} bytes for ${scenario.id}.`);
      const expectedSources = parseTranslatedSources(expected);
      const actualSources = parseTranslatedSources(actual);
      if (expectedSources.error || actualSources.error) {
        if (expectedSources.error !== actualSources.error) throw new Error(`Translation diagnostic differs for ${scenario.id}.`);
        const normalError = normal[scenario.id];
        const expectedMessage = expectedSources.error.replace(/^ERROR:\s+[^:]+:\s*/, '');
        if (!normalError || normalError.status === 0 || !normalError.error.includes(expectedMessage))
          throw new Error(`Normal translation diagnostic differs for ${scenario.id}.`);
        continue;
      }
      const expectedText = expectedSources.sources.map(source => source.Text).join('');
      if (scenario.id === 'arithmetic-repeat-1' || scenario.id === 'arithmetic-repeat-2' || scenario.id === 'arithmetic-changed-class' || scenario.id === 'arithmetic-changed-bytes' || scenario.id === 'clang' || scenario.id === 'host') {
        const normalText = normal[scenario.id === 'arithmetic-repeat-2' ? 'arithmetic-repeat-1' : scenario.id].text;
        if (normalText !== expectedText) throw new Error(`Normal wasm2cs output differs for ${scenario.id}.`);
      }
      sourceRecords[scenario.id] = expectedSources.sources.map(source => ({ name: source.Name, bytes: Buffer.byteLength(source.Text, 'utf8'), sha256: sha256(Buffer.from(source.Text, 'utf8')) }));
      const summaryId = scenario.id === 'arithmetic-repeat-1' ? 'arithmetic' : scenario.id === 'arithmetic-changed-class' ? 'arithmeticChanged' : scenario.id;
      generatedSourceSummaries[summaryId] = sourceSummary(expectedSources.sources);
    }
    for (const [id, expectedSummary] of Object.entries(evidence.generatedSources ?? {}))
      if (JSON.stringify(generatedSourceSummaries[id]) !== JSON.stringify(expectedSummary)) throw new Error(`SH-12 evidence generated-source summary differs for ${id}.`);
    if (translatedById['arithmetic-repeat-1'] !== translatedById['arithmetic-repeat-2']) throw new Error('Repeated identical input produced different generated text.');
    if (translatedById['arithmetic-repeat-1'] === translatedById['arithmetic-changed-class']) throw new Error('Changed class input did not change generated text.');
    if (translatedById['arithmetic-repeat-1'] === translatedById['arithmetic-changed-bytes']) throw new Error('Changed WASM bytes did not change generated text.');
    const arithmeticSources = parseTranslatedSources(translatedById['arithmetic-repeat-1']).sources;
    const changedArithmeticSources = parseTranslatedSources(translatedById['arithmetic-changed-bytes']).sources;
    const clangSources = parseTranslatedSources(translatedById.clang).sources;
    const generatedExecution = {
      arithmetic: compileGeneratedModule('arithmetic', arithmeticSources, 'arithmetic-repeat-1'),
      arithmeticChangedBytes: compileGeneratedModule('arithmetic-changed-bytes', changedArithmeticSources, 'arithmetic-changed-bytes'),
      clang: compileGeneratedModule('clang', clangSources, 'clang'),
      host: compileGeneratedModule('host', parseTranslatedSources(translatedById.host).sources, 'host')
    };
    const toolchainPath = join(artifactRoot, 'toolchain.json');
    const toolchain = JSON.parse(readFileSync(toolchainPath, 'utf8'));
    const info = dotnetInfo();
    const record = {
      schemaVersion: 1,
      milestone: 'SH-12',
      bundleSha256: manifest.bundleSha256,
      runtimeSha256: startup.runtimeSha256,
      guestAssemblyHashes: Object.fromEntries(startup.assemblies.map(assembly => [assembly.name, manifest.entries.find(entry => entry.path === `_framework/${assembly.virtualPath ?? assembly.name}`)?.sha256 ?? null])),
      inputScenarios: scenarios.map(({ id, className, inputBytes, inputSha256 }) => ({ id, className, inputBytes, inputSha256 })),
      wrappers: official.wrappers,
      sourceRecords,
      generatedSourceSummaries,
      generatedExecution,
      negativeScenarios: {
        missingWasm2cs: translated.missingWasm2cs.result,
        corruptWasm2cs: translated.corruptWasm2cs.result
      },
      translated: positive,
      environment: { platform: platform(), release: release(), architecture: arch(), node: process.version, dotnetSdk: info.version, runtimePack: toolchain.runtimePack, toolchainSha256: sha256File(toolchainPath) },
      limits: { timeoutMs, maxOutputBytes },
      outerEngine: translated.provenance.outerEngine,
      outerTranslator: translated.provenance.outerTranslator
    };
    json(join(artifactRoot, 'translate-results.json'), record);
    rmSync(failurePath, { force: true });
    console.log(`SH-12 translate passed: ${scenarios.length} inputs, guest output matched, generated Arithmetic, Clang, and HostAbi C# executed.`);
  } catch (error) {
    json(failurePath, { schemaVersion: 1, milestone: 'SH-12', status: 'blocked', reason: error instanceof Error ? error.message : String(error) });
    throw error;
  }
}

function managed() {
  const failurePath = join(artifactRoot, 'managed-failure.json');
  try {
    if (!existsSync(managedProfilePath)) throw new Error(`SH-11 profile is missing: ${managedProfilePath}`);
    const contract = JSON.parse(readFileSync(managedProfilePath, 'utf8'));
    if (contract.schemaVersion !== 1 || contract.invocationsPerInstance !== 3 || contract.instances !== 2)
      throw new Error('SH-11 profile has unsupported fixed iteration or instance conditions.');
    const timeoutMs = Number(contract.timeoutMs);
    const gcHeapLimit = Number(contract.gcHeapLimitBytes);
    const processMemoryLimit = Number(contract.outerProcessMemoryBytes);
    if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0 || !Number.isSafeInteger(gcHeapLimit) || gcHeapLimit <= 0 ||
        !Number.isSafeInteger(processMemoryLimit) || processMemoryLimit <= 0)
      throw new Error('SH-11 profile contains invalid timeout or memory limits.');
    const manifest = verifyBundle();
    const startup = bootConfig(manifest);
    if (contract.runtimeSha256 !== startup.runtimeSha256)
      throw new Error(`SH-11 runtime hash differs from ${managedProfilePath}.`);
    if (contract.bundleSha256 && contract.bundleSha256 !== manifest.bundleSha256)
      throw new Error(`SH-11 bundle hash differs from ${managedProfilePath}; rerun prepare and update the pinned profile.`);
    const generated = generatedManifest();
    if (generated.manifest.bundleSha256 !== manifest.bundleSha256)
      throw new Error('Generated sources do not match the verified SH-11 bundle; run generate first.');

    const normal = normalManagedResults(timeoutMs);
    const official = officialManagedResults(timeoutMs);
    if (contract.managedProbeWrapper !== official.wrappers.probe ||
        contract.managedReentryWrapper !== official.wrappers.reentry ||
        contract.managedThrowWrapper !== official.wrappers.throw)
      throw new Error('SH-11 managed export wrappers differ from the pinned profile.');
    const translated = translatedManagedResults(startup, generated, official.wrappers, timeoutMs, gcHeapLimit);
    const normalRuns = verifyProbeRuns('normal .NET', normal.probes);
    const officialRuns = verifyProbeRuns('official browser-WASM', official.probes);
    if (!official.throwing.thrown || official.throwing.message !== 'SH-11 deliberate uncaught exception')
      throw new Error(`Official browser-WASM did not propagate the deliberate managed exception: ${JSON.stringify(official.throwing)}.`);
    const instanceRuns = translated.instances.map((instance, index) => {
      if (instance.failure !== null || instance.state !== 'Exited' || instance.managedReturn !== 0 || instance.exitCode !== 0)
        throw new Error(`Translated Mono instance ${index + 1} did not exit successfully: ${JSON.stringify(instance)}.`);
      if (instance.stderr.split('SH11_STDERR').length - 1 !== contract.invocationsPerInstance)
        throw new Error(`Translated Mono instance ${index + 1} did not capture stderr.`);
      if (!instance.managedThrow?.thrown || instance.managedThrow.type !== 'System.InvalidOperationException' ||
          !instance.managedThrow.message.includes(contract.managedThrowWrapper))
        throw new Error(`Translated Mono instance ${index + 1} did not propagate the deliberate managed exception.`);
      if (instance.reentryStatus !== 3)
        throw new Error(`Translated Mono instance ${index + 1} did not complete host re-entry: ${instance.reentryStatus}.`);
      if (instance.linearMaximumBytes !== contract.linearMemoryBytes)
        throw new Error(`Translated Mono instance ${index + 1} has linear-memory limit ${instance.linearMaximumBytes}; expected ${contract.linearMemoryBytes}.`);
      if (instance.outerGcAvailable <= 0 || instance.outerGcAvailable > gcHeapLimit)
        throw new Error(`Translated Mono instance ${index + 1} did not apply the outer GC heap limit: ${instance.outerGcAvailable}.`);
      if (instance.outerPeakWorkingSet <= 0 || instance.outerPeakWorkingSet > processMemoryLimit)
        throw new Error(`Translated Mono instance ${index + 1} exceeded the outer process memory limit: ${instance.outerPeakWorkingSet}.`);
      return verifyProbeRuns(`translated Mono instance ${index + 1}`, instance.managedProbeResults.map(item => ({
        status: item.status,
        result: item.payload ? JSON.parse(item.payload) : null
      })));
    });
    compareProbeRuns(normalRuns, officialRuns, instanceRuns[0]);
    compareProbeRuns(normalRuns, normalRuns, instanceRuns[1]);
    const first = translated.instances[0];
    const second = translated.instances[1];
    if (first.memoryIdentity === second.memoryIdentity || first.tableIdentity === second.tableIdentity || first.runtimeIdentity === second.runtimeIdentity)
      throw new Error('Translated Mono instances share runtime, memory, or indirect table identity.');
    if (!Array.isArray(first.globalInitial) || !Array.isArray(second.globalInitial) ||
        !Array.isArray(first.globalFinal) || !Array.isArray(second.globalFinal) ||
        first.globalInitial.length !== second.globalInitial.length)
      throw new Error('Translated Mono global storage snapshots are incomplete or inconsistent.');
    if (JSON.stringify(first.globalInitial) !== JSON.stringify(second.globalInitial) ||
        JSON.stringify(first.globalFinal) !== JSON.stringify(second.globalFinal))
      throw new Error('Translated Mono global state differs between independent instances.');
    if (first.globalInitial.length === 0 || first.globalFinal.length === 0 ||
        JSON.stringify(first.globalInitial) === JSON.stringify(first.globalFinal))
      throw new Error('Translated Mono global snapshots did not observe mutable global state.');
    if (!first.hostState?.fileMarker || first.hostState.callbacks !== 1 || !first.hostState.exited || first.hostState.exitCode !== 17 ||
        second.hostState?.fileMarker || second.hostState?.callbacks !== 0 || second.hostState?.exited || second.hostState?.exitCode !== 0)
      throw new Error('Translated Mono host file, callback, or exit state leaked between instances.');
    if (JSON.parse(first.managedProbeResults[0].payload).Invocation !== 1)
      throw new Error('First translated Mono instance did not start with a fresh managed static state.');
    if (JSON.parse(second.managedProbeResults[0].payload).Invocation !== 1)
      throw new Error('Second translated Mono instance did not start with isolated managed static state.');
    const toolchainPath = join(artifactRoot, 'toolchain.json');
    if (!existsSync(toolchainPath)) throw new Error(`Toolchain evidence is missing: ${toolchainPath}`);
    const toolchain = JSON.parse(readFileSync(toolchainPath, 'utf8'));
    const info = dotnetInfo();
    const cpuList = cpus();
    const result = {
      schemaVersion: 1,
      milestone: 'SH-11',
      bundleSha256: manifest.bundleSha256,
      runtimeSha256: startup.runtimeSha256,
      profile: contract,
      wrappers: official.wrappers,
      probes: contract.probes,
      environment: {
        platform: platform(),
        release: release(),
        architecture: arch(),
        cpuModel: cpuList[0]?.model ?? null,
        cpuCount: cpuList.length,
        node: process.version,
        dotnetSdk: info.version,
        runtimePack: toolchain.runtimePack ?? null,
        emscripten: toolchain.emscripten?.version ?? null,
        toolchainPath: 'toolchain.json',
        toolchainSha256: sha256File(toolchainPath)
      },
      normalDotnet: { probes: normalRuns, guestHeap: normal.probes.map(run => run.result.GuestHeap), durationMs: normal.durationMs },
      officialBrowserWasm: { probes: officialRuns, guestHeap: official.probes.map(run => run.result.GuestHeap), throwing: official.throwing },
      translatedMono: {
        instances: translated.instances,
        durationMs: translated.durationMs,
        globalSnapshots: translated.instances.map(instance => ({ initial: instance.globalInitial, final: instance.globalFinal })),
        runtimeIsolated: first.runtimeIdentity !== second.runtimeIdentity,
        memoryIsolated: first.memoryIdentity !== second.memoryIdentity,
        tableIsolated: first.tableIdentity !== second.tableIdentity
      },
      limits: { timeoutMs, gcHeapLimit, outerProcessMemoryBytes: processMemoryLimit, linearMemoryBytes: contract.linearMemoryBytes },
      outerEngine: false
    };
    json(join(artifactRoot, 'managed-results.json'), result);
    rmSync(failurePath, { force: true });
    console.log(`SH-11 managed passed: ${contract.probes.length} probes, 3 invocations, 2 isolated translated instances.`);
  } catch (error) {
    json(failurePath, {
      schemaVersion: 1,
      milestone: 'SH-11',
      status: 'blocked',
      reason: error instanceof Error ? error.message : String(error)
    });
    throw error;
  }
}

function audit() {
  const manifest = verifyBundle();
  const canonical = profile.canonicalBundle;
  if (profile.inventory?.bundleSha256 !== canonical.sha256)
    throw new Error('SH-01 inventory does not identify the canonical bundle.');
  const lineage = new Set((profile.bundleLineage ?? []).map(item => item.sha256));
  if (!lineage.has(canonical.sha256)) throw new Error('Canonical bundle is absent from the bundle lineage.');
  for (const hash of [
    '10e2f5cee5fecd86681ef9e043082a04df131a0d7eb76855ac895ea0cbf3c9e6',
    '95ca67de55675a09b263777eea0a33e298bfd0bb0d006decb5f68a8e2cab6773',
    '01edf9ec336605992764326157649e4e6f555e91243f52aa1801a859c9471523',
    '2b89bf1bd90a650fe81dad2de5ebcd4a8dd339b21f0b7d465895287251bf3d1a'
  ]) if (!lineage.has(hash)) throw new Error(`Historical bundle ${hash} is absent from the bundle lineage.`);

  for (const path of [
    join(root, 'docs', 'self-hosting', 'SH-03-integer-coverage.json'),
    join(root, 'docs', 'self-hosting', 'SH-04-floating-coverage.json')
  ]) {
    const record = JSON.parse(readFileSync(path, 'utf8'));
    if (!lineage.has(record.bundleSha256)) throw new Error(`${path} refers to a bundle outside the recorded lineage.`);
  }

  const runtimeSha256 = runtimeEntry(manifest).sha256;
  for (const path of [managedProfilePath, join(root, 'docs', 'self-hosting', 'SH-12-translate.json')]) {
    const record = JSON.parse(readFileSync(path, 'utf8'));
    if (record.bundleSha256 !== canonical.sha256 || record.runtimeSha256 !== runtimeSha256)
      throw new Error(`${path} does not match the canonical bundle and runtime.`);
  }
  const translation = JSON.parse(readFileSync(join(root, 'docs', 'self-hosting', 'SH-12-translate.json'), 'utf8'));
  if (!(translation.inputScenarios ?? []).some(item => item.id === 'arithmetic-changed-bytes') ||
      translation.checks?.changedWasmChangesOutputAndResult !== true)
    throw new Error('SH-12 evidence does not include the changed-WASM behavior check.');

  for (const name of ['reference-results.json', 'generated-manifest.json', 'compile-results.json', 'host-results.json', 'hello-results.json', 'managed-results.json', 'translate-results.json']) {
    const path = join(artifactRoot, name);
    if (!existsSync(path)) throw new Error(`Required self-hosting evidence is missing: ${path}`);
    const record = JSON.parse(readFileSync(path, 'utf8'));
    if (record.bundleSha256 !== canonical.sha256) throw new Error(`${name} does not match the canonical bundle.`);
  }
  const translated = JSON.parse(readFileSync(join(artifactRoot, 'translate-results.json'), 'utf8'));
  if (!translated.sourceRecords?.['arithmetic-changed-bytes'] || translated.generatedExecution?.arithmeticChangedBytes?.output !== 'GENERATED_OK')
    throw new Error('Translated SH-12 evidence does not prove changed-WASM generated behavior.');
  const unitySelfHost = JSON.parse(readFileSync(join(root, 'docs', 'self-hosting', 'SH-13-unity.json'), 'utf8'));
  if (unitySelfHost.status !== 'verified' || unitySelfHost.unity !== '6000.6.0f1' || unitySelfHost.bundleSha256 !== canonical.sha256)
    throw new Error('SH-13 Unity evidence is not a verified run of the canonical bundle on Unity 6000.6.0f1.');
  const unityEvidence = JSON.parse(readFileSync(join(root, 'docs', 'self-hosting', 'SH-12.5-unity.json'), 'utf8'));
  const packagePath = join(root, 'artifacts', 'com.mfakane.wasm2cs-0.1.0-preview.1.tgz');
  if (unityEvidence.status !== 'verified' || !existsSync(packagePath) || sha256File(packagePath) !== unityEvidence.packageSha256)
    throw new Error('SH-12.5 Unity evidence does not match the current package.');

  const completions = [
    ['SH-01-runtime-profile.md', '38b7afe'],
    ['SH-02-typed-ir.md', 'b4ac1d7'],
    ['SH-03-i64.md', '51b7603'],
    ['SH-04-floating-point.md', 'f2f97a1'],
    ['SH-05-memory-data.md', '8d15d70'],
    ['SH-05.5-lowering.md', 'aa20b45'],
    ['SH-06-tables.md', '37946f3'],
    ['SH-07-exceptions.md', '13e386b'],
    ['SH-08-large-codegen.md', 'bf279a2'],
    ['SH-09-host.md', '555cf67'],
    ['SH-10-mono-startup.md', '2e6913a'],
    ['SH-11-managed-runtime.md', 'a41058f'],
    ['SH-12-self-hosting.md', 'cc41a6f'],
    ['SH-12.5-audit-cleanup.md', 'a64f6df'],
    ['SH-13-unity.md', '3f96025'],
    ['SH-14-reproducibility.md', '63b62f3']
  ];
  for (const [name, commit] of completions) {
    const text = readFileSync(join(root, 'docs', 'milestones', name), 'utf8');
    if (!new RegExp('`' + commit + '[0-9a-f]*`').test(text))
      throw new Error(`${name} does not record completion commit ${commit}.`);
    const result = run('git', ['cat-file', '-e', `${commit}^{commit}`], { allowFailure: true });
    if (result.status !== 0) throw new Error(`${name} refers to missing commit ${commit}.`);
  }
  const readme = readFileSync(join(root, 'README.md'), 'utf8');
  const implementation = readFileSync(join(root, 'IMPLEMENTATION.md'), 'utf8');
  if (readme.includes('This goal is planned, not yet implemented.') || implementation.includes('remaining unimplemented sequence\nis [SH-10 through SH-14]'))
    throw new Error('Top-level implementation status is stale.');

  json(join(artifactRoot, 'audit-results.json'), {
    schemaVersion: 1,
    milestone: 'SH-12.5',
    bundleSha256: canonical.sha256,
    runtimeSha256,
    bundleLineage: [...lineage],
    completionCommits: Object.fromEntries(completions),
    status: 'passed'
  });
  console.log(`SH-12.5 audit passed: canonical bundle ${canonical.sha256}, ${completions.length} completion records checked.`);
}

function verifyFailure(stage, error, manifest = null) {
  return {
    schemaVersion: 1,
    milestone: 'SH-14',
    status: 'failed',
    failedStage: stage,
    exitCode: 1,
    bundleSha256: manifest?.bundleSha256 ?? null,
    cacheKey: manifest ? cacheKey(manifest.bundleSha256) : null,
    stages: verifyStages.map(name => ({ name, status: 'not-run', exitCode: null })),
    reason: error instanceof Error ? error.message : String(error),
    workloadUpdated: false
  };
}

function stageEvidence(name) {
  const files = {
    inventory: 'inventory.json',
    generate: 'generated-manifest.json',
    compile: 'compile-results.json',
    host: 'host-results.json',
    hello: 'hello-results.json',
    managed: 'managed-results.json',
    translate: 'translate-results.json'
  };
  const path = join(artifactRoot, files[name] ?? '');
  return files[name] && existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : null;
}

function verify() {
  const resultPath = join(artifactRoot, 'verify-results.json');
  const finish = (body, code) => {
    json(resultPath, body);
    if (code !== 0) {
      console.error(`self-hosting: verify failed at ${body.failedStage}: ${body.reason ?? `exit ${code}`}`);
      process.exitCode = code;
    } else {
      console.log(`SH-14 verify passed: ${verifyStages.length} stages, bundle ${body.bundleSha256}.`);
    }
  };
  let manifest;
  try {
    manifest = verifyBundle();
  } catch (error) {
    finish(verifyFailure('bundle', error), 1);
    return;
  }
  const stampPath = join(environmentRoot, 'cache-stamp.json');
  if (existsSync(stampPath)) {
    const stamp = JSON.parse(readFileSync(stampPath, 'utf8'));
    if (acceptCacheStamp(stamp, manifest.bundleSha256).reason === 'mismatch') {
      finish(verifyFailure('cache', new Error(`Cache stamp ${stamp.bundleSha256} does not match bundle ${manifest.bundleSha256}.`), manifest), 1);
      return;
    }
  }
  const wabt = ['wasm-validate', 'wasm-objdump', 'wasm2wat'].map(name => tool(savedWabtTool(name) ?? name));
  const tools = [tool('dotnet'), tool('node'), ...wabt];
  const absent = missingTools(tools);
  const wrongWabt = wabt.filter(item => item.available && !item.version?.includes(profile.wabt));
  let sdk = null;
  if (!absent.length) {
    try {
      sdk = dotnetInfo();
    } catch (error) {
      finish(verifyFailure('tools', error, manifest), 1);
      return;
    }
  }
  if (absent.length || tool('node').version !== `v${profile.node}` || wrongWabt.length || sdk?.version !== profile.sdk) {
    finish(verifyFailure('tools', new Error(`Pinned tools are missing or differ. Missing: ${absent.join(', ') || 'none'}. Node ${tool('node').version ?? 'missing'}, SDK ${sdk?.version ?? 'missing'}.`), manifest), 1);
    return;
  }
  const planned = runStages(verifyStages, name => {
    const child = run(process.execPath, [fileURLToPath(import.meta.url), name], { allowFailure: true });
    if (child.status !== 0) {
      const tail = (child.output ?? '').trim().split(/\r?\n/).slice(-20).join('\n');
      if (tail) console.error(tail);
    }
    return child.status;
  });
  for (const stage of planned.stages) {
    if (stage.status !== 'passed') continue;
    const evidence = stageEvidence(stage.name);
    if (evidence?.bundleSha256 && evidence.bundleSha256 !== manifest.bundleSha256) {
      stage.status = 'failed';
      stage.exitCode = 1;
      planned.status = 'failed';
      planned.failedStage = stage.name;
      planned.exitCode = 1;
      break;
    }
  }
  const passedNames = planned.stages.filter(stage => stage.status === 'passed').map(stage => stage.name);
  const measurements = Object.fromEntries(passedNames.map(name => [name, extractMeasurement(name, stageEvidence(name))]));
  finish({
    schemaVersion: 1,
    milestone: 'SH-14',
    status: planned.status,
    failedStage: planned.failedStage,
    exitCode: planned.exitCode,
    bundleSha256: manifest.bundleSha256,
    cacheKey: cacheKey(manifest.bundleSha256),
    environment: { platform: platform(), arch: arch(), release: release(), node: process.version, dotnetSdk: sdk.version },
    stages: planned.stages,
    measurements,
    workloadUpdated: false,
    thresholds: null
  }, planned.status === 'passed' ? 0 : planned.exitCode || 1);
}

function readSummary(directory) {
  const text = readFileSync(join(directory, 'summary.txt'), 'utf8');
  const summary = {};
  for (const line of text.split(/\r?\n/)) {
    const index = line.indexOf('=');
    if (index > 0) summary[line.slice(0, index)] = line.slice(index + 1);
  }
  return summary;
}

function translationFiles(directory) {
  const translations = join(directory, 'translations');
  if (!existsSync(translations)) throw new Error(`Translation output is missing: ${translations}`);
  return Object.fromEntries(readdirSync(translations).filter(name => name.endsWith('.txt')).map(name =>
    [name.slice(0, -4), readFileSync(join(translations, name), 'utf8')]));
}

function requireMeasured(summary, label) {
  for (const key of ['startupDurationMs', 'executionDurationMs', 'linearPages', 'outerGcHeap']) {
    const value = Number(summary[key]);
    if (!Number.isFinite(value) || value <= 0) throw new Error(`${label} did not record ${key}.`);
  }
  if (summary.unityVersion !== '6000.6.0f1')
    throw new Error(`${label} Unity version is ${summary.unityVersion}; 6000.6.0f1 is required.`);
}

function unityPrepare() {
  const output = optionValue('--output', '');
  if (!output) throw new Error('unity-prepare requires --output.');
  const manifest = verifyBundle();
  const startup = bootConfig(manifest);
  const generated = generatedManifest();
  if (generated.manifest.bundleSha256 !== manifest.bundleSha256 || generated.manifest.input?.sha256 !== startup.runtimeSha256)
    throw new Error('Generated sources do not match the canonical runtime. Run generate first.');
  const evidence = JSON.parse(readFileSync(join(root, 'docs', 'self-hosting', 'SH-12-translate.json'), 'utf8'));
  if (evidence.bundleSha256 !== manifest.bundleSha256 || evidence.runtimeSha256 !== startup.runtimeSha256 || !evidence.wrapper)
    throw new Error('SH-12 evidence does not match the canonical bundle.');
  const scenarios = translateScenarios();
  const runner = helloRunnerSource(startup, null, { wrapper: evidence.wrapper, requests: scenarios }, { host: 'unity' });
  mkdirSync(output, { recursive: true });
  writeFileSync(join(output, 'UnitySelfHostRunner.cs'), runner);
  const lines = startup.assemblies.map(assembly => {
    const name = assembly.virtualPath ?? assembly.name;
    return `${name} ${sha256File(join(bundleRoot, '_framework', name))}`;
  }).sort();
  if (lines.length !== evidence.guestAssemblyCount)
    throw new Error(`Guest assembly count ${lines.length} differs from SH-12 evidence.`);
  writeFileSync(join(output, 'guest-manifest.txt'), lines.join('\n') + '\n');
  json(join(output, 'prepare.json'), {
    schemaVersion: 1,
    milestone: 'SH-13',
    bundleSha256: manifest.bundleSha256,
    runtimeSha256: startup.runtimeSha256,
    guestAssemblyCount: lines.length,
    wrapper: evidence.wrapper,
    generatedEntries: generated.manifest.entries.length
  });
  console.log(`SH-13 Unity runner prepared: ${lines.length} guest assemblies, ${generated.manifest.entries.length} generated sources.`);
}

function unityCheck() {
  const player = optionValue('--player', '');
  const editor = optionValue('--editor', '');
  const repeat = optionValue('--repeat', '');
  const missing = optionValue('--missing', '');
  const corrupt = optionValue('--corrupt', '');
  const editorMissing = optionValue('--editor-missing', '');
  const editorCorrupt = optionValue('--editor-corrupt', '');
  const il2cppOut = optionValue('--il2cpp-out', '');
  if (!player || !editor || !missing || !corrupt || !editorMissing || !editorCorrupt || !il2cppOut)
    throw new Error('unity-check requires --player, --editor, --missing, --corrupt, --editor-missing, --editor-corrupt, and --il2cpp-out.');
  const evidence = JSON.parse(readFileSync(join(root, 'docs', 'self-hosting', 'SH-12-translate.json'), 'utf8'));
  const manifest = verifyBundle();
  if (evidence.bundleSha256 !== manifest.bundleSha256) throw new Error('SH-12 evidence does not match the canonical bundle.');
  const playerSummary = readSummary(player);
  const editorSummary = readSummary(editor);
  if (playerSummary.scenario !== 'translate' || editorSummary.scenario !== 'translate')
    throw new Error('Unity self-host did not run the translate scenario.');
  if (playerSummary.state !== 'Exited' || editorSummary.state !== 'Exited' || playerSummary.failureType || editorSummary.failureType)
    throw new Error(`Unity self-host failed: player ${JSON.stringify(playerSummary)} editor ${JSON.stringify(editorSummary)}`);
  if (playerSummary.managedReturn !== '0' || editorSummary.managedReturn !== '0')
    throw new Error('Unity self-host managed return was not 0.');
  requireMeasured(playerSummary, 'Player');
  requireMeasured(editorSummary, 'Editor');
  for (const directory of [player, editor, repeat, missing, corrupt, editorMissing, editorCorrupt].filter(Boolean)) {
    const exit = readFileSync(join(directory, 'process-exit.txt'), 'utf8').trim();
    if (exit !== '0') throw new Error(`${directory} process exit was ${exit}.`);
  }
  const playerOutputs = translationFiles(player);
  const editorOutputs = translationFiles(editor);
  const ids = ['arithmetic-repeat-1', 'arithmetic-repeat-2', 'arithmetic-changed-class', 'arithmetic-changed-bytes', 'clang', 'host', 'invalid'];
  for (const id of ids) {
    if (playerOutputs[id] !== editorOutputs[id]) throw new Error(`Editor and IL2CPP outputs differ for ${id}.`);
    if (repeat && translationFiles(repeat)[id] !== playerOutputs[id]) throw new Error(`Repeated IL2CPP output differs for ${id}.`);
  }
  if (playerOutputs['arithmetic-repeat-1'] !== playerOutputs['arithmetic-repeat-2'])
    throw new Error('Repeated identical input produced different generated text.');
  if (playerOutputs['arithmetic-repeat-1'] === playerOutputs['arithmetic-changed-class'] ||
      playerOutputs['arithmetic-repeat-1'] === playerOutputs['arithmetic-changed-bytes'])
    throw new Error('Changed input did not change generated text.');
  const parsed = Object.fromEntries(ids.map(id => [id, parseTranslatedSources(playerOutputs[id])]));
  if (parsed.invalid.error !== evidence.checks.invalidDiagnostic)
    throw new Error(`Invalid diagnostic differs: ${parsed.invalid.error}`);
  const summaries = {
    arithmetic: sourceSummary(parsed['arithmetic-repeat-1'].sources),
    arithmeticChanged: sourceSummary(parsed['arithmetic-changed-class'].sources),
    'arithmetic-changed-bytes': sourceSummary(parsed['arithmetic-changed-bytes'].sources),
    clang: sourceSummary(parsed.clang.sources),
    host: sourceSummary(parsed.host.sources)
  };
  for (const [id, expected] of Object.entries(evidence.generatedSources))
    if (JSON.stringify(summaries[id]) !== JSON.stringify(expected))
      throw new Error(`Unity generated-source summary differs from SH-12 for ${id}.`);
  for (const [label, directory, expectedMessage] of [
    ['Player missing', missing, evidence.checks.missingWasm2cs.message],
    ['Editor missing', editorMissing, evidence.checks.missingWasm2cs.message],
    ['Player corrupt', corrupt, evidence.checks.corruptWasm2cs.message],
    ['Editor corrupt', editorCorrupt, evidence.checks.corruptWasm2cs.message]
  ]) {
    const summary = readSummary(directory);
    if (summary.state !== 'Failed' || summary.failureMessage !== expectedMessage)
      throw new Error(`${label} Wasm2Cs.dll failure was not detected: ${JSON.stringify(summary)}`);
  }
  const generatedExecution = {
    arithmetic: compileGeneratedModule('arithmetic', parsed['arithmetic-repeat-1'].sources, 'arithmetic-repeat-1'),
    arithmeticChangedBytes: compileGeneratedModule('arithmetic-changed-bytes', parsed['arithmetic-changed-bytes'].sources, 'arithmetic-changed-bytes'),
    clang: compileGeneratedModule('clang', parsed.clang.sources, 'clang'),
    host: compileGeneratedModule('host', parsed.host.sources, 'host')
  };
  rmSync(il2cppOut, { recursive: true, force: true });
  mkdirSync(il2cppOut, { recursive: true });
  for (const source of [...parsed['arithmetic-repeat-1'].sources, ...parsed['arithmetic-changed-bytes'].sources, ...parsed.clang.sources])
    writeFileSync(join(il2cppOut, source.Name), source.Text);
  json(join(player, 'unity-check.json'), {
    schemaVersion: 1,
    milestone: 'SH-13',
    bundleSha256: manifest.bundleSha256,
    player: playerSummary,
    editor: editorSummary,
    summaries,
    generatedExecution,
    outerEngine: false,
    outerTranslator: false
  });
  console.log('SH-13 Unity output matches SH-12 and the obtained sources compiled outside Unity.');
}

function optionValue(name, fallback) {
  const index = args.indexOf(name);
  if (index < 0) return fallback;
  if (index + 1 >= args.length || args[index + 1].startsWith('--'))
    throw new Error(`${name} requires a value.`);
  return args[index + 1];
}

function runtimeEntry(manifest) {
  const entry = manifest.entries.find(item => item.path === '_framework/dotnet.native.wasm');
  if (!entry) throw new Error('Bundle does not contain _framework/dotnet.native.wasm.');
  return entry;
}

function invokeBundle(request, options = {}) {
  const manifest = verifyBundle();
  const main = join(bundleRoot, 'main.mjs');
  if (!existsSync(main)) throw new Error('Bundle is missing main.mjs.');
  const host = tool('node');
  if (host.version !== `v${profile.node}`) throw new Error(`Node ${profile.node} is required; selected Node is ${host.version ?? 'missing'}.`);
  const requestPath = join(artifactRoot, `.request-${process.pid}.json`);
  const resultPath = join(artifactRoot, `.result-${process.pid}.json`);
  writeFileSync(requestPath, JSON.stringify(request));
  try {
    const result = run(process.execPath, [main], {
      cwd: bundleRoot,
      env: { ...process.env, SELF_HOSTING_REQUEST_FILE: requestPath, SELF_HOSTING_RESULT_FILE: resultPath },
      timeoutMs: options.timeoutMs
    });
    const streamed = request.scenarios.find(scenario => scenario.operation === 'translate-sources-stream');
    let parsed;
    try {
      parsed = streamed ? parseBundleOutput(result.output, streamed.id) : { response: parseReferenceOutput(result.output), sources: [] };
    } catch (error) {
      if (!existsSync(resultPath)) throw error;
      const saved = JSON.parse(readFileSync(resultPath, 'utf8'));
      if (saved?.protocol !== 1) throw error;
      parsed = streamed ? parseBundleOutput(`${result.output}\n${JSON.stringify(saved)}`, streamed.id) : { response: saved, sources: [] };
    }
    return { manifest, response: parsed.response, sources: parsed.sources, host };
  } finally {
    rmSync(requestPath, { force: true });
    rmSync(resultPath, { force: true });
  }
}

function generate() {
  const failurePath = join(artifactRoot, 'generate-failure.json');
  try {
    const manifest = verifyBundle();
    const entry = runtimeEntry(manifest);
    const className = optionValue('--class-name', 'DotnetRuntime');
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(className)) throw new Error(`Invalid generated class name '${className}'.`);
    const wasmPath = join(bundleRoot, ...entry.path.split('/'));
    const wasm = readFileSync(wasmPath);
    const started = process.hrtime.bigint();
    const request = {
      protocol: 1,
      scenarios: [{ id: 'runtime', operation: 'translate-sources-stream', className,
        wasmBase64: wasm.toString('base64'), inputBytes: wasm.length, inputSha256: sha256(wasm) }]
    };
    const invoked = invokeBundle(request);
    const result = invoked.response.results?.find(item => item.id === 'runtime');
    if (!result || result.output !== 'STREAM') throw new Error(result?.output ?? 'Guest did not return generated sources.');
    const sources = invoked.sources;
    if (!Array.isArray(sources) || sources.length === 0) throw new Error('Guest returned no generated sources.');
    const staging = join(artifactRoot, `.generated-${process.pid}`);
    rmSync(staging, { recursive: true, force: true });
    mkdirSync(staging, { recursive: true });
    const names = new Set();
    const entries = sources.map((source, index) => {
      if (!source || typeof source.Name !== 'string' || !/^[A-Za-z_][A-Za-z0-9_.-]*\.g\.cs$/.test(source.Name) ||
          names.has(source.Name) || typeof source.Text !== 'string')
        throw new Error(`Guest returned an invalid source at index ${index}.`);
      names.add(source.Name);
      const bytes = Buffer.from(source.Text, 'utf8');
      writeFileSync(join(staging, source.Name), bytes);
      return { path: source.Name, bytes: bytes.length, sha256: sha256(bytes) };
    });
    const generated = {
      schemaVersion: 1,
      milestone: 'SH-08',
      bundleSha256: manifest.bundleSha256,
      input: { path: entry.path, bytes: wasm.length, sha256: sha256(wasm) },
      className,
      entries,
      metrics: { durationMs: Number(process.hrtime.bigint() - started) / 1e6, hostMaxRss: process.resourceUsage().maxRSS }
    };
    rmSync(join(artifactRoot, 'generated'), { recursive: true, force: true });
    renameSync(staging, join(artifactRoot, 'generated'));
    json(join(artifactRoot, 'generated-manifest.json'), generated);
    rmSync(failurePath, { force: true });
    console.log(`Generated ${sources.length} C# source file(s) for ${className}; ${entries.reduce((sum, item) => sum + item.bytes, 0)} bytes at ${join(artifactRoot, 'generated')}.`);
  } catch (error) {
    json(failurePath, { schemaVersion: 1, milestone: 'SH-08', status: 'blocked', reason: error instanceof Error ? error.message : String(error) });
    throw error;
  }
}

function generatedManifest() {
  const manifestPath = join(artifactRoot, 'generated-manifest.json');
  if (!existsSync(manifestPath)) throw new Error(`Generated sources are missing. Run generate first: ${manifestPath}`);
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const generatedRoot = join(artifactRoot, 'generated');
  if (!existsSync(generatedRoot)) throw new Error('Generated source directory is missing.');
  for (const entry of manifest.entries ?? []) {
    const path = join(generatedRoot, entry.path);
    if (!existsSync(path)) throw new Error(`Generated source is missing: ${entry.path}`);
    const bytes = readFileSync(path);
    if (bytes.length !== entry.bytes || sha256(bytes) !== entry.sha256)
      throw new Error(`Generated source changed: ${entry.path}`);
  }
  return { manifest, generatedRoot };
}

function xml(value) {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
}

function compile() {
  const failurePath = join(artifactRoot, 'compile-failure.json');
  const compiledRoot = join(artifactRoot, 'compiled');
  try {
    const { manifest, generatedRoot } = generatedManifest();
    rmSync(compiledRoot, { recursive: true, force: true });
    mkdirSync(compiledRoot, { recursive: true });
    const runtimeProject = join(root, 'src', 'Wasm2Cs.Runtime', 'Wasm2Cs.Runtime.csproj');
    run('dotnet', ['build', runtimeProject, '--configuration', 'Release', '--framework', 'netstandard2.0', '--nologo']);
    const runtimeDll = join(root, 'src', 'Wasm2Cs.Runtime', 'bin', 'Release', 'netstandard2.0', 'Wasm2Cs.Runtime.dll');
    if (!existsSync(runtimeDll)) throw new Error(`Runtime assembly is missing: ${runtimeDll}`);
    const projectPath = join(compiledRoot, 'Generated.csproj');
    writeFileSync(projectPath, `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>9.0</LangVersion><ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <OutputType>Library</OutputType></PropertyGroup>
  <ItemGroup><Compile Include="${xml(join(generatedRoot, '*.g.cs'))}" /><Reference Include="Wasm2Cs.Runtime"><HintPath>${xml(runtimeDll)}</HintPath></Reference></ItemGroup>
</Project>
`);
    const started = process.hrtime.bigint();
    const result = run('dotnet', ['build', projectPath, '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
    const assemblyPath = join(compiledRoot, 'bin', 'Release', 'net10.0', 'Generated.dll');
    if (!existsSync(assemblyPath)) throw new Error(`Compiled assembly is missing: ${assemblyPath}`);
    json(join(artifactRoot, 'compile-results.json'), {
      schemaVersion: 1, milestone: 'SH-08', bundleSha256: manifest.bundleSha256,
      className: manifest.className, sourceCount: manifest.entries.length,
      sourceManifest: 'generated-manifest.json', sourceDirectory: 'generated',
      projectPath: 'compiled/Generated.csproj', assemblyPath: 'compiled/bin/Release/net10.0/Generated.dll',
      assemblySha256: sha256File(assemblyPath), retained: true,
      durationMs: Number(process.hrtime.bigint() - started) / 1e6,
      output: result.output
    });
    rmSync(failurePath, { force: true });
    console.log(`Compiled ${manifest.entries.length} generated C# source file(s) for ${manifest.className}; assembly saved at ${assemblyPath}.`);
  } catch (error) {
    json(failurePath, { schemaVersion: 1, milestone: 'SH-08', status: 'blocked', reason: error instanceof Error ? error.message : String(error) });
    throw error;
  }
}

function prepare() {
  mkdirSync(environmentRoot, { recursive: true });
  rmSync(bundleRoot, { recursive: true, force: true });
  rmSync(manifestPath, { force: true });
  const log = [];
  let info;
  try {
    info = dotnetInfo();
    log.push(`$ dotnet --info\n${info.text}`);
    if (info.version !== profile.sdk) throw new Error(`SDK ${profile.sdk} is required; selected SDK is ${info.version}.`);
    const env = isolatedEnvironment(info.basePath);
    const initialWorkloads = recordCommand(log, 'dotnet', ['workload', 'list'], { env, allowFailure: true });
    let workloadText = initialWorkloads.output;
    if (!workloadText.includes(profile.workloadManifest)) {
      if (args.includes('--skip-workload-install')) {
        throw new Error(`wasm-tools ${profile.workloadManifest} is not installed; rerun without --skip-workload-install in the dedicated environment.`);
      }
      const install = recordCommand(log, 'dotnet', [
        'workload', 'install', 'wasm-tools', '--version', profile.workloadSet, '--disable-parallel',
        '--temp-dir', join(environmentRoot, 'workload-temp')
      ], { env, allowFailure: true });
      if (install.status !== 0) throw new Error(`Dedicated wasm-tools installation failed (exit ${install.status}).\n${install.output}`);
      workloadText = recordCommand(log, 'dotnet', ['workload', 'list'], { env }).output;
      if (!workloadText.includes(profile.workloadManifest)) throw new Error(`wasm-tools ${profile.workloadManifest} did not appear in the dedicated workload list.`);
    }
    const toolchain = {
      schemaVersion: 1,
      milestone: 'SH-01',
      profile: profile.profile,
      sdk: { requested: profile.sdk, selected: info.version, basePath: info.basePath },
      workload: workloadText,
      tools: {
        node: tool('node'),
        wabt: ['wasm-validate', 'wasm-objdump', 'wasm2wat'].map(name => tool(savedWabtTool(name) ?? name)),
        clang: tool('clang'),
        wasmLd: tool('wasm-ld'),
        emcc: tool('emcc')
      },
      environment: {
        cliHome: 'environment/dotnet-home',
        nugetPackages: 'environment/nuget-packages',
        msbuildUserExtensions: 'environment/msbuild',
        workloadTemp: 'environment/workload-temp'
      },
      build: {
        project: 'samples/SelfHosting/SelfHosting.csproj',
        configuration: 'Release',
        runtimeIdentifier: profile.runtimeIdentifier,
        properties: profile.buildProperties,
        runtimeOptions: profile.runtimeOptions
      }
    };
    const missingWabt = toolchain.tools.wabt.filter(item => !item.available);
    if (missingWabt.length) throw new Error(`Pinned WABT tools are required during prepare: ${missingWabt.map(item => item.command).join(', ')}`);
    const wrongWabt = toolchain.tools.wabt.filter(item => !item.version?.includes(profile.wabt));
    if (wrongWabt.length) throw new Error(`WABT ${profile.wabt} is required; got ${wrongWabt.map(item => `${item.command}: ${item.version}`).join(', ')}`);
    if (toolchain.tools.node.version !== `v${profile.node}`) {
      throw new Error(`Node ${profile.node} is required; selected Node is ${toolchain.tools.node.version ?? 'missing'}.`);
    }
    json(join(artifactRoot, 'toolchain.json'), toolchain);
    const coreProject = join(root, 'src', 'Wasm2Cs', 'Wasm2Cs.csproj');
    recordCommand(log, 'dotnet', ['build', coreProject, '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo'], { env });
    const publishRoot = join(root, 'samples', 'SelfHosting', 'bin', 'Release');
    rmSync(publishRoot, { recursive: true, force: true });
    rmSync(join(root, 'samples', 'SelfHosting', 'obj', 'Release'), { recursive: true, force: true });
    const project = join(root, 'samples', 'SelfHosting', 'SelfHosting.csproj');
    recordCommand(log, 'dotnet', [
      'publish', project, '--configuration', 'Release', '--runtime', profile.runtimeIdentifier,
      '-m:1', '-p:UseSharedCompilation=false', '-p:TargetLatestRuntimePatch=false',
      `-p:RuntimeFrameworkVersion=${profile.runtimePack}`, '--nologo'
    ], { env });
    const appBundle = walkFiles(publishRoot).map(path => dirname(path)).find(path =>
      path.endsWith('AppBundle') && existsSync(join(path, 'main.mjs')) &&
      existsSync(join(path, '_framework', 'dotnet.native.wasm')));
    if (!appBundle) throw new Error('publish completed without an AppBundle containing main.mjs and dotnet.native.wasm.');
    rmSync(bundleRoot, { recursive: true, force: true });
    cpSync(appBundle, bundleRoot, { recursive: true });
    const buildOutputRoot = dirname(appBundle);
    const evidenceDirectory = join(bundleRoot, 'build-evidence');
    for (const name of ['wasm-props.json', 'SelfHosting.deps.json']) {
      const source = join(buildOutputRoot, name);
      if (!existsSync(source)) throw new Error(`publish did not produce ${name}; effective settings cannot be recorded.`);
      mkdirSync(evidenceDirectory, { recursive: true });
      cpSync(source, join(evidenceDirectory, name));
    }
    const depsText = readFileSync(join(evidenceDirectory, 'SelfHosting.deps.json'), 'utf8');
    const runtimePack = /runtimepack\.Microsoft\.NETCore\.App\.Runtime\.Mono\.browser-wasm[\\/]([0-9.]+)/i.exec(depsText)?.[1];
    if (runtimePack !== profile.runtimePack) throw new Error(`Expected runtime pack ${profile.runtimePack}; publish used ${runtimePack ?? 'none'}.`);
    const wasmProps = JSON.parse(readFileSync(join(evidenceDirectory, 'wasm-props.json'), 'utf8'));
    const emccVersion = wasmProps.items?.EmccProperties?.find(item => item.identity === 'RuntimeEmccVersion')?.value ?? null;
    const toolchainPath = join(artifactRoot, 'toolchain.json');
    const updatedToolchain = JSON.parse(readFileSync(toolchainPath, 'utf8'));
    updatedToolchain.runtimePack = runtimePack;
    updatedToolchain.emscripten = { version: emccVersion, source: 'build-evidence/wasm-props.json' };
    json(toolchainPath, updatedToolchain);
    json(join(bundleRoot, 'effective-build.json'), {
      schemaVersion: 1,
      profile: profile.profile,
      targetFramework: profile.targetFramework,
      runtimeIdentifier: profile.runtimeIdentifier,
      runtimePack,
      emscripten: emccVersion,
      buildProperties: profile.buildProperties,
      runtimeOptions: profile.runtimeOptions,
      sourceProject: 'samples/SelfHosting/SelfHosting.csproj',
      evidence: ['build-evidence/wasm-props.json', 'build-evidence/SelfHosting.deps.json']
     });
     const manifest = writeManifest();
     verifyCanonicalBundle(manifest);
     json(join(environmentRoot, 'cache-stamp.json'), {
       schemaVersion: 1,
       bundleSha256: manifest.bundleSha256,
       cacheKey: cacheKey(manifest.bundleSha256)
     });
     rmSync(join(artifactRoot, 'prepare-failure.json'), { force: true });
     console.log(`Prepared bundle at ${bundleRoot}; bundle SHA-256: ${manifest.bundleSha256}`);
  } catch (error) {
    json(join(artifactRoot, 'prepare-failure.json'), {
      schemaVersion: 1,
      milestone: 'SH-01',
      status: 'blocked',
      requiredSdk: profile.sdk,
      selectedSdk: info?.version ?? null,
      reason: error instanceof Error ? error.message : String(error)
    });
    throw error;
  } finally {
    writeFileSync(join(artifactRoot, 'prepare.log'), log.join('\n\n'));
  }
}

try {
  if (!command || command === '--help' || command === '-h') {
    console.log(help);
  } else if (command === 'prepare') {
    prepare();
  } else if (command === 'inventory') {
    inventory();
  } else if (command === 'reference') {
    reference();
  } else if (command === 'generate') {
    generate();
  } else if (command === 'compile') {
    compile();
  } else if (command === 'host') {
    host();
  } else if (command === 'hello') {
    hello();
  } else if (command === 'managed') {
    managed();
  } else if (command === 'translate') {
    translate();
  } else if (command === 'audit') {
    audit();
  } else if (command === 'verify') {
    verify();
  } else if (command === 'unity-prepare') {
    unityPrepare();
  } else if (command === 'unity-check') {
    unityCheck();
  } else {
    console.error(help);
    process.exitCode = 2;
  }
} catch (error) {
  console.error(`self-hosting: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}
