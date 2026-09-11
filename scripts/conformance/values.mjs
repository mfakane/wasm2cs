// JSON transports bits as fixed-width hexadecimal strings, never floating-point numbers.
const widths = { i32: 32, i64: 64, f32: 32, f64: 64 };
export function validateValue(value, allowNaN = false) {
  const width = widths[value.Type];
  if (!width) throw new Error(`Unsupported value type: ${value.Type}`);
  if (allowNaN && value.NaN !== undefined) {
    if (!value.Type.startsWith('f') || !['canonical', 'arithmetic'].includes(value.NaN) || value.Bits !== undefined)
      throw new Error('Invalid NaN expectation');
  } else if (typeof value.Bits !== 'string' || !new RegExp(`^[0-9a-f]{${width / 4}}$`).test(value.Bits) || value.NaN !== undefined)
    throw new Error('Invalid value bits');
  return width;
}
export function fromWabt(value, allowNaN = false) {
  const width = widths[value.type];
  if (!width) throw new Error(`Unsupported WAST value type: ${value.type}`);
  const result = { Type: value.type };
  if (allowNaN && /^nan:(canonical|arithmetic)$/.test(value.value)) result.NaN = value.value.slice(4);
  else {
    if (typeof value.value !== 'string' || !/^\d+$/.test(value.value)) throw new Error('Invalid WAST bit representation');
    const bits = BigInt(value.value);
    if (bits >= 1n << BigInt(width)) throw new Error('WAST value exceeds bit width');
    result.Bits = bits.toString(16).padStart(width / 4, '0');
  }
  validateValue(result, allowNaN);
  return result;
}
export function decodeValue(value) {
  validateValue(value);
  const bits = BigInt('0x' + value.Bits);
  if (value.Type === 'i32') return Number(BigInt.asIntN(32, bits));
  if (value.Type === 'i64') return BigInt.asIntN(64, bits);
  const view = new DataView(new ArrayBuffer(8));
  if (value.Type === 'f32') { view.setUint32(0, Number(bits)); return view.getFloat32(0); }
  view.setBigUint64(0, bits); return view.getFloat64(0);
}
export function encodeValue(type, value) {
  if (!widths[type]) throw new Error(`Unsupported value type: ${type}`);
  const view = new DataView(new ArrayBuffer(8));
  let bits;
  if (type === 'i32') { if (typeof value !== 'number' || !Number.isInteger(value)) throw new Error('Expected i32'); bits = BigInt.asUintN(32, BigInt(value)); }
  else if (type === 'i64') { if (typeof value !== 'bigint') throw new Error('Expected i64'); bits = BigInt.asUintN(64, value); }
  else {
    if (typeof value !== 'number') throw new Error('Expected float');
    if (type === 'f32') { view.setFloat32(0, value); bits = BigInt(view.getUint32(0)); }
    else { view.setFloat64(0, value); bits = view.getBigUint64(0); }
  }
  return { Type: type, Bits: bits.toString(16).padStart(widths[type] / 4, '0') };
}
export function matches(expected, actual) {
  const width = validateValue(expected, true);
  validateValue(actual);
  if (expected.Type !== actual.Type) return false;
  if (expected.NaN === undefined) return expected.Bits === actual.Bits;
  const bits = BigInt('0x' + actual.Bits);
  const quiet = width === 32 ? 0x00400000n : 0x0008000000000000n;
  const exponent = width === 32 ? 0x7f800000n : 0x7ff0000000000000n;
  const magnitude = bits & ((1n << BigInt(width - 1)) - 1n);
  return expected.NaN === 'canonical' ? magnitude === (exponent | quiet) : (magnitude & (exponent | quiet)) === (exponent | quiet);
}
export function trapKind(message) {
  const kinds = {
    'unreachable': 'Unreachable', 'integer divide by zero': 'DivisionByZero',
    'integer overflow': 'IntegerOverflow', 'invalid conversion to integer': 'InvalidConversionToInteger', 'out of bounds memory access': 'MemoryOutOfBounds'
  };
  if (!Object.hasOwn(kinds, message)) throw new Error(`Unsupported WAST trap: ${message}`);
  return kinds[message];
}
