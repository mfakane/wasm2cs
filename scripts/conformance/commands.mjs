import { fromWabt, decodeValue, encodeValue, matches, trapKind } from './values.mjs';

// Consume WABT's command JSON explicitly. This is intentionally not a general WAST runtime.
export function convertCommands(document, loadBinary) {
  if (!Array.isArray(document.commands)) throw new Error('Missing WAST commands');
  const cases = [];
  let moduleName;
  for (const command of document.commands) {
    const entry = { Kind: command.type, Line: command.line };
    switch (command.type) {
      case 'module':
        entry.Binary = loadBinary(command.filename).toString('base64');
        moduleName = command.name;
        break;
      case 'assert_return': case 'assert_trap': {
        const action = command.action;
        if (!action || !['invoke', 'get'].includes(action.type)) throw new Error(`Unsupported WAST action: ${action?.type}`);
        if (action.module !== undefined && action.module !== moduleName) throw new Error('Cross-module WAST action is unsupported');
        entry.Action = action.type;
        entry.Export = action.field;
        if (typeof entry.Export !== 'string') throw new Error('Missing WAST export');
        if (action.type === 'get' && action.args !== undefined) throw new Error('Get action cannot have arguments');
        entry.Args = action.type === 'invoke' ? action.args.map(value => fromWabt(value)) : [];
        if (command.type === 'assert_return') entry.Expected = command.expected.map(value => fromWabt(value, true));
        else entry.Trap = trapKind(command.text);
        break;
      }
      case 'assert_invalid': case 'assert_malformed':
        if (command.module_type === 'text' && command.type === 'assert_malformed') {
          entry.Kind = 'skip'; entry.Reason = 'WAT text syntax test; translator accepts binaries only';
        } else {
          if (command.module_type !== 'binary') throw new Error(`Unsupported WAST module format: ${command.module_type}`);
          entry.Binary = loadBinary(command.filename).toString('base64');
        }
        break;
      default: throw new Error(`Unsupported WAST command: ${command.type}`);
    }
    cases.push(entry);
  }
  return cases;
}

export function verifyReference(cases) {
  let instance;
  let returns = 0, traps = 0, invalid = 0;
  for (const test of cases) {
    switch (test.Kind) {
      case 'module': instance = new WebAssembly.Instance(new WebAssembly.Module(Buffer.from(test.Binary, 'base64'))); break;
      case 'assert_return': case 'assert_trap': {
        if (!['invoke', 'get'].includes(test.Action)) throw new Error(`Unknown conformance action: ${test.Action}`);
        let result;
        try {
          result = test.Action === 'get' ? instance.exports[test.Export].value : instance.exports[test.Export](...test.Args.map(decodeValue));
        } catch (error) {
          if (test.Kind !== 'assert_trap' || !(error instanceof WebAssembly.RuntimeError)) throw error;
          // V8's wording for division by zero differs from WAST's wording.
          const messages = { Unreachable: 'unreachable', DivisionByZero: 'divide by zero', IntegerOverflow: 'unrepresentable', MemoryOutOfBounds: 'out of bounds' };
          if (!messages[test.Trap] || !error.message.includes(messages[test.Trap])) throw error;
          traps++; break;
        }
        if (test.Kind === 'assert_trap') throw new Error(`Line ${test.Line}: missing reference trap`);
        const results = result === undefined ? [] : Array.isArray(result) ? result : [result];
        if (results.length !== test.Expected.length || results.some((value, i) => !matches(test.Expected[i], encodeValue(test.Expected[i].Type, value))))
          throw new Error(`Line ${test.Line}: reference result differs`);
        returns++; break;
      }
      case 'assert_invalid': case 'assert_malformed':
        if (WebAssembly.validate(Buffer.from(test.Binary, 'base64'))) throw new Error(`Line ${test.Line}: reference accepts invalid module`);
        invalid++; break;
      case 'skip': break;
      default: throw new Error(`Unknown conformance command: ${test.Kind}`);
    }
  }
  return { Returns: returns, Traps: traps, Invalid: invalid };
}
