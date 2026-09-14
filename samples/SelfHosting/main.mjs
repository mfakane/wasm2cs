import { dotnet } from './_framework/dotnet.js';

// Jiterpreter trace generation is explicitly disabled for the reference
// profile. The MSBuild property covers the build; this option covers startup.
if (typeof dotnet.withRuntimeOptions !== 'function') {
  throw new Error('The browser-wasm runtime does not expose withRuntimeOptions().');
}

const requestText = process.env.SELF_HOSTING_REQUEST_FILE
  ? (await import('node:fs')).readFileSync(process.env.SELF_HOSTING_REQUEST_FILE, 'utf8')
  : process.env.SELF_HOSTING_REQUEST;
const request = requestText
  ? JSON.parse(process.env.SELF_HOSTING_REQUEST_FILE ? requestText : Buffer.from(requestText, 'base64url').toString('utf8'))
  : { scenarios: [{ id: 'hello', operation: 'hello' }] };

const runtime = dotnet
  .withDiagnosticTracing(false)
  .withRuntimeOptions(['--no-jiterpreter-traces-enabled']);
const { getAssemblyExports, getConfig, runMainAndExit } = await runtime.create();
const config = getConfig();
const exports = await getAssemblyExports(config.mainAssemblyName);
const driver = exports.SelfHostingDriver;

const results = request.scenarios.map(scenario => {
  if (scenario.operation === 'hello') {
    return { id: scenario.id, operation: scenario.operation, output: driver.Hello() };
  }
  if (scenario.operation === 'translate') {
    return {
      id: scenario.id,
      operation: scenario.operation,
      className: scenario.className,
      output: driver.TranslateBase64(scenario.wasmBase64, scenario.className)
    };
  }
  if (scenario.operation === 'translate-sources') {
    return {
      id: scenario.id,
      operation: scenario.operation,
      className: scenario.className,
      output: driver.TranslateSourcesBase64(scenario.wasmBase64, scenario.className)
    };
  }
  if (scenario.operation === 'translate-sources-stream') {
    const started = driver.BeginTranslateSourcesBase64(scenario.wasmBase64, scenario.className);
    if (started !== 'OK') {
      console.log(JSON.stringify({ protocol: 1, stream: true, id: scenario.id, error: started }));
      return { id: scenario.id, operation: scenario.operation, output: 'STREAM_ERROR' };
    }
    while (true) {
      const next = driver.NextTranslateSource();
      if (next === '') break;
      if (next.startsWith('ERROR:')) {
        console.log(JSON.stringify({ protocol: 1, stream: true, id: scenario.id, error: next }));
        return { id: scenario.id, operation: scenario.operation, output: 'STREAM_ERROR' };
      }
      console.log(JSON.stringify({ protocol: 1, stream: true, id: scenario.id,
        source: { Name: driver.CurrentTranslateSourceName(), Text: next } }));
    }
    return { id: scenario.id, operation: scenario.operation, output: 'STREAM' };
  }
  throw new Error(`Unknown reference operation: ${scenario.operation}`);
});

console.log(JSON.stringify({ protocol: 1, results }));
await runMainAndExit();
