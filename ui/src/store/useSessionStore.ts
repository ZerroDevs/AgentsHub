/**
 * Multi-Session State Store (Zustand + LocalStorage Persistence)
 * Strictly isolates model, quota, tokens, PID, and chat timelines per session.
 */

import { create } from 'zustand';
import {
  SessionRecord,
  ChatMessage,
  AgentExecutionState,
  TimelineNode,
  TokenUsage,
  ThoughtTimelineNode,
  CommandTimelineNode,
  FileTimelineNode,
  DiffTimelineNode,
  ChatAttachment,
  PermissionDenialInfo,
} from '../types/agent';
import { GrantedRule, PermissionScope, revokeRule } from '../lib/permissionRules';
import { generateSessionTitle } from '../services/sessionTitleGenerator';
import { classifyTool, extractCommand, extractPath, normaliseOutput } from '../lib/toolMeta';
import { migrateTimelineNodes } from '../lib/timelineMigration';
import { useWorkspaceStore } from './useWorkspaceStore';
import { isUnsetProfile } from '../lib/profileName';

const STORAGE_KEY_SESSIONS = 'agentshub_sessions_v2';

/** Titles that mean "not named yet". A session holding one of these is still eligible for naming. */
const PLACEHOLDER_TITLES = new Set(['new session', 'workspace session', 'untitled session', 'untitled', '']);

function isPlaceholderTitle(title: string | undefined): boolean {
  const raw = (title || '').trim();
  if (PLACEHOLDER_TITLES.has(raw.toLowerCase())) return true;
  // A title made only of markup ("<USER_REQUEST>") names nothing, so the session stays nameable.
  return raw.replace(/<[^>]*>/g, '').trim().length === 0;
}
const STORAGE_KEY_ACTIVE = 'agentshub_active_session_v2';
/**
 * Tabs open in the chat tab bar. Separate from the session list on purpose: closing a tab hides it
 * from the bar and leaves the conversation in the sidebar, where deleting still means deleting.
 */
const STORAGE_KEY_TABS = 'agentshub_open_tabs_v1';

function readOpenTabs(sessions: Record<string, SessionRecord>): string[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY_TABS);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) {
        const valid = parsed.filter((id): id is string => typeof id === 'string' && Boolean(sessions[id]));
        if (valid.length > 0) return valid;
      }
    }
  } catch {
    // Fall through to "everything is open".
  }
  return Object.keys(sessions);
}

function persistOpenTabs(ids: string[]): void {
  try {
    localStorage.setItem(STORAGE_KEY_TABS, JSON.stringify(ids));
  } catch {
    // Tab state is a convenience; the sidebar is the source of truth.
  }
}
/** Conversation-scoped allow-rules, kept only so a crash cannot leave them in the profile forever. */
const STORAGE_KEY_CONV_RULES = 'agentshub_conv_rules_v1';

function readConversationRules(): Record<string, GrantedRule[]> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY_CONV_RULES);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return {};

    const result: Record<string, GrantedRule[]> = {};
    for (const [sessionId, entries] of Object.entries(parsed as Record<string, unknown>)) {
      if (!Array.isArray(entries)) continue;
      const rules = entries.filter(
        (entry): entry is GrantedRule =>
          Boolean(entry) &&
          typeof (entry as GrantedRule).profile === 'string' &&
          typeof (entry as GrantedRule).rule === 'string'
      );
      if (rules.length > 0) result[sessionId] = rules;
    }
    return result;
  } catch {
    return {};
  }
}

function persistConversationRules(rules: Record<string, GrantedRule[]>): void {
  try {
    localStorage.setItem(STORAGE_KEY_CONV_RULES, JSON.stringify(rules));
  } catch (err) {
    console.warn('[SessionStore] Could not persist conversation rules:', err);
  }
}

/** Result of a regenerate or edit: the prompt to re-send plus the id of the new reply placeholder. */
interface PromptDispatch {
  prompt: string;
  assistantMsgId: string;
}

interface SessionStoreState {
  sessions: Record<string, SessionRecord>;
  activeSessionId: string;
  /**
   * Tool calls the CLI refused for lack of permission, per session. Not persisted: a denial belongs
   * to one turn, and a stale one would offer to grant a rule the user never saw refused.
   */
  pendingDenials: Record<string, PermissionDenialInfo[]>;
  /** Rules granted for exactly one turn, revoked when that turn settles. */
  onceRules: Record<string, GrantedRule[]>;
  /** Rules granted "for this conversation", revoked when the chat goes away. Persisted so a crash
   *  cannot leave them behind: they are revoked on the next start. */
  conversationRules: Record<string, GrantedRule[]>;

  // Actions
  /** `workspaceId: null` creates a session deliberately not bound to any workspace. */
  createSession: (
    profile?: string,
    initialModel?: string,
    workspaceId?: string | null,
    engine?: 'agy' | 'deepseek' | 'qwen' | 'zai' | 'vyce' | 'opencode' | 'byok'
  ) => string;
  switchSession: (sessionId: string) => void;
  renameSession: (sessionId: string, title: string) => void;
  pinSession: (sessionId: string, isPinned?: boolean) => void;
  deleteSession: (sessionId: string) => void;
  clearSessionMessages: (sessionId: string) => void;
  /** Turns the DeepSeek tool loop on or off for one session. */
  setToolsEnabled: (sessionId: string, enabled: boolean) => void;
  /** Persists the unsent composer text so a tab switch does not lose it. */
  setDraft: (sessionId: string, draft: string) => void;
  /** DeepSeek's two composer chips, stored per session. */
  setDeepSeekOption: (sessionId: string, option: 'deepThink' | 'webSearch', value: boolean) => void;
  /** Ids shown in the tab bar, in order. */
  openTabIds: string[];
  openTab: (sessionId: string) => void;
  /** Closes the tab only. The session stays in the sidebar. */
  closeTab: (sessionId: string) => void;

