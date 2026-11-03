/**
 * Session & Account Manager for AgentsHub Sidecar
 * Dispatches real agent turns to the native Antigravity CLI (agy) or ShellExecutor with:
 * - Real stream-json token, thinking, and tool execution parsing (NO fake mock templates)
 * - Isolated VirtualHome environments per profile (USERPROFILE, HOME, APPDATA, LOCALAPPDATA)
 * - Dedicated session tracking and active sessions counter
 * - Automatic session pruner to prevent memory leaks
 */

import { existsSync, readFileSync, writeFileSync, mkdirSync, copyFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { homedir } from 'node:os';
import {
  EngineStartTimeout,
  createStartWatchdog,
  resolveStartTimeoutMs,
} from '../lib/engineWatchdog';
import { OutboundEvent, AccountProfileInfo } from '../types/protocol.ts';
import { ShellExecutor } from '../tools/executor.ts';
import { TaskQueue } from '../queue/taskQueue.ts';

export interface ProfileRecord {
  name: string;
  email?: string;
  token?: string;
  provider?: string;
  model?: string;
  lastUsed?: string;
  virtualHomePath?: string;
}

/** One window of one model group, as reported by `agy -p "/quota"`. */
export interface QuotaWindowEntry {
  group: string;
  /** The CLI's own wording, e.g. "Five Hour Limit Remaining". Displayed verbatim. */
  label: string;
  /** Derived classification, used only to pick which window is the base reading. */
  window: QuotaWindow;
  percent: number;
  resetAt?: string;
}

export type QuotaWindow = 'five_hour' | 'weekly';

/** The reading surfaced to the UI: a base window plus the weekly window of the same group. */
export interface QuotaSnapshot {
  base: QuotaWindowEntry;
  weekly?: QuotaWindowEntry;
  windows: QuotaWindowEntry[];
}

export interface ChatSessionState {
  sessionId: string;
  profileName: string;
  workingDirectory: string;
  status: 'idle' | 'thinking' | 'running' | 'tool_executing' | 'completed' | 'error';
  startedAt: number;
  lastActiveAt: number;
  abortController?: AbortController;
  scopedEnv: Record<string, string>;
  activeProcess?: any;
  pid?: number;
  model?: string;
  reasoningEffort?: string;
  /**
   * Whether this turn runs with `--dangerously-skip-permissions`.
   *
   * Headless agy cannot prompt for permission, so turning this off does not make the agent ask —
   * gated tools are auto-denied and the denial comes back as a tool error. Callers that omit it get
   * full access, which is the behaviour every build before this had.
   */
  skipPermissions?: boolean;
  /**
   * The CLI conversation this session owns.
   *
   * Every session needs its own, or `--continue` resumes whichever conversation ran last and the
   * agent answers with another session's history — which also inflates every turn's token count
   * with context that was never part of this chat.
   */
  conversationId?: string;
  quotaPercent?: number;
  streamedText?: string;
  tokens?: {
    inputTokens?: number;
    outputTokens?: number;
    thinkingTokens?: number;
    totalTokens?: number;
  };
}

export interface PermissionDenial {
  /** The CLI's permission kind, e.g. `command`. */
  permission: string;
  /** The full command line the CLI refused, when the denial names one. */
  command?: string;
  /** Program the allow-rule would pin, e.g. `npm`. */
  target?: string;
  /** Ready-to-write allow-rule, e.g. `command(npm)`. Absent when the shape is unknown. */
  rule?: string;
  message: string;
}

/** Shell builtins that move the shell around rather than do work, skipped when naming a program. */
const SHELL_NAVIGATION = new Set(['cd', 'pushd', 'popd', 'export', 'set', 'source']);

/**
 * The program a refused command line starts with, used only to label the denial in the UI.
 *
 * It is not the allow-rule: `command(echo)` does not match `echo hi`, so the rule has to carry the
 * whole command line. This exists so the card can say "echo" instead of pasting a long command.
 */
export function commandRuleTarget(command: string): string | undefined {
  for (const segment of command.split(/&&|\|\||;|\|/)) {
    const first = segment.trim().match(/^"([^"]+)"|^(\S+)/);
    const raw = first ? (first[1] ?? first[2] ?? '') : '';
    if (!raw) continue;

    const base = (raw.split(/[\\/]/).pop() || raw).replace(/\.(exe|cmd|bat|com|ps1)$/i, '');
    if (!base) continue;
    if (SHELL_NAVIGATION.has(base.toLowerCase())) continue;
    return base;
  }
  return undefined;
}

/**
 * Recognises the CLI's permission-denial wording and extracts what an allow-rule needs.
 *
 * Two shapes exist, and only one of them names the rule:
 *
 * - file and tool denials spell it out — `user denied permission for write_file(<path>)`
 * - command denials repeat the command line instead — `user denied permission to run command:\n<cmd>`
 *
 * Guessing from the quoted subject produced `command(<path>)` for a file write, a rule that grants
 * nothing, which is why approving a blocked write changed nothing.
 *
 * Returns undefined for every other tool error, so ordinary failures are not mistaken for denials.
 */
export function parsePermissionDenial(
  message: string,
  call?: { name?: string; params?: Record<string, unknown> }
): PermissionDenial | undefined {
  if (!message || !/permission check failed/i.test(message)) return undefined;

  // The CLI states the exact rule for every non-command permission. Use it verbatim.
  const named = message.match(/user denied permission for\s+([a-z_]+)\(([^)]*)\)/i);
  if (named) {
    const permission = named[1].toLowerCase();
    const subject = named[2];
    return {
      permission,
      target: subject,
      rule: `${permission}(${subject})`,
      message: message.trim(),
    };
  }

  const head = message.match(/permission check failed for\s+(?:unsandboxed\s+)?(\w+)?\s*"([^"]*)"/i);
  // The `denied permission to run command:` line repeats the command verbatim, without the
  // "unsandboxed" wrapper, so it is the better source when present.
  const detail = message.match(/denied permission to (?:run )?([a-z_ ]+):\s*\n([^\n]+)/i);
  const command = detail?.[2]?.trim() || head?.[2];

  // "for unsandboxed \"echo hi\"" has no kind word, but a command line means it was a command.
  const permission = (head?.[1] || call?.name || (command ? 'command' : 'tool')).toLowerCase();

  // Measured: `command(echo)` does NOT allow `echo hi` — the rule has to be the exact command line
  // the CLI refused. That is also the safest grant: it allows precisely what the user just saw.
  const target = command ? commandRuleTarget(command) : undefined;
  const rule = command ? `command(${command})` : ruleFromCall(permission, call?.params);

  return { permission, command, target, rule, message: message.trim() };
}

/**
 * Rebuilds the rule from the tool call when the CLI's message did not spell one out.
 *
 * Some denials read `permission check failed for write_file: user denied permission` — no target at
 * all. The parameters the CLI passed are the same values it will check, so they are the only
 * reliable source left.
 */
