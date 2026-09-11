import { readFileSync } from 'node:fs';
import { invokeBits, getBits } from './ConformanceTools/bit-bridge.mjs';
import { referenceTrap } from './ConformanceTools/commands.mjs';
const input = JSON.parse(readFileSync(0, 'utf8'));
const instance = new WebAssembly.Instance(new WebAssembly.Module(Buffer.from(input.Module, 'base64')),
  { env: { exchange32: v => v, exchange64: v => v } });
new Uint8Array(instance.exports.memory.buffer).set(Buffer.from(input.Memory, 'base64'));
function nan(value) {
  if (!value.Type.startsWith('f')) return false;
  const bits = BigInt('0x' + value.Bits);
  return value.Type === 'f32' ? (bits & 0x7fffffffn) > 0x7f800000n : (bits & 0x7fffffffffffffffn) > 0x7ff0000000000000n;
}
const outcomes = input.Calls.map(call => {
  try { return { Values: invokeBits(instance.exports[call.Export], call.Args, call.Results) }; }
  catch (error) {
    let trap = referenceTrap(error);
    // V8 merges NaN and out-of-range conversions into one diagnostic. These
    // single-instruction conversion calls let us classify the input bits exactly.
    if (trap === 'FloatUnrepresentable') trap = nan(call.Args[0]) ? 'InvalidConversionToInteger' : 'IntegerOverflow';
    return { Trap: trap };
  }
});
console.log(JSON.stringify({ Outcomes: outcomes, Memory: Buffer.from(instance.exports.memory.buffer).toString('base64'),
  Globals: { g32: getBits(instance.exports.g32, 'f32'), g64: getBits(instance.exports.g64, 'f64') } }));
