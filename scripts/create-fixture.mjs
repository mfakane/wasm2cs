// Dependency-free binary fixture assembler. The WAT companion is for human review.
import { writeFileSync } from 'node:fs';
const u32 = n => {
  const bytes = [];
  do { const b = n & 127; n >>>= 7; bytes.push(b | (n ? 128 : 0)); } while (n);
  return bytes;
};
const vector = values => [...u32(values.length), ...values.flat()];
const section = (id, bytes) => [id, ...u32(bytes.length), ...bytes];
const name = text => [...u32(Buffer.byteLength(text)), ...Buffer.from(text)];
const types = [[0x60, 2, 0x7f, 0x7f, 1, 0x7f], [0x60, 1, 0x7f, 1, 0x7f], [0x60, 0, 1, 0x7f]];
const functions = [0, 0, 0, 1, 1, 2, 2, 1, 1];
const names = ['add', 'sub', 'mul', 'square', 'snapshot', 'minimum', 'negative', 'tee', 'early'];
const bodies = [
  [0, 0x20, 0, 0x20, 1, 0x6a, 0x0b],
  [0, 0x20, 0, 0x20, 1, 0x6b, 0x0b],
  [0, 0x20, 0, 0x20, 1, 0x6c, 0x0b],
  [0, 0x20, 0, 0x20, 0, 0x6c, 0x0b],
  // Push the original parameter, mutate it, then add a zero-initialized local.
  [1, 1, 0x7f, 0x20, 0, 0x41, 42, 0x21, 0, 0x20, 1, 0x6a, 0x0b],
  [0, 0x41, 0x80, 0x80, 0x80, 0x80, 0x78, 0x0b],
  [0, 0x41, 0x7f, 0x0b],
  [1, 1, 0x7f, 0x20, 0, 0x22, 1, 0x20, 1, 0x6a, 0x0b],
  [0, 0x01, 0x41, 5, 0x1a, 0x41, 9, 0x20, 0, 0x0f, 0x0b],
];
const binary = Uint8Array.from([
  0, 97, 115, 109, 1, 0, 0, 0,
  ...section(1, vector(types)),
  ...section(3, vector(functions.map(n => u32(n)))),
  ...section(7, vector(names.map((n, i) => [...name(n), 0, ...u32(i)]))),
  ...section(10, vector(bodies.map(b => [...u32(b.length), ...b]))),
]);
if (!WebAssembly.validate(binary)) throw new Error('Invalid fixture');
writeFileSync(new URL('../samples/Smoke/Arithmetic.wasm', import.meta.url), binary);
console.log(`Wrote ${binary.length}-byte Arithmetic.wasm`);
