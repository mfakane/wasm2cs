import { readFileSync } from 'node:fs';
const input = JSON.parse(readFileSync(0, 'utf8'));
const instance = new WebAssembly.Instance(new WebAssembly.Module(Buffer.from(input.Module, 'base64')));
console.log(JSON.stringify(input.Calls.map(args => {
  try { return { Trapped: false, Value: instance.exports.f(...args) ?? 0 }; }
  catch (error) {
    if (!(error instanceof WebAssembly.RuntimeError)) throw error;
    return { Trapped: true, Value: 0 };
  }
})));
