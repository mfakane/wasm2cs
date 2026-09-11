import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { convertCommands, verifyReference } from './commands.mjs';

const pins = JSON.parse(readFileSync(new URL('floats-sources.json', import.meta.url)));
const root = fileURLToPath(new URL('../../tests/Conformance/', import.meta.url));
const wabt = execFileSync('wast2json', ['--version'], { encoding: 'utf8' }).trim();
if (wabt !== '1.0.41' || process.version !== 'v22.17.0') throw new Error('Requires WABT 1.0.41 and Node v22.17.0');
function u32(n) { const b = []; do { const v = n & 127; n >>>= 7; b.push(v | (n ? 128 : 0)); } while (n); return b; }
function renameBinary(bytes, rename) {
  let position = 8;
  function read() { let n = 0, shift = 0, b; do { b = bytes[position++]; n += (b & 127) * 2 ** shift; shift += 7; } while (b & 128); return n; }
  const output = [...bytes.subarray(0, 8)];
  while (position < bytes.length) {
    const start = position, id = bytes[position++], size = read(), end = position + size;
    if (id !== 7) { output.push(...bytes.subarray(start, end)); position = end; continue; }
    const count = read(), section = [...u32(count)];
    for (let i = 0; i < count; i++) {
      const length = read(), name = bytes.subarray(position, position + length).toString('utf8'); position += length;
      const renamed = Buffer.from(rename(name)), kind = bytes[position++], index = read();
      section.push(...u32(renamed.length), ...renamed, kind, ...u32(index));
    }
    if (position !== end) throw new Error('Invalid export section in fixture');
    output.push(7, ...u32(section.length), ...section);
  }
  return Buffer.from(output);
}
for (const [name, hash] of Object.entries(pins.Files)) {
  const url = `https://raw.githubusercontent.com/WebAssembly/spec/${pins.Commit}/test/core/${name}.wast`;
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${name}: ${response.status}`);
  const bytes = Buffer.from(await response.arrayBuffer());
  if (createHash('sha256').update(bytes).digest('hex') !== hash) throw new Error(`${name}: pinned hash differs`);
  writeFileSync(join(root, name + '.wast'), bytes);
  const renames = {};
  function rename(field) {
    if (/^[A-Za-z_][A-Za-z_0-9]*$/.test(field)) return field;
    const renamed = 'export_' + Buffer.from(field).toString('hex');
    renames[field] = renamed;
    return renamed;
  }
  const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-floats-'));
  try {
    const json = join(directory, 'commands.json'), output = join(root, name + '.json');
    execFileSync('wast2json', [join(root, name + '.wast'), '-o', json], { stdio: 'inherit' });
    const cases = convertCommands(JSON.parse(readFileSync(json)), file => readFileSync(join(directory, file)));
    const counts = verifyReference(cases);
    // C# identifiers are a current public-API restriction. Rename only binary
    // export fields and matching actions, after verifying the original modules.
    for (const test of cases) {
      if (test.Kind === 'module' || test.Kind === 'assert_invalid') {
        test.Binary = renameBinary(Buffer.from(test.Binary, 'base64'), rename).toString('base64');
      }
      if (test.Export !== undefined) test.Export = rename(test.Export);
    }
    if (JSON.stringify(verifyReference(cases)) !== JSON.stringify(counts)) throw new Error('Export adaptation changed counts');
    writeFileSync(output, JSON.stringify({ SchemaVersion: 2, Source: name + '.wast', SourceSha256: hash,
      Wabt: wabt, Engine: { Node: process.version, V8: process.versions.v8 }, Counts: counts, Cases: cases, Commit: pins.Commit, SourceUrl: url,
      ExportRenames: renames }, null, 2) + '\n');
    console.log(`${name}: ${JSON.stringify(counts)}`);
  } finally { rmSync(directory, { recursive: true, force: true }); }
}