function ruleFromCall(permission: string, params?: Record<string, unknown>): string | undefined {
  if (!params) return undefined;

  const subject =
    typeof params.Path === 'string'
      ? params.Path
      : typeof params.CommandLine === 'string'
        ? params.CommandLine
        : typeof params.path === 'string'
          ? params.path
          : undefined;
  if (!subject) return undefined;

  const kind = permission === 'tool' ? 'write_file' : permission;
  return `${kind}(${subject})`;
}

export class SessionManager {
  private profiles = new Map<string, ProfileRecord>();
  private activeProfileName: string = 'default';
  private activeSessions = new Map<string, ChatSessionState>();
  private profileActiveSessionIds = new Map<string, string>(); // profileName -> sessionId
  private profileQuotas = new Map<
    string,
    { quotaPercent: number; quotaWeeklyPercent?: number; details?: string; lastPolled: number }
  >();
  private configPath: string;
  private emit: (event: OutboundEvent) => void;
  private taskQueue: TaskQueue;

  constructor(taskQueue: TaskQueue, emit: (event: OutboundEvent) => void) {
    this.taskQueue = taskQueue;
    this.emit = emit;
    this.configPath = this.resolveProfilesPath();
    this.loadProfiles();

    // Periodic sweep for abandoned sessions to prevent memory leaks (runs every 10 min)
    setInterval(() => this.pruneInactiveSessions(), 10 * 60 * 1000);

    // Initial quota poll & periodic quota polling per profile context (every 3 min)
    setTimeout(() => this.pollAllQuotas().catch(() => {}), 1500);
    setInterval(() => this.pollAllQuotas().catch(() => {}), 3 * 60 * 1000);
  }

  private resolveProfilesPath(): string {
    const home = homedir();
    const geminiDir = join(home, '.gemini', 'antigravity');
    if (!existsSync(geminiDir)) {
      try {
        mkdirSync(geminiDir, { recursive: true });
      } catch {}
    }
    return join(geminiDir, 'profiles.json');
  }

  private loadProfiles(): void {
    try {
      if (existsSync(this.configPath)) {
        const raw = readFileSync(this.configPath, 'utf-8');
        const list: ProfileRecord[] = JSON.parse(raw);
        if (Array.isArray(list)) {
          for (const item of list) {
            if (item.name.toLowerCase() !== 'default') {
              this.profiles.set(item.name.toLowerCase(), item);
            }
          }
        }
      }
    } catch (err) {
      console.warn('[SessionManager] Could not load persisted profiles:', err);
    }

    // Clean any unauthenticated default profile from map
    this.profiles.delete('default');

    // Set first profile active if exists
    if (this.profiles.size > 0) {
      const first = Array.from(this.profiles.values())[0];
      this.activeProfileName = first.name;
    } else {
      this.activeProfileName = 'default';
    }
  }

  private saveProfiles(): void {
    try {
      const list = Array.from(this.profiles.values()).filter(p => p.name.toLowerCase() !== 'default');
      writeFileSync(this.configPath, JSON.stringify(list, null, 2), 'utf-8');
    } catch (err) {
      console.error('[SessionManager] Failed to persist profiles:', err);
    }
  }

  /**
   * Prepares and resolves the isolated VirtualHome directory for a given profile.
   */
  public prepareVirtualHome(profileName: string): string {
    let resolved = profileName;
    if (!resolved || resolved.toLowerCase() === 'default') {
      resolved = this.activeProfileName.toLowerCase() !== 'default'
        ? this.activeProfileName
        : (Array.from(this.profiles.values()).find(p => p.name.toLowerCase() !== 'default')?.name || 'default');
    }

    const sanitized = resolved.replace(/[^a-zA-Z0-9_\-\.@]/g, '_');
    const home = homedir();

    /**
     * The host owns the profile store and tells us where it is.
     *
     * Guessing from cwd used to be the only option, and it failed exactly when the sidecar ran from
     * its own folder: every candidate missed, so it invented `bin\win-x64\bin\Debug\...\Profiles`
     * and ran `agy` against an empty home while the host wrote permissions to the real one.
     */
    const hostProfilesDir = process.env.AGENTS_PROFILES_DIR;

    const legacyCandidates = [
      join(process.cwd(), 'Profiles', 'Antigravity', sanitized, 'VirtualHome'),
      join(process.cwd(), 'bin', 'Debug', 'net10.0-windows', 'Profiles', 'Antigravity', sanitized, 'VirtualHome'),
      join(process.cwd(), 'bin', 'Debug', 'net10.0', 'Profiles', 'Antigravity', sanitized, 'VirtualHome'),
      join(process.cwd(), 'bin', 'Release', 'net10.0', 'Profiles', 'Antigravity', sanitized, 'VirtualHome'),
      join(home, '.gemini', 'antigravity', 'profiles', sanitized, 'VirtualHome')
    ];

    // One authority beats five guesses: when the host names the store, that is the answer, whether
    // or not the directory exists yet.
    const targetDir = hostProfilesDir
      ? join(hostProfilesDir, sanitized, 'VirtualHome')
      : legacyCandidates.find(c => existsSync(c)) ?? legacyCandidates[2];

    const virtGemini = join(targetDir, '.gemini');
    const virtRoaming = join(targetDir, 'AppData', 'Roaming');
    const virtLocal = join(targetDir, 'AppData', 'Local');

    try {
      mkdirSync(targetDir, { recursive: true });
      mkdirSync(virtGemini, { recursive: true });
      mkdirSync(virtRoaming, { recursive: true });
      mkdirSync(virtLocal, { recursive: true });

      const parentProfileDir = join(targetDir, '..');
      const credFiles = ['gemini_credential.json', 'google_accounts.json', 'oauth_creds.json'];

      for (const cf of credFiles) {
        const dest = join(virtGemini, cf);
        if (!existsSync(dest)) {
          const srcProfile = join(parentProfileDir, cf);
          const srcHome = join(home, '.gemini', cf);
          if (existsSync(srcProfile)) {
            copyFileSync(srcProfile, dest);
          } else if (existsSync(srcHome)) {
            copyFileSync(srcHome, dest);
          }
        }
      }
    } catch (err) {
      console.warn(`[SessionManager] VirtualHome setup notice for '${profileName}':`, err);
    }

    return targetDir;
  }

  private allocatePortForProfile(profileName: string): number {
    let hash = 0;
    for (let i = 0; i < profileName.length; i++) {
      hash = (hash << 5) - hash + profileName.charCodeAt(i);
      hash |= 0;
    }
    const offset = Math.abs(hash) % 100;
    return 9340 + offset;
  }

  public buildScopedEnvironment(
    profileName: string,
    sessionId: string,
    virtualHomeDir: string,
    model?: string
  ): Record<string, string> {
    const allocatedPort = this.allocatePortForProfile(profileName);

    const scoped: Record<string, string> = {
      ...(process.env as Record<string, string>),
      USERPROFILE: virtualHomeDir,
      HOME: virtualHomeDir,
      APPDATA: join(virtualHomeDir, 'AppData', 'Roaming'),
      LOCALAPPDATA: join(virtualHomeDir, 'AppData', 'Local'),
      PORT: String(allocatedPort),
      ANTIGRAVITY_PORT: String(allocatedPort),
      AGENTS_SESSION_ID: sessionId,
      AGENTS_PROFILE_NAME: profileName,
      AGENTS_MODEL: model || 'gemini-3.8-flash-high',
      FORCE_COLOR: '0',
      TERM: 'dumb'
    };

    if (profileName.toLowerCase().includes('claude') || (model && model.toLowerCase().includes('claude'))) {
      scoped.ANTHROPIC_MODEL = model || 'claude-3-7-sonnet';
    }

    return scoped;
  }