  // Real-time Chat & Streaming Actions
  addUserPrompt: (
    sessionId: string,
    prompt: string,
    profile?: string,
    attachments?: ChatAttachment[]
  ) => { userMsgId: string; assistantMsgId: string };
  /**
   * Removes the assistant reply and its timeline, then opens a fresh empty reply for the same user
   * prompt. Returns the prompt that has to be re-sent to the engine, or null when the pair cannot be
   * resolved.
   */
  prepareRegenerate: (sessionId: string, assistantMessageId: string) => PromptDispatch | null;
  /**
   * Forks the conversation at a user message: that message and everything after it are dropped and
   * replaced by the edited prompt. Returns the edited text plus the ids of the new pair.
   */
  editUserPrompt: (sessionId: string, userMessageId: string, newPrompt: string) => PromptDispatch | null;
  /** Marks an in-flight turn as user-aborted and settles every running node. */
  markStopped: (sessionId: string) => void;
  /** Appends a prompt that arrived mid-turn; the controller sends them in order. */
  queuePrompt: (sessionId: string, prompt: string, attachments?: ChatAttachment[]) => void;
  /** Drops one queued prompt by id, whether it was sent or taken back for editing. */
  removeQueuedPrompt: (sessionId: string, id: string) => void;
  appendChatToken: (sessionId: string, text: string) => void;
  /**
   * Replaces the last assistant message instead of appending to it.
   *
   * The transcript is append-only, so anything streamed once can never be taken back. A turn that
   * streams a raw action object would keep it forever; this lets the host settle the text at the end.
   */
  setAssistantText: (sessionId: string, text: string) => void;
  appendThinkingChunk: (
    sessionId: string,
    text?: string,
    durationSeconds?: number,
    thinkingTokens?: number,
    state?: 'active' | 'done'
  ) => void;
  startToolNode: (sessionId: string, callId: string, tool: string, params: Record<string, any>) => void;
  finishToolNode: (
    sessionId: string,
    callId: string,
    tool: string,
    success: boolean,
    result?: any,
    error?: string,
    durationMs?: number
  ) => void;
  updateSessionStatus: (
    sessionId: string,
    status: AgentExecutionState,
    error?: string,
    pid?: number
  ) => void;
  /**
   * Records one completed turn's usage: the latest turn is kept for display and its numbers are
   * added to the session total. Called once per `token_usage` event, which the sidecar emits exactly
   * once per turn.
   */
  recordTurnUsage: (sessionId: string, usage: TokenUsage) => void;
  updateSessionTelemetry: (
    sessionId: string,
    data: {
      model?: string;
      reasoningEffort?: string;
      /** The CLI conversation this session owns; set once, then reused by every later turn. */
      conversationId?: string;
      quotaPercent?: number;
      tokens?: Partial<TokenUsage>;
    }
  ) => void;

  /**
   * Records a refusal so the composer can offer to allow it. Repeated refusals of the same rule
   * collapse into one entry — the agent often retries a command it was told not to.
   */
  addPermissionDenial: (denial: PermissionDenialInfo) => void;
  dismissPermissionDenial: (sessionId: string, rule?: string) => void;
  /** Records a granted rule under the scope the user picked, so it can be undone later. */
  registerGrantedRule: (sessionId: string, profile: string, rule: string, scope: PermissionScope) => void;
  /** Hands back and forgets the run-once rules for a session, once its turn has settled. */
  consumeOnceRules: (sessionId: string) => GrantedRule[];
  /** Hands back and forgets the conversation rules for a session. */
  consumeConversationRules: (sessionId: string) => GrantedRule[];
  /** Revokes conversation rules left behind by a previous run. */
  flushStaleConversationRules: () => void;

  // Getters
  getActiveSession: () => SessionRecord | undefined;
}

