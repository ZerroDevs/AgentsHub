/**
 * AgentsHub Cross-Platform Sidecar Protocol
 * Defines bidirectional JSON-RPC and streaming contracts between C# .NET Host and Bun Sidecar Engine.
 */

export type AgentState =
  | 'idle'
  | 'thinking'
  | 'executing_tool'
  | 'streaming'
  | 'completed'
  | 'error'
  | 'aborted';

export type ToolType =
  | 'shell_exec'
  | 'file_read'
  | 'file_write'
  | 'file_patch'
  | 'dir_tree'
  | 'pty_session';

// --- Inbound Messages (Host -> Sidecar) ---

export interface BaseInboundRequest {
  id: string;
  action: string;
}

export interface AuthRequest extends BaseInboundRequest {
  action: 'auth';
  token: string;
}

export interface PingRequest extends BaseInboundRequest {
  action: 'ping';
}

export interface GetStatusRequest extends BaseInboundRequest {
  action: 'get_status';
}

export interface StartTaskRequest extends BaseInboundRequest {
  action: 'start_task';
  taskId: string;
  prompt: string;
  model?: string;
  profile?: string;
  workingDirectory?: string;
  environment?: Record<string, string>;
  toolsAllowed?: ToolType[];
}

export interface AbortTaskRequest extends BaseInboundRequest {
  action: 'abort_task';
  taskId: string;
  reason?: string;
}

export interface ExecuteToolRequest extends BaseInboundRequest {
  action: 'execute_tool';
  callId: string;
  taskId?: string;
  tool: ToolType;
  params: Record<string, any>;
  timeoutMs?: number;
}

export interface TerminalInputRequest extends BaseInboundRequest {
  action: 'terminal_input';
  sessionId: string;
  data: string;
}

export interface TerminalResizeRequest extends BaseInboundRequest {
  action: 'terminal_resize';
  sessionId: string;
  cols: number;
  rows: number;
}

export interface ShutdownRequest extends BaseInboundRequest {
  action: 'shutdown';
  force?: boolean;
}

export interface SwitchAccountRequest extends BaseInboundRequest {
  action: 'switch_account';
  profile: string;
  provider?: string;
}

export interface ListAccountsRequest extends BaseInboundRequest {
  action: 'list_accounts';
}

export interface RefreshTokenRequest extends BaseInboundRequest {
  action: 'refresh_token';
  profile: string;
}

export interface StartSessionRequest extends BaseInboundRequest {
  action: 'start_session';
  profile: string;
  workingDirectory?: string;
  model?: string;
}

export interface StopSessionRequest extends BaseInboundRequest {
  action: 'stop_session';
  profile: string;
}

export interface CheckQuotaRequest extends BaseInboundRequest {
  action: 'check_quota';
  /** Profile whose quota pool to read. Omit for the active profile. */
  profile?: string;
}

export interface SendChatRequest extends BaseInboundRequest {
  action: 'send_chat';
  sessionId: string;
  prompt: string;
  model?: string;
  profile?: string;
  workingDirectory?: string;
  /** Tier is already encoded in the model id; only forwarded when no model was chosen. */
  reasoningEffort?: string;
  /** Omitted means full access. `false` runs the turn under the CLI's review policy. */
  skipPermissions?: boolean;
  /** The session's own CLI conversation. Omitted starts a new one. */
  conversationId?: string;
}

export type InboundMessage =
  | AuthRequest
  | PingRequest
  | GetStatusRequest
  | StartSessionRequest
  | StopSessionRequest
  | StartTaskRequest
  | AbortTaskRequest
  | ExecuteToolRequest
  | TerminalInputRequest
  | TerminalResizeRequest
  | ShutdownRequest
  | SwitchAccountRequest
  | ListAccountsRequest
  | RefreshTokenRequest
  | CheckQuotaRequest
  | SendChatRequest;

// --- Outbound Messages (Sidecar -> Host) ---

export interface ServerReadyEvent {
  event: 'server_ready';
  port: number;
  pid: number;
  platform: 'windows' | 'darwin' | 'linux';
  arch: string;
  version: string;
  timestamp: string;
}

export interface PongEvent {
  event: 'pong';
  replyTo?: string;
  pid: number;
  activeSessionsCount: number;
  port?: number;
  uptimeSec?: number;
  timestamp: string;
}

export interface SidecarStatusEvent {
  event: 'sidecar_status';
  status?: string;
  port?: number;
  pid: number;
  activeSessionsCount: number;
  uptimeSec?: number;
  version?: string;
  timestamp: string;
}

export interface AgentStateEvent {
  event: 'agent_state';
  taskId?: string;
  state: AgentState;
  message?: string;
  timestamp: string;
}

export interface StreamDeltaEvent {
  event: 'stream_delta';
  taskId: string;
  text: string;
  index: number;
  timestamp: string;
}

export interface ChatTokenEvent {
  event: 'chat_token';
  sessionId: string;
  profile?: string;
  text: string;
  timestamp: string;
}

export interface ThinkingEvent {
  event: 'thinking';
  sessionId: string;
  profile?: string;
  text?: string;
  durationSeconds?: number;
  thinkingTokens?: number;
  state?: 'active' | 'done';
  timestamp: string;
}

