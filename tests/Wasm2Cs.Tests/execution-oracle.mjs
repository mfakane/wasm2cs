import { readFileSync } from 'node:fs';
const input = JSON.parse(readFileSync(0, 'utf8'));
let instance;
try { instance = new WebAssembly.Instance(new WebAssembly.Module(Buffer.from(input.Module, 'base64'))); }
catch (error) {
  if (!(error instanceof WebAssembly.RuntimeError)) throw error;
  console.log(JSON.stringify({ InstantiationTrap: true }));
  process.exit(0);
}
const exportFunction = instance.exports[input.Export ?? 'f'];
const outcomes = input.Calls.map(args => {
  try { return { Trapped: false, Value: exportFunction(...args) ?? 0 }; }
  catch (error) {
    if (!(error instanceof WebAssembly.RuntimeError)) throw error;
    return { Trapped: true, Value: 0 };
  }
});
const memory = Object.values(instance.exports).find(value => value instanceof WebAssembly.Memory);
const globals = Object.fromEntries(Object.entries(instance.exports).filter(([, value]) => value instanceof WebAssembly.Global)
  .map(([name, value]) => [name, value.value]));
console.log(JSON.stringify({ Outcomes: outcomes, Memory: memory ? Buffer.from(memory.buffer).toString('base64') : null, Globals: globals }));
