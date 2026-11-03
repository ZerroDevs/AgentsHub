/**
 * Proves the start deadline works, and that it fixes the failure that started this.
 *
 * Part 1 exercises the helper's three states (fires, cancelled by traffic, cancelled by dispose).
 * Part 2 is the real regression: it points the sidecar at the broken `agy` build that hung at
 * startup on 2026-10-29 and asserts a turn ends with a readable error instead of waiting forever.
 *
 * Run: bun run tools/watchdog-probe.ts
 */
import { existsSync } from 'node:fs';
import { createStartWatchdog, EngineStartTimeout } from '../sidecar/lib/engineWatchdog';
import { SessionManager } from '../sidecar/session/sessionManager';
import { TaskQueue } from '../sidecar/queue/taskQueue';

const BROKEN_AGY = 'C:/Users/Gaming/AppData/Local/agy/bin/agy.exe.broken-20261029-0007';

async function part1() {
  const fired = createStartWatchdog('stub', 120);
  const t0 = Date.now();
  const err = await fired.promise.catch(e => e);
  console.log(
    `[1] fires: ${err instanceof EngineStartTimeout} after ${Date.now() - t0}ms, message ok: ${/did not start/.test(err.message)}`
  );

  const cancelled = createStartWatchdog('stub', 120);
  cancelled.markAlive();
  const raced = await Promise.race([
    cancelled.promise.then(() => 'rejected'),
    new Promise(resolve => setTimeout(() => resolve('stayed pending'), 300)),
  ]);
  console.log(`[1] markAlive cancels: ${raced === 'stayed pending'}`);

  const disposed = createStartWatchdog('stub', 120);
  disposed.dispose();
  const afterDispose = await Promise.race([
    disposed.promise.then(() => 'rejected'),
    new Promise(resolve => setTimeout(() => resolve('stayed pending'), 300)),
  ]);
  console.log(`[1] dispose cancels: ${afterDispose === 'stayed pending'}`);
}

async function part2() {
  if (!existsSync(BROKEN_AGY)) {
    console.log(`[2] skipped: ${BROKEN_AGY} is not present`);
    return;
  }

  process.env.AGY_CLI_PATH = BROKEN_AGY;
  process.env.AGY_START_TIMEOUT_MS = '3000';

  const events: any[] = [];
  const queue = new TaskQueue(2, (e: any) => events.push(e));
  const manager = new SessionManager(queue, (e: any) => events.push(e));

  const t0 = Date.now();
  await manager.handleSendChat({
    sessionId: `watchdog_${Date.now()}`,
    prompt: 'say hi',
    profile: 'ZXR@1',
    skipPermissions: true,
    workingDirectory: 'C:/Users/Gaming/AppData/Local/Temp/cwdprobe',
  });
  const elapsed = Date.now() - t0;

  const status = [...events].reverse().find(e => e.event === 'session_status');
  console.log(`[2] turn ended in ${elapsed}ms with state=${status?.state}`);
  console.log(`[2] error: ${String(status?.error || '').slice(0, 120)}`);
  console.log(`[2] readable failure: ${/did not start/.test(String(status?.error || ''))}`);
  console.log(`[2] bounded (under 15s): ${elapsed < 15_000}`);
}

await part1();
await part2();
process.exit(0);
