import { mkdirSync, writeFileSync, readFileSync } from 'node:fs';
import wabtFactory from 'wabt';
import { encodeValue, trapKind } from './values.mjs';
const commit='977f97014c962f7bd1291fcc6d28b41a924882bf'; // official wg-1.0
const root=new URL('../../tests/Conformance/',import.meta.url);
mkdirSync(root,{recursive:true});
for (const file of ['test/core/i32.wast','LICENSE']) {
  const response=await fetch(`https://raw.githubusercontent.com/WebAssembly/spec/${commit}/${file}`);
  if (!response.ok) throw new Error(`${file}: ${response.status}`);
  writeFileSync(new URL(file.split('/').at(-1),root),await response.text());
}
const source=readFileSync(new URL('i32.wast',root),'utf8');
// This adapter handles the fixed i32.wast's S-expressions; it is not a WAST runtime.
// Unknown commands fail instead of silently reducing coverage.
const tokens=[...source.matchAll(/;;[^\n]*|"(?:\\.|[^"\\])*"|[()]|[^\s()]+/g)].filter(m=>!m[0].startsWith(';;'));
let position=0;
function expression() {
  const token=tokens[position++];
  if (!token) throw new Error('Unexpected end');
  if (token[0] !== '(') return token[0];
  const items=[];
  while (tokens[position]?.[0] !== ')') items.push(expression());
  const end=tokens[position++];
  return {items,raw:source.slice(token.index,end.index+1),line:source.slice(0,token.index).split('\n').length};
}
const wabt=await wabtFactory();
function binary(module) {
  const parsed=wabt.parseWat('i32.wast',module.raw);
  try { return Buffer.from(parsed.toBinary({canonicalize_lebs:true}).buffer); }
  finally { parsed.destroy(); }
}
const integer=node=>{
  if (node.items[0] !== 'i32.const') throw new Error('Non-i32 constant');
  const text=node.items[1].replaceAll('_','');
  return Number(BigInt.asIntN(32,text.startsWith('-') ? -BigInt(text.slice(1)) : BigInt(text)));
};
const cases=[];
let instance;
while(position<tokens.length) {
  const form=expression(), [kind,subject,...rest]=form.items;
  const entry={Kind:kind,Line:form.line};
  if (kind === 'module') {
    const bytes=binary(form); instance=new WebAssembly.Instance(new WebAssembly.Module(bytes));
    entry.Binary=bytes.toString('base64');
  } else if (kind === 'assert_return' || kind === 'assert_trap') {
    if (subject.items[0] !== 'invoke') throw new Error('Unexpected action');
    entry.Export=JSON.parse(subject.items[1]); entry.Args=subject.items.slice(2).map(integer);
    if (kind === 'assert_return') {
      if (rest.length !== 1) throw new Error('Unexpected return arity');
      entry.Expected=integer(rest[0]);
      if (instance.exports[entry.Export](...entry.Args) !== entry.Expected) throw new Error('Node differs from official expected value');
    } else {
      entry.Message=JSON.parse(rest[0]);
      let trapped=false;
      try { instance.exports[entry.Export](...entry.Args); } catch(e) { if (!(e instanceof WebAssembly.RuntimeError)) throw e; trapped=true; }
      if (!trapped) throw new Error('Node did not trap');
    }
  } else if (kind === 'assert_invalid') {
    if (/\b(?:i64|f32|f64)\b/.test(subject.raw)) {
      entry.Kind='skip'; entry.Reason='Invalid module uses unsupported non-i32 types';
    } else {
      const bytes=binary(subject);
      if (WebAssembly.validate(bytes)) throw new Error('Official invalid module unexpectedly validates');
      entry.Binary=bytes.toString('base64');
    }
  } else if (kind === 'assert_malformed') {
    entry.Kind='skip'; entry.Reason='WAT text syntax test; translator accepts binaries only';
  } else throw new Error(`Unsupported WAST command: ${kind}`);
  if (entry.Kind === 'assert_return' || entry.Kind === 'assert_trap') {
    entry.Action = 'invoke';
    entry.Args = entry.Args.map(value => encodeValue('i32', value));
    if (entry.Kind === 'assert_return') entry.Expected = [encodeValue('i32', entry.Expected)];
    else { entry.Trap = trapKind(entry.Message); delete entry.Message; }
  }
  cases.push(entry);
}
writeFileSync(new URL('i32.json',root),JSON.stringify({SchemaVersion:2,Commit:commit,Cases:cases},null,2)+'\n');
console.log(`${cases.length} official WAST commands prepared at ${commit}:`,Object.fromEntries([...new Set(cases.map(c=>c.Kind))].map(k=>[k,cases.filter(c=>c.Kind===k).length])));