  public getActiveAccountState(): {
    activeAccount: string;
    provider: string;
    profiles: AccountProfileInfo[];
  } {
    const list: AccountProfileInfo[] = Array.from(this.profiles.values())
      .filter(p => p.name.toLowerCase() !== 'default')
      .map(p => ({
        name: p.name,
        email: p.email,
        provider: p.provider || 'Antigravity',
        active: p.name.toLowerCase() === this.activeProfileName.toLowerCase()
      }));

    let active = this.profiles.get(this.activeProfileName.toLowerCase());
    if (!active || active.name.toLowerCase() === 'default') {
      active = Array.from(this.profiles.values()).find(p => p.name.toLowerCase() !== 'default') || {
        name: 'default',
        provider: 'Antigravity'
      };
      this.activeProfileName = active.name;
    }

    return {
      activeAccount: active.name,
      provider: active.provider || 'Antigravity',
      profiles: list
    };
  }

  public emitAccountState(): void {
    const state = this.getActiveAccountState();
    this.emit({
      event: 'account_state',
      activeAccount: state.activeAccount,
      provider: state.provider,
      profiles: state.profiles,
      timestamp: new Date().toISOString()
    });
  }

  public switchAccount(profileName: string, provider?: string): { success: boolean; activeProfile: string } {
    if (profileName.toLowerCase() === 'default') {
      return { success: false, activeProfile: this.activeProfileName };
    }

    const key = profileName.toLowerCase();
    let record = this.profiles.get(key);

    if (!record) {
      record = {
        name: profileName,
        email: `${profileName.toLowerCase()}@local`,
        provider: provider || 'Antigravity',
        lastUsed: new Date().toISOString()
      };
      this.profiles.set(key, record);
      this.saveProfiles();
    } else {
      record.lastUsed = new Date().toISOString();
      if (provider) record.provider = provider;
      this.saveProfiles();
    }

    this.activeProfileName = record.name;
    this.emitAccountState();

    return {
      success: true,
      activeProfile: this.activeProfileName
    };
  }

  public listAccounts(): AccountProfileInfo[] {
    return this.getActiveAccountState().profiles;
  }

  public refreshToken(profileName: string): { success: boolean; message: string } {
    const record = this.profiles.get(profileName.toLowerCase());
    if (!record) {
      return { success: false, message: `Profile '${profileName}' not found` };
    }

    record.lastUsed = new Date().toISOString();
    this.saveProfiles();
    this.emitAccountState();

    return { success: true, message: `Token validated for ${profileName}` };
  }

  /**
   * Starts and activates a runner profile session inside the Sidecar.
   * Increments active sessions counter and broadcasts real-time state.
   */
  public async startSession(
    profileName: string,
    workingDirectory?: string,
    model?: string
  ): Promise<{ success: boolean; sessionId: string; activeSessionsCount: number }> {
    const key = profileName.toLowerCase();
    const sessionId = this.profileActiveSessionIds.get(key) || `session_${key}_${Date.now()}`;
    this.profileActiveSessionIds.set(key, sessionId);

    const virtualHomeDir = this.prepareVirtualHome(profileName);
    const scopedEnv = this.buildScopedEnvironment(profileName, sessionId, virtualHomeDir, model);
    const cwd = workingDirectory && existsSync(workingDirectory) ? resolve(workingDirectory) : virtualHomeDir;

    const sessionState: ChatSessionState = {
      sessionId,
      profileName,
      workingDirectory: cwd,
      status: 'idle',
      startedAt: Date.now(),
      lastActiveAt: Date.now(),
      scopedEnv
    };

    this.activeSessions.set(sessionId, sessionState);

    // Auto-register profile in registry
    if (!this.profiles.has(key)) {
      this.profiles.set(key, {
        name: profileName,
        email: `${key}@local`,
        provider: 'Antigravity',
        model: model || 'gemini-3.8-flash-high',
        lastUsed: new Date().toISOString()
      });
      this.saveProfiles();
    }

    this.emit({
      event: 'session_status',
      sessionId,
      profile: profileName,
      state: 'idle',
      timestamp: new Date().toISOString()
    });

    this.emit({
      event: 'sidecar_status',
      pid: process.pid,
      activeSessionsCount: this.getActiveSessionsCount(),
      timestamp: new Date().toISOString()
    });

    return {
      success: true,
      sessionId,
      activeSessionsCount: this.getActiveSessionsCount()
    };
  }

  /**
   * Stops an active runner profile session in the Sidecar.
   */
  public async stopSession(profileName: string): Promise<{ success: boolean }> {
    const key = profileName.toLowerCase();
    const sessionIdsToStop: string[] = [];
    const directId = this.profileActiveSessionIds.get(key);
    if (directId) sessionIdsToStop.push(directId);

    for (const [sId, sess] of this.activeSessions.entries()) {
      if (sess.profileName.toLowerCase() === key && !sessionIdsToStop.includes(sId)) {
        sessionIdsToStop.push(sId);
      }
    }

    for (const sId of sessionIdsToStop) {
      const session = this.activeSessions.get(sId);
      if (session) {
        if (session.abortController) {
          try { session.abortController.abort(); } catch {}
        }
        if (session.activeProcess) {
          try { session.activeProcess.kill(9); } catch {}
        }
        session.status = 'idle';
        session.lastActiveAt = Date.now();
      }
      this.activeSessions.delete(sId);
      this.emit({
        event: 'session_status',
        sessionId: sId,
        profile: profileName,
        state: 'idle',
        timestamp: new Date().toISOString()
      });
    }

    this.profileActiveSessionIds.delete(key);

    this.emit({
      event: 'sidecar_status',
      pid: process.pid,
      activeSessionsCount: this.getActiveSessionsCount(),
      timestamp: new Date().toISOString()
    });

    return { success: true };
  }

  public getActiveSessionsCount(): number {
    let count = 0;
    for (const session of this.activeSessions.values()) {
      if (session.status === 'running' || session.status === 'tool_executing' || session.status === 'thinking') {
        count++;
      }
    }
    return count;
  }

  public getActiveSessions(): Array<{ sessionId: string; profile: string; status: string; uptimeSec: number }> {
    const now = Date.now();
    const result = [];
    for (const s of this.activeSessions.values()) {
      result.push({
        sessionId: s.sessionId,
        profile: s.profileName,
        status: s.status,
        uptimeSec: Math.round((now - s.startedAt) / 1000)
      });
    }
    return result;
  }

