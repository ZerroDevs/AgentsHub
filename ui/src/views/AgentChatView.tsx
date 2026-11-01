import React, { useEffect, useRef, useState } from 'react';
import { UserRound, Sparkles, MessageSquare,
  ChevronDown,
  Users,
  Terminal,
  Plus,
  X,
  Loader2,
  Star,
  PanelLeftClose,
  PanelLeftOpen,
  Brain,
  AlertTriangle,
  ExternalLink, } from 'lucide-react';
import { useSessionStore } from '../store/useSessionStore';
import { useProfileStore } from '../store/useProfileStore';
import { useSidecarStore } from '../store/useSidecarStore';
import { useWorkspaceStore } from '../store/useWorkspaceStore';
import { cancelQueuedPrompt, queuePrompt, sendPrompt, stopGeneration } from '../services/chatController';
import { ChatMessageList } from '../components/chat/ChatMessageList';
import { ChatInput } from '../components/chat/ChatInput';
import { PermissionRequestBar } from '../components/chat/PermissionRequestBar';
import { ChatAttachment } from '../types/agent';
import { profileLabel } from '../lib/profileName';
import { TerminalPanel } from '../components/terminal/TerminalPanel';
import { webviewBridge } from '../services/webviewBridge';
import { SelectionToolbar } from '../components/chat/SelectionToolbar';

interface AgentChatViewProps {
  onOpenGitModal: () => void;
}

/** Docked terminal sizing. The floor keeps a shell usable; the ceiling keeps the chat readable. */
const MIN_TERMINAL_WIDTH = 320;
const DEFAULT_TERMINAL_WIDTH = 460;
const MIN_TRANSCRIPT_WIDTH = 420;

