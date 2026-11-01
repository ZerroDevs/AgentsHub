/**
 * Chat orchestration shared by the composer, the message action bar and the session header.
 *
 * Every entry point that starts or stops a turn goes through here so the store transitions and the
 * bridge calls cannot drift apart. The store itself stays a pure state machine.
 */

import { webviewBridge } from './webviewBridge';
import { useSessionStore } from '../store/useSessionStore';
import { useWorkspaceStore } from '../store/useWorkspaceStore';
import { useProfileStore } from '../store/useProfileStore';
import { ChatAttachment } from '../types/agent';
import { profileForDispatch } from '../lib/profileName';
import { resolveModelId, useModelStore } from '../store/useModelStore';
import { logUi } from './uiLogger';

interface DispatchTarget {
  /** Files the user attached; carried as data URLs because the store keeps no path. */
  attachments?: ChatAttachment[];
  sessionId: string;
  prompt: string;
}

function resolveContext(sessionId: string) {
  const session = useSessionStore.getState().sessions[sessionId];
  const { models } = useModelStore.getState();

  // The session stores whatever the picker last wrote, which for older sessions can be a display
  // label rather than an id. Resolving against the catalog turns that back into a real id — or into
  // `undefined`, which means "let the engine use its default".
  // OpenCode's ids are `provider/model` and belong to its own catalogue; resolving them against the
  // agy list would replace a real id with a guess.
  const model =
    session?.engine === 'opencode' || session?.engine === 'zai'
      ? session?.model || undefined
      : models.length > 0
        ? resolveModelId(models, session?.model, session?.reasoningEffort)
        : session?.model || undefined;

  return {
    model,
    profile: profileForDispatch(session?.profile),
    /**
     * Effort is deliberately not sent. The engine rejects a tiered model id combined with
     * `--effort` ("conflicts with --effort=low") and rejects `--effort` for models without tiers,
     * because the tier is already encoded in the model id. Choosing an effort selects that id.
     */
    workingDirectory: useWorkspaceStore.getState().activeWorkspace?.workingDirectory || undefined,
    /**
     * Sent explicitly so the turn cannot inherit a stale default. The toggle is stored per profile,
     * which is also what the terminal tab's checkbox reads.
     */
    skipPermissions: resolveSkipPermissions(session?.profile),
    /**
     * The session's own conversation. Empty on the first turn, which starts a fresh one; the sidecar
     * reports the id back and it is stored on the session from then on.
     */
    conversationId: session?.conversationId,
    /** Omitted means agy. DeepSeek sessions never reach the sidecar. */
    engine: session?.engine,
    /** DeepSeek only: an answer-only chat skips tools and the protocol line. */
    toolsEnabled: session?.toolsEnabled,
    /** DeepSeek only: their two composer chips. */
    deepThink: session?.deepThink,
    webSearch: session?.webSearch,
  };
}

/** Full access is the long-standing behaviour, so an unknown profile keeps it on. */
function resolveSkipPermissions(profile?: string): boolean {
  const { profiles, activeProfileName } = useProfileStore.getState();
  const target = profileForDispatch(profile) || activeProfileName;
  const match = profiles.find(p => p.name.toLowerCase() === (target || '').toLowerCase());
  return match?.skipPermissions !== false;
}

async function dispatch({ sessionId, prompt, attachments }: DispatchTarget): Promise<void> {
  const context = resolveContext(sessionId);

  try {
    await webviewBridge.invoke('send_chat', {
      sessionId,
      prompt,
      model: context.model,
      profile: context.profile,
      workingDirectory: context.workingDirectory,
      skipPermissions: context.skipPermissions,
      conversationId: context.conversationId,
      engine: context.engine,
      toolsEnabled: context.toolsEnabled,
      deepThink: context.deepThink,
      webSearch: context.webSearch,
      // The host writes these to temp files and hands them to the engine as real files.
      attachments: attachments
        ?.filter(a => Boolean(a.dataUrl))
        .map(a => ({ name: a.name, type: a.type, dataUrl: a.dataUrl })),
    });
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Failed to dispatch chat prompt';
    useSessionStore.getState().updateSessionStatus(sessionId, 'error', message);
    // A refused send only ever reached the transcript; Live Logs is where failures are looked for.
    logUi('error', 'Chat', `send_chat failed: ${message}`);
  }
}

/** Appends the user prompt plus a pending reply, then hands the prompt to the engine. */
export async function sendPrompt(
  sessionId: string,
  prompt: string,
  attachments?: ChatAttachment[]
): Promise<void> {
  useSessionStore.getState().addUserPrompt(sessionId, prompt, undefined, attachments);
  await dispatch({ sessionId, prompt, attachments });
}

/** How long the finished turn is left alone before a queued prompt goes out. */
const QUEUE_DELAY_MS = 1500;

/** How often the session is checked for having finished. */
const QUEUE_POLL_MS = 250;

/** One watcher per session; it drains that session's queue in order. */
const queueTimers = new Map<string, number>();

function isBusy(sessionId: string): boolean {
  const status = useSessionStore.getState().sessions[sessionId]?.status;
  return status === 'running' || status === 'thinking' || status === 'tool_executing';
}

