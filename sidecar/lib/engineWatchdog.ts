/**
 * A deadline for the engine's first byte.
 *
 * `agy` can hang before it writes anything: a broken build, a locked binary, a half-finished
 * self-update. The turn then blocks on `reader.read()` and never reaches its exit handling, so the
 * UI waits on a spinner that cannot resolve. The same hang makes `agy models` and the quota probe
 * time out with no explanation.
 *
 * Measured, and the reason this file exists rather than a bare `proc.kill`: killing the process does
 * NOT unblock a pending read. `proc.exited` resolves (137) while `reader.read()` stays pending, so
 * the turn has to race the deadline instead of waiting for the stream to close.
 */

export class EngineStartTimeout extends Error {
  constructor(
    readonly binary: string,
    readonly timeoutMs: number
  ) {
    super(
      `The engine did not start. ${binary} produced no output in ${Math.round(timeoutMs / 1000)}s. ` +
        'Run `agy --version` to confirm the binary works.'
    );
    this.name = 'EngineStartTimeout';
  }
}

export interface StartWatchdog {
  /**
   * Rejects with {@link EngineStartTimeout} unless `markAlive()` was called first. Race the turn's
   * own work against this; do not await it on its own.
   */
  promise: Promise<never>;
  /** First byte arrived on either stream. The process is alive, so the deadline is cancelled. */
  markAlive: () => void;
  /** Clears the timer. Safe to call more than once, and after the deadline fired. */
  dispose: () => void;
  /** True once the deadline fired. Lets callers skip a retry that would hang the same way. */
  hasTimedOut: () => boolean;
}

/** Matches `ModelCatalogService.ProbeTimeout`, so both probes agree on what "dead" means. */
export const DEFAULT_START_TIMEOUT_MS = 20_000;

export function resolveStartTimeoutMs(fallback: number = DEFAULT_START_TIMEOUT_MS): number {
  const raw = Number(process.env.AGY_START_TIMEOUT_MS);
  return Number.isFinite(raw) && raw >= 1_000 ? raw : fallback;
}

export function createStartWatchdog(
  binary: string,
  timeoutMs: number = resolveStartTimeoutMs(),
  onTimeout?: () => void
): StartWatchdog {
  let timer: ReturnType<typeof setTimeout> | null = null;
  let settled = false;
  let timedOut = false;

  let rejectWith: ((error: Error) => void) | null = null;
  const promise = new Promise<never>((_resolve, reject) => {
    rejectWith = reject;
  });
  // The turn may finish first, leaving nobody to observe a late rejection. Swallow it here; callers
  // that race the promise still see the rejection through their own handler.
  promise.catch(() => {});

  timer = setTimeout(() => {
    if (settled) return;
    settled = true;
    timedOut = true;
    timer = null;
    try {
      onTimeout?.();
    } catch {
      // A failed kill must not swallow the error the caller needs.
    }
    rejectWith?.(new EngineStartTimeout(binary, timeoutMs));
  }, timeoutMs);

  return {
    promise,
    markAlive: () => {
      if (settled) return;
      settled = true;
      if (timer) clearTimeout(timer);
      timer = null;
    },
    dispose: () => {
      settled = true;
      if (timer) clearTimeout(timer);
      timer = null;
    },
    hasTimedOut: () => timedOut,
  };
}
