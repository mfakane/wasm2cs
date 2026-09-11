import { readFileSync, writeFileSync } from 'node:fs';
const bytes = readFileSync(new URL('../samples/CAlgorithms/Algorithms.wasm',import.meta.url));
const instance = new WebAssembly.Instance(new WebAssembly.Module(bytes));
const cases = [];
for (const Seed of [0,1,-1,-2147483648,2147483647]) {
  for (const Length of [0,1,8,64,255,256,300]) {
    cases.push({ Seed, Length, Expected: instance.exports.f(Seed,Length) });
  }
}
writeFileSync(new URL('../unity/Smoke/AlgorithmsGolden.json',import.meta.url), JSON.stringify({ Cases: cases },null,2)+'\n');
console.log(`Generated ${cases.length} WebAssembly oracle vectors for Unity.`);
