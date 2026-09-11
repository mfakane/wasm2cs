import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const commit = '977f97014c962f7bd1291fcc6d28b41a924882bf';
const source = `https://raw.githubusercontent.com/WebAssembly/spec/${commit}/test/core/i64.wast`;
const response = await fetch(source);
if (!response.ok) throw new Error(`i64.wast: ${response.status}`);
const bytes = Buffer.from(await response.arrayBuffer());
if (createHash('sha256').update(bytes).digest('hex') !== '36f1d5b1f27495db6ce12dfecc9ed9e5f4a1bbae300357a655901ee866604de5')
  throw new Error('Pinned i64.wast hash differs');
const input = fileURLToPath(new URL('../../tests/Conformance/i64.wast', import.meta.url));
const output = fileURLToPath(new URL('../../tests/Conformance/i64.json', import.meta.url));
writeFileSync(input, bytes);
execFileSync(process.execPath, [fileURLToPath(new URL('prepare-typed.mjs', import.meta.url)), input, output], { stdio: 'inherit' });
const document = JSON.parse(readFileSync(output, 'utf8'));
writeFileSync(output, JSON.stringify({ ...document, Commit: commit, SourceUrl: source }, null, 2) + '\n');
