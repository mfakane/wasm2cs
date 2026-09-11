import { readFileSync } from 'node:fs';
import { decodeValue, encodeValue } from './ConformanceTools/values.mjs';
import { referenceTrap } from './ConformanceTools/commands.mjs';
const input = JSON.parse(readFileSync(0, 'utf8'));
const hostCalls = [];
const imports = input.ImportMask ? { env: { exchange(value) {
  hostCalls.push(encodeValue('i64', value));
  return BigInt.asIntN(64, value ^ decodeValue(input.ImportMask));
} } } : {};
const instance = new WebAssembly.Instance(new WebAssembly.Module(Buffer.from(input.Module, 'base64')), imports);
const memory = Object.values(instance.exports).find(value => value instanceof WebAssembly.Memory);
if (input.Memory) new Uint8Array(memory.buffer).set(Buffer.from(input.Memory, 'base64'));
const outcomes = input.Calls.map(call => {
  try {
    const value = instance.exports[call.Export](...call.Args.map(decodeValue));
    const values = value === undefined ? [] : Array.isArray(value) ? value : [value];
    if (values.length !== call.Results.length) throw new Error('Reference return arity differs');
    return { Values: values.map((v, i) => encodeValue(call.Results[i], v)) };
  } catch (error) { return { Trap: referenceTrap(error) }; }
});
const globals = Object.fromEntries(Object.entries(instance.exports).filter(([, value]) => value instanceof WebAssembly.Global)
  .map(([name, global]) => [name, encodeValue('i64', global.value)]));
console.log(JSON.stringify({ Outcomes: outcomes, Memory: memory ? Buffer.from(memory.buffer).toString('base64') : null,
  Globals: globals, HostCalls: hostCalls }));