function stopQueueTimer(sessionId: string): void {
  const timer = queueTimers.get(sessionId);
  if (timer === undefined) return;

  window.clearInterval(timer);
  queueTimers.delete(sessionId);
}

/**
 * Holds a prompt until the running turn settles, then sends it.
 *
 * The store keeps the queue so the composer can show and edit it; the send goes through
 * `sendPrompt` like every other turn, so no engine needs to know about it. The countdown starts when
 * the session stops being busy, and a sent prompt's turn becomes busy again before the next one is
 * considered, which is what keeps them in order instead of firing together.
 */
export function queuePrompt(
  sessionId: string,
  prompt: string,
  attachments?: ChatAttachment[]
): void {
  useSessionStore.getState().queuePrompt(sessionId, prompt, attachments);
  ensureQueueWatcher(sessionId);
}

/** Takes one queued prompt back: the × on its row. */
export function cancelQueuedPrompt(sessionId: string, id: string): void {
  useSessionStore.getState().removeQueuedPrompt(sessionId, id);

  const remaining = useSessionStore.getState().sessions[sessionId]?.queuedPrompts?.length ?? 0;
  if (remaining === 0) stopQueueTimer(sessionId);
}

function ensureQueueWatcher(sessionId: string): void {
  if (queueTimers.has(sessionId)) return;

  const watchedFrom = Date.now();
  let sawBusy = isBusy(sessionId);
  let idleSince: number | null = null;

  const timer = window.setInterval(() => {
    const state = useSessionStore.getState();
    const queued = state.sessions[sessionId]?.queuedPrompts?.[0];

    if (!queued) {
      stopQueueTimer(sessionId);
      return;
    }

    if (isBusy(sessionId)) {
      sawBusy = true;
      idleSince = null;
      return;
    }

    // A turn that never reported running (a dispatch that failed outright) must not stall the queue.
    if (!sawBusy) {
      if (Date.now() - watchedFrom < 5000) return;
      sawBusy = true;
    }

    idleSince ??= Date.now();
    if (Date.now() - idleSince < QUEUE_DELAY_MS) return;

    stopQueueTimer(sessionId);
    state.removeQueuedPrompt(sessionId, queued.id);
    void sendPrompt(sessionId, queued.prompt, queued.attachments);

    // The rest wait for this prompt's own turn to finish.
    if ((useSessionStore.getState().sessions[sessionId]?.queuedPrompts?.length ?? 0) > 0) {
      ensureQueueWatcher(sessionId);
    }
  }, QUEUE_POLL_MS);

  queueTimers.set(sessionId, timer);
}

/**
 * Stops the active turn: the sidecar kills the child process, then the store settles the UI. The
 * local state is updated even when the bridge call fails, because leaving the composer disabled on
 * a dead sidecar would trap the user.
 */
export async function stopGeneration(sessionId: string): Promise<void> {
  try {
    // A DeepSeek turn runs in the host's own WebView2, so `abort_task` (which reaches the sidecar)
    // would be a no-op for it. Route the stop to whichever engine owns the session.
    const session = useSessionStore.getState().sessions[sessionId];
    const action =
      session?.engine === 'deepseek'
        ? 'deepseek_cancel'
        : session?.engine === 'zai'
          ? 'zai_cancel'
          : 'abort_task';
    await webviewBridge.invoke(action, { taskId: sessionId, sessionId, reason: 'user_stopped' });
  } catch (err) {
    console.warn('[chatController] stop failed:', err);
  } finally {
    useSessionStore.getState().markStopped(sessionId);
  }
}

/** Replaces one assistant reply with a fresh attempt at the same user prompt. */
export async function regenerateResponse(sessionId: string, assistantMessageId: string): Promise<void> {
  const prepared = useSessionStore.getState().prepareRegenerate(sessionId, assistantMessageId);
  if (!prepared) return;
  await dispatch({ sessionId, prompt: prepared.prompt });
}

/**
 * Re-runs the blocked turn with a prompt that names the target that was just allowed.
 *
 * A plain retry re-samples the request, and the model picks a different filename or rephrases the
 * command — neither of which the freshly granted rule covers, so the turn is denied again and the
 * user loops. Naming the approved target is the only way to make the grant land on the same call.
 */
export async function retryWithPermissionGrant(
  sessionId: string,
  assistantMessageId: string,
  denial: { permission: string; command?: string; target?: string; rule?: string }
): Promise<void> {
  const prepared = useSessionStore.getState().prepareRegenerate(sessionId, assistantMessageId);
  if (!prepared) return;

  const subject = denial.command || denial.target || denial.rule;
  const noun = denial.command ? 'command' : 'path';
  const prompt = subject
    ? `Permission granted for: ${subject}\nContinue the previous task and use exactly that ${noun}. Do not change, rename or rephrase it.`
    : prepared.prompt;

  await dispatch({ sessionId, prompt });
}

/** Forks the thread at a user message and runs the edited prompt. */
export async function editAndResend(
  sessionId: string,
  userMessageId: string,
  newPrompt: string
): Promise<void> {
  const prepared = useSessionStore.getState().editUserPrompt(sessionId, userMessageId, newPrompt);
  if (!prepared) return;
  await dispatch({ sessionId, prompt: prepared.prompt });
}
