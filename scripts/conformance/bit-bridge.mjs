import { decodeValue, encodeValue } from './values.mjs';

// WASM-to-WASM calls keep signaling NaNs out of JS Number conversions. Only integer
// bits cross the JS boundary, including each element of a multiple-value result.
const types = { i32: 0x7f, i64: 0x7e, f32: 0x7d, f64: 0x7c };
const integer = type => type === 'f32' ? 'i32' : type === 'f64' ? 'i64' : type;
const fromBits = type => type === 'f32' ? [0xbe] : type === 'f64' ? [0xbf] : [];
const toBits = type => type === 'f32' ? [0xbc] : type === 'f64' ? [0xbd] : [];
function u32(n) { const bytes = []; do { const b = n & 127; n >>>= 7; bytes.push(b | (n ? 128 : 0)); } while (n); return bytes; }
const vector = items => [...u32(items.length), ...items.flat()];
const section = (id, bytes) => [id, ...u32(bytes.length), ...bytes];
const signature = (args, results) => [0x60, ...vector(args.map(t => types[t])), ...vector(results.map(t => types[t]))];
const cache = new WeakMap();

export function invokeBits(fn, args, results) {
  const parameters = args.map(a => a.Type);
  const key = JSON.stringify([parameters, results]);
  let functions = cache.get(fn);
  if (!functions) cache.set(fn, functions = new Map());
  let bridge = functions.get(key);
  if (!bridge) {
    const body = [...vector(results.map(t => [1, types[t]])),
      ...parameters.flatMap((t, i) => [0x20, ...u32(i), ...fromBits(t)]), 0x10, 0,
      ...results.flatMap((_, i) => [0x21, ...u32(parameters.length + results.length - 1 - i)]),
      ...results.flatMap((t, i) => [0x20, ...u32(parameters.length + i), ...toBits(t)]), 0x0b];
    const bytes = [0,97,115,109,1,0,0,0,
      ...section(1, vector([signature(parameters, results), signature(parameters.map(integer), results.map(integer))])),
      ...section(2, [1, 1, 109, 1, 102, 0, 0]), // import m.f, type 0
      ...section(3, [1, 1]), ...section(7, [1, 1, 102, 0, 1]),
      ...section(10, [1, ...u32(body.length), ...body])];
    bridge = new WebAssembly.Instance(new WebAssembly.Module(Uint8Array.from(bytes)), { m: { f: fn } }).exports.f;
    functions.set(key, bridge);
  }
  const value = bridge(...args.map(a => decodeValue({ ...a, Type: integer(a.Type) })));
  const values = results.length === 0 ? [] : results.length === 1 ? [value] : value;
  return values.map((v, i) => ({ ...encodeValue(integer(results[i]), v), Type: results[i] }));
}

export function getBits(global, type) {
  const body = [0, 0x23, 0, ...toBits(type), 0x0b];
  function build(mutable) {
    return new WebAssembly.Module(Uint8Array.from([0,97,115,109,1,0,0,0,
      ...section(1, vector([signature([], [integer(type)])])),
      ...section(2, [1, 1, 109, 1, 103, 3, types[type], mutable]),
      ...section(3, [1, 0]), ...section(7, [1, 1, 102, 0, 0]),
      ...section(10, [1, ...u32(body.length), ...body])]));
  }
  let instance;
  try { instance = new WebAssembly.Instance(build(0), { m: { g: global } }); }
  catch (error) {
    if (!(error instanceof WebAssembly.LinkError)) throw error;
    instance = new WebAssembly.Instance(build(1), { m: { g: global } });
  }
  return { ...encodeValue(integer(type), instance.exports.f()), Type: type };
}
