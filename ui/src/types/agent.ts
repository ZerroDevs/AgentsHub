/**
 * Agent & Timeline TypeScript Contracts for AgentsHub Modern SPA
 */

export type AgentExecutionState =
  | 'idle'
  | 'thinking'
  | 'running'
  | 'tool_executing'
  | 'completed'
  | 'error'
  | 'aborted';

export type TimelineNodeType = 'thought' | 'command' | 'file' | 'diff' | 'group' | 'custom';

/**
 * Semantic class of a tool invocation. The sidecar and the store both normalise to this so the
 * stepper never has to pattern-match on human-readable tool labels ("Ran", "Explored"), which is
 * what previously leaked raw JSON payloads into command rows.
 */
export type ToolKind = 'command' | 'read' | 'search' | 'write' | 'other';

/** How a run of consecutive nodes is summarised in the collapsed stepper row. */
export type GroupKind = 'commands' | 'explore' | 'edits' | 'thinking' | 'mixed';

export interface BaseTimelineNode {
  id: string;
  type: TimelineNodeType;
  timestamp: number;
  status: 'running' | 'completed' | 'failed';
  /** Raw tool name as reported by the engine, kept for the drawer header. */
  toolName?: string;
  toolKind?: ToolKind;
}

export interface ThoughtTimelineNode extends BaseTimelineNode {
  type: 'thought';
  text: string;
  durationSeconds?: number;
  tokens?: number;
  isStreaming?: boolean;
}

export interface CommandTimelineNode extends BaseTimelineNode {
  type: 'command';
  /** Clean, single-line human-readable command (never a stringified JSON payload). */
  command: string;
  exitCode?: number;
  /** Merged stdout+stderr, kept for backwards compatibility with persisted sessions. */
  output?: string;
  stdout?: string;
  stderr?: string;
  params?: Record<string, unknown>;
  durationMs?: number;
}

export interface FileTimelineNode extends BaseTimelineNode {
  type: 'file';
  path: string;
  action: 'read' | 'explored' | 'list' | 'search';
  count?: number;
  contentPreview?: string;
}

export interface DiffTimelineNode extends BaseTimelineNode {
  type: 'diff';
  file: string;
  additions: number;
  deletions: number;
  patch?: string;
}

export interface ActionGroupTimelineNode extends BaseTimelineNode {
  type: 'group';
  title: string;
  summary: string;
  groupKind?: GroupKind;
  durationSeconds?: number;
  startedAt?: number;
  endedAt?: number;
  children: TimelineNode[];
}

export type TimelineNode =
  | ThoughtTimelineNode
  | CommandTimelineNode
  | FileTimelineNode
  | DiffTimelineNode
  | ActionGroupTimelineNode;

export interface TokenUsage {
  inputTokens: number;
  outputTokens: number;
  thinkingTokens: number;
  totalTokens: number;
}

export interface ChatAttachment {
  id: string;
  name: string;
  size: number;
  type?: string;
  isImage: boolean;
  dataUrl?: string;
}

export interface ChatMessage {
  id: string;
  sessionId: string;
  sender: 'user' | 'assistant' | 'system';
  prompt?: string;
  attachments?: ChatAttachment[];
  markdown?: string;
  timeline: TimelineNode[];
  status: AgentExecutionState;
  error?: string;
  profile?: string;
  timestamp: number;
  /** 1-based conversation turn this message belongs to. Used to gate title generation. */
  turn?: number;
  /** Set when this message was created by editing an earlier prompt (conversation fork). */
  forkedFrom?: string;
}

export interface QueuedPrompt {
  /** Stable key: the queue holds several, and removing one must not depend on its text. */
  id: string;
  prompt: string;
  attachments?: ChatAttachment[];
}

export interface SessionRecord {
  id: string;
  title: string;
  isTitleCustom: boolean;
  /**
   * Latched once the first prompt has produced a title. While true no later turn may rewrite
   * `title`; only an explicit user rename (isTitleCustom) can change it.
   */
  titleLocked?: boolean;
  isPinned: boolean;
  createdAt: number;
  updatedAt: number;
  profile: string;
  /**
   * Workspace this session belongs to. Undefined means "unfiled" — sessions created before
   * workspaces were tracked, which the sidebar shows in their own bucket rather than guessing.
   */
  workspaceId?: string;
  model: string;
  reasoningEffort?: string;
  /**
   * Which engine answers this session. `agy` is the CLI; `deepseek` runs chat.deepseek.com inside
   * the app's own WebView2 and reports through the same events, so nothing downstream branches.
   */
  engine?: 'agy' | 'deepseek' | 'qwen' | 'zai' | 'vyce' | 'opencode';
  /** DeepSeek only: false turns the chat answer-only, with no tools and no protocol line. */
  toolsEnabled?: boolean;
  /** Unsent composer text, kept so switching tabs does not discard it. */
  draft?: string;
  /** DeepSeek only: the DeepThink chip in their composer. Defaults to on. */
  deepThink?: boolean;
  /** DeepSeek only: the Search chip in their composer. Defaults to off. */
  webSearch?: boolean;
  /**
   * The CLI conversation this session owns. Without it every turn resumed whichever conversation
   * ran last, so chats shared memory and token counts.
   */
  conversationId?: string;
  /**
   * Prompts sent while a turn was still running, oldest first. Each is dispatched once the turn
   * before it settles, so no text is dropped and nothing races the engine.
   */
  queuedPrompts?: QueuedPrompt[];
  quotaPercent?: number;
  /** Accumulated across every completed turn in this session. */
  tokens: TokenUsage;
  /** Usage reported by the most recent completed turn, shown as the primary counter. */
  lastTurnTokens?: TokenUsage;
  status: AgentExecutionState;
  pid?: number;
  messages: ChatMessage[];
  activeExecutionCardId?: string;
}

