// Replay [[export, [i32 args]], ...] on one instance; report each outcome and the final memory SHA-256.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
const [file, callsFile] = process.argv.slice(2);
const { instance } = await WebAssembly.instantiate(readFileSync(file), {});
const outcomes = JSON.parse(readFileSync(callsFile, 'utf8')).map(([name, args]) => {
  try { return `${name}(${args.join(',')}) = ${instance.exports[name](...args)}`; }
  catch (error) {
    if (!(error instanceof WebAssembly.RuntimeError)) throw error;
    return `${name}(${args.join(',')}) trap`;
  }
});
outcomes.push('memory ' + createHash('sha256').update(new Uint8Array(instance.exports.memory.buffer)).digest('hex'));
console.log(outcomes.join('\n'));