function loadPersistedSessions(): { sessions: Record<string, SessionRecord>; activeId: string } {
  try {
    const rawSessions = localStorage.getItem(STORAGE_KEY_SESSIONS);
    const rawActive = localStorage.getItem(STORAGE_KEY_ACTIVE);

    if (rawSessions) {
      const parsed = JSON.parse(rawSessions);
      if (parsed && typeof parsed === 'object' && Object.keys(parsed).length > 0) {
        for (const k of Object.keys(parsed)) {
          const s = parsed[k];
          // Legacy records may hold a bare "default" (or nothing at all); an empty id means
          // "unconfigured" and the host resolves the active profile from there.
          if (isUnsetProfile(s.profile)) {
            s.profile = '';
          }
          if (Array.isArray(s.messages)) {
            for (const m of s.messages) {
              if (isUnsetProfile(m.profile)) {
                m.profile = '';
              }
              // Repair rows written by the old renderer, which stored stringified params bags as
              // command text and never recorded a tool kind.
              if (Array.isArray(m.timeline)) {
                m.timeline = migrateTimelineNodes(m.timeline);
              }
            }
          }
          // A session that already has messages has had its one title generation; latch it so the
          // old retitle-on-every-turn behaviour cannot come back through a stale record.
          // Sessions named from a prompt that opened with markup are stuck on "<USER_REQUEST>".
          // Re-derive the title from the first real prompt so the sidebar reads like a list again.
          if (isPlaceholderTitle(s.title) && Array.isArray(s.messages)) {
            const firstUser = s.messages.find((m: any) => m?.sender === 'user' && typeof m?.prompt === 'string');
            if (firstUser?.prompt) {
              const regenerated = generateSessionTitle(firstUser.prompt);
              if (!isPlaceholderTitle(regenerated)) {
                s.title = regenerated;
                s.titleLocked = true;
              }
            }
          }

          if (typeof s.titleLocked !== 'boolean') {
            // Only latch a real title. Locking a placeholder is what made sessions stay "New Session"
            // forever: the record had messages, so it was marked as already named.
            s.titleLocked = Array.isArray(s.messages) && s.messages.length > 0 && !isPlaceholderTitle(s.title);
          }
        }
        const activeId = rawActive && parsed[rawActive] ? rawActive : Object.keys(parsed)[0];
        return { sessions: parsed, activeId };
      }
    }
  } catch (err) {
    console.warn('[SessionStore] Failed to load local sessions:', err);
  }

  // Create initial default session
  const defaultId = `sess_${Date.now()}_${Math.random().toString(36).slice(2, 7)}`;
  const defaultSession: SessionRecord = {
    id: defaultId,
    title: 'Workspace Session',
    isTitleCustom: false,
    titleLocked: false,
    isPinned: false,
    createdAt: Date.now(),
    updatedAt: Date.now(),
    profile: '',
    // Unknown until the engine reports one; the composer shows the resolved default.
    model: '',
    quotaPercent: undefined,
    tokens: { inputTokens: 0, outputTokens: 0, thinkingTokens: 0, totalTokens: 0 },
    status: 'idle',
    messages: [],
  };

  return {
    sessions: { [defaultId]: defaultSession },
    activeId: defaultId,
  };
}

function persistState(sessions: Record<string, SessionRecord>, activeId: string) {
  try {
    // A queued prompt has no watcher after a reload, so persisting it would leave a row that never
    // sends and carries its attachments in storage for nothing. It lives as long as the window does.
    const persistable = Object.fromEntries(
      Object.entries(sessions).map(([id, session]) => {
        if (!session.queuedPrompts) return [id, session];

        const { queuedPrompts: _queued, ...rest } = session;
        return [id, rest];
      })
    );

    localStorage.setItem(STORAGE_KEY_SESSIONS, JSON.stringify(persistable));
    localStorage.setItem(STORAGE_KEY_ACTIVE, activeId);
  } catch (err) {
    console.warn('[SessionStore] Could not persist sessions:', err);
  }
}

/**
 * Splits a tool result into stdout/stderr for the code drawer. Engines return either a plain string
 * or a structured object, and a failure belongs in stderr rather than in the output stream.
 */
function shapeCommandResult(
  result: unknown,
  success: boolean,
  error?: string
): { stdout?: string; stderr?: string; exitCode?: number } {
  if (result && typeof result === 'object') {
    const record = result as Record<string, unknown>;
    const stdout =
      typeof record.stdout === 'string' ? record.stdout : typeof record.output === 'string' ? record.output : '';
    const stderr =
      typeof record.stderr === 'string' ? record.stderr : typeof record.error === 'string' ? record.error : '';
    const exitCode = typeof record.exitCode === 'number' ? record.exitCode : undefined;

    if (stdout || stderr || exitCode !== undefined) {
      return {
        stdout: normaliseOutput(stdout) || undefined,
        stderr: normaliseOutput(stderr) || undefined,
        exitCode,
      };
    }
  }

  const text =
    result === undefined || result === null
      ? ''
      : typeof result === 'string'
        ? result
        : JSON.stringify(result, null, 2);

  if (success) return { stdout: normaliseOutput(text) || undefined };
  return { stderr: normaliseOutput(text || error || 'Command failed') || undefined };
}

/**
 * One-time reset of the stored token totals.
 *
 * Every count recorded before sessions owned their own conversation came from one shared
 * conversation, so a four-message chat could carry hundreds of thousands of tokens of *other*
 * chats' history. Those numbers cannot be corrected after the fact, only cleared — the counters
 * restart at zero and mean what they say from here on.
 */
const TOKEN_RESET_KEY = 'agentshub_token_totals_reset_v1';

function resetContaminatedTotals(sessions: Record<string, SessionRecord>): Record<string, SessionRecord> {
  try {
    if (localStorage.getItem(TOKEN_RESET_KEY) === 'done') return sessions;
  } catch {
    return sessions;
  }

  const empty = { inputTokens: 0, outputTokens: 0, thinkingTokens: 0, totalTokens: 0 };
  const next: Record<string, SessionRecord> = {};
  for (const [id, session] of Object.entries(sessions)) {
    next[id] = { ...session, tokens: { ...empty }, lastTurnTokens: undefined };
  }

  try {
    localStorage.setItem(TOKEN_RESET_KEY, 'done');
  } catch {
    // The cleared view is still returned; the flag is best-effort.
  }
  return next;
}

const initialData = loadPersistedSessions();
const migratedSessions = resetContaminatedTotals(initialData.sessions);
if (migratedSessions !== initialData.sessions) {
  persistState(migratedSessions, initialData.activeId);
}

