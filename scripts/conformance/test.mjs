import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fromWabt, decodeValue, encodeValue, matches, validateValue, trapKind } from './values.mjs';
import { convertCommands, verifyReference } from './commands.mjs';

const patterns = {
  i32: ['00000000', 'ffffffff', '80000000', '7fffffff'],
  i64: ['0000000000000000', 'ffffffffffffffff', '8000000000000000', '7fffffffffffffff', '0020000000000001'],
  f32: ['00000000', '80000000', '00000001', '7f7fffff', '7f800000', 'ff800000', '7fc00123', 'ffc00123'],
  f64: ['0000000000000000', '8000000000000000', '0000000000000001', '7fefffffffffffff', '7ff0000000000000', 'fff0000000000000', '7ff8000000000123', 'fff8000000000123']
};
for (const [Type, values] of Object.entries(patterns)) for (const Bits of values) {
  const expected = { Type, Bits };
  const transported = JSON.parse(JSON.stringify(expected));
  assert.deepEqual(encodeValue(Type, decodeValue(transported)), expected);
  assert.deepEqual(fromWabt({ type: Type, value: BigInt('0x' + Bits).toString() }), expected);
}
// Signaling NaNs must survive the data format even where a JS float conversion quiets them.
for (const value of [{ Type: 'f32', Bits: '7f800001' }, { Type: 'f64', Bits: '7ff0000000000001' }]) {
  assert.deepEqual(JSON.parse(JSON.stringify(value)), value);
  assert.deepEqual(fromWabt({ type: value.Type, value: BigInt('0x' + value.Bits).toString() }), value);
}
assert.equal(matches({ Type: 'f32', NaN: 'canonical' }, { Type: 'f32', Bits: 'ffc00000' }), true);
assert.equal(matches({ Type: 'f32', NaN: 'canonical' }, { Type: 'f32', Bits: '7fc00001' }), false);
assert.equal(matches({ Type: 'f64', NaN: 'arithmetic' }, { Type: 'f64', Bits: 'fff8000000001234' }), true);
assert.equal(matches({ Type: 'f64', NaN: 'arithmetic' }, { Type: 'f64', Bits: '7ff0000000000001' }), false);
assert.equal(matches({ Type: 'f64', NaN: 'arithmetic' }, { Type: 'f64', Bits: '7ff0000000000000' }), false);
assert.throws(() => validateValue({ Type: 'i64', Bits: 9007199254740992 }), /bits/);
assert.throws(() => fromWabt({ type: 'i64', value: 9007199254740992 }), /representation/);
assert.throws(() => fromWabt({ type: 'f32', value: '4294967296' }), /width/);
assert.throws(() => fromWabt({ type: 'v128', value: '0' }), /value type/);
assert.throws(() => decodeValue({ Type: 'f32', NaN: 'canonical' }), /bits/);
assert.throws(() => trapKind('new trap'), /Unsupported WAST trap/);
const convert = command => convertCommands({ commands: [command] }, () => Buffer.from([0]));
for (const type of ['register', 'action', 'assert_exhaustion', 'unknown'])
  assert.throws(() => convert({ type }), /Unsupported WAST command/);
assert.throws(() => convert({ type: 'assert_return', action: { type: 'unknown' } }), /Unsupported WAST action/);
assert.throws(() => convert({ type: 'assert_return', action: { type: 'invoke', module: '$missing' } }), /Cross-module/);
assert.throws(() => convert({ type: 'assert_invalid', module_type: 'new' }), /module format/);
assert.deepEqual(convert({ type: 'assert_malformed', line: 3, module_type: 'text' }),
  [{ Kind: 'skip', Line: 3, Reason: 'WAT text syntax test; translator accepts binaries only' }]);
assert.equal(convert({ type: 'assert_malformed', line: 4, module_type: 'binary', filename: 'bad.wasm' })[0].Binary, 'AA==');
assert.throws(() => verifyReference([{ Kind: 'unknown' }]), /Unknown conformance command/);
assert.throws(() => verifyReference([{ Kind: 'assert_return', Action: 'unknown' }]), /Unknown conformance action/);
const files = process.argv.slice(2);
assert.ok(files.length > 0, 'Expected at least one fixture');
for (const file of files) {
  const fixture = JSON.parse(readFileSync(file, 'utf8'));
  assert.equal(fixture.SchemaVersion, 2);
  assert.equal(createHash('sha256').update(readFileSync(join(dirname(file), fixture.Source))).digest('hex'), fixture.SourceSha256, 'Fixture source changed without regeneration');
  assert.deepEqual(verifyReference(fixture.Cases), fixture.Counts);
}
console.log(`PASS: typed bit transport, NaN rules, unknown WAST rejection, and ${files.length} fixture reference executions (Node ${process.version}, V8 ${process.versions.v8}).`);
