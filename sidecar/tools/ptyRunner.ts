/**
 * Pseudo-Terminal (PTY) & Interactive Stream Runner
 * Handles cross-platform interactive terminal subprocesses and bidirectional streaming.
 */

import { ShellExecutor } from './executor.ts';

export interface PtySessionConfig {
  sessionId: string;
  command?: string;
  cwd?: string;
  cols?: number;
  rows?: number;
  env?: Record<string, string>;
  onData: (data: string) => void;
  onExit: (code: number) => void;
}

export class PtyRunner {
  private activeSessions = new Map<string, {
    proc: any;
  }>();

  public spawnSession(config: PtySessionConfig): void {
    if (this.activeSessions.has(config.sessionId)) {
      this.terminateSession(config.sessionId);
    }

    const command = config.command || (process.platform === 'win32' ? 'cmd.exe' : '/bin/bash');
    const { executable, args } = ShellExecutor.getShellCommand(command);

    const mergedEnv: Record<string, string> = {
      ...(process.env as Record<string, string>),
      ...(config.env || {}),
      TERM: 'xterm-256color',
      COLORTERM: 'truecolor',
      COLUMNS: String(config.cols || 120),
      LINES: String(config.rows || 32)
    };

    const proc = Bun.spawn([executable, ...args], {
      windowsHide: true,
      cwd: config.cwd || process.cwd(),
      env: mergedEnv,
      stdin: 'pipe',
      stdout: 'pipe',
      stderr: 'pipe'
    });

    this.activeSessions.set(config.sessionId, {
      proc
    });

    const pumpStream = async (stream: ReadableStream<Uint8Array> | null) => {
      if (!stream) return;
      const reader = stream.getReader();
      const decoder = new TextDecoder();
      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          if (value) {
            config.onData(decoder.decode(value, { stream: true }));
          }
        }
      } catch {}
    };

    pumpStream(proc.stdout);
    pumpStream(proc.stderr);

    proc.exited.then((code: number) => {
      this.activeSessions.delete(config.sessionId);
      config.onExit(code);
    });
  }

  public writeInput(sessionId: string, data: string): boolean {
    const session = this.activeSessions.get(sessionId);
    if (!session || !session.proc || !session.proc.stdin) return false;

    try {
      session.proc.stdin.write(data);
      if (typeof session.proc.stdin.flush === 'function') {
        session.proc.stdin.flush();
      }
      return true;
    } catch {
      return false;
    }
  }

  public resize(sessionId: string, cols: number, rows: number): void {
    // Bun.spawn doesn't have native ioctl resize, handled via env or signal if supported on Unix
    const session = this.activeSessions.get(sessionId);
    if (!session) return;

    if (process.platform !== 'win32') {
      try {
        session.proc.kill('SIGWINCH');
      } catch {}
    }
  }

  public terminateSession(sessionId: string): void {
    const session = this.activeSessions.get(sessionId);
    if (!session) return;

    try {
      session.proc.kill(9);
    } catch {}

    this.activeSessions.delete(sessionId);
  }

  public terminateAll(): void {
    for (const [id] of this.activeSessions) {
      this.terminateSession(id);
    }
  }
}