export interface TokenUsageEvent {
  event: 'token_usage';
  sessionId: string;
  profile?: string;
  usage: {
    inputTokens?: number;
    outputTokens?: number;
    thinkingTokens?: number;
    totalTokens?: number;
  };
  /**
   * What the numbers describe. `turn` means one completed turn, which is what the UI accumulates;
   * anything else must not be added to the session total.
   */
  scope?: 'turn' | 'session';
  timestamp: string;
}

export interface QuotaUpdateEvent {
  event: 'quota_update';
  profile: string;
  sessionId?: string;
  /** 5-hour window of the reported group — the base reading the UI shows. */
  quotaPercent: number;
  /** Weekly window of the same group, shown in tooltips. */
  quotaWeeklyPercent?: number;
  quotaResetAt?: string;
  quotaWeeklyResetAt?: string;
  quotaGroup?: string;
  /** Every window the CLI reported, so the UI can show the full picture if it wants to. */
  quotaWindows?: Array<{
    group: string;
    label: string;
    window: 'five_hour' | 'weekly';
    percent: number;
    resetAt?: string;
  }>;
  details?: string;
  timestamp: string;
}

export interface QuotaCheckCompletedEvent {
  event: 'quota_check_completed';
  /** Profile that was checked, or null when the active profile was used. */
  profile: string | null;
  success: boolean;
  /** Only present on success; a failed check reports no number rather than a default. */
  quotaPercent?: number;
  timestamp: string;
}

export interface ModelInfoEvent {
  event: 'model_info';
  sessionId?: string;
  profile?: string;
  model: string;
  reasoningEffort?: string;
  timestamp: string;
}

export interface SessionStatusEvent {
  event: 'session_status';
  sessionId: string;
  profile?: string;
  state: 'idle' | 'thinking' | 'running' | 'tool_executing' | 'completed' | 'error';
  error?: string;
  pid?: number;
  model?: string;
  quotaPercent?: number;
  tokens?: {
    inputTokens?: number;
    outputTokens?: number;
    thinkingTokens?: number;
    totalTokens?: number;
  };
  timestamp: string;
}

export interface AccountProfileInfo {
  name: string;
  email?: string;
  active: boolean;
  provider?: string;
}

export interface AccountStateEvent {
  event: 'account_state';
  activeAccount: string;
  provider: string;
  profiles: AccountProfileInfo[];
  timestamp: string;
}

export interface ToolStartEvent {
  event: 'tool_start';
  sessionId?: string;
  profile?: string;
  taskId?: string;
  callId: string;
  tool: string;
  /**
   * Semantic kind of the call. The `tool` field is a display label ("Ran", "Explored"), so the UI
   * groups and renders from this instead of pattern-matching on the label.
   */
  toolKind?: 'command' | 'read' | 'search' | 'write' | 'other';
  params: Record<string, any>;
  timestamp: string;
}

export interface ToolOutputEvent {
  event: 'tool_output';
  sessionId?: string;
  profile?: string;
  taskId?: string;
  callId: string;
  tool: string;
  toolKind?: 'command' | 'read' | 'search' | 'write' | 'other';
  success: boolean;
  result?: any;
  error?: string;
  exitCode?: number;
  durationMs: number;
  timestamp: string;
}

/**
 * A tool call the CLI refused because headless runs cannot prompt for permission.
 *
 * `rule` is the allow-rule that would let this exact call through, so the UI can offer "allow" and
 * write it to the profile's settings.json. Absent when the denial's shape is not recognised.
 */
export interface PermissionDeniedEvent {
  event: 'permission_denied';
  sessionId?: string;
  profile?: string;
  callId: string;
  tool: string;
  toolKind?: 'command' | 'read' | 'search' | 'write' | 'other';
  permission: string;
  command?: string;
  target?: string;
  rule?: string;
  message: string;
  timestamp: string;
}

/**
 * The CLI conversation a session owns, reported once per new conversation.
 *
 * Without this the host had no way to keep sessions apart, so every turn resumed whichever
 * conversation ran last and the agent answered with another chat's history.
 */
export interface ConversationStartedEvent {
  event: 'conversation_started';
  sessionId: string;
  profile?: string;
  conversationId: string;
  timestamp: string;
}

export interface TaskCompletedSummary {  success: boolean;
  durationMs: number;
  tokensUsed?: number;
  stepsCount: number;
  error?: string;
  completedAt: string;
}

export interface TaskCompletedEvent {
  event: 'task_completed';
  taskId: string;
  summary: TaskCompletedSummary;
  timestamp: string;
}

export interface TerminalDataEvent {
  event: 'terminal_data';
  sessionId: string;
  data: string;
  timestamp: string;
}

export interface ErrorEvent {
  event: 'error';
  replyTo?: string;
  code: string;
  message: string;
  timestamp: string;
}

export type OutboundEvent =
  | ServerReadyEvent
  | PongEvent
  | SidecarStatusEvent
  | AgentStateEvent
  | StreamDeltaEvent
  | ChatTokenEvent
  | ThinkingEvent
  | TokenUsageEvent
  | QuotaUpdateEvent
  | QuotaCheckCompletedEvent
  | ModelInfoEvent
  | SessionStatusEvent
  | AccountStateEvent
  | ToolStartEvent
  | ToolOutputEvent
  | PermissionDeniedEvent
  | ConversationStartedEvent
  | TaskCompletedEvent
  | TerminalDataEvent
  | ErrorEvent;
