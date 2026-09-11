import { writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';
const u = n => { const a=[]; do { let b=n&127; n>>>=7; a.push(b|(n?128:0)); } while(n); return a; };
const str = s => [...u(Buffer.byteLength(s)),...Buffer.from(s)];
const section = (id,a) => [id,...u(a.length),...a];
const body = a => [...u(a.length+1),0,...a];
const bytes = Buffer.from([0,97,115,109,1,0,0,0,
  ...section(1,[4,0x60,2,0x7f,0x7f,1,0x7f, 0x60,1,0x7f,0, 0x60,1,0x7f,1,0x7f, 0x60,0,0]),
  ...section(2,[2,...str('env'),...str('exchange'),0,0,...str('env'),...str('notify'),0,1]),
  ...section(3,[3,2,2,3]), ...section(5,[1,1,1,1]),
  ...section(7,[4,...str('f'),0,2,...str('reenter'),0,3,...str('notify'),0,1,...str('memory'),2,0]),
  ...section(8,[4]),
  ...section(10,[3,...body([0x20,0,0x10,1,0x41,16,0x20,0,0x10,0,0x41,1,0x6a,0x0b]),
    ...body([0x20,0,0x41,1,0x6a,0x0b]),...body([0x41,42,0x10,1,0x0b])]),
  ...section(11,[1,0,0x41,16,0x0b,3,7,8,9])]);
const log=[];
let instance;
instance = new WebAssembly.Instance(new WebAssembly.Module(bytes),{env:{
  notify: value => log.push(value),
  exchange: (offset,length) => {
    const memory=new Uint8Array(instance.exports.memory.buffer,offset,length);
    const sum=memory.reduce((a,b)=>a+b,0); memory.set([9,8,7]);
    return instance.exports.reenter(sum);
  }
}});
assert.equal(instance.exports.f(3),26);
instance.exports.notify(99);
assert.deepEqual(log,[42,3,99]);
assert.deepEqual([...new Uint8Array(instance.exports.memory.buffer,16,3)],[9,8,7]);
writeFileSync(new URL('../samples/Host/Host.wasm',import.meta.url),bytes);
console.log('PASS: WebAssembly host callbacks, start order, memory exchange, and reentry.');
