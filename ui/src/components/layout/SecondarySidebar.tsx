import React, { useState } from 'react';
import { Sparkles, Brain, ChevronDown, ChevronRight, Cpu, File, FileCode, FileText, Filter, Folder, FolderOpen, GitBranch, KeyRound, Loader2, Plus, Settings, SquareTerminal, Terminal, Trash2, Zap } from 'lucide-react';
import { useWorkspaceStore } from '../../store/useWorkspaceStore';
import { useSessionStore } from '../../store/useSessionStore';
import { useProfileStore } from '../../store/useProfileStore';
import { createPortal } from 'react-dom';
import { useSidecarStore } from '../../store/useSidecarStore';
import { webviewBridge } from '../../services/webviewBridge';
import { engineIcons } from '../../lib/toolIcons';
import { BYOK_PROVIDER_CHANGED } from '../chat/ByokProviderSwitcher';
import { BackendSessionItem, WorkspaceFileItem } from '../../types/agent';

interface SecondarySidebarProps {
  onOpenWorkspaceModal: () => void;
}

export const SecondarySidebar: React.FC<SecondarySidebarProps> = ({ onOpenWorkspaceModal }) => {
  const {
    workspaces,
    activeWorkspaceId,
    activeWorkspace,
    branch,
    files,
    backendSessions,
    isLoadingFiles,
    switchWorkspace,
    deleteBackendSession,
    clearWorkspace,
  } = useWorkspaceStore();

  const {
    sessions,
    activeSessionId,
    createSession,
    switchSession,
    deleteSession,
    renameSession,
  } = useSessionStore();

  const { activeProfileName } = useProfileStore();
  const { saveWorkspace, loadWorkspaces } = useWorkspaceStore();

  // Creating a workspace belongs where you pick one, not only in the settings dialog.
  const [newWorkspace, setNewWorkspace] = useState<{ name: string; dir: string } | null>(null);
  const [newWorkspaceError, setNewWorkspaceError] = useState<string | null>(null);
  const [sessionFilter, setSessionFilter] = useState('');
  const [isFilterVisible, setIsFilterVisible] = useState(false);

  // Files: collapsible, and noise hidden by default so `__pycache__` does not push real sources out.
  const [isFilesOpen, setIsFilesOpen] = useState(
    () => localStorage.getItem('agentshub_sidebar_files_open') !== 'false'
  );
  const [showNoisyFiles, setShowNoisyFiles] = useState(
    () => localStorage.getItem('agentshub_sidebar_files_all') === 'true'
  );

  const toggleFiles = () => {
    setIsFilesOpen(open => {
      localStorage.setItem('agentshub_sidebar_files_open', String(!open));
      return !open;
    });
  };

  const toggleNoisyFiles = () => {
    setShowNoisyFiles(show => {
      localStorage.setItem('agentshub_sidebar_files_all', String(!show));
      return !show;
    });
  };

  const NOISY = /(^|[\\/])(__pycache__|node_modules|\.git|\.venv|venv|dist|build|bin|obj|target)([\\/]|$)|(\.pyc|\.pyo|\.class|\.o|\.obj|\.log)$/i;
  const visibleFiles = showNoisyFiles ? files : files.filter(file => !NOISY.test(file.path));
  const { setActiveView, toggleSidebar } = useSidecarStore();

  const [collapsedGroups, setCollapsedGroups] = useState<Record<string, boolean>>({});
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameDraft, setRenameDraft] = useState('');

  const beginRename = (sessionId: string, currentTitle: string) => {
    setRenamingId(sessionId);
    setRenameDraft(currentTitle);
  };

  const commitRename = (sessionId: string) => {
    const title = renameDraft.trim();
    if (title) renameSession(sessionId, title);
    setRenamingId(null);
    setRenameDraft('');
  };

  const toggleGroup = (groupKey: string) => {
    setCollapsedGroups(prev => ({ ...prev, [groupKey]: !prev[groupKey] }));
  };

  const handleNewSession = () => {
    const newId = createSession(activeProfileName);
    switchSession(newId);
    setActiveView('chat');
  };

  /**
   * Starts a chat inside a specific project.
   *
   * Passing the workspace id explicitly is what makes the `+` mean "new conversation in *this*
   * project" rather than "somewhere in whatever is active".
   */
  /**
   * `+` asks which engine answers the new conversation instead of assuming the CLI.
   *
   * Positioned with fixed coordinates from the button's rect: the list scrolls, and a popover
   * inside an overflow container gets clipped.
   */
  const [newSessionMenu, setNewSessionMenu] = useState<{ workspaceId: string | null; top: number; left: number } | null>(null);

  const openNewSessionMenu = (event: React.MouseEvent, workspaceId: string | null) => {
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    setNewSessionMenu({
      workspaceId,
      top: rect.bottom + 4,
      left: Math.min(rect.left, window.innerWidth - 208),
    });
  };

  const startSession = (
    workspaceId: string | null,
    engine: 'agy' | 'deepseek' | 'qwen' | 'zai' | 'vyce' | 'opencode' | 'byok'
  ) => {
    setNewSessionMenu(null);

    const newId = createSession(activeProfileName, undefined, workspaceId, engine);
    switchSession(newId);
    setActiveView('chat');

    if (engine === 'deepseek') {
      // First use needs a token; the chat carries a sign-in bar if this is declined.
      void webviewBridge
        .invoke('deepseek_status')
        .then((res: any) => (res?.signedIn ? null : webviewBridge.invoke('deepseek_login')))
        .catch(() => {});
    }
  };

  /**
   * A local chat needs something to answer with before the session exists. This starts the local
   * server, and if no model is loaded it runs the first one on the shelf instead of opening a chat
   * that would fail on its first message.
   *
   * The session opens first and the server work runs behind it: loading a model takes seconds to
   * minutes, and making the click wait for that made the whole menu feel stuck.
   */
  const startLocalModelSession = (workspaceId: string | null) => {
    startSession(workspaceId, 'byok');

    void (async () => {
      try {
        const state: any = await webviewBridge.invoke('llamacpp_use');
        const loaded = state?.provider?.models ?? [];

        if (loaded.length === 0) {
          const shelf: any = await webviewBridge.invoke('local_models_list');
          const first = shelf?.models?.[0];
          if (first?.path) await webviewBridge.invoke('local_models_run', { path: first.path });
        }
      } catch {
        // The chat carries its own error state; a toast here would only duplicate it.
      } finally {
        // The provider became local while the composer was already on screen. Both chips reread it
        // from this event: otherwise the session still says "Your key" and offers the old models.
        window.dispatchEvent(new CustomEvent(BYOK_PROVIDER_CHANGED));
      }
    })();
  };

  // Merge active local sessions with backend sessions
  const localSessionList = Object.values(sessions).map(s => ({
    sessionId: s.id,
    goal: s.title,
    engine: s.engine,
    profile: s.profile,
    stepCount: s.messages.length,
    lastActiveUtc: new Date(s.updatedAt).toISOString(),
    workspaceId: s.workspaceId,
    isLocal: true,
  }));

  const allSessions: Array<BackendSessionItem & { isLocal?: boolean }> = [...localSessionList];

  // The CLI's own transcripts are deliberately not listed.
  //
  // They were merged here, but they are not in this store, so `switchSession` returned early and
  // every row was a dead click. Fifteen unopenable entries buried the sessions that actually work,
  // and importing them would be a feature rather than a fix. Kept in the code, not in the list, so
  // bringing them back means deleting this guard once there is a loader.
  backendSessions.forEach(() => {
    return;
  });

  // Workspace tree: each project with the sessions that ran in it. Sessions created before
  // workspaces were tracked have no id and land in "Unfiled" rather than being guessed into a project.
  const workspaceGroups = [
    ...workspaces.map(ws => ({
      id: ws.id,
      name: ws.name,
      items: allSessions.filter(s => (s as any).workspaceId === ws.id),
    })),
    {
      id: '__unfiled__',
      name: 'Unfiled',
      items: allSessions.filter(s => !(s as any).workspaceId),
    },
  ]
    .map(group => ({
      ...group,
      items: sessionFilter.trim()
        ? group.items.filter(s =>
            String((s as any).goal ?? '').toLowerCase().includes(sessionFilter.trim().toLowerCase())
          )
        : group.items,
    }))
    .filter(group => group.id !== '__unfiled__' || group.items.length > 0);

  // Group sessions into Today, Yesterday, Earlier
  const now = Date.now();
  const oneDay = 86400000;
  const groups: { today: typeof allSessions; yesterday: typeof allSessions; earlier: typeof allSessions } = {
    today: [],
    yesterday: [],
    earlier: [],
  };

  allSessions.forEach(s => {
    const timestamp = s.lastActiveUtc ? new Date(s.lastActiveUtc).getTime() : now;
    const diff = now - timestamp;
    if (diff < oneDay) {
      groups.today.push(s);
    } else if (diff < 2 * oneDay) {
      groups.yesterday.push(s);
    } else {
      groups.earlier.push(s);
    }
  });

  const getFileIcon = (file: WorkspaceFileItem) => {
    if (file.isDirectory) return <Folder className="w-3.5 h-3.5 text-amber-500 flex-shrink-0" />;
    if (['.js', '.ts', '.tsx', '.jsx', '.cs', '.py', '.html', '.css'].includes(file.extension)) {
      return <FileCode className="w-3.5 h-3.5 text-sky-400 flex-shrink-0" />;
    }
    if (['.md', '.txt', '.log'].includes(file.extension)) {
      return <FileText className="w-3.5 h-3.5 text-emerald-400 flex-shrink-0" />;
    }
    return <File className="w-3.5 h-3.5 text-content-dim flex-shrink-0" />;
  };

  const renderSessionGroup = (title: string, items: typeof allSessions, groupKey: string) => {
    // Workspace rows always render: a project with no chats yet still needs its own `+`.
    const isWorkspace = groupKey !== '__unfiled__';
    if (items.length === 0 && !isWorkspace) return null;
    const isCollapsed = !!collapsedGroups[groupKey];

    return (
      <div key={groupKey} className="group/row space-y-1">
        <div className="flex items-center gap-1">
          <button
            type="button"
            onClick={() => toggleGroup(groupKey)}
            className="min-w-0 flex-1 flex items-center justify-between px-2 py-1.5 text-[11px] font-medium text-content-muted hover:text-content-main rounded hover:bg-surface-card transition select-none"
          >
            <span className="flex min-w-0 items-center space-x-1.5">
              {isCollapsed ? <ChevronRight className="w-3 h-3 flex-shrink-0 text-content-dim" /> : <ChevronDown className="w-3 h-3 flex-shrink-0 text-content-dim" />}
              <span className="truncate">{title}</span>
            </span>
            {/* A count of zero is not information. It only appears once there is something to count. */}
            {items.length > 0 ? (
              <span className="ml-1 font-mono text-[9.5px] text-content-dim">{items.length}</span>
            ) : null}
          </button>

          <button
            type="button"
            onClick={event => openNewSessionMenu(event, isWorkspace ? groupKey : null)}
            className="flex-shrink-0 rounded p-1 text-content-dim opacity-0 transition group-hover/row:opacity-100 focus-visible:opacity-100 hover:bg-surface-hover hover:text-accent"
            title={`New conversation in ${title} (choose engine)`}
            aria-label={`New conversation in ${title}`}
          >
            <Plus className="h-3.5 w-3.5" />
          </button>
        </div>

        {!isCollapsed && (
          <div className="space-y-1">
            {items.map(s => {
              const isActive = s.sessionId === activeSessionId;
              const isRenaming = renamingId === s.sessionId;
              const isDeepSeek = (s as { engine?: string }).engine === 'deepseek';
              const isQwen = (s as { engine?: string }).engine === 'qwen';
              const isZai = (s as { engine?: string }).engine === 'zai';
              const isVyce = (s as { engine?: string }).engine === 'vyce';
              const isOpenCode = (s as { engine?: string }).engine === 'opencode';
              const isByok = (s as { engine?: string }).engine === 'byok';

              if (isRenaming) {
                return (
                  <div key={s.sessionId} className="flex items-center gap-1.5 rounded-lg bg-surface-card px-2.5 py-1.5">
                    <Zap className="h-3.5 w-3.5 flex-shrink-0 fill-sky-400 text-sky-400" />
                    <input
                      autoFocus
                      value={renameDraft}
                      onChange={event => setRenameDraft(event.target.value)}
                      onBlur={() => commitRename(s.sessionId)}
                      onKeyDown={event => {
                        if (event.key === 'Enter') {
                          event.preventDefault();
                          commitRename(s.sessionId);
                        }
                        if (event.key === 'Escape') {
                          event.preventDefault();
                          setRenamingId(null);
                        }
                      }}
                      className="w-full min-w-0 bg-transparent text-xs text-content-main focus:outline-none"
                    />
                  </div>
                );
              }

              return (
                <div
                  key={s.sessionId}
                  onClick={() => {
                    // Backend-only sessions are the CLI's own history. They are not in this store,
                    // so switchSession would return early and the click would look broken.
                    if (!s.isLocal) return;
                    switchSession(s.sessionId);
                    setActiveView('chat');
                  }}
                  onDoubleClick={() => {
                    // Backend-only sessions live outside the store, so only local ones can be renamed.
                    if (s.isLocal) beginRename(s.sessionId, s.goal || '');
                  }}
                  className={`group w-full px-2.5 py-2 rounded-lg text-xs flex items-center space-x-2 transition select-none ${
                    s.isLocal ? 'cursor-pointer' : 'cursor-default opacity-70'
                  } ${
                    isActive
                      ? 'bg-surface-card border border-surface-border text-content-main shadow-sm'
                      : 'hover:bg-surface-card text-content-muted hover:text-content-main'
                  }`}
                  title={
                    s.isLocal
                      ? `${s.goal || s.sessionId}${isQwen ? ' (Qwen, runs in this app)' : isZai ? ' (Z.ai GLM, runs in this app)' : isDeepSeek ? ' (DeepSeek, runs in this app)' : isVyce ? ' (Vyce Code, runs in a hidden console)' : isOpenCode ? ' (OpenCode, headless)' : isByok ? ' (Your API key, via the Claude Code CLI)' : ''}. Double-click to rename`
                      : `${s.goal || s.sessionId} — found in the CLI's own history. Not importable yet, so it cannot be opened here.`
                  }
                >
                  {isQwen ? (
                    <Sparkles
                      className={`w-3.5 h-3.5 flex-shrink-0 ${isActive ? 'text-violet-400' : 'text-violet-400/70'}`}
                      aria-label="Qwen chat"
                    />
                  ) : isZai ? (
                    <Sparkles
                      className={`w-3.5 h-3.5 flex-shrink-0 ${isActive ? 'text-accent' : 'text-accent/70'}`}
                      aria-label="Z.ai GLM chat"
                    />
                  ) : isDeepSeek ? (
                    <Brain
                      className={`w-3.5 h-3.5 flex-shrink-0 ${isActive ? 'text-accent' : 'text-accent/70'}`}
                      aria-label="DeepSeek chat"
                    />
                  ) : isVyce ? (
                    <SquareTerminal
                      className={`w-3.5 h-3.5 flex-shrink-0 ${isActive ? 'text-emerald-400' : 'text-emerald-400/70'}`}
                      aria-label="Vyce chat"
                    />
                  ) : isOpenCode ? (
                    <Terminal
                      className={`w-3.5 h-3.5 flex-shrink-0 ${isActive ? 'text-amber-400' : 'text-amber-400/70'}`}
                      aria-label="OpenCode chat"
                    />
                  ) : isByok ? (
                    <KeyRound
                      className={`w-3.5 h-3.5 flex-shrink-0 ${isActive ? 'text-amber-400' : 'text-amber-400/70'}`}
                      aria-label="Chat on your own API key"
                    />
                  ) : (
                    <Zap className={`w-3.5 h-3.5 flex-shrink-0 ${isActive ? 'text-sky-400 fill-sky-400' : 'text-sky-500'}`} />
                  )}
                  <span className="truncate flex-1 font-medium text-xs text-left" title={s.goal || s.sessionId}>
                    {s.goal || s.sessionId}
                  </span>
                  <span className="text-[10px] font-mono text-content-dim flex-shrink-0">
                    {isActive ? 'active' : s.stepCount ? `${s.stepCount} steps` : ''}
                  </span>
                  <button
                    type="button"
                    onClick={async e => {
                      e.stopPropagation();
                      if (s.isLocal) {
                        deleteSession(s.sessionId);
                      } else {
                        await deleteBackendSession(s);
                      }
                    }}
                    className="opacity-0 group-hover:opacity-100 p-1 rounded text-content-dim hover:text-rose-400 hover:bg-rose-500/10 transition flex-shrink-0"
                    title="Delete session"
                  >
                    <Trash2 className="w-3.5 h-3.5" />
                  </button>
                </div>
              );
            })}
          </div>
        )}
      </div>
    );
  };

  return (
    <aside
      id="conversations-sidebar"
      className="w-64 bg-surface-sidebar border-r border-surface-border flex flex-col flex-shrink-0 transition-all duration-200 select-none z-10"
    >
      {/* One compact row. The old block spent about 200px on two section labels, a full width
          select, a full width button and a runner row the chat header already shows. */}
      <div className="border-b border-surface-border px-2 py-2 space-y-1.5">
        <div className="flex items-center gap-1">
          <div className="relative min-w-0 flex-1">
            <select
              value={activeWorkspaceId || '__none__'}
              onChange={e => {
                if (e.target.value === '__new__') {
                  setNewWorkspace({ name: '', dir: '' });
                  setNewWorkspaceError(null);
                  return;
                }
                if (e.target.value === '__none__') {
                  clearWorkspace();
                  return;
                }
                switchWorkspace(e.target.value);
              }}
              title="Switch workspace"
              className="w-full cursor-pointer appearance-none rounded-md border border-surface-border bg-surface-card py-1 pl-2 pr-6 font-mono text-[11px] text-content-main shadow-sm focus:border-sky-500 focus:outline-none"
            >
              {workspaces.map(ws => (
                <option key={ws.id} value={ws.id}>
                  {ws.name}
                </option>
              ))}
              {workspaces.length === 0 && <option value="">Loading workspaces...</option>}
              {/* No folder at all. Chats started here land in the Unfiled group. */}
              <option value="__none__">Unfiled (no workspace)</option>
              <option value="__new__">+ New workspace...</option>
            </select>
            <ChevronDown className="pointer-events-none absolute right-1.5 top-1.5 h-3 w-3 text-content-dim" />
          </div>

          <button
            type="button"
            onClick={() => setIsFilterVisible(visible => !visible)}
            title="Filter sessions"
            aria-label="Filter sessions"
            className={`rounded p-1 transition-colors hover:bg-surface-card hover:text-content-main ${
              isFilterVisible || sessionFilter ? 'text-accent' : 'text-content-dim'
            }`}
          >
            <Filter className="h-3.5 w-3.5" />
          </button>

          <button
            type="button"
            onClick={event => openNewSessionMenu(event, activeWorkspaceId || null)}
            title={`New conversation in ${activeWorkspace?.name || 'the active workspace'} (choose engine)`}
            aria-label="New conversation"
            className="rounded p-1 text-accent transition-colors hover:bg-surface-card"
          >
            <Plus className="h-4 w-4" />
          </button>

          <button
            type="button"
            onClick={() => setActiveView('profiles')}
            title="Configure workspaces in AccountsHub"
            aria-label="Configure workspaces in AccountsHub"
            className="rounded p-1 text-content-dim transition-colors hover:bg-surface-card hover:text-content-main"
          >
            <Settings className="h-3.5 w-3.5" />
          </button>
        </div>

        {isFilterVisible ? (
          <input
            value={sessionFilter}
            onChange={e => setSessionFilter(e.target.value)}
            placeholder="Filter sessions..."
            autoFocus
            className="w-full rounded-md border border-surface-border bg-surface-deep px-2 py-1 font-mono text-[11px] text-content-main outline-none focus:border-sky-500"
          />
        ) : null}

        {newWorkspace
          ? createPortal(
              <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm">
                <div className="w-full max-w-md space-y-3 rounded-2xl border border-surface-border bg-surface-card p-5 shadow-2xl">
                  <h3 className="text-sm font-bold text-content-main">New workspace</h3>

                  <label className="block space-y-1">
                    <span className="font-mono text-[10px] uppercase tracking-wide text-content-dim">Name</span>
                    <input
                      value={newWorkspace.name}
                      onChange={e => setNewWorkspace({ ...newWorkspace, name: e.target.value })}
                      placeholder="ChessSenseV2"
                      autoFocus
                      className="w-full rounded-lg border border-surface-border bg-surface-deep px-2.5 py-1.5 font-mono text-xs text-content-main outline-none focus:border-sky-500"
                    />
                  </label>

                  <div className="space-y-1">
                    <span className="font-mono text-[10px] uppercase tracking-wide text-content-dim">Folder</span>
                    <div className="flex gap-1.5">
                      <input
                        value={newWorkspace.dir}
                        onChange={e => setNewWorkspace({ ...newWorkspace, dir: e.target.value })}
                        placeholder="C:/path/to/project"
                        className="min-w-0 flex-1 rounded-lg border border-surface-border bg-surface-deep px-2.5 py-1.5 font-mono text-xs text-content-main outline-none focus:border-sky-500"
                      />
                      <button
                        type="button"
                        onClick={async () => {
                          try {
                            const res: any = await webviewBridge.invoke('browse_directory', {
                              title: 'Open workspace',
                            });
                            const picked = res?.path as string | undefined;
                            if (!picked) return;

                            // Name follows the folder unless the user already typed one.
                            const folderName = picked.split(/[\\/]/).filter(Boolean).pop() || '';
                            setNewWorkspace(current => ({
                              dir: picked,
                              name: current && current.name.trim() ? current.name : folderName,
                            }));
                          } catch {
                            // Cancelling the dialog is not an error.
                          }
                        }}
                        title="Select a folder"
                        className="flex flex-shrink-0 items-center gap-1 rounded-lg border border-surface-border bg-surface-deep px-2 py-1.5 font-mono text-[10.5px] text-content-muted transition-colors hover:border-sky-500/50 hover:text-content-main"
                      >
                        <FolderOpen className="h-3.5 w-3.5" />
                        Browse
                      </button>
                    </div>
                    <span className="block text-[10px] text-content-dim">
                      The agent runs in this folder; skills live in its <span className="font-mono">.agents/skills</span>.
                    </span>
                  </div>

                  {newWorkspaceError ? (
                    <p className="text-[11px] text-state-danger">{newWorkspaceError}</p>
                  ) : null}

                  <div className="flex justify-end gap-2 pt-1">
                    <button
                      type="button"
                      onClick={() => setNewWorkspace(null)}
                      className="rounded-md border border-surface-border bg-surface-deep px-2.5 py-1 text-[11px] text-content-main"
                    >
                      Cancel
                    </button>
                    <button
                      type="button"
                      onClick={async () => {
                        const name = newWorkspace.name.trim();
                        const dir = newWorkspace.dir.trim();
                        if (!name) return setNewWorkspaceError('A name is required.');
                        if (!dir) return setNewWorkspaceError('Pick a folder for this workspace.');

                        const newId = `ws_${Date.now().toString(36)}`;
                        const ok = await saveWorkspace({ id: newId, name, workingDirectory: dir } as any);
                        if (!ok) return setNewWorkspaceError('The host could not create this workspace.');

                        await loadWorkspaces();
                        switchWorkspace(newId);
                        setNewWorkspace(null);
                      }}
                      className="rounded-md border border-sky-500/50 bg-sky-500 px-2.5 py-1 text-[11px] font-semibold text-slate-950"
                    >
                      Create
                    </button>
                  </div>
                </div>
              </div>,
              document.body
            )
          : null}
      </div>

      {/* 2. Sessions, grouped by workspace */}
      <div className="flex-1 overflow-y-auto px-2 py-3 space-y-3" id="sessions-scroll-container">
        {workspaceGroups.map(group => renderSessionGroup(group.name, group.items, group.id))}

        {allSessions.length === 0 && (
          <div className="flex flex-col items-center gap-2.5 px-4 py-8 text-center">
            <p className="text-xs leading-relaxed text-content-muted">
              No conversations yet. A new one opens in {activeWorkspace?.name || 'the active workspace'}.
            </p>
            <button
              onClick={handleNewSession}
              className="rounded-lg bg-sky-500 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-sky-400"
            >
              Start a conversation
            </button>
          </div>
        )}
      </div>

      {/* 4. Active Repo Files Tree (Live Real Filesystem) */}
      <div className="p-3 border-t border-surface-border bg-surface-deep/40 max-h-48 flex flex-col">
        <div className="flex items-center justify-between pb-1.5">
          <button
            type="button"
            onClick={toggleFiles}
            className="flex items-center gap-1 text-[11px] font-medium text-content-muted transition-colors hover:text-content-main"
            title={isFilesOpen ? 'Hide the file list' : 'Show the file list'}
          >
            <ChevronDown className={`w-3 h-3 transition-transform ${isFilesOpen ? '' : '-rotate-90'}`} />
            Files
          </button>

          <span className="flex items-center gap-2">
            <button
              type="button"
              onClick={toggleNoisyFiles}
              className={`rounded border px-1 py-0.5 text-[9px] font-mono transition-colors ${
                showNoisyFiles
                  ? 'border-sky-500/40 bg-sky-500/10 text-accent'
                  : 'border-surface-border text-content-dim hover:text-content-main'
              }`}
              title={showNoisyFiles ? 'Hide build/cache files' : 'Show build and cache files'}
            >
              {showNoisyFiles ? 'all' : 'src'}
            </button>
            <span className="text-[10px] text-sky-400 font-mono flex items-center gap-1">
              <GitBranch className="w-2.5 h-2.5" />
              <span>{branch}</span>
            </span>
          </span>
        </div>

        <div className={`space-y-0.5 text-xs text-content-muted font-mono overflow-y-auto flex-1 ${isFilesOpen ? '' : 'hidden'}`}>
          {isLoadingFiles ? (
            <div className="py-2 px-1 text-[11px] text-content-dim italic flex items-center gap-1.5">
              <Loader2 className="w-3 h-3 animate-spin text-sky-400" />
              <span>Scanning files...</span>
            </div>
          ) : visibleFiles.length === 0 ? (
            <div className="py-2 text-[11px] text-content-dim italic">
              {files.length === 0 ? 'Workspace empty' : 'Only build/cache files here. Press "all"'}
            </div>
          ) : (
            visibleFiles.slice(0, 30).map(file => (
              <div
                key={file.path}
                className="py-0.5 hover:text-content-main cursor-pointer flex items-center space-x-1.5 transition text-[11px] truncate group"
                title={`File: ${file.path}`}
                onClick={() => {
                  // Dispatch custom event so input dock can append reference
                  window.dispatchEvent(new CustomEvent('insert-prompt-file', { detail: { path: file.path } }));
                }}
              >
                {getFileIcon(file)}
                <span className={`truncate ${file.isDirectory ? 'font-medium text-content-main' : ''}`}>
                  {file.name}
                </span>
              </div>
            ))
          )}
        </div>
      </div>
      {/* Engine picker for a new conversation. Fixed, not absolute: the list scrolls and would clip it. */}
      {newSessionMenu
        ? createPortal(
            <>
              <div className="fixed inset-0 z-[70]" onClick={() => setNewSessionMenu(null)} />
              <div
                style={{ top: newSessionMenu.top, left: newSessionMenu.left }}
                className="fixed z-[71] w-[352px] rounded-xl border border-surface-border bg-surface-card p-1.5 shadow-xl"
                role="menu"
              >
                {/* Two columns so the list grows by adding cells, not by growing taller. Each cell is
                    half as wide, so the subtitles are short and clip rather than wrap. */}
                <div className="grid grid-cols-2 gap-1">
                  <button
                    type="button"
                    role="menuitem"
                    title="Antigravity: the CLI, with tools and skills"
                    onClick={() => startSession(newSessionMenu.workspaceId, 'agy')}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <img src={engineIcons.agy} alt="" className="mt-0.5 h-5 w-5 flex-shrink-0 object-contain" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use Antigravity</span>
                      <span className="block truncate text-[9.5px] text-content-dim">CLI, tools and skills</span>
                    </span>
                  </button>

                  <button
                    type="button"
                    role="menuitem"
                    title="DeepSeek: free, via chat.deepseek.com"
                    onClick={() => startSession(newSessionMenu.workspaceId, 'deepseek')}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <img src={engineIcons.deepseek} alt="" className="mt-0.5 h-5 w-5 flex-shrink-0 object-contain" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use DeepSeek</span>
                      <span className="block truncate text-[9.5px] text-content-dim">Free web chat</span>
                    </span>
                  </button>

                  <button
                    type="button"
                    role="menuitem"
                    title="Qwen: free, no account needed"
                    onClick={() => startSession(newSessionMenu.workspaceId, 'qwen')}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <img src={engineIcons.qwen} alt="" className="mt-0.5 h-5 w-5 flex-shrink-0 object-contain" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use Qwen</span>
                      <span className="block truncate text-[9.5px] text-content-dim">Free, no account</span>
                    </span>
                  </button>

                  <button
                    type="button"
                    role="menuitem"
                    title="Z.ai (GLM): free without an account, better signed in"
                    onClick={() => startSession(newSessionMenu.workspaceId, 'zai')}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <img src={engineIcons.zai} alt="" className="mt-0.5 h-5 w-5 flex-shrink-0 object-contain" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use Z.ai (GLM)</span>
                      <span className="block truncate text-[9.5px] text-content-dim">Free as guest</span>
                    </span>
                  </button>

                  <button
                    type="button"
                    role="menuitem"
                    title="Bring your own API key: the Claude Code CLI, on your provider"
                    onClick={() => startSession(newSessionMenu.workspaceId, 'byok')}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <img src={engineIcons.byok} alt="" className="mt-0.5 h-5 w-5 flex-shrink-0 object-contain" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use your own key</span>
                      <span className="block truncate text-[9.5px] text-content-dim">Claude Code CLI, any provider</span>
                    </span>
                  </button>

                  <button
                    type="button"
                    role="menuitem"
                    title="llama.cpp: a model running on this machine"
                    onClick={() => void startLocalModelSession(newSessionMenu.workspaceId)}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <Cpu className="mt-0.5 h-5 w-5 flex-shrink-0 text-accent" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use a local model</span>
                      <span className="block truncate text-[9.5px] text-content-dim">llama.cpp, on this machine</span>
                    </span>
                  </button>

                  <button
                    type="button"
                    role="menuitem"
                    title="Vyce: Vyce Code, in a hidden console"
                    onClick={() => startSession(newSessionMenu.workspaceId, 'vyce')}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <img src={engineIcons.vyce} alt="" className="mt-0.5 h-5 w-5 flex-shrink-0 object-contain" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use Vyce</span>
                      <span className="block truncate text-[9.5px] text-content-dim">Hidden console</span>
                    </span>
                  </button>

                  <button
                    type="button"
                    role="menuitem"
                    title="OpenCode: headless, any provider you signed in"
                    onClick={() => startSession(newSessionMenu.workspaceId, 'opencode')}
                    className="flex w-full items-start gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover focus-visible:bg-surface-hover focus-visible:outline-none"
                  >
                    <img src={engineIcons.opencode} alt="" className="mt-0.5 h-5 w-5 flex-shrink-0 object-contain" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11px] font-medium text-content-main">Use OpenCode</span>
                      <span className="block truncate text-[9.5px] text-content-dim">Headless, signed in</span>
                    </span>
                  </button>
                </div>
              </div>
            </>,
            document.body
          )
        : null}
    </aside>
  );
};