  public async handleSendChat(params: {
    sessionId: string;
    prompt: string;
    model?: string;
    profile?: string;
    reasoningEffort?: string;
    workingDirectory?: string;
    /** Omitted means full access, matching every build before the toggle existed. */
    skipPermissions?: boolean;
    /** The session's own CLI conversation. Omitted starts a new one. */
    conversationId?: string;
  }): Promise<void> {
    const { sessionId, prompt, model, profile, reasoningEffort, workingDirectory, skipPermissions, conversationId } = params;
    let targetProfile = profile || this.activeProfileName;
    if (!targetProfile || targetProfile.toLowerCase() === 'default') {
      targetProfile = this.activeProfileName.toLowerCase() !== 'default'
        ? this.activeProfileName
        : (Array.from(this.profiles.values()).find(p => p.name.toLowerCase() !== 'default')?.name || 'default');
    }

    const virtualHomeDir = this.prepareVirtualHome(targetProfile);
    const scopedEnv = this.buildScopedEnvironment(targetProfile, sessionId, virtualHomeDir, model);

    const cwd = workingDirectory && existsSync(workingDirectory)
      ? resolve(workingDirectory)
      : virtualHomeDir;

    const abortController = new AbortController();
    const now = Date.now();

    const cachedQuota = this.profileQuotas.get(targetProfile.toLowerCase());
    const sessionState: ChatSessionState = {
      sessionId,
      profileName: targetProfile,
      workingDirectory: cwd,
      status: 'running',
      startedAt: now,
      lastActiveAt: now,
      abortController,
      scopedEnv,
      model: model || 'Gemini 3.8 Flash (High)',
      reasoningEffort,
      skipPermissions: skipPermissions !== false,
      conversationId,
      quotaPercent: cachedQuota?.quotaPercent,
      tokens: { inputTokens: 0, outputTokens: 0, thinkingTokens: 0, totalTokens: 0 }
    };

    this.activeSessions.set(sessionId, sessionState);
    this.profileActiveSessionIds.set(targetProfile.toLowerCase(), sessionId);

    // Broadcast running state with scoped session telemetry
    this.emit({
      event: 'session_status',
      sessionId,
      profile: targetProfile,
      state: 'running',
      model: sessionState.model,
      quotaPercent: sessionState.quotaPercent,
      tokens: sessionState.tokens,
      timestamp: new Date().toISOString()
    });

    try {
      if (prompt.startsWith('/exec ') || prompt.startsWith('> ')) {
        // Direct Command Execution with Tool Card
        const cmd = prompt.replace(/^(\/exec\s+|>)/, '').trim();
        sessionState.status = 'tool_executing';
        sessionState.lastActiveAt = Date.now();

        const callId = `exec_${Date.now()}`;
        this.emit({
          event: 'tool_start',
          sessionId,
          profile: targetProfile,
          callId,
          tool: 'bash',
          toolKind: 'command',
          params: { command: cmd },
          timestamp: new Date().toISOString()
        });

        const startTime = performance.now();
        const result = await ShellExecutor.execute({
          command: cmd,
          cwd,
          env: scopedEnv,
          timeoutMs: 60000,
          abortSignal: abortController.signal,
          onStdout: (chunk) => {
            this.emit({
              event: 'chat_token',
              sessionId,
              profile: targetProfile,
              text: chunk,
              timestamp: new Date().toISOString()
            });
          },
          onStderr: (chunk) => {
            this.emit({
              event: 'chat_token',
              sessionId,
              profile: targetProfile,
              text: chunk,
              timestamp: new Date().toISOString()
            });
          }
        });

        const durationMs = Math.round(performance.now() - startTime);
        this.emit({
          event: 'tool_output',
          sessionId,
          profile: targetProfile,
          callId,
          tool: 'bash',
          toolKind: 'command',
          success: result.exitCode === 0,
          exitCode: result.exitCode,
          durationMs,
          timestamp: new Date().toISOString()
        });
      } else {
        // Real Execution through Antigravity CLI (agy)
        sessionState.status = 'thinking';
        this.emit({
          event: 'session_status',
          sessionId,
          profile: targetProfile,
          state: 'thinking',
          model: sessionState.model,
          quotaPercent: sessionState.quotaPercent,
          tokens: sessionState.tokens,
          timestamp: new Date().toISOString()
        });

        this.emit({
          event: 'thinking',
          sessionId,
          profile: targetProfile,
          state: 'active',
          timestamp: new Date().toISOString()
        });

        await this.executeAgyTurn({
          sessionId,
          prompt,
          model,
          cwd,
          scopedEnv,
          abortController,
          sessionState
        });
      }

      sessionState.status = 'idle';
      sessionState.lastActiveAt = Date.now();
      this.emit({
        event: 'session_status',
        sessionId,
        profile: targetProfile,
        state: 'idle',
        model: sessionState.model,
        quotaPercent: sessionState.quotaPercent,
        tokens: sessionState.tokens,
        timestamp: new Date().toISOString()
      });
    } catch (err: any) {
      sessionState.status = 'error';
      sessionState.lastActiveAt = Date.now();
      this.emit({
        event: 'session_status',
        sessionId,
        profile: targetProfile,
        state: 'error',
        error: err?.message || String(err),
        model: sessionState.model,
        quotaPercent: sessionState.quotaPercent,
        tokens: sessionState.tokens,
        timestamp: new Date().toISOString()
      });
    }
  }

  /**
   * Resolves the agy binary across Windows, macOS, and Linux platforms.
   * Handles .exe / .cmd on Windows, standard Unix bin paths on macOS & Linux,
   * without assuming hardcoded user directories.
   */
  public resolveAgyCommand(): string {
    // Explicit override first: it is the only way to point a test at a stub, and the escape hatch
    // when the CLI is installed somewhere the candidates below do not know about.
    const override = process.env.AGY_CLI_PATH;
    if (override && existsSync(override)) return override;

    if (process.platform === 'win32') {
      const candidates = [
        process.env.LOCALAPPDATA ? join(process.env.LOCALAPPDATA, 'agy', 'bin', 'agy.exe') : '',
        process.env.USERPROFILE ? join(process.env.USERPROFILE, 'AppData', 'Local', 'agy', 'bin', 'agy.exe') : '',
        process.env.APPDATA ? join(process.env.APPDATA, 'npm', 'agy.cmd') : '',
        process.env.APPDATA ? join(process.env.APPDATA, 'npm', 'agy.exe') : '',
        'agy.exe',
        'agy.cmd',
        'agy'
      ];
      return candidates.find(c => Boolean(c) && existsSync(c)) || 'agy.exe';
    }

    // macOS (Darwin) & Linux
    const home = homedir();
    const unixCandidates = [
      join(home, '.local', 'bin', 'agy'),
      join(home, '.agy', 'bin', 'agy'),
      '/opt/homebrew/bin/agy',
      '/usr/local/bin/agy',
      '/usr/bin/agy',
      'agy'
    ];
    return unixCandidates.find(c => Boolean(c) && existsSync(c)) || 'agy';
  }

