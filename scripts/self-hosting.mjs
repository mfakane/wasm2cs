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
import { tmpdir } from 'node:os';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const artifactRoot = join(root, 'artifacts', 'self-hosting');
const environmentRoot = join(artifactRoot, 'environment');
const bundleRoot = join(artifactRoot, 'bundle');
const manifestPath = join(artifactRoot, 'bundle-manifest.json');
const profilePath = join(root, 'docs', 'self-hosting', 'SH-01-profile.json');
const profile = JSON.parse(readFileSync(profilePath, 'utf8'));
const args = process.argv.slice(2);
const command = args[0];

const help = `Usage: node scripts/self-hosting.mjs <prepare|inventory|reference|generate|compile|host>

prepare    install/check the isolated workload and publish the guest bundle
inventory  validate the bundle and record a full WABT inventory
reference  run the bundle through the official Node.js browser-wasm host
generate   translate dotnet.native.wasm into deterministic C# source files
compile    compile the generated C# files against the runtime ABI
host       run the C# SH-09 ABI fixture and construct the full generated runtime

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
    maxBuffer: 512 * 1024 * 1024
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

function parseReferenceOutput(output) {
  for (const line of output.trim().split(/\r?\n/).reverse()) {
    try {
      const value = JSON.parse(line);
      if (value?.protocol === 1 && !value.stream) return value;
    } catch {
      // Runtime diagnostics are retained in the log; keep looking for the protocol line.
    }
  }
  throw new Error(`The guest did not emit a reference protocol result.\n${output}`);
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
  if (!response) throw new Error(`The guest did not emit a reference protocol result.\n${output}`);
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
  const record = {
    schemaVersion: 1,
    milestone: 'SH-01',
    profile: profile.profile,
    bundleSha256: manifest.bundleSha256,
    host: { node: host, command: 'official browser-wasm AppBundle/main.mjs' },
    request: { protocol: request.protocol, scenarios: request.scenarios.map(({ id, operation, className, inputBytes, inputSha256 }) => ({ id, operation, className, inputBytes, inputSha256 })) },
    results: response.results
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
  if (!wat2wasm) throw new Error('Pinned wat2wasm is required for the SH-09 fixture.');
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

function hostRunnerSource() {
  const now = csharpIdentifier('env', 'emscripten_get_now');
  const entropy = csharpIdentifier('env', 'mono_wasm_browser_entropy');
  const write = csharpIdentifier('wasi_snapshot_preview1', 'fd_write');
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
var fullRuntime = new DotnetRuntime(new DotnetRuntime.Bindings());
_ = fullRuntime.memory;
_ = fullRuntime.__indirect_function_table;
Console.WriteLine("HOST_ABI_RESULT:" + JsonSerializer.Serialize(actual));
`;
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

function invokeBundle(request) {
  const manifest = verifyBundle();
  const main = join(bundleRoot, 'main.mjs');
  if (!existsSync(main)) throw new Error('Bundle is missing main.mjs.');
  const host = tool('node');
  if (host.version !== `v${profile.node}`) throw new Error(`Node ${profile.node} is required; selected Node is ${host.version ?? 'missing'}.`);
  const requestPath = join(artifactRoot, `.request-${process.pid}.json`);
  writeFileSync(requestPath, JSON.stringify(request));
  try {
    const result = run(process.execPath, [main], {
      cwd: bundleRoot,
      env: { ...process.env, SELF_HOSTING_REQUEST_FILE: requestPath }
    });
    const stream = request.scenarios.find(scenario => scenario.operation === 'translate-sources-stream');
    const parsed = stream ? parseBundleOutput(result.output, stream.id) : { response: parseReferenceOutput(result.output), sources: [] };
    return { manifest, response: parsed.response, sources: parsed.sources, host };
  } finally {
    rmSync(requestPath, { force: true });
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
    console.log(`Generated ${sources.length} C# source file(s) for ${className}; ${entries.reduce((sum, item) => sum + item.bytes, 0)} bytes.`);
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
  try {
    const { manifest, generatedRoot } = generatedManifest();
    const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-sh08-compile-'));
    try {
      for (const entry of manifest.entries) writeFileSync(join(directory, entry.path), readFileSync(join(generatedRoot, entry.path)));
      const runtimeProject = join(root, 'src', 'Wasm2Cs.Runtime', 'Wasm2Cs.Runtime.csproj');
      run('dotnet', ['build', runtimeProject, '--configuration', 'Release', '--framework', 'netstandard2.0', '--nologo']);
      const runtimeDll = join(root, 'src', 'Wasm2Cs.Runtime', 'bin', 'Release', 'netstandard2.0', 'Wasm2Cs.Runtime.dll');
      if (!existsSync(runtimeDll)) throw new Error(`Runtime assembly is missing: ${runtimeDll}`);
      writeFileSync(join(directory, 'Generated.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>9.0</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <OutputType>Library</OutputType></PropertyGroup>
  <ItemGroup><Compile Include="*.g.cs" /><Reference Include="Wasm2Cs.Runtime"><HintPath>${xml(runtimeDll)}</HintPath></Reference></ItemGroup>
</Project>
`);
      const started = process.hrtime.bigint();
      const result = run('dotnet', ['build', join(directory, 'Generated.csproj'), '--configuration', 'Release', '-m:1', '-p:UseSharedCompilation=false', '--nologo']);
      json(join(artifactRoot, 'compile-results.json'), {
        schemaVersion: 1, milestone: 'SH-08', bundleSha256: manifest.bundleSha256,
        className: manifest.className, sourceCount: manifest.entries.length,
        durationMs: Number(process.hrtime.bigint() - started) / 1e6,
        output: result.output
      });
      rmSync(failurePath, { force: true });
      console.log(`Compiled ${manifest.entries.length} generated C# source file(s) for ${manifest.className}.`);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
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
    if (!/^\s*wasm-tools(?:\s|$)/mi.test(workloadText)) {
      if (args.includes('--skip-workload-install')) {
        throw new Error('wasm-tools is not installed; rerun without --skip-workload-install in the dedicated environment.');
      }
      const install = recordCommand(log, 'dotnet', [
        'workload', 'install', 'wasm-tools', '--skip-manifest-update', '--disable-parallel',
        '--temp-dir', join(environmentRoot, 'workload-temp')
      ], { env, allowFailure: true });
      if (install.status !== 0) throw new Error(`Dedicated wasm-tools installation failed (exit ${install.status}).`);
      workloadText = recordCommand(log, 'dotnet', ['workload', 'list'], { env }).output;
      if (!/^\s*wasm-tools(?:\s|$)/mi.test(workloadText)) throw new Error('wasm-tools installation did not appear in the dedicated workload list.');
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
      '-m:1', '-p:UseSharedCompilation=false', '--nologo'
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
  } else {
    console.error(help);
    process.exitCode = 2;
  }
} catch (error) {
  console.error(`self-hosting: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}