export type SlashCommandSource = 'builtin' | 'skill' | 'mcp' | 'profile';

/**
 * One selectable model, collapsed to its base identity.
 *
 * The engine lists a separate id per reasoning tier (`…-high`, `…-medium`, `…-low`). Presenting those
 * as separate models duplicates the list, so the tiers travel with the model and the effort picker
 * chooses between them.
 */
export interface ModelInfo {
  /** Base id, used when the chosen effort has no dedicated variant. */
  id: string;
  label: string;
  /** Model family, derived by the host from the id prefix. */
  provider: string;
  /** Effort level to concrete engine id, for the tiers the engine exposes. */
  tiers: Record<string, string>;
}

/** Effort levels offered by the picker. The engine rejects `xhigh`/`max` for these models. */
export type ReasoningEffort = 'low' | 'medium' | 'high';

/** Result of asking the engine which models it supports. */
export interface ModelCatalog {
  models: ModelInfo[];
  /** Effort values the engine accepts, in the engine's own order. */
  efforts: string[];
  /** `cli` when the engine answered, `fallback` when it could not be queried. */
  source: string;
  error?: string | null;
}

export interface SlashCommand {
  /** Command body without the leading slash, e.g. `add-dir`. */
  name: string;
  description: string;
  source: SlashCommandSource;
  /** Engine that reported the command, or `*` when it applies to every engine. */
  engine?: string;
  /** Argument hint shown after the name, e.g. `<path>`. */
  hint?: string;
  /** Inserts `name + ' '` instead of sending immediately when true. */
  takesArgument?: boolean;
}

/** One quota bucket as reported by `agy -p "/quota"`. */
export interface QuotaWindowInfo {
  group: string;
  /** The CLI's own label, e.g. "Five Hour Limit Remaining". */
  label: string;
  percent: number;
  resetAt?: string;
}

export interface RunnerProfile {
  name: string;
  email?: string;
  provider?: string;
  active: boolean;
  isRunning: boolean;
  /**
   * Remaining quota for *this* profile only. Each account authenticates separately and draws from
   * its own pool, so this value must never be shared between profiles.
   */
  quotaPercent?: number;
  /** Weekly window of the same model group, shown in tooltips rather than on the ring. */
  quotaWeeklyPercent?: number;
  /** Reset time of the 5-hour window, ISO string as reported by the CLI. */
  quotaResetAt?: string;
  quotaWeeklyResetAt?: string;
  /** Model group the reading belongs to (e.g. "Gemini Models"). */
  quotaGroup?: string;
  /**
   * Every window the CLI reported, in its own wording. The UI renders these verbatim so new or
   * renamed quota buckets appear without a code change.
   */
  quotaWindows?: QuotaWindowInfo[];
  /** When `quotaPercent` was last read, epoch ms. Used to decide whether a refresh is due. */
  lastQuotaChecked?: number;
  port?: number;
  pid?: number;
  /**
   * Whether this profile's turns run with `--dangerously-skip-permissions`.
   *
   * Shared with the terminal tab's own checkbox: one profile, one answer, so chat and terminal
   * cannot disagree. Off does not make the agent ask — headless runs auto-deny gated tools.
   */
  skipPermissions?: boolean;
}

/** A tool call the CLI refused because headless runs cannot answer a permission prompt. */
export interface PermissionDenialInfo {
  sessionId: string;
  profile?: string;
  callId: string;
  tool: string;
  /** The CLI's permission kind, e.g. `command`. */
  permission: string;
  command?: string;
  target?: string;
  /** Allow-rule that would let the same call through, e.g. `command(npm)`. */
  rule?: string;
  message: string;
  receivedAt: number;
}

export interface SidecarHealth {
  status: 'Online' | 'Offline' | 'Starting' | 'Connecting' | 'Error';
  port: number;
  pid: number;
  isOnline: boolean;
  latencyMs: number;
  activeSessions: number;
  version: string;
  error?: string;
}

export interface HardwareTelemetry {
  netKbps: number;
  netMbps: number;
  ramMb: number;
  totalRamMb: number;
  memoryUsedGb: number;
  memoryTotalGb: number;
  hubMemMb: number;
}

export type ActiveView =
  | 'chat'
  | 'dashboard'
  | 'repositories'
  | 'profiles'
  | 'mcpskills'
  | 'settings';

export interface ProjectWorkspace {
  id: string;
  name: string;
  workingDirectory: string;
  path?: string;
  accentColor?: string;
  colorTag?: string;
  isDefault?: boolean;
  antigravityProfile?: string;
  primaryCli?: string;
  secondaryCli?: string;
  splitMode?: 'single' | 'horizontal' | 'vertical';
  splitRatio?: string;
  environmentVariables?: Record<string, string>;
}

export interface WorkspaceFileItem {
  name: string;
  path: string;
  isDirectory: boolean;
  extension: string;
}

export interface BackendSessionItem {
  sessionId: string;
  goal?: string;
  profile?: string;
  transcriptPath?: string;
  stepCount?: number;
  lastActiveUtc?: string;
}
