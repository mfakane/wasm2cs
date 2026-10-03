// Count every instruction in every code body of each .wasm (via WABT wasm-objdump -d).
// Local declarations (local[..]) are skipped.
// The CLI stops at its first rejection; this walk does not, so the counts cover whole modules.
// Usage: node scripts/wasm-opcodes.mjs <file.wasm>...
import { execFileSync } from 'node:child_process';

for (const file of process.argv.slice(2)) {
  const text = execFileSync('wasm-objdump', ['-d', file], { encoding: 'utf8', maxBuffer: 1 << 28 });
  const counts = new Map();
  for (const line of text.split('\n')) {
    const bar = line.indexOf('| ');
    if (!/^\s*[0-9a-f]+:/.test(line) || bar < 0) continue;
    const name = line.slice(bar + 2).trim().split(/\s+/)[0];
    if (name && !name.startsWith('local[')) counts.set(name, (counts.get(name) ?? 0) + 1);
  }
  const sorted = [...counts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  console.log(`${file}: ${sorted.reduce((n, [, c]) => n + c, 0)} instructions, ${sorted.length} distinct`);
  for (const [name, count] of sorted) console.log(`  ${name} ${count}`);
}
