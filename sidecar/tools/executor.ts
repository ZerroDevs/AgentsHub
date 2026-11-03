/**
 * Unified Cross-Platform Shell Executor
 * Executes shell commands via Bun.spawn with automatic OS detection, async streaming, and process abort control.
 */

export interface ShellExecOptions {
  command: string;
  cwd?: string;
  env?: Record<string, string>;
  timeoutMs?: number;
  abortSignal?: AbortSignal;
  onStdout?: (chunk: string) => void;
  onStderr?: (chunk: string) => void;
}

export interface ShellExecResult {
  exitCode: number;
  stdout: string;
  stderr: string;
  durationMs: number;
  aborted: boolean;
  timedOut: boolean;
}

export class ShellExecutor {
  private static readonly isWindows = process.platform === 'win32';
  private static readonly isDarwin = process.platform === 'darwin';

  public static getShellCommand(userCommand: string): { executable: string; args: string[] } {
    if (this.isWindows) {
      const comspec = process.env.COMSPEC || 'cmd.exe';
      return {
        executable: comspec,
        args: ['/d', '/s', '/c', userCommand]
      };
    }

    const preferredShell = process.env.SHELL || '/bin/bash';
    return {
      executable: preferredShell,
      args: ['-c', userCommand]
    };
  }

  public static async execute(options: ShellExecOptions): Promise<ShellExecResult> {
    const startTime = performance.now();
    const { executable, args } = this.getShellCommand(options.command);

    let stdoutAcc = '';
    let stderrAcc = '';
    let timedOut = false;
    let aborted = false;

    const mergedEnv: Record<string, string> = {
      ...(process.env as Record<string, string>),
      ...(options.env || {}),
      FORCE_COLOR: '0',
      TERM: 'dumb'
    };

    let timeoutTimer: ReturnType<typeof setTimeout> | undefined;

    const proc = Bun.spawn([executable, ...args], {
      // Without this a console child of the windowless sidecar allocates its own console window,
      // which is the black box that flashes on every tool call.
      windowsHide: true,
      cwd: options.cwd || process.cwd(),
      env: mergedEnv,
      stdout: 'pipe',
      stderr: 'pipe',
      stdin: 'ignore'
    });

    const cleanup = () => {
      if (timeoutTimer) clearTimeout(timeoutTimer);
    };

    if (options.timeoutMs && options.timeoutMs > 0) {
      timeoutTimer = setTimeout(() => {
        timedOut = true;
        try {
          proc.kill(9);
        } catch {}
      }, options.timeoutMs);
    }

    if (options.abortSignal) {
      options.abortSignal.addEventListener('abort', () => {
        aborted = true;
        try {
          proc.kill(9);
        } catch {}
      });
    }

    const readStream = async (
      stream: ReadableStream<Uint8Array> | null,
      onChunk?: (text: string) => void
    ): Promise<string> => {
      if (!stream) return '';
      const reader = stream.getReader();
      const decoder = new TextDecoder();
      let accumulated = '';

      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          if (value) {
            const text = decoder.decode(value, { stream: true });
            accumulated += text;
            onChunk?.(text);
          }
        }
      } catch (err) {
        // Stream reading interrupted upon abort or process termination
      } finally {
        reader.releaseLock();
      }
      return accumulated;
    };

    const [stdout, stderr, exitCode] = await Promise.all([
      readStream(proc.stdout, options.onStdout),
      readStream(proc.stderr, options.onStderr),
      proc.exited
    ]);

    cleanup();

    const durationMs = Math.round(performance.now() - startTime);

    return {
      exitCode,
      stdout,
      stderr,
      durationMs,
      aborted,
      timedOut
    };
  }
}
