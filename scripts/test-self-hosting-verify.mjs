import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { acceptCacheStamp, cacheKey, extractMeasurement, missingTools, runStages } from './self-hosting-verify.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const script = fileURLToPath(new URL('./self-hosting.mjs', import.meta.url));
const profile = JSON.parse(readFileSync(new URL('../docs/self-hosting/SH-01-profile.json', import.meta.url), 'utf8'));

const stopped = runStages(['one', 'two', 'three'], name => name === 'two' ? 7 : 0);
assert.equal(stopped.status, 'failed');
assert.equal(stopped.failedStage, 'two');
assert.equal(stopped.exitCode, 7);
assert.deepEqual(stopped.stages.map(item => item.status), ['passed', 'failed', 'not-run']);
assert.equal(stopped.stages.filter(item => item.status === 'passed').length, 1);

const crashed = runStages(['one', 'two'], name => {
  if (name === 'one') throw new Error('tool missing');
  return 0;
});
assert.equal(crashed.status, 'failed');
assert.equal(crashed.stages[0].status, 'failed');
assert.equal(crashed.stages[0].exitCode, null);
assert.equal(crashed.stages[1].status, 'not-run');
assert.notEqual(crashed.status, 'passed');

assert.equal(runStages([], () => 0).status, 'failed');
assert.equal(runStages(['only'], () => 0).status, 'passed');
assert.equal(missingTools([{ command: 'dotnet', available: false }, { command: 'node', available: true }]).join(), 'dotnet');

const hash = profile.canonicalBundle.sha256;
assert.equal(cacheKey(hash), `wasm2cs-self-hosting-${hash}`);
assert.notEqual(cacheKey(hash), cacheKey('b'.repeat(64)));
assert.equal(acceptCacheStamp(null, hash).reason, 'absent');
assert.equal(acceptCacheStamp({ bundleSha256: hash, cacheKey: cacheKey(hash) }, hash).accepted, true);
assert.equal(acceptCacheStamp({ bundleSha256: 'b'.repeat(64), cacheKey: cacheKey('b'.repeat(64)) }, hash).reason, 'mismatch');
assert.equal(extractMeasurement('generate', null).measured, false);
assert.equal(extractMeasurement('hello', { results: { startupDurationMs: 1, executionDurationMs: 2, linearPages: 3, outerGcHeap: 4 } }).measured, true);

function runVerify(directory, env = {}) {
  return spawnSync(process.execPath, [script, 'verify'], {
    cwd: root,
    env: { ...process.env, SELF_HOSTING_ARTIFACTS: directory, ...env },
    encoding: 'utf8'
  });
}

function resultOf(directory) {
  return JSON.parse(readFileSync(join(directory, 'verify-results.json'), 'utf8'));
}

const clean = mkdtempSync(join(tmpdir(), 'wasm2cs-verify-clean-'));
try {
  const cleanRun = runVerify(clean);
  assert.notEqual(cleanRun.status, 0, cleanRun.stdout + cleanRun.stderr);
  const cleanResult = resultOf(clean);
  assert.equal(cleanResult.status, 'failed');
  assert.equal(cleanResult.failedStage, 'bundle');
  assert.equal(cleanResult.workloadUpdated, false);
  assert.equal(cleanResult.stages.some(item => item.status === 'passed'), false);
  assert.doesNotMatch(cleanRun.stderr, /workload install/);
} finally {
  rmSync(clean, { recursive: true, force: true });
}

const corrupt = mkdtempSync(join(tmpdir(), 'wasm2cs-verify-corrupt-'));
try {
  mkdirSync(join(corrupt, 'bundle'));
  writeFileSync(join(corrupt, 'bundle-manifest.json'), JSON.stringify({
    schemaVersion: 1, milestone: 'SH-01', profile: profile.profile, entries: [], bundleSha256: 'a'.repeat(64)
  }));
  const corruptRun = runVerify(corrupt);
  assert.notEqual(corruptRun.status, 0);
  const corruptResult = resultOf(corrupt);
  assert.equal(corruptResult.status, 'failed');
  assert.equal(corruptResult.failedStage, 'bundle');
  assert.equal(corruptResult.stages.every(item => item.status === 'not-run'), true);
} finally {
  rmSync(corrupt, { recursive: true, force: true });
}

