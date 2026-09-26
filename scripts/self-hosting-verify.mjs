// SH-14: fail closed. A missing, mismatched, or unrun stage is not a pass.
export const verifyStages = ['inventory', 'generate', 'compile', 'host', 'hello', 'managed', 'translate'];

export function cacheKey(bundleSha256) {
  if (!/^[0-9a-f]{64}$/.test(bundleSha256)) throw new Error(`Cache key requires a bundle SHA-256, got ${bundleSha256}.`);
  return `wasm2cs-self-hosting-${bundleSha256}`;
}

export function acceptCacheStamp(stamp, bundleSha256) {
  if (stamp == null) return { accepted: false, reason: 'absent' };
  if (stamp.bundleSha256 !== bundleSha256 || stamp.cacheKey !== cacheKey(bundleSha256))
    return { accepted: false, reason: 'mismatch' };
  return { accepted: true, reason: 'match' };
}

export function missingTools(tools) {
  return tools.filter(item => !item?.available).map(item => item?.command ?? 'missing');
}

export function runStages(stageNames, spawnStage) {
  if (!Array.isArray(stageNames) || stageNames.length === 0)
    return { status: 'failed', failedStage: null, exitCode: 1, stages: [] };
  const stages = [];
  let failed = null;
  let exitCode = 0;
  for (const name of stageNames) {
    if (failed) {
      stages.push({ name, status: 'not-run', exitCode: null });
      continue;
    }
    let code;
    try {
      code = spawnStage(name);
    } catch {
      code = null;
    }
    if (code !== 0) {
      failed = name;
      exitCode = Number.isInteger(code) && code !== 0 ? code : 1;
      stages.push({ name, status: 'failed', exitCode: code });
    } else {
      stages.push({ name, status: 'passed', exitCode: 0 });
    }
  }
  const passed = failed == null && stages.every(item => item.status === 'passed' && item.exitCode === 0);
  return { status: passed ? 'passed' : 'failed', failedStage: failed, exitCode: passed ? 0 : exitCode, stages };
}

export function extractMeasurement(stage, record) {
  if (!record) return { measured: false };
  if (stage === 'generate') {
    const entries = Array.isArray(record.entries) ? record.entries : [];
    return {
      measured: true,
      files: entries.length,
      bytes: entries.reduce((sum, item) => sum + (Number(item.bytes) || 0), 0),
      durationMs: record.metrics?.durationMs ?? null,
      hostMaxRss: record.metrics?.hostMaxRss ?? null
    };
  }
  if (stage === 'compile') return { measured: true, sourceCount: record.sourceCount ?? null, durationMs: record.durationMs ?? null };
  if (stage === 'host') return { measured: true, imports: Array.isArray(record.imports) ? record.imports.length : null };
  if (stage === 'hello' || stage === 'translate') {
    const run = stage === 'hello' ? record.results : record.translated;
    if (!run) return { measured: false };
    return {
      measured: true,
      startupDurationMs: run.startupDurationMs ?? null,
      executionDurationMs: run.executionDurationMs ?? null,
      linearPages: run.linearPages ?? null,
      outerGcHeap: run.outerGcHeap ?? null,
      outerWorkingSet: run.outerWorkingSet ?? null
    };
  }
  if (stage === 'managed') {
    return {
      measured: true,
      normalDurationMs: record.normalDotnet?.durationMs ?? null,
      translatedDurationMs: record.translatedMono?.durationMs ?? null,
      guestHeap: record.normalDotnet?.guestHeap ?? null,
      outerProcessMemoryBytes: record.limits?.outerProcessMemoryBytes ?? null,
      linearMemoryBytes: record.limits?.linearMemoryBytes ?? null
    };
  }
  return { measured: false };
}