export const AgentChatView: React.FC<AgentChatViewProps> = ({ onOpenGitModal }) => {
  const {
    sessions,
    activeSessionId,
    openTabIds,
    getActiveSession,
    createSession,
    switchSession,
    closeTab,
    setToolsEnabled,
    setDeepSeekOption,
  } = useSessionStore();

  const {
    profiles,
    activeProfileName,
    defaultModel,
    switchActiveProfile,
    startProfile,
    stopProfile,
    defaultProfileName,
    autoRunDefaultProfile,
    setDefaultProfile,
  } = useProfileStore();

  const { setActiveView, toggleSidebar, isSidebarCollapsed } = useSidecarStore();
  const { activeWorkspace } = useWorkspaceStore();

  const [isProfilePopoverOpen, setIsProfilePopoverOpen] = useState(false);
  const [isTerminalOpen, setIsTerminalOpen] = useState(false);

  /**
   * Docked terminal width, in pixels from the right edge.
   *
   * A side dock is what every IDE does and it keeps the transcript's full height; the previous
   * bottom strip stole ~280px from the message list and squeezed the composer.
   */
  const [terminalWidth, setTerminalWidth] = useState<number>(() => {
    try {
      const saved = Number(localStorage.getItem('agentshub_terminal_width'));
      if (Number.isFinite(saved) && saved >= MIN_TERMINAL_WIDTH) return saved;
    } catch {
      // Fall through to the default.
    }
    return DEFAULT_TERMINAL_WIDTH;
  });
  const dockRef = useRef<HTMLDivElement>(null);
  /** Mirrors `terminalWidth` so the pointerup handler persists the value the drag ended on. */
  const widthRef = useRef(terminalWidth);

  const startTerminalResize = (event: React.PointerEvent<HTMLDivElement>) => {
    const dock = dockRef.current;
    if (!dock) return;

    event.preventDefault();
    const bounds = dock.getBoundingClientRect();

    const move = (moveEvent: PointerEvent) => {
      const next = bounds.right - moveEvent.clientX;
      // Leave the transcript enough room to stay readable; never collapse the terminal to a sliver.
      const ceiling = Math.max(MIN_TERMINAL_WIDTH + 80, bounds.width - MIN_TRANSCRIPT_WIDTH);
      const clamped = Math.min(Math.max(next, MIN_TERMINAL_WIDTH), ceiling);
      widthRef.current = clamped;
      setTerminalWidth(clamped);
    };

    const end = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', end);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      try {
        localStorage.setItem('agentshub_terminal_width', String(Math.round(widthRef.current)));
      } catch {
        // Persisting the width is a convenience, not a requirement.
      }
    };

    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', end);
  };


  const activeSession = getActiveSession();
  const activeProfile = profiles.find(
    p => p.name.toLowerCase() === activeProfileName.toLowerCase()
  ) || profiles[0];

  const isExecuting =
    activeSession?.status === 'running' ||
    activeSession?.status === 'thinking' ||
    activeSession?.status === 'tool_executing';

  /**
   * The runner a send will actually use: the session's own profile, else the configured default,
   * else whatever is active. The guard and the auto-start both target this one — starting a
   * different profile would satisfy the UI while the message still went to a stopped runner.
   */
  const runnerName = activeSession?.profile || defaultProfileName || activeProfileName;
  const runner = profiles.find(p => p.name.toLowerCase() === runnerName.toLowerCase());
  /**
   * Block only when the runner is known and stopped. An empty profile list means the host has not
   * answered yet (startup) or the name is stale — blocking then would refuse perfectly valid sends.
   */
  const isRunnerStopped = Boolean(runner) && !runner?.isRunning;

  /**
   * A send needs a running runner. Rather than starting one behind the user's back, the default is
   * to say so and keep their text; `autoRunDefaultProfile` opts into starting the default runner.
   */
  const handleSendMessage = async (prompt: string, attachments?: ChatAttachment[]) => {
    if (!activeSession) return;

    // A turn is in flight: hold the prompt instead of dropping it. It goes out a few seconds after
    // the turn settles, through the same dispatch every other prompt uses, so every engine queues.
    if (isExecuting) {
      queuePrompt(activeSession.id, prompt, attachments);
      return;
    }

    // A DeepSeek turn runs in our own WebView2 and never touches the CLI runner. This guard used
    // to swallow the send silently, which read as "the composer does nothing".
    if (!isHostEngine && isRunnerStopped) {
      if (!autoRunDefaultProfile) return;
      await startProfile(runnerName);
    }

    await sendPrompt(activeSession.id, prompt, attachments);
  };

  const handleStopGenerating = () => {
    if (!activeSession) return;
    void stopGeneration(activeSession.id);
  };

  const handleNewTab = () => {
    // The profile's default model is a *label* ("Gemini 3.8 Flash (High)"), not an id. Seeding the
    // session with it made the first effort change persist that label and send it as `--model`.
    const newId = createSession(defaultProfileName || activeProfileName);
    switchSession(newId);
  };

  const isDeepSeek = activeSession?.engine === 'deepseek';
  /**
   * The engines that run inside the host instead of the sidecar's CLI process: the two page engines
   * and Vyce's hidden console. None of them has a runner process, so the runner bar does not apply.
   */
  const isHostEngine =
    isDeepSeek ||
    activeSession?.engine === 'qwen' ||
    activeSession?.engine === 'zai' ||
    activeSession?.engine === 'vyce' ||
    activeSession?.engine === 'opencode';
  const isQwen = activeSession?.engine === 'qwen';
  const isZai = activeSession?.engine === 'zai';

  /**
   * Whether Z.ai has an account behind it. Guest use is supported, so this only decides whether the
   * window offers a sign-in or says thank you.
   */
  const [zaiSignedIn, setZaiSignedIn] = useState<boolean | null>(null);

  useEffect(() => {
    if (!isZai) return;

    let alive = true;
    void webviewBridge
      .invoke('zai_status')
      .then((res: any) => {
        if (alive) setZaiSignedIn(Boolean(res?.signedIn));
      })
      .catch(() => {
        if (alive) setZaiSignedIn(null);
      });

    return () => {
      alive = false;
    };
  }, [isZai]);
  const isOpenCode = activeSession?.engine === 'opencode';

  /** A turn from a host engine never starts a runner, so gating one on the runner is wrong. */
  const blockedNotice =
    isHostEngine || !isRunnerStopped || autoRunDefaultProfile
      ? null
      : `Runner ${profileLabel(runnerName)} is stopped. Start it to send a message.`;

  const [qwenMenuOpen, setQwenMenuOpen] = useState(false);

  /**
   * Sign-in state for a DeepSeek session.
   *
   * Polled rather than pushed: the token appears inside a WebView2 the host owns, and the user can
   * take any amount of time in it. Polling stops as soon as it reports signed in.
   */
  const [deepSeekSignedIn, setDeepSeekSignedIn] = useState<boolean | null>(null);
  const [deepSeekMenuOpen, setDeepSeekMenuOpen] = useState(false);
  const [webChats, setWebChats] = useState<Array<{ id: string; title: string }> | null>(null);

  const loadWebChats = async () => {
    try {
      const res: any = await webviewBridge.invoke('deepseek_chats');
      setWebChats(Array.isArray(res?.chats) ? res.chats : []);
    } catch {
      setWebChats([]);
    }
  };

  const [deepSeekAccounts, setDeepSeekAccounts] = useState<{
    active: string;
    accounts: Array<{ id: string; label: string }>;
  } | null>(null);

  // Loaded when the menu opens: the list can change from another window.
  useEffect(() => {
    if (!deepSeekMenuOpen) return;
    void webviewBridge
      .invoke('deepseek_accounts')
      .then((res: any) => setDeepSeekAccounts(res ?? null))
      .catch(() => setDeepSeekAccounts(null));
  }, [deepSeekMenuOpen]);

  const switchDeepSeekAccount = async (id: string) => {
    try {
      await webviewBridge.invoke('deepseek_switch_account', { id });
      setDeepSeekAccounts(current => (current ? { ...current, active: id } : current));
      setDeepSeekSignedIn(null);
    } catch {
      // The picker keeps showing the old account, which is the truth.
    }
  };


  /** A destructive DeepSeek tool call waiting for a yes or no. */
  const [deepSeekApproval, setDeepSeekApproval] = useState<{ callId: string; action: string; preview: string } | null>(
    null
  );

  useEffect(() => {
    const off = webviewBridge.on('deepseek_approval_request', (data: any) => {
      if (!data?.callId) return;
      setDeepSeekApproval({ callId: data.callId, action: data.action || '', preview: data.preview || '' });
    });
    return () => {
      off?.();
    };
  }, []);

  const answerDeepSeekApproval = (approved: boolean) => {
    const pending = deepSeekApproval;
    setDeepSeekApproval(null);
    if (!pending) return;
    void webviewBridge.invoke('deepseek_approve', { callId: pending.callId, approved }).catch(() => {});
  };

  useEffect(() => {
    if (!isDeepSeek) {
      setDeepSeekSignedIn(null);
      return;
    }

    let alive = true;
    let timer = 0;

    const check = async () => {
      try {
        const res: any = await webviewBridge.invoke('deepseek_status');
        if (!alive) return;
        const signedIn = Boolean(res?.signedIn);
        setDeepSeekSignedIn(signedIn);
        if (signedIn && timer) {
          window.clearInterval(timer);
          timer = 0;
        }
      } catch {
        if (alive) setDeepSeekSignedIn(false);
      }
    };

    void check();
    timer = window.setInterval(check, 4000);

    return () => {
      alive = false;
      if (timer) window.clearInterval(timer);
    };
  }, [isDeepSeek, activeSession?.id]);

  /** Sign-in flow: the host parks the window by itself once a token appears. */
  const openDeepSeekLogin = () => {
    void webviewBridge.invoke('deepseek_login').catch(() => {});
  };

  /** Just looking: no watcher, so the window stays until the user closes it. */
  const openDeepSeekWindow = () => {
    void webviewBridge.invoke('deepseek_open').catch(() => {});
  };

  // The tab bar shows the open subset, not every session: closing a tab must not delete a chat.
  const openTabs = openTabIds.map(id => sessions[id]).filter(Boolean);

  return (
    <div className="flex-1 flex flex-col h-full overflow-hidden bg-surface-deep text-content-main select-none relative">
      <SelectionToolbar />
      {/* 1. Top Context Bar */}
      <div className="h-12 border-b border-surface-border px-4 flex items-center justify-between bg-surface-deep z-20 flex-shrink-0 relative">
        <div className="flex items-center space-x-3">
          {/* Sessions panel toggle. It belongs here rather than in the global header, which showed
              it on every view even where that panel does not exist. */}
          <button
            type="button"
            onClick={toggleSidebar}
            title={isSidebarCollapsed ? 'Show the sessions panel' : 'Hide the sessions panel'}
            aria-label={isSidebarCollapsed ? 'Show the sessions panel' : 'Hide the sessions panel'}
            className="flex h-7 w-7 items-center justify-center rounded-lg text-content-dim transition-colors hover:bg-surface-card hover:text-content-main"
          >
            {isSidebarCollapsed ? <PanelLeftOpen className="w-4 h-4" /> : <PanelLeftClose className="w-4 h-4" />}
          </button>

          {/* Active Session Label */}
          <div className="flex items-center space-x-2">
            <MessageSquare className="w-4 h-4 text-accent" />
            <span className="text-xs font-semibold text-content-main">
              {activeSession?.title || 'Active Agent Session'}
            </span>
          </div>

          <span className="opacity-30">|</span>

          {/* A web-engine session has no runner, so the profile dropdown would be a lie. Qwen gets
              a plain label: there is no account to pick, and no window to manage. */}
          {isQwen ? (
            <div className="relative">
              <button
                type="button"
                onClick={() => setQwenMenuOpen(open => !open)}
                className="flex items-center gap-1.5 rounded-lg border border-violet-500/30 bg-violet-500/10 px-2.5 py-1 font-mono text-[11px] text-violet-300 transition hover:bg-violet-500/15"
                title="Qwen window, sign-in, and answer mode"
              >
                <Sparkles className="h-3 w-3" />
                Qwen
                <ChevronDown className="h-3 w-3 opacity-70" />
              </button>

              {qwenMenuOpen ? (
                <>
                  <div className="fixed inset-0 z-[70]" onClick={() => setQwenMenuOpen(false)} />
                  <div className="absolute left-0 top-full z-[71] mt-1 w-64 overflow-hidden rounded-lg border border-surface-border bg-surface-card shadow-xl">
                    <label className="flex items-center justify-between border-b border-surface-border px-3 py-2 text-[11px] text-content-main">
                      <span>Tools in this chat</span>
                      <input
                        type="checkbox"
                        checked={activeSession?.toolsEnabled !== false}
                        onChange={event => setToolsEnabled(activeSession.id, event.target.checked)}
                        className="h-3.5 w-3.5 accent-violet-500"
                      />
                    </label>

                    <button
                      type="button"
                      onClick={() => {
                        setQwenMenuOpen(false);
                        void webviewBridge.invoke('qwen_login', {}).catch(() => {});
                      }}
                      className="flex w-full items-center gap-2 border-t border-surface-border px-3 py-2 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover"
                    >
                      <UserRound className="h-3 w-3" />
                      Sign in to keep your chats
                    </button>

                    <button
                      type="button"
                      onClick={() => {
                        setQwenMenuOpen(false);
                        void webviewBridge.invoke('qwen_open', {}).catch(() => {});
                      }}
                      className="flex w-full items-center gap-2 border-t border-surface-border px-3 py-2 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover"
                    >
                      <ExternalLink className="h-3 w-3" />
                      Open the Qwen window
                    </button>

                    <p className="border-t border-surface-border px-3 py-2 text-[10px] leading-relaxed text-content-dim">
                      Answers come from chat.qwen.ai. Guest chats are not saved on their side.
                    </p>
                  </div>
                </>
              ) : null}
            </div>
          ) : isDeepSeek ? (
            <div className="relative">
              <button
                type="button"
                onClick={() => setDeepSeekMenuOpen(open => !open)}
                className="flex items-center gap-1.5 rounded-lg border border-sky-500/30 bg-sky-500/10 px-2.5 py-1 font-mono text-[11px] text-accent transition hover:bg-sky-500/15"
                title="DeepSeek account and window"
                aria-expanded={deepSeekMenuOpen}
              >
                <Brain className="h-3 w-3" />
                DeepSeek
                <ChevronDown className="h-3 w-3" />
              </button>

              {deepSeekMenuOpen ? (
                <div className="absolute left-0 top-9 z-50 w-60 overflow-hidden rounded-lg border border-surface-border bg-surface-card shadow-xl">
                  <div className="border-b border-surface-border px-3 py-2">
                    <p className="mb-1 font-mono text-[9.5px] uppercase tracking-wide text-content-dim">Account</p>

                    {(deepSeekAccounts?.accounts ?? []).map(account => (
                      <button
                        key={account.id}
                        type="button"
                        onClick={() => void switchDeepSeekAccount(account.id)}
                        className="flex w-full items-center gap-2 rounded px-1 py-0.5 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover"
                      >
                        <span
                          className={`h-1.5 w-1.5 flex-shrink-0 rounded-full ${
                            account.id === deepSeekAccounts?.active ? 'bg-accent' : 'bg-surface-strong'
                          }`}
                        />
                        <span className="truncate">{account.label}</span>
                      </button>
                    ))}

                    <div className="mt-1.5 flex items-center justify-between gap-1">
                      <button
                        type="button"
                        onClick={() => {
                          setDeepSeekMenuOpen(false);
                          setActiveView('profiles');
                        }}
                        className="rounded border border-surface-border px-1.5 py-0.5 font-mono text-[10px] text-content-muted transition-colors hover:text-content-main"
                      >
                        Manage in AccountsHub
                      </button>

                      {/* The warning lives on the control that causes it, not in a paragraph. */}
                      <span
                        className="flex h-4 w-4 cursor-help items-center justify-center rounded-full border border-surface-border font-mono text-[9px] text-content-dim"
                        title="Changing accounts rebuilds the DeepSeek window, so this chat forgets its page and starts a fresh conversation."
                        aria-label="Changing accounts may make this chat forget its conversation"
                      >
                        ?
                      </span>
                    </div>
                  </div>

                  <label className="flex items-center justify-between border-b border-surface-border px-3 py-2 text-[11px] text-content-main">
                    <span>Tools in this chat</span>
                    <input
                      type="checkbox"
                      checked={activeSession?.toolsEnabled !== false}
                      onChange={event => setToolsEnabled(activeSession.id, event.target.checked)}
                      className="h-3.5 w-3.5 accent-sky-500"
                    />
                  </label>

                  <button
                    type="button"
                    onClick={() => void loadWebChats()}
                    className="flex w-full items-center gap-2 border-t border-surface-border px-3 py-2 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover"
                  >
                    <ExternalLink className="h-3 w-3" />
                    Continue a chat from the web
                  </button>

                  {webChats ? (
                    <div className="max-h-40 overflow-y-auto border-t border-surface-border px-2 py-1">
                      {webChats.length === 0 ? (
                        <p className="px-1 py-1 text-[10px] text-content-dim">No chats found.</p>
                      ) : (
                        webChats.slice(0, 12).map(chat => (
                          <button
                            key={chat.id}
                            type="button"
                            onClick={() => {
                              useSessionStore.getState().updateSessionTelemetry(activeSession.id, { conversationId: chat.id });
                              setWebChats(null);
                              setDeepSeekMenuOpen(false);
                            }}
                            className="block w-full truncate rounded px-1 py-0.5 text-left text-[10.5px] text-content-muted transition-colors hover:bg-surface-hover hover:text-content-main"
                            title={chat.title}
                          >
                            {chat.title}
                          </button>
                        ))
                      )}
                    </div>
                  ) : null}

                  {activeSession?.conversationId ? (
                    <button
                      type="button"
                      onClick={() => {
                        setDeepSeekMenuOpen(false);
                        void webviewBridge
                          .invoke('deepseek_open_chat', { conversationId: activeSession.conversationId })
                          .catch(() => {});
                      }}
                      className="flex w-full items-center gap-2 px-3 py-2 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover"
                    >
                      <ExternalLink className="h-3 w-3" />
                      Open this chat on the web
                    </button>
                  ) : null}

                  <button
                    type="button"
                    onClick={() => {
                      setDeepSeekMenuOpen(false);
                      openDeepSeekWindow();
                    }}
                    className="flex w-full items-center gap-2 px-3 py-2 text-left text-[11px] text-content-main transition-colors hover:bg-surface-hover"
                  >
                    <PanelLeftOpen className="h-3 w-3" />
                    Open the DeepSeek window
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setDeepSeekMenuOpen(false);
                      void webviewBridge
                        .invoke('deepseek_signout')
                        .then(() => setDeepSeekSignedIn(false))
                        .catch(() => {});
                    }}
                    className="flex w-full items-center gap-2 border-t border-surface-border px-3 py-2 text-left text-[11px] text-state-danger transition-colors hover:bg-surface-hover"
                  >
                    <X className="h-3 w-3" />
                    Sign out and forget the account
                  </button>
                  <p className="border-t border-surface-border px-3 py-2 text-[10px] leading-relaxed text-content-dim">
                    Answers come from chat.deepseek.com, driven in this app with your own account.
                  </p>
                </div>
              ) : null}
            </div>
          ) : null}

          {/* Z.ai has no runner process, so its own chip stands where the runner dropdown does. */}
          {isZai ? (
            <div className="flex items-center gap-1.5">
              <button
                type="button"
                onClick={() => void webviewBridge.invoke('zai_open').catch(() => {})}
                className="flex items-center gap-1.5 rounded-lg border border-sky-500/30 bg-sky-500/10 px-2.5 py-1 text-[11px] text-accent transition-colors hover:bg-sky-500/15"
                title="Open the Z.ai window"
              >
                <Sparkles className="h-3 w-3" />
                Z.ai
              </button>

              {/* The guest allowance runs out mid-chat, so the way up to an account is always one
                  click away rather than a warning discovered at the send button. */}
              {zaiSignedIn === false ? (
                <button
                  type="button"
                  onClick={() => void webviewBridge.invoke('zai_login').catch(() => {})}
                  className="rounded-lg border border-surface-border px-2 py-1 text-[10.5px] text-content-muted transition-colors hover:border-sky-500/50 hover:text-content-main"
                  title="Sign in to Z.ai for GLM-5.3, GLM-5.2 and a larger allowance. Guest use works without it."
                >
                  Sign in for more
                </button>
              ) : null}
            </div>
          ) : null}

          {/* Runner Dropdown with Multi-Profile Popover */}
          <div className={isHostEngine ? 'hidden' : 'relative'}>
            <button
              onClick={() => setIsProfilePopoverOpen(!isProfilePopoverOpen)}
              className="text-xs px-2.5 py-1 rounded-lg bg-surface-card border border-surface-border hover:border-sky-500 text-content-main font-mono flex items-center space-x-2 transition shadow-sm"
              title="Manage Antigravity Profiles & Multi-Agent Execution"
            >
              <span
                className={`w-2 h-2 rounded-full ${
                  activeProfile?.isRunning ? 'bg-emerald-400 animate-pulse' : 'bg-slate-500'
                }`}
              />
              <span>Runner: {profileLabel(activeProfileName)}</span>
              <ChevronDown className="w-3 h-3 text-content-dim ml-1" />
            </button>

            {/* Multi-Profile Popover Panel */}
            {isProfilePopoverOpen && (
              <div className="absolute left-0 top-10 w-80 bg-surface-card border border-surface-border rounded-xl shadow-2xl p-3.5 z-50 space-y-3 font-sans">
                <div className="flex items-center justify-between pb-2 border-b border-surface-border">
                  <span className="text-xs font-semibold text-content-main flex items-center gap-1.5">
                    <Users className="w-3.5 h-3.5 text-accent" />
                    <span>Multi-Profile Agents</span>
                  </span>
                  <span className="text-[10px] font-mono text-content-dim">
                    {profiles.filter(p => p.active).length} Selected
                  </span>
                </div>

                <p className="text-[11px] text-content-muted leading-relaxed">
                  Select one or multiple profiles to run sessions in parallel. Start or stop instances directly.
                </p>

                {/* Profiles List */}
                <div className="space-y-1.5 max-h-56 overflow-y-auto pr-1">
                  {profiles.map(p => {
                    const isSelected = p.name.toLowerCase() === activeProfileName.toLowerCase();
                    return (
                      <div
                        key={p.name}
                        className={`flex items-center justify-between p-2 rounded-lg border text-xs transition ${
                          isSelected
                            ? 'bg-surface-deep/80 border-sky-500/40'
                            : 'bg-surface-deep/40 border-surface-border/60 hover:border-surface-border'
                        }`}
                      >
                        <div
                          className="flex items-center space-x-2 cursor-pointer flex-1"
                          onClick={() => switchActiveProfile(p.name)}
                        >
                          <input
                            type="checkbox"
                            checked={isSelected}
                            onChange={() => switchActiveProfile(p.name)}
                            className="rounded accent-sky-500 cursor-pointer"
                          />
                          <span
                            className={`w-2 h-2 rounded-full ${
                              p.isRunning ? 'bg-emerald-400 animate-pulse' : 'bg-slate-500'
                            }`}
                          />
                          <span className="font-mono text-xs text-content-main">{p.name}</span>
                        </div>

                        <div className="flex items-center space-x-2">
                          {(() => {
                            const isDefault =
                              defaultProfileName.toLowerCase() === p.name.toLowerCase();
                            return (
                              <button
                                type="button"
                                onClick={() => setDefaultProfile(isDefault ? '' : p.name)}
                                title={
                                  isDefault
                                    ? `${p.name} is the default runner. Click to clear`
                                    : `Use ${p.name} as the default runner`
                                }
                                aria-pressed={isDefault}
                                className={`p-1 rounded transition-colors ${
                                  isDefault
                                    ? 'text-state-warning hover:bg-amber-500/10'
                                    : 'text-content-dim hover:text-state-warning hover:bg-surface-hover'
                                }`}
                              >
                                <Star className={`w-3.5 h-3.5 ${isDefault ? 'fill-current' : ''}`} />
                              </button>
                            );
                          })()}

                          <span
                            className={`px-1.5 py-0.5 text-[9px] font-mono rounded border ${
                              p.isRunning
                                ? 'bg-emerald-500/15 text-state-success border-emerald-500/30'
                                : 'bg-surface-strong text-content-muted border-surface-border'
                            }`}
                          >
                            {p.isRunning ? 'Running' : 'Offline'}
                          </span>

                          <button
                            type="button"
                            onClick={() => (p.isRunning ? stopProfile(p.name) : startProfile(p.name))}
                            className={`px-2 py-0.5 rounded text-[10px] font-mono font-medium transition ${
                              p.isRunning
                                ? 'bg-rose-500/15 text-state-danger border border-rose-500/30 hover:bg-rose-500/25'
                                : 'bg-emerald-500/15 text-state-success border border-emerald-500/30 hover:bg-emerald-500/25'
                            }`}
                          >
                            {p.isRunning ? 'Stop' : 'Start'}
                          </button>
                        </div>
                      </div>
                    );
                  })}
                </div>

                <div className="pt-2 border-t border-surface-border flex items-center justify-between text-xs">
                  <button
                    onClick={() => {
                      setIsProfilePopoverOpen(false);
                      setActiveView('profiles');
                    }}
                    className="text-[11px] text-accent hover:underline"
                  >
                    Full Profiles Manager &rarr;
                  </button>
                  <button
                    onClick={() => setIsProfilePopoverOpen(false)}
                    className="text-[11px] px-2.5 py-1 rounded-md bg-surface-deep border border-surface-border text-content-main hover:border-sky-500 transition"
                  >
                    Done
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Right Controls: one terminal control, the compact toggle that used to sit under the
            runner. Model, effort, quota and tokens live in the composer footer, where they belong
            to the prompt rather than to the window chrome. */}
        <div className="flex items-center space-x-2">
          <button
            type="button"
            onClick={() => setIsTerminalOpen(open => !open)}
            title={isTerminalOpen ? 'Close the terminal' : 'Open a terminal in the workspace folder'}
            className={`flex items-center gap-1.5 rounded-md border px-2 py-0.5 font-mono text-[10.5px] transition-colors ${
              isTerminalOpen
                ? 'border-sky-500/50 bg-sky-500/10 text-accent'
                : 'border-surface-border bg-surface-deep text-content-muted hover:text-content-main'
            }`}
          >
            <Terminal className="w-3 h-3" />
            Terminal
          </button>
        </div>
      </div>

      {/* 2. Multi-Session Agent Tabs Bar */}
      <div className="h-9 px-4 flex items-center justify-between text-xs overflow-x-auto select-none z-10 flex-shrink-0">
        <div className="flex items-center space-x-1 overflow-x-auto scrollbar-none flex-1 py-1">
          {openTabs.map(tab => {
            const isActive = tab.id === activeSessionId;
            const isTabRunning =
              tab.status === 'running' ||
              tab.status === 'thinking' ||
              tab.status === 'tool_executing';

            return (
              <div
                key={tab.id}
                onClick={() => switchSession(tab.id)}
                className={`group flex items-center space-x-1.5 px-3 py-1 rounded-t-lg text-xs transition cursor-pointer select-none max-w-[200px] border-t border-x ${
                  isActive
                    ? 'bg-surface-card border-surface-border text-accent font-semibold shadow-sm'
                    : 'bg-surface-deep/60 border-transparent text-content-muted hover:text-content-main hover:bg-surface-card/40'
                }`}
              >
                {isTabRunning ? (
                  <Loader2 className="w-3 h-3 animate-spin text-accent flex-shrink-0" />
                ) : (
                  <span
                    className={`w-1.5 h-1.5 rounded-full flex-shrink-0 ${
                      isActive ? 'bg-sky-400' : 'bg-content-dim'
                    }`}
                  />
                )}
                <span className="truncate text-[11px] font-mono">{tab.title}</span>
                <button
                  type="button"
                  onClick={e => {
                    e.stopPropagation();
                    closeTab(tab.id);
                  }}
                  className="ml-1 p-0.5 text-[10px] opacity-0 transition hover:text-content-main group-hover:opacity-100"
                  title="Close the tab. The conversation stays in the sidebar."
                  aria-label="Close tab"
                >
                  <X className="w-3 h-3" />
                </button>
              </div>
            );
          })}
        </div>

        <button
          type="button"
          onClick={handleNewTab}
          title="Open New Agent Tab"
          className="ml-2 px-2.5 py-1 rounded-md bg-surface-card border border-surface-border hover:border-sky-500 text-content-muted hover:text-accent transition text-[11px] flex items-center gap-1 flex-shrink-0 shadow-sm"
        >
          <Plus className="w-3 h-3" />
          <span>New Tab</span>
        </button>
      </div>

      {/* 3. Transcript + composer on the left, terminal docked to the right. The terminal used to
          sit under the composer and ate the bottom of the transcript; docked, both keep full height. */}
      <div ref={dockRef} className="flex min-h-0 flex-1 overflow-hidden">
        <div className="relative flex min-w-0 flex-1 flex-col overflow-hidden">
          {activeSession ? (
            <ChatMessageList
              messages={activeSession.messages}
              activeExecutionCardId={activeSession.activeExecutionCardId}
              isGenerating={isExecuting}
              blockedNotice={blockedNotice}
              onStartRunner={() => void startProfile(runnerName)}
            />
          ) : (
            <div className="flex flex-1 flex-col items-center justify-center gap-3 text-content-dim">
              <p className="font-mono text-xs">No conversation open.</p>
              <button
                type="button"
                onClick={() => {
                  const id = createSession(defaultProfileName || activeProfileName);
                  switchSession(id);
                }}
                className="rounded-md border border-surface-border bg-surface-card px-3 py-1.5 text-xs text-content-main transition-colors hover:border-sky-500"
              >
                New conversation
              </button>
              <p className="max-w-[240px] text-center font-mono text-[10.5px] leading-relaxed">
                Every conversation stays in the sidebar. Closing a tab never deletes one.
              </p>
            </div>
          )}

          {/* Blocked tool calls sit directly above the composer: that is where the switch that
              caused them lives, and where the retry is offered. */}
          {activeSession ? <PermissionRequestBar sessionId={activeSession.id} /> : null}

          {deepSeekApproval ? (
            <div className="flex items-center gap-2 border-b border-amber-500/25 bg-amber-500/10 px-4 py-2 text-[11px] text-state-warning">
              <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0" />
              <span className="flex-1 truncate" title={deepSeekApproval.preview}>
                DeepSeek wants to {deepSeekApproval.preview}
              </span>
              <button
                type="button"
                onClick={() => answerDeepSeekApproval(true)}
                className="flex-shrink-0 rounded border border-amber-500/40 bg-amber-500/10 px-2 py-0.5 font-mono text-[10px] transition-colors hover:bg-amber-500/20"
              >
                Allow
              </button>
              <button
                type="button"
                onClick={() => answerDeepSeekApproval(false)}
                className="flex-shrink-0 rounded border border-surface-border px-2 py-0.5 font-mono text-[10px] text-content-muted transition-colors hover:text-content-main"
              >
                Deny
              </button>
            </div>
          ) : null}

          {activeSession ? (
            <ChatInput
              sessionId={activeSession.id}
              profileName={activeSession?.profile || activeProfileName}
            blockedNotice={blockedNotice}
              onStartRunner={() => void startProfile(runnerName)}
              onSendMessage={handleSendMessage}
              onStop={handleStopGenerating}
              isExecuting={isExecuting}
              queuedPrompts={activeSession.queuedPrompts}
              onRemoveQueued={id => cancelQueuedPrompt(activeSession.id, id)}
              activeModel={isOpenCode ? activeSession.model || undefined : activeSession?.model || defaultModel || undefined}
              reasoningEffort={activeSession?.reasoningEffort}
              lastTurnTokens={activeSession?.lastTurnTokens}
              sessionTokens={activeSession?.tokens}
              turnCount={activeSession?.messages.filter(message => message.sender === 'assistant').length}
              onOpenGitModal={onOpenGitModal}
              engine={activeSession.engine}
              deepSeekSignedIn={deepSeekSignedIn}
              onDeepSeekLogin={openDeepSeekLogin}
              deepThink={activeSession.deepThink}
              webSearch={activeSession.webSearch}
              onDeepSeekOption={(option, value) => setDeepSeekOption(activeSession.id, option, value)}
              activeCli={activeWorkspace?.primaryCli}
              workingDirectory={activeWorkspace?.workingDirectory}
            />
          ) : null}
        </div>

        {isTerminalOpen ? (
          <>
            {/* Splitter: drag to resize, double-click to restore the default width. */}
            <div
              role="separator"
              aria-orientation="vertical"
              aria-label="Resize terminal"
              onPointerDown={startTerminalResize}
              onDoubleClick={() => {
                widthRef.current = DEFAULT_TERMINAL_WIDTH;
                setTerminalWidth(DEFAULT_TERMINAL_WIDTH);
                try {
                  localStorage.setItem('agentshub_terminal_width', String(DEFAULT_TERMINAL_WIDTH));
                } catch {
                  // Optional.
                }
              }}
              className="group relative w-1.5 flex-shrink-0 cursor-col-resize bg-surface-border/40 transition-colors hover:bg-accent/40 active:bg-accent/60"
              title="Drag to resize · double-click to reset"
            >
              <span className="pointer-events-none absolute inset-y-0 left-1/2 w-px -translate-x-1/2 bg-surface-border transition-colors group-hover:bg-accent/70" />
            </div>

            <div
              style={{ width: terminalWidth, maxWidth: `calc(100% - ${MIN_TRANSCRIPT_WIDTH}px)` }}
              className="flex h-full min-w-0 flex-shrink-0 flex-col overflow-hidden border-l border-surface-border"
            >
              <TerminalPanel
                cwd={activeWorkspace?.workingDirectory}
                onClose={() => setIsTerminalOpen(false)}
              />
            </div>
          </>
        ) : null}
      </div>
    </div>
  );
};
