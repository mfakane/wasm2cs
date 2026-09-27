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
const streamFile = process.env.SELF_HOSTING_STREAM_FILE;
const streamFs = streamFile ? await import('node:fs') : null;
const streamFd = streamFs?.openSync(streamFile, 'w');
const emitStream = value => {
  const line = JSON.stringify(value);
  if (streamFd !== undefined) streamFs.writeSync(streamFd, line + '\n');
  else console.log(line);
};

const results = request.scenarios.map(scenario => {
  if (scenario.operation === 'hello') {
    return { id: scenario.id, operation: scenario.operation, output: driver.Hello() };
  }
  if (scenario.operation === 'managed-probe') {
    const runs = [];
    for (let invocation = 0; invocation < (scenario.invocations ?? 3); invocation++) {
      const status = driver.RunManagedProbe();
      runs.push({ status, result: JSON.parse(driver.GetManagedProbeResult()) });
    }
    return { id: scenario.id, operation: scenario.operation, runs };
  }
  if (scenario.operation === 'managed-exports') {
    return { id: scenario.id, operation: scenario.operation, exports: Object.keys(driver).sort() };
  }
  if (scenario.operation === 'managed-throw') {
    try {
      driver.ThrowManagedProbe();
      return { id: scenario.id, operation: scenario.operation, thrown: false };
    } catch (error) {
      return {
        id: scenario.id,
        operation: scenario.operation,
        thrown: true,
        type: error?.name ?? 'Error',
        message: String(error?.message ?? error)
      };
    }
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
      emitStream({ protocol: 1, stream: true, id: scenario.id, error: started });
      return { id: scenario.id, operation: scenario.operation, output: 'STREAM_ERROR' };
    }
    while (true) {
      const next = driver.NextTranslateSource();
      if (next === '') break;
      if (next.startsWith('ERROR:')) {
        emitStream({ protocol: 1, stream: true, id: scenario.id, error: next });
        return { id: scenario.id, operation: scenario.operation, output: 'STREAM_ERROR' };
      }
      const name = driver.CurrentTranslateSourceName();
      // Keep each stdout protocol line below the host line limit. A split
      // line is dropped by JSON.parse and would compile as a missing method.
      for (let offset = 0; offset < next.length; offset += 4 * 1024) {
        let end = Math.min(next.length, offset + 4 * 1024);
        if (end < next.length && next.charCodeAt(end - 1) >= 0xd800 && next.charCodeAt(end - 1) <= 0xdbff) end--;
        emitStream({ protocol: 1, stream: true, id: scenario.id,
          source: { Name: name, Text: next.slice(offset, end) } });
      }
    }
    return { id: scenario.id, operation: scenario.operation, output: 'STREAM' };
  }
  throw new Error(`Unknown reference operation: ${scenario.operation}`);
});
if (streamFd !== undefined) streamFs.closeSync(streamFd);

const resultLine = JSON.stringify({ protocol: 1, results });
if (process.env.SELF_HOSTING_RESULT_FILE) {
  const { writeFileSync } = await import('node:fs');
  writeFileSync(process.env.SELF_HOSTING_RESULT_FILE, resultLine);
}
console.log(resultLine);
await runMainAndExit();