const mismatch = mkdtempSync(join(tmpdir(), 'wasm2cs-verify-mismatch-'));
try {
  mkdirSync(join(mismatch, 'bundle'));
  const body = { schemaVersion: 1, milestone: 'SH-01', profile: profile.profile, entries: [] };
  writeFileSync(join(mismatch, 'bundle-manifest.json'), JSON.stringify({
    ...body,
    bundleSha256: createHash('sha256').update(JSON.stringify(body)).digest('hex')
  }));
  const mismatchRun = runVerify(mismatch);
  assert.notEqual(mismatchRun.status, 0);
  assert.equal(resultOf(mismatch).status, 'failed');
  assert.equal(resultOf(mismatch).failedStage, 'bundle');
  assert.match(mismatchRun.stderr, /differs from canonical bundle/);
} finally {
  rmSync(mismatch, { recursive: true, force: true });
}

const bundle = join(root, 'artifacts', 'self-hosting');
const bundleManifest = join(bundle, 'bundle-manifest.json');
if (existsSync(bundleManifest) && readFileSync(bundleManifest, 'utf8').includes(hash)) {
  const cached = mkdtempSync(join(tmpdir(), 'wasm2cs-verify-cache-'));
  const blind = mkdtempSync(join(tmpdir(), 'wasm2cs-verify-tools-'));
  try {
    symlinkSync(join(bundle, 'bundle'), join(cached, 'bundle'));
    writeFileSync(join(cached, 'bundle-manifest.json'), readFileSync(join(bundle, 'bundle-manifest.json')));
    mkdirSync(join(cached, 'environment'));
    writeFileSync(join(cached, 'environment', 'cache-stamp.json'), JSON.stringify({
      bundleSha256: 'b'.repeat(64), cacheKey: cacheKey('b'.repeat(64))
    }));
    const cacheRun = runVerify(cached);
    assert.notEqual(cacheRun.status, 0);
    const cacheResult = resultOf(cached);
    assert.equal(cacheResult.status, 'failed');
    assert.equal(cacheResult.failedStage, 'cache');
    assert.equal(cacheResult.stages.every(item => item.status === 'not-run'), true);
    assert.doesNotMatch(cacheRun.stderr, /workload install/);

    symlinkSync(join(bundle, 'bundle'), join(blind, 'bundle'));
    writeFileSync(join(blind, 'bundle-manifest.json'), readFileSync(join(bundle, 'bundle-manifest.json')));
    const toolRun = runVerify(blind, { PATH: join(blind, 'no-tools') });
    assert.notEqual(toolRun.status, 0);
    assert.equal(resultOf(blind).failedStage, 'tools');
    assert.equal(resultOf(blind).stages.every(item => item.status === 'not-run'), true);
    assert.doesNotMatch(toolRun.stderr, /workload install/);
  } finally {
    rmSync(cached, { recursive: true, force: true });
    rmSync(blind, { recursive: true, force: true });
  }
}

const regressionPath = fileURLToPath(new URL('../.github/workflows/regression.yml', import.meta.url));
const selfHostingPath = fileURLToPath(new URL('../.github/workflows/self-hosting.yml', import.meta.url));
if (existsSync(regressionPath) && existsSync(selfHostingPath)) {
  const regression = readFileSync(regressionPath, 'utf8');
  const selfHosting = readFileSync(selfHostingPath, 'utf8');
  assert.doesNotMatch(regression, /self-hosting\.mjs|test-unity-self-hosting/);
  assert.doesNotMatch(selfHosting, /restore-keys/);
  assert.match(selfHosting, /wasm2cs-self-hosting-/);
  assert.match(selfHosting, /workflow_dispatch/);
  assert.doesNotMatch(selfHosting, /test-unity-self-hosting/);
}

console.log('self-hosting verify checks passed');
