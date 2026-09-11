import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { convertCommands, verifyReference } from './commands.mjs';

const [input, output] = process.argv.slice(2);
if (!input || !output || process.argv.length !== 4) throw new Error('Usage: node scripts/conformance/prepare-typed.mjs input.wast output.json');
const wabtVersion = execFileSync('wast2json', ['--version'], { encoding: 'utf8' }).trim();
if (wabtVersion !== '1.0.41' || process.version !== 'v22.17.0') throw new Error('Regeneration requires WABT 1.0.41 and Node.js v22.17.0');
const directory = mkdtempSync(join(tmpdir(), 'wasm2cs-wast-'));
try {
  const json = join(directory, 'commands.json');
  execFileSync('wast2json', [resolve(input), '-o', json], { stdio: 'inherit' });
  const cases = convertCommands(JSON.parse(readFileSync(json, 'utf8')), file => readFileSync(join(directory, file)));
  const counts = verifyReference(cases);
  writeFileSync(output, JSON.stringify({ SchemaVersion: 2, Source: basename(input),
    SourceSha256: createHash('sha256').update(readFileSync(input)).digest('hex'),
    Wabt: wabtVersion, Engine: { Node: process.version, V8: process.versions.v8 }, Counts: counts, Cases: cases }, null, 2) + '\n');
  console.log(`${output}: ${JSON.stringify(counts)}`);
} finally { rmSync(directory, { recursive: true, force: true }); }
