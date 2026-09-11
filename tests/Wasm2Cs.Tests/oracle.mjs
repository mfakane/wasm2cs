import { readFileSync } from 'node:fs';
const wasm = new WebAssembly.Instance(new WebAssembly.Module(readFileSync(process.argv[2])));
const calls = JSON.parse(readFileSync(0, 'utf8'));
console.log(JSON.stringify(calls.map(call => wasm.exports[call.Name](...call.Args))));