export const useSessionStore = create<SessionStoreState>((set, get) => ({
  sessions: migratedSessions,
  activeSessionId: initialData.activeId,
  openTabIds: readOpenTabs(migratedSessions),
  pendingDenials: {},
  onceRules: {},
  conversationRules: readConversationRules(),

  registerGrantedRule: (sessionId, profile, rule, scope) => {
    if (scope === 'persist') return;

    set(state => {
      if (scope === 'once') {
        const existing = state.onceRules[sessionId] ?? [];
        return { onceRules: { ...state.onceRules, [sessionId]: [...existing, { profile, rule }] } };
      }

      const existing = state.conversationRules[sessionId] ?? [];
      const next = { ...state.conversationRules, [sessionId]: [...existing, { profile, rule }] };
      persistConversationRules(next);
      return { conversationRules: next };
    });
  },

  consumeOnceRules: sessionId => {
    const existing = get().onceRules[sessionId] ?? [];
    if (existing.length > 0) {
      set(state => ({ onceRules: { ...state.onceRules, [sessionId]: [] } }));
    }
    return existing;
  },

  consumeConversationRules: sessionId => {
    const existing = get().conversationRules[sessionId] ?? [];
    if (existing.length > 0) {
      const next = { ...get().conversationRules };
      delete next[sessionId];
      persistConversationRules(next);
      set({ conversationRules: next });
    }
    return existing;
  },

  flushStaleConversationRules: () => {
    const all = get().conversationRules;
    const sessionIds = Object.keys(all);
    if (sessionIds.length === 0) return;

    for (const sessionId of sessionIds) {
      for (const entry of all[sessionId]) void revokeRule(entry);
    }
    persistConversationRules({});
    set({ conversationRules: {} });
  },

  addPermissionDenial: denial => {
    set(state => {
      const existing = state.pendingDenials[denial.sessionId] ?? [];
      const key = denial.rule || denial.callId;
      const deduped = existing.filter(d => (d.rule || d.callId) !== key);

      /**
       * A refusal means the turn is stuck on a decision, so the "Waiting for the first token…" line
       * is a lie while it is shown. Settling only an empty reply — no text, no timeline — leaves a
       * turn that is still producing steps untouched.
       */
      const target = state.sessions[denial.sessionId];
      let sessions = state.sessions;
      const lastIndex = (target?.messages.length ?? 0) - 1;
      const last = lastIndex >= 0 ? target!.messages[lastIndex] : undefined;
      if (target && last && last.sender === 'assistant' && last.status === 'thinking' &&
          !last.markdown && (!last.timeline || last.timeline.length === 0)) {
        const messages = [...target.messages];
        messages[lastIndex] = { ...last, status: 'completed' };
        sessions = { ...state.sessions, [denial.sessionId]: { ...target, messages } };
      }

      return {
        sessions,
        pendingDenials: {
          ...state.pendingDenials,
          [denial.sessionId]: [denial, ...deduped].slice(0, 5),
        },
      };
    });
  },

  dismissPermissionDenial: (sessionId, rule) => {
    set(state => {
      const existing = state.pendingDenials[sessionId] ?? [];
      const remaining = rule ? existing.filter(d => d.rule !== rule) : [];
      return {
        pendingDenials: {
          ...state.pendingDenials,
          [sessionId]: remaining,
        },
      };
    });
  },

  getActiveSession: () => {
    const { sessions, activeSessionId } = get();
    return sessions[activeSessionId];
  },

  createSession: (profile = '', initialModel = '', workspaceId?: string | null, engine?: 'agy' | 'deepseek' | 'qwen' | 'zai' | 'vyce' | 'opencode' | 'byok') => {
    const resolvedProfile = isUnsetProfile(profile) ? '' : profile;
    const id = `sess_${Date.now()}_${Math.random().toString(36).slice(2, 7)}`;
    const newSession: SessionRecord = {
      id,
      title: 'New Session',
      isTitleCustom: false,
      titleLocked: false,
      isPinned: false,
      createdAt: Date.now(),
      updatedAt: Date.now(),
      profile: resolvedProfile,
      // Bind to the active workspace unless the caller asked for an unbound session (Quick Start).
      workspaceId:
        workspaceId === null
          ? undefined
          : workspaceId ?? (useWorkspaceStore.getState().activeWorkspaceId || undefined),
      model: initialModel,
      engine: engine ?? 'agy',
      // Unknown until this profile's quota is actually read; the ring renders an empty track.
      quotaPercent: undefined,
      tokens: { inputTokens: 0, outputTokens: 0, thinkingTokens: 0, totalTokens: 0 },
      status: 'idle',
      messages: [],
    };

    set(state => {
      const nextSessions = { ...state.sessions, [id]: newSession };
      const nextTabs = state.openTabIds.includes(id) ? state.openTabIds : [...state.openTabIds, id];
      persistState(nextSessions, id);
      persistOpenTabs(nextTabs);
      return {
        sessions: nextSessions,
        activeSessionId: id,
        openTabIds: nextTabs,
      };
    });

    return id;
  },

  switchSession: (sessionId: string) => {
    // A session belongs to a workspace, so opening it must take the app there. Without this the
    // header and the FILES panel kept showing whichever workspace was active before.
    const target = get().sessions[sessionId];
    if (target?.workspaceId && target.workspaceId !== useWorkspaceStore.getState().activeWorkspaceId) {
      void useWorkspaceStore.getState().switchWorkspace(target.workspaceId);
    }

    set(state => {
      if (!state.sessions[sessionId]) return state;
      // Picking a session from the sidebar reopens its tab rather than opening a duplicate.
      const nextTabs = state.openTabIds.includes(sessionId) ? state.openTabIds : [...state.openTabIds, sessionId];
      persistState(state.sessions, sessionId);
      persistOpenTabs(nextTabs);
      return { activeSessionId: sessionId, openTabIds: nextTabs };
    });
  },

  openTab: (sessionId: string) => {
    set(state => {
      if (!state.sessions[sessionId] || state.openTabIds.includes(sessionId)) return state;
      const nextTabs = [...state.openTabIds, sessionId];
      persistOpenTabs(nextTabs);
      return { openTabIds: nextTabs };
    });
  },

  /**
   * Removes the tab and nothing else. The session keeps its messages, its conversation id and its
   * place in the sidebar; only the tab bar forgets it.
   */
  closeTab: (sessionId: string) => {
    set(state => {
      if (!state.openTabIds.includes(sessionId)) return state;

      const nextTabs = state.openTabIds.filter(id => id !== sessionId);
      const nextActive =
        state.activeSessionId === sessionId ? (nextTabs[nextTabs.length - 1] ?? '') : state.activeSessionId;

      persistOpenTabs(nextTabs);
      persistState(state.sessions, nextActive);
      return { openTabIds: nextTabs, activeSessionId: nextActive };
    });
  },

  renameSession: (sessionId: string, title: string) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const updated: SessionRecord = {
        ...target,
        title: title.trim() || 'Untitled Session',
        isTitleCustom: true,
        titleLocked: true,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  pinSession: (sessionId: string, isPinned?: boolean) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const updated: SessionRecord = {
        ...target,
        isPinned: isPinned !== undefined ? isPinned : !target.isPinned,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  deleteSession: (sessionId: string) => {
    // A conversation-scoped grant dies with its conversation.
    const scopedRules = get().conversationRules[sessionId] ?? [];
    if (scopedRules.length > 0) {
      for (const entry of scopedRules) void revokeRule(entry);
      const nextRules = { ...get().conversationRules };
      delete nextRules[sessionId];
      persistConversationRules(nextRules);
      set({ conversationRules: nextRules });
    }

    set(state => {
      const keys = Object.keys(state.sessions);
      if (keys.length <= 1) {
        // Clear instead of removing last remaining session
        const singleId = keys[0];
        const cleared: SessionRecord = {
          ...state.sessions[singleId],
          title: 'Workspace Session',
          isTitleCustom: false,
          titleLocked: false,
          messages: [],
          status: 'idle',
          tokens: { inputTokens: 0, outputTokens: 0, thinkingTokens: 0, totalTokens: 0 },
        };
        const nextSessions = { [singleId]: cleared };
        persistState(nextSessions, singleId);
        return { sessions: nextSessions, activeSessionId: singleId };
      }

      const nextSessions = { ...state.sessions };
      delete nextSessions[sessionId];

      let nextActiveId = state.activeSessionId;
      if (state.activeSessionId === sessionId) {
        nextActiveId = Object.keys(nextSessions)[0];
      }

      // A deleted session cannot keep a tab: that is the one case where the bar loses an entry
      // without the user closing it.
      const nextTabs = state.openTabIds.filter(id => id !== sessionId);
      persistOpenTabs(nextTabs);
      persistState(nextSessions, nextActiveId);
      return {
        sessions: nextSessions,
        activeSessionId: nextActiveId,
        openTabIds: nextTabs,
      };
    });
  },

  setDeepSeekOption: (sessionId, option, value) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const nextSessions = { ...state.sessions, [sessionId]: { ...target, [option]: value } };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  setDraft: (sessionId, draft) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target || target.draft === draft) return state;

      const nextSessions = { ...state.sessions, [sessionId]: { ...target, draft } };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  setToolsEnabled: (sessionId, enabled) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const nextSessions = { ...state.sessions, [sessionId]: { ...target, toolsEnabled: enabled } };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  clearSessionMessages: (sessionId: string) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const updated: SessionRecord = {
        ...target,
        messages: [],
        status: 'idle',
        activeExecutionCardId: undefined,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  addUserPrompt: (sessionId: string, prompt: string, profile?: string, attachments?: ChatAttachment[]) => {
    const userMsgId = `msg_user_${Date.now()}`;
    const assistantMsgId = `msg_asst_${Date.now() + 1}`;

    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const resolvedProfile = !isUnsetProfile(profile)
        ? (profile as string)
        : (isUnsetProfile(target.profile) ? '' : target.profile);

      const turn = target.messages.filter(m => m.sender === 'user').length + 1;

      const userMsg: ChatMessage = {
        id: userMsgId,
        sessionId,
        sender: 'user',
        prompt,
        attachments,
        timeline: [],
        status: 'completed',
        timestamp: Date.now(),
        turn,
      };

      const assistantMsg: ChatMessage = {
        id: assistantMsgId,
        sessionId,
        sender: 'assistant',
        markdown: '',
        timeline: [],
        status: 'thinking',
        profile: resolvedProfile,
        timestamp: Date.now(),
        turn,
      };

      // Name the session from the first prompt, then latch it. A session still sitting on a
      // placeholder (including one persisted by an older build) gets another chance here, so a stuck
      // "New Session" recovers on its next message instead of staying nameless.
      const isFirstTurn = target.messages.length === 0;
      const shouldGenerateTitle = !target.isTitleCustom && (isFirstTurn || isPlaceholderTitle(target.title));

      const candidate = shouldGenerateTitle ? generateSessionTitle(prompt) : target.title;
      // The generator returns a placeholder when the prompt carries no usable words ("hi", "thanks").
      // Keeping the old title and staying unlocked lets a later turn try again.
      const newTitle = isPlaceholderTitle(candidate) ? target.title : candidate;
      const titleLocked = target.isTitleCustom || target.titleLocked || !isPlaceholderTitle(newTitle);

      const updated: SessionRecord = {
        ...target,
        title: newTitle,
        isTitleCustom: target.isTitleCustom,
        titleLocked,
        profile: resolvedProfile,
        status: 'thinking',
        activeExecutionCardId: assistantMsgId,
        messages: [...target.messages, userMsg, assistantMsg],
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });

    return { userMsgId, assistantMsgId };
  },

  prepareRegenerate: (sessionId, assistantMessageId) => {
    const target = get().sessions[sessionId];
    if (!target) return null;

    const index = target.messages.findIndex(m => m.id === assistantMessageId);
    if (index < 0 || target.messages[index].sender !== 'assistant') return null;

    let userIndex = -1;
    for (let i = index - 1; i >= 0; i -= 1) {
      if (target.messages[i].sender === 'user') {
        userIndex = i;
        break;
      }
    }
    if (userIndex < 0) return null;

    const sourcePrompt = target.messages[userIndex].prompt || '';
    const assistantMsgId = `msg_asst_${Date.now()}`;
    const placeholder: ChatMessage = {
      id: assistantMsgId,
      sessionId,
      sender: 'assistant',
      markdown: '',
      timeline: [],
      status: 'thinking',
      profile: target.profile,
      timestamp: Date.now(),
      turn: target.messages[index].turn,
    };

    // Keep the user message, replace only the reply that is being regenerated.
    const messages = [...target.messages.slice(0, index), placeholder];
    const updated: SessionRecord = {
      ...target,
      messages,
      status: 'thinking',
      activeExecutionCardId: assistantMsgId,
      updatedAt: Date.now(),
    };

    set(state => {
      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });

    return { prompt: sourcePrompt, assistantMsgId };
  },

  editUserPrompt: (sessionId, userMessageId, newPrompt) => {
    const target = get().sessions[sessionId];
    if (!target) return null;

    const index = target.messages.findIndex(m => m.id === userMessageId);
    if (index < 0 || target.messages[index].sender !== 'user') return null;

    const original = target.messages[index];
    const text = newPrompt.trim();
    if (!text) return null;

    const turn = original.turn ?? target.messages.slice(0, index + 1).filter(m => m.sender === 'user').length;
    const userMsgId = `msg_user_${Date.now()}`;
    const assistantMsgId = `msg_asst_${Date.now() + 1}`;

    const userMsg: ChatMessage = {
      ...original,
      id: userMsgId,
      prompt: text,
      timestamp: Date.now(),
      status: 'completed',
      turn,
      forkedFrom: original.id,
    };

    const assistantMsg: ChatMessage = {
      id: assistantMsgId,
      sessionId,
      sender: 'assistant',
      markdown: '',
      timeline: [],
      status: 'thinking',
      profile: target.profile,
      timestamp: Date.now(),
      turn,
    };

    // Fork: everything from the edited prompt onward is dropped, the rest of the thread survives.
    const messages = [...target.messages.slice(0, index), userMsg, assistantMsg];
    const updated: SessionRecord = {
      ...target,
      messages,
      status: 'thinking',
      activeExecutionCardId: assistantMsgId,
      updatedAt: Date.now(),
    };

    set(state => {
      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });

    return { prompt: text, assistantMsgId };
  },

  queuePrompt: (sessionId, prompt, attachments) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const entry = { id: `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`, prompt, attachments };
      const queuedPrompts = [...(target.queuedPrompts ?? []), entry];
      const nextSessions = { ...state.sessions, [sessionId]: { ...target, queuedPrompts } };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  removeQueuedPrompt: (sessionId, id) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target?.queuedPrompts?.some(entry => entry.id === id)) return state;

      const queuedPrompts = target.queuedPrompts.filter(entry => entry.id !== id);
      const nextSessions = {
        ...state.sessions,
        [sessionId]: { ...target, queuedPrompts: queuedPrompts.length > 0 ? queuedPrompts : undefined },
      };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  markStopped: sessionId => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const messages = [...target.messages];
      const lastIndex = messages.length - 1;
      const lastMsg = messages[lastIndex];

      if (lastMsg && lastMsg.sender === 'assistant') {
        messages[lastIndex] = {
          ...lastMsg,
          status: 'aborted',
          timeline: lastMsg.timeline.map(node => {
            if (node.status !== 'running') return node;
            // A command that was killed did not finish; reasoning that was cut short still ran.
            return { ...node, status: node.type === 'command' ? 'failed' : 'completed' };
          }),
        };
      }

      const updated: SessionRecord = {
        ...target,
        status: 'aborted',
        activeExecutionCardId: undefined,
        messages,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  appendChatToken: (sessionId: string, text: string) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target || target.messages.length === 0) return state;

      const messages = [...target.messages];
      const lastIndex = messages.length - 1;
      const lastMsg = messages[lastIndex];

      if (lastMsg && lastMsg.sender === 'assistant') {
        messages[lastIndex] = {
          ...lastMsg,
          markdown: (lastMsg.markdown || '') + text,
          status: lastMsg.status === 'aborted' ? 'aborted' : 'running',
        };

        const updated: SessionRecord = {
          ...target,
          messages,
          updatedAt: Date.now(),
        };

        const nextSessions = { ...state.sessions, [sessionId]: updated };
        return { sessions: nextSessions };
      }

      return state;
    });
  },

  setAssistantText: (sessionId: string, text: string) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target || target.messages.length === 0) return state;

      const messages = [...target.messages];
      const lastIndex = messages.length - 1;
      const lastMsg = messages[lastIndex];

      if (!lastMsg || lastMsg.sender !== 'assistant') return state;

      messages[lastIndex] = { ...lastMsg, markdown: text };
      return { sessions: { ...state.sessions, [sessionId]: { ...target, messages, updatedAt: Date.now() } } };
    });
  },

  appendThinkingChunk: (sessionId, text, durationSeconds, thinkingTokens, thinkingState) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target || target.messages.length === 0) return state;

      const messages = [...target.messages];
      const lastIndex = messages.length - 1;
      const lastMsg = messages[lastIndex];

      if (!lastMsg || lastMsg.sender !== 'assistant') return state;

      const timeline = [...lastMsg.timeline];
      // Find existing thought node or create new
      let thoughtIndex = timeline.findIndex(n => n.type === 'thought' && n.status === 'running');

      if (thoughtIndex === -1 && thinkingState !== 'done') {
        const newNode: ThoughtTimelineNode = {
          id: `thought_${Date.now()}`,
          type: 'thought',
          text: text || '',
          durationSeconds: durationSeconds || 0,
          tokens: thinkingTokens || 0,
          isStreaming: true,
          status: 'running',
          timestamp: Date.now(),
        };
        timeline.push(newNode);
      } else if (thoughtIndex >= 0) {
        const current = timeline[thoughtIndex] as ThoughtTimelineNode;
        const isDone = thinkingState === 'done';
        const mergedText = text ? current.text + (current.text ? '\n' : '') + text : current.text;
        const mergedTokens = thinkingTokens !== undefined ? thinkingTokens : current.tokens;

        // Nothing was actually said and no reasoning tokens were counted, so there is no step to
        // show. Removing it here is what keeps "Thought" rows with an empty drawer out of the chat.
        if (isDone && !mergedText.trim() && !mergedTokens) {
          timeline.splice(thoughtIndex, 1);
        } else {
          timeline[thoughtIndex] = {
            ...current,
            text: mergedText,
            durationSeconds: durationSeconds !== undefined ? durationSeconds : current.durationSeconds,
            tokens: mergedTokens,
            isStreaming: !isDone,
            status: isDone ? 'completed' : 'running',
          };
        }
      }

      messages[lastIndex] = {
        ...lastMsg,
        timeline,
        status: thinkingState === 'done' ? 'running' : 'thinking',
      };

      const updated: SessionRecord = {
        ...target,
        status: thinkingState === 'done' ? target.status : 'thinking',
        messages,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      return { sessions: nextSessions };
    });
  },

  startToolNode: (sessionId, callId, tool, params) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target || target.messages.length === 0) return state;

      const messages = [...target.messages];
      const lastIndex = messages.length - 1;
      const lastMsg = messages[lastIndex];

      if (!lastMsg || lastMsg.sender !== 'assistant') return state;

      const timeline = [...lastMsg.timeline];
      const toolKind = classifyTool(tool, params);

      let node: TimelineNode;

      if (toolKind === 'command') {
        const cmd: CommandTimelineNode = {
          id: callId,
          type: 'command',
          // The engine's label ("Ran") is display text, not a command. Only the params carry the
          // command, and a payload that has none must never be printed as JSON.
          command: extractCommand(params) || tool,
          params,
          toolName: tool,
          toolKind,
          status: 'running',
          timestamp: Date.now(),
        };
        node = cmd;
      } else if (toolKind === 'read' || toolKind === 'search') {
        const fileNode: FileTimelineNode = {
          id: callId,
          type: 'file',
          path: extractPath(params) || tool,
          action: toolKind === 'search' ? 'search' : 'read',
          count: 1,
          toolName: tool,
          toolKind,
          status: 'running',
          timestamp: Date.now(),
        };
        node = fileNode;
      } else if (toolKind === 'write') {
        const diffNode: DiffTimelineNode = {
          id: callId,
          type: 'diff',
          file: extractPath(params) || 'Code edit',
          additions: 0,
          deletions: 0,
          toolName: tool,
          toolKind,
          status: 'running',
          timestamp: Date.now(),
        };
        node = diffNode;
      } else {
        // Unrecognised tool: fall back to its name and target, never to a stringified params bag.
        const genNode: CommandTimelineNode = {
          id: callId,
          type: 'command',
          command: extractPath(params) || tool,
          params,
          toolName: tool,
          toolKind: 'other',
          status: 'running',
          timestamp: Date.now(),
        };
        node = genNode;
      }

      timeline.push(node);

      messages[lastIndex] = {
        ...lastMsg,
        timeline,
        status: 'tool_executing',
      };

      const updated: SessionRecord = {
        ...target,
        status: 'tool_executing',
        messages,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      return { sessions: nextSessions };
    });
  },

  finishToolNode: (sessionId, callId, _tool, success, result, error, durationMs) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target || target.messages.length === 0) return state;

      const messages = [...target.messages];
      const lastIndex = messages.length - 1;
      const lastMsg = messages[lastIndex];

      if (!lastMsg || lastMsg.sender !== 'assistant') return state;

      const timeline = lastMsg.timeline.map(node => {
        if (node.id === callId) {
          const status: 'completed' | 'failed' = success ? 'completed' : 'failed';
          if (node.type === 'command') {
            const shaped = shapeCommandResult(result, success, error);
            return {
              ...node,
              status,
              ...shaped,
              durationMs,
            } as CommandTimelineNode;
          }
          if (node.type === 'file') {
            const preview = typeof result === 'string' ? result : result?.output ?? result?.content ?? '';
            return {
              ...node,
              status,
              contentPreview: typeof preview === 'string' ? preview.slice(0, 4000) : undefined,
            } as FileTimelineNode;
          }
          if (node.type === 'diff') {
            return {
              ...node,
              status,
              additions: typeof result === 'object' && result?.additions ? result.additions : node.additions,
              deletions: typeof result === 'object' && result?.deletions ? result.deletions : node.deletions,
              patch: typeof result === 'string' ? result : result?.patch,
            } as DiffTimelineNode;
          }
          return { ...node, status } as TimelineNode;
        }
        return node;
      });

      messages[lastIndex] = {
        ...lastMsg,
        timeline,
        status: 'running',
      };

      const updated: SessionRecord = {
        ...target,
        messages,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      return { sessions: nextSessions };
    });
  },

  updateSessionStatus: (sessionId, status, error, pid) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const messages = [...target.messages];
      const lastMsg = messages[messages.length - 1];

      // The sidecar answers an abort with `idle`, which can land after the local `aborted` state.
      // Keeping `aborted` is what makes the Stopped badge survive that race.
      const resolvedStatus: AgentExecutionState =
        status === 'idle' && lastMsg?.sender === 'assistant' && lastMsg.status === 'aborted'
          ? 'aborted'
          : status;

      if (messages.length > 0) {
        const lastIndex = messages.length - 1;
        if (lastMsg && lastMsg.sender === 'assistant') {
          // Finalize any streaming or running timeline nodes
          const finalizedTimeline = lastMsg.timeline
            .filter(node => {
              // A thought row with no text and no token count has nothing to expand.
              if (node.type !== 'thought') return true;
              return Boolean(node.text.trim()) || Boolean(node.tokens);
            })
            .map(node => {
              if (node.status === 'running') {
                if (node.type === 'thought') {
                  return { ...node, status: 'completed' as const, isStreaming: false };
                }
                return { ...node, status: 'completed' as const };
              }
              return node;
            });

          const isSettled =
            resolvedStatus === 'idle' || resolvedStatus === 'completed' || resolvedStatus === 'error' || resolvedStatus === 'aborted';
          /**
           * An empty reply that has finished is a dead end, not progress: leaving it on `idle` kept
           * "Waiting for the first token…" on screen forever after a blocked turn.
           */
          const isEmptyReply = !lastMsg.markdown && finalizedTimeline.length === 0;
          const settledStatus: AgentExecutionState =
            isSettled && isEmptyReply && resolvedStatus !== 'aborted' ? 'error' : resolvedStatus;

          messages[lastIndex] = {
            ...lastMsg,
            timeline: finalizedTimeline,
            status: settledStatus,
            error: error || lastMsg.error || (settledStatus === 'error' && isEmptyReply ? 'The turn ended without a reply.' : undefined),
          };
        }
      }

      const isTerminal =
        resolvedStatus === 'idle' || resolvedStatus === 'completed' || resolvedStatus === 'error' || resolvedStatus === 'aborted';

      const updated: SessionRecord = {
        ...target,
        status: resolvedStatus,
        pid: pid || target.pid,
        activeExecutionCardId: isTerminal ? undefined : target.activeExecutionCardId,
        messages,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  recordTurnUsage: (sessionId, usage) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const totals = target.tokens || { inputTokens: 0, outputTokens: 0, thinkingTokens: 0, totalTokens: 0 };
      const accumulated: TokenUsage = {
        inputTokens: totals.inputTokens + (usage.inputTokens || 0),
        outputTokens: totals.outputTokens + (usage.outputTokens || 0),
        thinkingTokens: totals.thinkingTokens + (usage.thinkingTokens || 0),
        totalTokens: totals.totalTokens + (usage.totalTokens || 0),
      };

      const updated: SessionRecord = {
        ...target,
        tokens: accumulated,
        lastTurnTokens: usage,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },

  updateSessionTelemetry: (sessionId, data) => {
    set(state => {
      const target = state.sessions[sessionId];
      if (!target) return state;

      const updated: SessionRecord = {
        ...target,
        model: data.model !== undefined ? data.model : target.model,
        reasoningEffort: data.reasoningEffort !== undefined ? data.reasoningEffort : target.reasoningEffort,
        conversationId: data.conversationId !== undefined ? data.conversationId : target.conversationId,
        quotaPercent: data.quotaPercent !== undefined ? data.quotaPercent : target.quotaPercent,
        tokens: data.tokens ? { ...target.tokens, ...data.tokens } : target.tokens,
        updatedAt: Date.now(),
      };

      const nextSessions = { ...state.sessions, [sessionId]: updated };
      persistState(nextSessions, state.activeSessionId);
      return { sessions: nextSessions };
    });
  },
}));