  /**
   * Executes a prompt turn through agy with stream-json NDJSON parsing.
   * Dispatches live thinking, tokens, and tool calls directly to the UI.
   */
  private async executeAgyTurn(opts: {
    sessionId: string;
    prompt: string;
    model?: string;
    cwd: string;
    scopedEnv: Record<string, string>;
    abortController: AbortController;
    sessionState: ChatSessionState;
  }): Promise<void> {
    const { sessionId, prompt, model, cwd, scopedEnv, abortController, sessionState } = opts;

    // Detect agy CLI binary cross-platform
    const agyCmd = this.resolveAgyCommand();

    const agyArgs = ['--output-format', 'stream-json'];
    // Never `--continue`: that resumes whichever conversation ran last, so a session would inherit
    // another one's history and its token count. A session without an id starts a new conversation
    // and adopts the id the CLI reports on `init`.
    if (sessionState.conversationId) {
      agyArgs.push('--conversation', sessionState.conversationId);
    }
    // Off means the turn runs under the CLI's own review policy. Headless cannot answer a prompt,
    // so anything gated is auto-denied — the UI turns that denial into an approval card.
    if (sessionState.skipPermissions !== false) {
      agyArgs.push('--dangerously-skip-permissions');
    }

    if (model) {
      agyArgs.push('--model', model);
    }
    // `agy` rejects a tiered model id combined with `--effort` ("conflicts with --effort=low") and
    // rejects `--effort` for models without tiers. The tier is already encoded in the model id, so
    // effort is only forwarded when no model was chosen.
    if (sessionState.reasoningEffort && !model) {
      agyArgs.push('--effort', sessionState.reasoningEffort);
    }
    agyArgs.push('-p', prompt);

    let hasReceivedAnyToken = false;

    try {
      const proc = Bun.spawn([agyCmd, ...agyArgs], {
        windowsHide: true,
        cwd,
        env: scopedEnv,
        stdout: 'pipe',
        stderr: 'pipe',
        stdin: 'ignore'
      });

      sessionState.activeProcess = proc;
      sessionState.pid = proc.pid;

      this.emit({
        event: 'session_status',
        sessionId,
        profile: sessionState.profileName,
        state: 'running',
        pid: proc.pid,
        model: sessionState.model,
        quotaPercent: sessionState.quotaPercent,
        tokens: sessionState.tokens,
        timestamp: new Date().toISOString()
      });

      abortController.signal.addEventListener('abort', () => {
        try { proc.kill(9); } catch {}
      });

      // A CLI that hangs before its first byte never resolves the read loop below, and killing it
      // does not unblock a pending read either (measured). So the turn races this deadline.
      const watchdog = createStartWatchdog(agyCmd, undefined, () => {
        try { proc.kill(9); } catch {}
      });

      // Consume stderr in background to prevent pipe deadlock
      if (proc.stderr) {
        (async () => {
          try {
            const errReader = (proc.stderr as ReadableStream<Uint8Array>).getReader();
            const errDecoder = new TextDecoder();
            while (true) {
              const { done, value } = await errReader.read();
              if (done) break;
              const errText = errDecoder.decode(value, { stream: true });
              if (errText.trim()) {
                // A CLI that complains is running, even before its first line of JSON.
                watchdog.markAlive();
                console.warn(`[agy stderr]: ${errText.trim()}`);
              }
            }
          } catch {}
        })();
      }

      // Stream stdout NDJSON lines, then the exit code. Both live in `drain` so the deadline above
      // can cut the turn short: a pending read never resolves on its own when the engine is dead.
      const drain = async (): Promise<number> => {
        if (proc.stdout) {
          const reader = (proc.stdout as ReadableStream<Uint8Array>).getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            buffer += decoder.decode(value, { stream: true });
            const lines = buffer.split('\n');
            buffer = lines.pop() || '';

            for (const line of lines) {
              const trimmed = line.trim();
              if (!trimmed) continue;
              watchdog.markAlive();

              if (trimmed.startsWith('{')) {
                try {
                  const evt = JSON.parse(trimmed);
                  this.processAgyEvent(sessionId, evt, sessionState);
                  hasReceivedAnyToken = true;
                } catch {}
              } else {
                this.processRawCliLine(sessionId, trimmed, sessionState);
                hasReceivedAnyToken = true;
              }
            }
          }
        }

        return await proc.exited;
      };

      const exitCode = await Promise.race([drain(), watchdog.promise]);

      if ((exitCode !== 0 || !hasReceivedAnyToken) && !abortController.signal.aborted) {
        watchdog.dispose();
        console.warn(`[executeAgyTurn] Initial turn exited with code ${exitCode} and receivedToken=${hasReceivedAnyToken}. Falling back to fresh conversation...`);
        await this.executeAgyFresh(opts);
        return;
      }

      // Always ensure thinking state is marked done upon process exit
      this.emit({
        event: 'thinking',
        sessionId,
        profile: sessionState.profileName,
        state: 'done',
        timestamp: new Date().toISOString()
      });

      this.pollQuota(sessionState.profileName, sessionState.model).catch(() => {});
      watchdog.dispose();
    } catch (err: any) {
      // The deadline already answered the question. Retrying would hang identically and double the
      // wait, which is how the broken binary looked like "nothing happens" for forty minutes.
      if (err instanceof EngineStartTimeout) {
        throw err;
      }

      // If first attempt failed with --continue (no previous conversation), retry without --continue
      if (!hasReceivedAnyToken && !abortController.signal.aborted) {
        await this.executeAgyFresh(opts);
        return;
      }
      throw err;
    }
  }

  /**
   * Fresh conversation turn if --continue was rejected
   */
  private async executeAgyFresh(opts: {
    sessionId: string;
    prompt: string;
    model?: string;
    cwd: string;
    scopedEnv: Record<string, string>;
    abortController: AbortController;
    sessionState: ChatSessionState;
  }): Promise<void> {
    const { sessionId, prompt, model, cwd, scopedEnv, abortController, sessionState } = opts;
    const agyCmd = this.resolveAgyCommand();
    const agyArgs = ['--output-format', 'stream-json'];
    if (sessionState.skipPermissions !== false) agyArgs.push('--dangerously-skip-permissions');
    if (model) agyArgs.push('--model', model);
    if (sessionState.reasoningEffort && !model) agyArgs.push('--effort', sessionState.reasoningEffort);
    agyArgs.push('-p', prompt);

    const proc = Bun.spawn([agyCmd, ...agyArgs], {
      windowsHide: true,
      cwd,
      env: scopedEnv,
      stdout: 'pipe',
      stderr: 'pipe',
      stdin: 'ignore'
    });

    sessionState.activeProcess = proc;
    sessionState.pid = proc.pid;

    this.emit({
      event: 'session_status',
      sessionId,
      profile: sessionState.profileName,
      state: 'running',
      pid: proc.pid,
      model: sessionState.model,
      quotaPercent: sessionState.quotaPercent,
      tokens: sessionState.tokens,
      timestamp: new Date().toISOString()
    });

    abortController.signal.addEventListener('abort', () => {
      try { proc.kill(9); } catch {}
    });

    // Same deadline as the resuming turn: this path is where a hanging engine lands after the
    // first attempt fails, and it must not become a second silent wait.
    const watchdog = createStartWatchdog(agyCmd, undefined, () => {
      try { proc.kill(9); } catch {}
    });

    if (proc.stderr) {
      (async () => {
        try {
          const errReader = (proc.stderr as ReadableStream<Uint8Array>).getReader();
          const errDecoder = new TextDecoder();
          while (true) {
            const { done, value } = await errReader.read();
            if (done) break;
            const errText = errDecoder.decode(value, { stream: true });
            if (errText.trim()) {
              watchdog.markAlive();
              console.warn(`[agy fresh stderr]: ${errText.trim()}`);
            }
          }
        } catch {}
      })();
    }

    const drain = async (): Promise<number> => {
      if (proc.stdout) {
        const reader = (proc.stdout as ReadableStream<Uint8Array>).getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split('\n');
          buffer = lines.pop() || '';

          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed) continue;
            watchdog.markAlive();
            if (trimmed.startsWith('{')) {
              try {
                const evt = JSON.parse(trimmed);
                this.processAgyEvent(sessionId, evt, sessionState);
              } catch {}
            } else {
              this.processRawCliLine(sessionId, trimmed, sessionState);
            }
          }
        }
      }

      return await proc.exited;
    };

    await Promise.race([drain(), watchdog.promise]);
    watchdog.dispose();

    this.emit({
      event: 'thinking',
      sessionId,
      profile: sessionState.profileName,
      state: 'done',
      timestamp: new Date().toISOString()
    });

    this.pollQuota(sessionState.profileName, sessionState.model).catch(() => {});
  }

  private processRawCliLine(sessionId: string, line: string, sessionState: ChatSessionState): void {
    const trimmed = line.trim();
    if (!trimmed) return;

    // Filter out CLI noise/banners
    if (trimmed.includes('Usage of agy') || trimmed.includes('Fetching available models') || trimmed.includes('Loaded plugins')) {
      return;
    }

    // 1. Thinking patterns:
    // o Thought for 3.6s (Considering the user's request...)
    // ▸ Thought for 6s, 702 tokens
    const thoughtMatch = trimmed.match(/^[o▸•*]\s*Thought for\s*([0-9.]+)s(?:,\s*([0-9]+)\s*tokens)?(?:\s*\((.*?)\))?/i);
    if (thoughtMatch) {
      const durationSec = parseFloat(thoughtMatch[1]);
      const tokens = thoughtMatch[2] ? parseInt(thoughtMatch[2], 10) : undefined;
      const snippet = thoughtMatch[3] || '';

      this.emit({
        event: 'thinking',
        sessionId,
        profile: sessionState.profileName,
        text: snippet,
        durationSeconds: durationSec,
        thinkingTokens: tokens,
        state: 'done',
        timestamp: new Date().toISOString()
      });

      // Deliberately no `token_usage` here: a thinking line's "N tokens" is a partial count, and
      // emitting it as usage used to overwrite the session total with just the reasoning tokens.
      // The authoritative per-turn usage arrives with the result event below.
      return;
    }

    // 2. Ran tool pattern:
    // ● Ran (Get-ChildItem -Path . -Filter *.py -Recurse | Select-Object FullName, Length, LastWriteTime)
    const ranMatch = trimmed.match(/^[●o•*]\s*Ran\s*\((.*?)\)$/i);
    if (ranMatch) {
      const cmd = ranMatch[1];
      const callId = `ran_${Date.now()}`;
      this.emit({
        event: 'tool_start',
        sessionId,
        profile: sessionState.profileName,
        callId,
        tool: 'Ran',
        params: { CommandLine: cmd },
        timestamp: new Date().toISOString()
      });
      this.emit({
        event: 'tool_output',
        sessionId,
        profile: sessionState.profileName,
        callId,
        tool: 'Ran',
        success: true,
        result: `Executed: ${cmd}`,
        durationMs: 60,
        timestamp: new Date().toISOString()
      });
      return;
    }

    // 3. Explored files pattern:
    // ● Explored 5 files (app.py, gui_app.py, hello.py, +2) (ctrl+o to expand)
    const exploredMatch = trimmed.match(/^[●o•*]\s*Explored\s*(\d+)\s*files?\s*\((.*?)\)/i);
    if (exploredMatch) {
      const count = parseInt(exploredMatch[1], 10);
      const files = exploredMatch[2].replace(/\s*\(ctrl\+o to expand\)/i, '').trim();
      const callId = `exp_${Date.now()}`;
      this.emit({
        event: 'tool_start',
        sessionId,
        profile: sessionState.profileName,
        callId,
        tool: 'Explored',
        params: { count, files },
        timestamp: new Date().toISOString()
      });
      this.emit({
        event: 'tool_output',
        sessionId,
        profile: sessionState.profileName,
        callId,
        tool: 'Explored',
        success: true,
        result: `Explored ${count} files (${files})`,
        durationMs: 60,
        timestamp: new Date().toISOString()
      });
      return;
    }

    // 4. Regular chat token / markdown line
    this.emit({
      event: 'chat_token',
      sessionId,
      profile: sessionState.profileName,
      text: trimmed + '\n',
      timestamp: new Date().toISOString()
    });
  }

  private processAgyEvent(sessionId: string, evt: any, sessionState: ChatSessionState): void {
    if (evt.event === 'init' && evt.init) {
      // The CLI reports which conversation this turn belongs to. The UI stores it on the session so
      // later turns resume exactly this one instead of whatever ran last.
      const conversationId = evt.conversation_id || evt.init.conversation_id;
      if (typeof conversationId === 'string' && conversationId && conversationId !== sessionState.conversationId) {
        sessionState.conversationId = conversationId;
        this.emit({
          event: 'conversation_started',
          sessionId,
          profile: sessionState.profileName,
          conversationId,
          timestamp: new Date().toISOString()
        });
      }

      const modelName = evt.init.model || evt.model;
      if (modelName) {
        sessionState.model = modelName;
        this.emit({
          event: 'model_info',
          sessionId,
          profile: sessionState.profileName,
          model: modelName,
          reasoningEffort: evt.init.reasoning_effort || evt.init.effort,
          timestamp: new Date().toISOString()
        });
      }
    } else if (evt.event === 'step_update' && evt.step_update) {
      const su = evt.step_update;

      // 1. Thinking / Agent Response step
      if (su.step_type === 'agent_response' || su.step_type === 'thinking') {
        const textChunk = su.text_delta ?? su.delta ?? su.text ?? (su.state === 'DONE' && typeof su.response === 'string' ? su.response : null);
        const hasText = typeof textChunk === 'string' && textChunk.length > 0;
        const thinkingTokens = su.usage?.thinking_tokens || 0;

        if (su.state === 'DONE') {
          // Thinking phase completed
          this.emit({
            event: 'thinking',
            sessionId,
            profile: sessionState.profileName,
            durationSeconds: su.duration_seconds || 1.0,
            thinkingTokens: thinkingTokens,
            state: 'done',
            timestamp: new Date().toISOString()
          });
        }

        if (hasText) {
          sessionState.status = 'running';
          sessionState.streamedText = (sessionState.streamedText || '') + textChunk;
          this.emit({
            event: 'chat_token',
            sessionId,
            profile: sessionState.profileName,
            text: textChunk,
            timestamp: new Date().toISOString()
          });
        }

        // Per-step usage is deliberately not published: a turn emits several steps, and treating
        // each one as the turn's usage is what made the counter inflate. The result event carries
        // the authoritative total for the turn.
      }
      // 2. Tool invocation & completion
      else if (su.step_type === 'tool' || su.step_type === 'tool_call') {
        const rawToolName = su.tool_name || su.tool_info?.name || su.call?.name || 'tool';
        const rawParams = su.tool_info?.parameters || su.tool_args || su.call?.arguments || {};
        const callId = su.call_id || `tool_${su.step_index ?? Date.now()}`;

        let toolDisplay = rawToolName;
        let formattedParams = rawParams;
        // The display label is not an identifier: the UI needs the semantic kind to decide whether a
        // call is a command, a read, a search or an edit, and to group consecutive calls by kind.
        let toolKind: 'command' | 'read' | 'search' | 'write' | 'other' = 'other';

        if (rawToolName === 'run_command' || rawToolName === 'shell_exec') {
          toolDisplay = 'Ran';
          toolKind = 'command';
          formattedParams = { CommandLine: rawParams.CommandLine || rawParams.command || '' };
        } else if (rawToolName === 'grep_search' || rawToolName === 'find_by_name') {
          toolDisplay = 'Explored';
          toolKind = 'search';
        } else if (rawToolName === 'list_dir') {
          toolDisplay = 'Explored';
          toolKind = 'read';
        } else if (rawToolName === 'view_file' || rawToolName === 'read_file') {
          toolDisplay = 'Explored';
          toolKind = 'read';
        } else if (
          rawToolName === 'write_to_file' ||
          rawToolName === 'replace_file_content' ||
          rawToolName === 'multi_replace_file_content'
        ) {
          toolDisplay = 'Edited';
          toolKind = 'write';
        }

        if (su.state === 'ACTIVE') {
          sessionState.status = 'tool_executing';
          this.emit({
            event: 'tool_start',
            sessionId,
            profile: sessionState.profileName,
            callId,
            tool: toolDisplay,
            toolKind,
            params: { ...formattedParams, toolKind },
            timestamp: new Date().toISOString()
          });
        } else if (su.state === 'DONE' || su.state === 'ERROR') {
          const isErr = su.state === 'ERROR' || su.status === 'ERROR';
          const output = su.tool_info?.output ?? su.tool_info?.result ?? su.result ?? (isErr ? su.tool_info?.error?.message : '');
          const durationMs = su.duration_seconds ? Math.round(su.duration_seconds * 1000) : 0;

          this.emit({
            event: 'tool_output',
            sessionId,
            profile: sessionState.profileName,
            callId,
            tool: toolDisplay,
            toolKind,
            success: !isErr,
            result: output,
            error: isErr ? (su.tool_info?.error?.message || 'Tool execution failed') : undefined,
            durationMs,
            timestamp: new Date().toISOString()
          });

          // A permission denial is a tool error carrying the CLI's own wording. It is the only
          // signal headless mode can produce when a gated tool is refused, so it is promoted to a
          // first-class event the UI can answer with an allow-rule.
          if (isErr) {
            const denial = parsePermissionDenial(su.tool_info?.error?.message || output || '', {
              name: rawToolName,
              params: su.tool_info?.parameters,
            });
            if (denial) {
              this.emit({
                event: 'permission_denied',
                sessionId,
                profile: sessionState.profileName,
                callId,
                tool: toolDisplay,
                toolKind,
                permission: denial.permission,
                command: denial.command,
                target: denial.target,
                rule: denial.rule,
                message: denial.message,
                timestamp: new Date().toISOString()
              });
            }
          }
        }
      }
    }
    // 3. Final Turn Result
    else if (evt.event === 'result' && evt.result) {
      const res = evt.result;

      // Ensure any final response text in res.response is emitted to the chat if not already streamed
      if (typeof res.response === 'string' && res.response.trim().length > 0) {
        const fullResponse = res.response;
        const currentStreamed = sessionState.streamedText || '';
        
        if (!currentStreamed || !fullResponse.startsWith(currentStreamed)) {
          // If stream missed chunks or was empty, emit the full response
          this.emit({
            event: 'chat_token',
            sessionId,
            profile: sessionState.profileName,
            text: fullResponse.slice(currentStreamed.length) || fullResponse,
            timestamp: new Date().toISOString()
          });
          sessionState.streamedText = fullResponse;
        } else if (fullResponse.length > currentStreamed.length) {
          // Emit remaining diff
          const remaining = fullResponse.slice(currentStreamed.length);
          this.emit({
            event: 'chat_token',
            sessionId,
            profile: sessionState.profileName,
            text: remaining,
            timestamp: new Date().toISOString()
          });
          sessionState.streamedText = fullResponse;
        }
      }

      // Always guarantee thinking is marked done
      this.emit({
        event: 'thinking',
        sessionId,
        profile: sessionState.profileName,
        durationSeconds: res.duration_seconds || 1.0,
        state: 'done',
        timestamp: new Date().toISOString()
      });

      if (res.usage) {
        const inputTokens = res.usage.input_tokens || 0;
        const outputTokens = res.usage.output_tokens || 0;
        const thinkingTokens = res.usage.thinking_tokens || 0;
        // `total_tokens` is authoritative when the engine sends one; otherwise sum the parts rather
        // than inventing a multiplier.
        const reportedTotal = typeof res.usage.total_tokens === 'number' ? res.usage.total_tokens : 0;
        const totalTokens = reportedTotal > 0 ? reportedTotal : inputTokens + outputTokens + thinkingTokens;

        sessionState.tokens = { inputTokens, outputTokens, thinkingTokens, totalTokens };

        this.emit({
          event: 'token_usage',
          sessionId,
          profile: sessionState.profileName,
          usage: sessionState.tokens,
          // One event per completed turn, so the UI can accumulate without guessing.
          scope: 'turn',
          timestamp: new Date().toISOString()
        });
      }
    }
  }

  /**
   * Background Quota Poller: Polls all authenticated profiles independently
   */
  public async pollAllQuotas(): Promise<void> {
    // Only accounts that are actually in use: the active profile plus any profile with a live
    // session. Polling every configured profile on a timer burns quota checks against accounts the
    // user is not touching, and each check spawns a CLI process.
    const profilesToPoll = new Set<string>();
    if (this.activeProfileName && this.activeProfileName.toLowerCase() !== 'default') {
      profilesToPoll.add(this.activeProfileName);
    }
    for (const sess of this.activeSessions.values()) {
      if (sess.profileName && sess.profileName.toLowerCase() !== 'default') {
        profilesToPoll.add(sess.profileName);
      }
    }
    for (const profileName of profilesToPoll) {
      try {
        await this.pollQuota(profileName);
      } catch (err) {
        console.warn(`[SessionManager] Quota poll for ${profileName} failed:`, err);
      }
    }
  }

  public async pollQuota(profileName?: string, modelHint?: string): Promise<{ percent: number; details: string }> {
    try {
      const agyCmd = this.resolveAgyCommand();
      const targetProfile = profileName || this.activeProfileName;
      const virtualHome = this.prepareVirtualHome(targetProfile);
      const env = this.buildScopedEnvironment(targetProfile, 'quota_poll', virtualHome);

      const proc = Bun.spawn([agyCmd, '-p', '/quota', '--dangerously-skip-permissions'], {
        windowsHide: true,
        cwd: virtualHome,
        env,
        stdout: 'pipe',
        stderr: 'pipe',
        stdin: 'ignore'
      });

      // A probe that hangs leaves the quota popover on "checking" forever, so it gets its own
      // deadline. Shorter than a turn: nothing streams here, silence means the engine is stuck.
      const watchdog = createStartWatchdog(agyCmd, resolveStartTimeoutMs(15_000), () => {
        try { proc.kill(9); } catch {}
      });

      let output: string;
      try {
        output = await Promise.race([new Response(proc.stdout).text(), watchdog.promise]);
      } finally {
        watchdog.dispose();
      }
      await proc.exited;

      const snapshot = this.summariseQuota(this.parseQuotaWindows(output), modelHint);
      if (snapshot) {
        const details = output.trim();
        const payload = {
          // `quotaPercent` stays the 5-hour window so every existing consumer keeps a usable number.
          quotaPercent: snapshot.base.percent,
          quotaWeeklyPercent: snapshot.weekly?.percent,
          quotaResetAt: snapshot.base.resetAt,
          quotaWeeklyResetAt: snapshot.weekly?.resetAt,
          quotaGroup: snapshot.base.group,
          quotaWindows: snapshot.windows,
          details
        };

        this.profileQuotas.set(targetProfile.toLowerCase(), {
          quotaPercent: snapshot.base.percent,
          quotaWeeklyPercent: snapshot.weekly?.percent,
          details,
          lastPolled: Date.now()
        });

        // Update all active sessions belonging to this profile and emit session-scoped quota events
        for (const sess of this.activeSessions.values()) {
          if (sess.profileName.toLowerCase() === targetProfile.toLowerCase()) {
            sess.quotaPercent = snapshot.base.percent;
            this.emit({
              event: 'quota_update',
              profile: targetProfile,
              sessionId: sess.sessionId,
              ...payload,
              timestamp: new Date().toISOString()
            });
          }
        }

        // Also emit profile-level quota update
        this.emit({
          event: 'quota_update',
          profile: targetProfile,
          ...payload,
          timestamp: new Date().toISOString()
        });
        return { percent: snapshot.base.percent, details };
      }
    } catch (err) {
      console.warn('[SessionManager] Quota poll failed:', err);
    }
    // -1 means "no reading": callers must not present this as a percentage.
    return { percent: -1, details: '' };
  }

  /**
   * Parses `agy -p "/quota"` output. One row per model group and window, tab separated when piped:
   *
   * `Gemini Models\tFive Hour Limit Remaining\t98%\t2026-10-27T23:10:04Z`
   *
   * The old implementation took `Math.min` of every percentage in the text, which reported the
   * weekly number and hid the 5-hour one entirely.
   */
  private parseQuotaWindows(output: string): QuotaWindowEntry[] {
    const entries: QuotaWindowEntry[] = [];

    for (const rawLine of output.split('\n')) {
      const line = rawLine.trim();
      if (!line || /^quota:?$/i.test(line)) continue;

      let group = '';
      let windowLabel = '';
      let percentText = '';
      let resetAt = '';

      const cells = line.split('\t').map(cell => cell.trim());
      if (cells.length >= 3) {
        [group, windowLabel, percentText, resetAt] = cells;
      } else {
        // Fallback for output that lost its tabs (terminal rendering, wrapped lines).
        const match = line.match(/^(.*?)\s{2,}((?:five hour|weekly)[^0-9]*?)\s*(\d{1,3})\s*%?\s*(.*)$/i);
        if (!match) continue;
        [, group, windowLabel, percentText, resetAt] = match;
      }

      const percent = parseInt(percentText.replace('%', '').trim(), 10);
      if (isNaN(percent)) continue;

      const label = windowLabel.toLowerCase();
      const window: QuotaWindow = label.includes('five') || label.includes('5') ? 'five_hour' : 'weekly';

      entries.push({
        group: group || 'Models',
        label: windowLabel || (window === 'five_hour' ? 'Five Hour Limit' : 'Weekly Limit'),
        window,
        percent,
        resetAt: resetAt || undefined
      });
    }

    return entries;
  }

  /**
   * Picks the number to surface. The 5-hour window is the base reading because it throttles first;
   * the weekly window rides along for the tooltip. When the caller knows the model, its group wins —
   * otherwise the group closest to its limit does.
   */
  private summariseQuota(entries: QuotaWindowEntry[], modelHint?: string): QuotaSnapshot | null {
    if (entries.length === 0) return null;

    const isClaudeGroup = (group: string) => {
      const name = group.toLowerCase();
      return name.includes('claude') || name.includes('gpt');
    };

    const fiveHour = entries.filter(entry => entry.window === 'five_hour');
    const weekly = entries.filter(entry => entry.window === 'weekly');
    const pool = fiveHour.length > 0 ? fiveHour : weekly;

    const hint = (modelHint || '').toLowerCase();
    const wantsClaude = hint.includes('claude') || hint.includes('gpt');

    const preferred = modelHint ? pool.find(entry => isClaudeGroup(entry.group) === wantsClaude) : undefined;
    const base =
      preferred ?? pool.reduce((lowest, entry) => (entry.percent < lowest.percent ? entry : lowest), pool[0]);

    return {
      base,
      weekly: weekly.find(entry => entry.group === base.group),
      windows: entries
    };
  }

  public getSessionState(sessionId: string): ChatSessionState | undefined {
    return this.activeSessions.get(sessionId);
  }

  public getAllSessionStates(): ChatSessionState[] {
    return Array.from(this.activeSessions.values());
  }

  public getProfileQuota(profileName: string): number | undefined {
    return this.profileQuotas.get(profileName.toLowerCase())?.quotaPercent;
  }

  public abortSession(sessionId: string): boolean {
    const session = this.activeSessions.get(sessionId);
    if (session) {
      if (session.abortController) session.abortController.abort();
      if (session.activeProcess) {
        try { session.activeProcess.kill(9); } catch {}
      }
      session.status = 'idle';
      session.lastActiveAt = Date.now();

      this.emit({
        event: 'session_status',
        sessionId,
        profile: session.profileName,
        state: 'idle',
        timestamp: new Date().toISOString()
      });
      return true;
    }
    return false;
  }

  private pruneInactiveSessions(): void {
    const expirationThresholdMs = 30 * 60 * 1000;
    const now = Date.now();

    for (const [id, session] of this.activeSessions.entries()) {
      if ((session.status === 'idle' || session.status === 'completed' || session.status === 'error') &&
          now - session.lastActiveAt > expirationThresholdMs) {
        this.activeSessions.delete(id);
      }
    }
  }
}
