/**
 * Sidecar health, the active view and the current Settings section
 */

import { create } from 'zustand';
import { SidecarHealth, ActiveView } from '../types/agent';
import { webviewBridge } from '../services/webviewBridge';
import { useSessionStore } from './useSessionStore';
import { useProfileStore } from './useProfileStore';
import { useWorkspaceStore } from './useWorkspaceStore';
import { useRepositoriesStore } from './useRepositoriesStore';
import { useSettingsStore } from './useSettingsStore';
import { revokeRule } from '../lib/permissionRules';
import { DEFAULT_RAIL_SHORTCUTS, conflictingView } from '../lib/railShortcuts';
import { logUi } from '../services/uiLogger';

/** One flush per page load: conversation grants from the previous run are revoked exactly once. */
let conversationRulesFlushed = false;

/** Every view the rail can show. One list, so a saved order can never name a view that does not exist. */
const VIEW_IDS: ActiveView[] = ['chat', 'profiles', 'mcpskills', 'dashboard', 'repositories', 'settings'];

/** Display name per view, shared by the rail's tooltips and the Settings checklist. */
export const RAIL_VIEW_LABELS: Record<ActiveView, string> = {
  chat: 'Chat',
  profiles: 'AccountsHub',
  mcpskills: 'MCP & Skills',
  dashboard: 'Dashboard',
  repositories: 'Git',
  settings: 'Settings',
};

/** Settings cannot be hidden: it holds the list that would bring it back. */
const UNHIDEABLE: ActiveView = 'settings';

/**
 * Rail order, as first shipped: talk to the agent, pick what it runs as and what it may use, then the
 * two overviews, then settings. A saved order replaces this; a new view added later is appended.
 */
const DEFAULT_RAIL_ORDER: ActiveView[] = VIEW_IDS;

/** Saved bindings, filled in with the shipped default for any view the file does not mention. */
function loadRailShortcuts(): Record<ActiveView, string> {
  const defaults = { ...DEFAULT_RAIL_SHORTCUTS };

  try {
    const raw = localStorage.getItem('agents_hub_rail_shortcuts');
    if (!raw) return defaults;

    const saved: unknown = JSON.parse(raw);
    if (!saved || typeof saved !== 'object' || Array.isArray(saved)) return defaults;

    for (const id of VIEW_IDS) {
      const value = (saved as Record<string, unknown>)[id];
      // An empty string is a deliberate "no shortcut", so it is kept rather than defaulted over.
      if (typeof value === 'string') defaults[id] = value;
    }
  } catch {
    // A damaged value falls back to the defaults.
  }

  return defaults;
}

function loadHiddenRailViews(): ActiveView[] {
  try {
    const raw = localStorage.getItem('agents_hub_rail_hidden');
    if (!raw) return [];

    const saved: unknown = JSON.parse(raw);
    if (!Array.isArray(saved)) return [];

    return [
      ...new Set(
        saved.filter(
          (id): id is ActiveView => typeof id === 'string' && VIEW_IDS.includes(id as ActiveView) && id !== UNHIDEABLE
        )
      ),
    ];
  } catch {
    return [];
  }
}

function loadRailOrder(): ActiveView[] {
  try {
    const raw = localStorage.getItem('agents_hub_rail_order');
    if (!raw) return DEFAULT_RAIL_ORDER;

    const saved: unknown = JSON.parse(raw);
    if (!Array.isArray(saved)) return DEFAULT_RAIL_ORDER;

    // Keep only real views, drop repeats, then append whatever the saved order predates.
    const kept = saved.filter((id): id is ActiveView => typeof id === 'string' && VIEW_IDS.includes(id as ActiveView));
    const unique = [...new Set(kept)];
    return [...unique, ...VIEW_IDS.filter(id => !unique.includes(id))];
  } catch {
    return DEFAULT_RAIL_ORDER;
  }
}

interface SidecarStoreState {
  health: SidecarHealth;
  activeView: ActiveView;
  /**
   * Which Settings section to show. Living here instead of inside SettingsView is what lets the
   * guide open Settings straight on the dependency list.
   */
  settingsCategory: string;
  isSidebarCollapsed: boolean;
  /** The icon rail, collapsed independently of the secondary panel. */
  isRailCollapsed: boolean;
  /** Order of the rail's view buttons, as the user arranged them. */
  railOrder: ActiveView[];
  /** Views the user took off the rail. The view itself still works; it just has no icon. */
  hiddenRailViews: ActiveView[];
  /** Off until the user turns it on in Settings. */
  railShortcutsEnabled: boolean;
  railShortcuts: Record<ActiveView, string>;
  isBridgeConnected: boolean;

  // Actions
  setHealth: (health: Partial<SidecarHealth>) => void;
  setActiveView: (view: ActiveView) => void;
  setSettingsCategory: (category: string) => void;
  toggleSidebar: () => void;
  toggleRail: () => void;
  setRailCollapsed: (collapsed: boolean) => void;
  setRailOrder: (order: ActiveView[]) => void;
  resetRailOrder: () => void;
  toggleRailViewHidden: (id: ActiveView) => void;
  showAllRailViews: () => void;
  setRailShortcutsEnabled: (enabled: boolean) => void;
  /** Binds a key, taking it from whichever view held it. Returns that view, if there was one. */
  setRailShortcut: (id: ActiveView, shortcut: string) => ActiveView | null;
  resetRailShortcuts: () => void;
  setSidebarCollapsed: (collapsed: boolean) => void;
  initBridgeListeners: () => () => void;
  refreshSidecarStatus: () => Promise<void>;
  hydrateInitialState: () => Promise<void>;
}

export const useSidecarStore = create<SidecarStoreState>((set, get) => ({
  health: {
    status: 'Offline',
    port: 0,
    pid: 0,
    isOnline: false,
    latencyMs: 0,
    activeSessions: 0,
    version: `v${__APP_VERSION__}`,
  },
  activeView: (function (): ActiveView {
    try {
      const v = localStorage.getItem('agents_hub_active_view') as ActiveView;
      if (VIEW_IDS.includes(v)) return v;
    } catch {}
    return 'chat';
  })(),
  settingsCategory: 'appearance',
  isSidebarCollapsed: (function (): boolean {
    try {
      return localStorage.getItem('agents_hub_sidebar_collapsed') === 'true';
    } catch {
      return false;
    }
  })(),
  /**
   * The icon rail. Separate from `isSidebarCollapsed` so either panel can go without the other:
   * the rail is navigation, the secondary panel is content, and they are hidden for different
   * reasons (more room for the editor versus more room for the chat).
   */
  isRailCollapsed: (function (): boolean {
    try {
      return localStorage.getItem('agents_hub_rail_collapsed') === 'true';
    } catch {
      return false;
    }
  })(),
  railOrder: loadRailOrder(),
  hiddenRailViews: loadHiddenRailViews(),
  railShortcutsEnabled: (function (): boolean {
    try {
      return localStorage.getItem('agents_hub_rail_shortcuts_on') === 'true';
    } catch {
      return false;
    }
  })(),
  railShortcuts: loadRailShortcuts(),
  isBridgeConnected: false,

  setHealth: healthUpdate =>
    set(state => ({
      health: { ...state.health, ...healthUpdate },
      isBridgeConnected: healthUpdate.isOnline !== undefined ? healthUpdate.isOnline : state.isBridgeConnected,
    })),

  setActiveView: view => {
    try {
      localStorage.setItem('agents_hub_active_view', view);
    } catch {}
    set({ activeView: view });
  },

  setSettingsCategory: category => set({ settingsCategory: category }),

  toggleSidebar: () => {
    const next = !get().isSidebarCollapsed;
    try {
      localStorage.setItem('agents_hub_sidebar_collapsed', String(next));
    } catch {}
    set({ isSidebarCollapsed: next });
  },

  toggleRail: () => {
    const next = !get().isRailCollapsed;
    try {
      localStorage.setItem('agents_hub_rail_collapsed', String(next));
    } catch {}
    set({ isRailCollapsed: next });
  },

  setRailCollapsed: collapsed => {
    try {
      localStorage.setItem('agents_hub_rail_collapsed', String(collapsed));
    } catch {}
    set({ isRailCollapsed: collapsed });
  },

  setSidebarCollapsed: collapsed => {
    try {
      localStorage.setItem('agents_hub_sidebar_collapsed', String(collapsed));
    } catch {}
    set({ isSidebarCollapsed: collapsed });
  },

  setRailOrder: order => {
    try {
      localStorage.setItem('agents_hub_rail_order', JSON.stringify(order));
    } catch {}
    set({ railOrder: order });
  },

  resetRailOrder: () => {
    try {
      localStorage.removeItem('agents_hub_rail_order');
    } catch {}
    set({ railOrder: DEFAULT_RAIL_ORDER });
  },

  toggleRailViewHidden: id => {
    if (id === UNHIDEABLE) return;

    const current = get().hiddenRailViews;
    const next = current.includes(id) ? current.filter(view => view !== id) : [...current, id];

    try {
      localStorage.setItem('agents_hub_rail_hidden', JSON.stringify(next));
    } catch {}
    set({ hiddenRailViews: next });
  },

  showAllRailViews: () => {
    try {
      localStorage.removeItem('agents_hub_rail_hidden');
    } catch {}
    set({ hiddenRailViews: [] });
  },

  setRailShortcutsEnabled: enabled => {
    try {
      localStorage.setItem('agents_hub_rail_shortcuts_on', String(enabled));
    } catch {}
    set({ railShortcutsEnabled: enabled });
  },

  setRailShortcut: (id, shortcut) => {
    const current = get().railShortcuts;
    const displaced = conflictingView(current, id, shortcut);

    const next = { ...current, [id]: shortcut };

    // Hand the old binding to the view that lost its key, so a swap leaves neither view dead. When
    // the view being edited had no key, the displaced view simply ends up unbound.
    if (displaced) next[displaced] = current[id];

    try {
      localStorage.setItem('agents_hub_rail_shortcuts', JSON.stringify(next));
    } catch {}
    set({ railShortcuts: next });

    return displaced;
  },

  resetRailShortcuts: () => {
    try {
      localStorage.removeItem('agents_hub_rail_shortcuts');
    } catch {}
    set({ railShortcuts: { ...DEFAULT_RAIL_SHORTCUTS } });
  },

  refreshSidecarStatus: async () => {
    try {
      const res = await webviewBridge.invoke('get_sidecar_status');
      if (res) {
        get().setHealth({
          status: res.status || (res.isOnline ? 'Online' : 'Offline'),
          port: res.port || 0,
          pid: res.pid || 0,
          isOnline: !!res.isOnline,
          latencyMs: res.latencyMs || 1,
          activeSessions: res.activeSessions || 0,
          version: res.version || `v${__APP_VERSION__}`,
        });
      }
    } catch (err) {
      console.warn('[SidecarStore] Refresh sidecar status failed:', err);
    }
  },

  hydrateInitialState: async () => {
    try {
      const res = await webviewBridge.invoke('get_initial_state');
      if (res) {
        // The host reads its own assembly version; the About card and the updater would otherwise
        // each carry a stale literal that no release ever updates.
        if (res.version) useSettingsStore.getState().setAppVersion(String(res.version));

        // Hydrate workspaces
        if (Array.isArray(res.workspaces)) {
          useWorkspaceStore.getState().setWorkspacesData(res.workspaces, res.activeWorkspaceId);
          useWorkspaceStore.getState().loadWorkspaceFiles();
          useWorkspaceStore.getState().loadBackendSessions();
        }

        // Hydrate profiles
        if (res.antigravity) {
          const ag = res.antigravity;
          const mapped = Array.isArray(ag.profiles)
            ? ag.profiles.map((p: any) => ({
                name: typeof p === 'string' ? p : p.name || 'Profile',
                email: typeof p === 'object' ? p.email : undefined,
                active: typeof p === 'object' ? !!p.active : p === ag.activeProfile,
                isRunning: typeof p === 'object' ? !!p.isRunning : false,
                // No fallback: a profile whose quota has never been read has no percentage, and
                // inventing one would show a healthy account that was never actually checked.
                quotaPercent: typeof p === 'object' ? p.quotaPercent : undefined,
                lastQuotaChecked: typeof p === 'object' ? p.lastQuotaChecked : undefined,
                provider: (typeof p === 'object' && p.provider) ? p.provider : 'Google Antigravity',
              }))
            : [];
          useProfileStore.getState().setProfilesData(mapped, ag.activeProfile, ag.model);
        }
      }

    } catch (err) {
      console.warn('[SidecarStore] Hydrate initial state error:', err);
    }
  },

  initBridgeListeners: () => {
    const unsubs: Array<() => void> = [];

    // A "for this conversation" grant from a previous run has no conversation left to belong to.
    // Guarded: a re-mount must not revoke rules granted earlier in this same run.
    if (!conversationRulesFlushed) {
      conversationRulesFlushed = true;
      useSessionStore.getState().flushStaleConversationRules();
    }

    // 1. Sidecar Status
    unsubs.push(
      webviewBridge.on('sidecar_status_changed', (data: any) => {
        get().setHealth({
          status: data.status || (data.isOnline ? 'Online' : 'Offline'),
          port: data.port || 0,
          pid: data.pid || 0,
          isOnline: !!data.isOnline,
          latencyMs: data.latencyMs || 1,
          activeSessions: data.activeSessions || 0,
          version: data.version || `v${__APP_VERSION__}`,
          error: data.error,
        });
      })
    );

    // 3. Workspaces updates from C#
    unsubs.push(
      webviewBridge.on('workspaces_updated', (data: any) => {
        if (Array.isArray(data.workspaces)) {
          useWorkspaceStore.getState().setWorkspacesData(data.workspaces, data.activeWorkspaceId);
        }
      })
    );

    unsubs.push(
      webviewBridge.on('workspace_activated', (data: any) => {
        if (data.activeWorkspace) {
          const currentList = useWorkspaceStore.getState().workspaces;
          const allWorkspaces = Array.isArray(data.workspaces) && data.workspaces.length > 0
            ? data.workspaces
            : currentList.map(w => w.id === data.activeWorkspaceId ? data.activeWorkspace : w);
          useWorkspaceStore.getState().setWorkspacesData(allWorkspaces, data.activeWorkspaceId);
          useWorkspaceStore.getState().loadWorkspaceFiles(data.activeWorkspace.workingDirectory);
          useWorkspaceStore.getState().loadBackendSessions();
        }
      })
    );

    unsubs.push(
      webviewBridge.on('workspace_file_changed', () => {
        useWorkspaceStore.getState().loadWorkspaceFiles();
      })
    );

    // 4. Profiles updates from C#
    unsubs.push(
      webviewBridge.on('profile_changed', (data: any) => {
        if (Array.isArray(data.profiles)) {
          useProfileStore.getState().setProfilesData(data.profiles, data.activeProfile);
        }
      })
    );

    unsubs.push(
      webviewBridge.on('profile_saved', (data: any) => {
        if (Array.isArray(data.profiles)) {
          useProfileStore.getState().setProfilesData(data.profiles, data.activeProfile);
        }
      })
    );

    unsubs.push(
      webviewBridge.on('antigravity_instance_status_changed', (data: any) => {
        if (data.profile) {
          useProfileStore.getState().setProfileRunning(data.profile, !!data.isRunning);
        }
      })
    );

    unsubs.push(
      webviewBridge.on('repo_manager_state_changed', () => {
        useRepositoriesStore.getState().loadRepoManagerState();
      })
    );

    // 5. Streaming Chat Tokens -> Target Session
    unsubs.push(
      webviewBridge.on('chat_token', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        if (data.text) {
          sessionStore.appendChatToken(targetSessionId, data.text);
        }
      })
    );

    // The host settles the answer once the turn is over. Streaming is append-only, so without this
    // a turn that streamed an action object kept it on screen forever.
    unsubs.push(
      webviewBridge.on('chat_final', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        if (typeof data?.text === 'string') {
          sessionStore.setAssistantText(targetSessionId, data.text);
        }
      })
    );

    // 6. Live Thinking Stream -> Target Session
    unsubs.push(
      webviewBridge.on('session_title', (data: any) => {
        // The web app names the conversation; take that while ours is still unnamed, and never
        // overwrite a title the user set themselves.
        const store = useSessionStore.getState();
        const session = store.sessions[data?.sessionId];
        const current = (session?.title || '').trim();
        if (!session || !data?.title) return;
        if (current && current !== 'New Session' && current !== 'New Chat') return;
        store.renameSession(data.sessionId, String(data.title).slice(0, 80));
      })
    );

    unsubs.push(
      webviewBridge.on('thinking', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        sessionStore.appendThinkingChunk(
          targetSessionId,
          data.text,
          data.durationSeconds,
          data.thinkingTokens,
          data.state
        );
      })
    );

    // 7. Tool Invocation Start -> Stepper Node
    unsubs.push(
      webviewBridge.on('tool_start', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        sessionStore.startToolNode(
          targetSessionId,
          data.callId || `call_${Date.now()}`,
          data.tool || 'command',
          data.params || {}
        );
      })
    );

    // 8. Tool Invocation Finish -> Stepper Node
    unsubs.push(
      webviewBridge.on('tool_output', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        sessionStore.finishToolNode(
          targetSessionId,
          data.callId || '',
          data.tool || '',
          data.success !== false,
          data.result,
          data.error,
          data.durationMs
        );
      })
    );

    // 9. Session Status / Completion
    unsubs.push(
      webviewBridge.on('session_status', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        sessionStore.updateSessionStatus(
          targetSessionId,
          data.state || 'idle',
          data.error,
          data.pid
        );

        // The engine reports its own failures here, so the console gets the reason even when the
        // user is looking at another page.
        if (data.state === 'error' && data.error) {
          logUi('error', 'Chat', String(data.error));
        }

        // A "run once" grant was written only so this turn could retry, and the turn is over now.
        const state = data.state || 'idle';
        if (state === 'idle' || state === 'completed' || state === 'error') {
          for (const entry of sessionStore.consumeOnceRules(targetSessionId)) {
            void revokeRule(entry);
          }
        }
      })
    );

    // 9b. A tool the CLI refused for lack of permission. Headless runs cannot prompt, so the
    // composer offers to write the allow-rule the CLI named instead.
    unsubs.push(
      webviewBridge.on('permission_denied', (data: any) => {
        if (!data || typeof data.message !== 'string') return;
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        if (!targetSessionId) return;

        logUi('warn', 'Chat', `Permission denied for ${data.tool ?? 'tool'}: ${data.message}`);

        sessionStore.addPermissionDenial({
          sessionId: targetSessionId,
          profile: typeof data.profile === 'string' ? data.profile : undefined,
          callId: typeof data.callId === 'string' ? data.callId : `denial_${Date.now()}`,
          tool: typeof data.tool === 'string' ? data.tool : 'tool',
          permission: typeof data.permission === 'string' ? data.permission : 'tool',
          command: typeof data.command === 'string' ? data.command : undefined,
          target: typeof data.target === 'string' ? data.target : undefined,
          rule: typeof data.rule === 'string' ? data.rule : undefined,
          message: data.message,
          receivedAt: Date.now(),
        });
      })
    );

    // 9c. The session adopted a CLI conversation. Stored on the record so every later turn resumes
    // exactly this one instead of whatever ran last.
    unsubs.push(
      webviewBridge.on('conversation_started', (data: any) => {
        if (!data || typeof data.conversationId !== 'string' || !data.conversationId) return;
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        if (!targetSessionId) return;
        sessionStore.updateSessionTelemetry(targetSessionId, { conversationId: data.conversationId });
      })
    );

    // 10. Dynamic Quota Sync — strictly per profile
    unsubs.push(
      webviewBridge.on('quota_update', (data: any) => {
        const pct = typeof data.quotaPercent === 'number' ? data.quotaPercent : parseInt(data.quotaPercent, 10);
        if (isNaN(pct)) return;

        const profile = typeof data.profile === 'string' && data.profile ? data.profile : undefined;
        if (profile) {
          useProfileStore.getState().updateProfileQuota(profile, pct, {
            weeklyPercent: typeof data.quotaWeeklyPercent === 'number' ? data.quotaWeeklyPercent : undefined,
            resetAt: typeof data.quotaResetAt === 'string' ? data.quotaResetAt : undefined,
            weeklyResetAt: typeof data.quotaWeeklyResetAt === 'string' ? data.quotaWeeklyResetAt : undefined,
            group: typeof data.quotaGroup === 'string' ? data.quotaGroup : undefined,
            windows: Array.isArray(data.quotaWindows)
              ? data.quotaWindows
                  .filter(
                    (w: any) =>
                      w && typeof w.group === 'string' && typeof w.label === 'string' && typeof w.percent === 'number'
                  )
                  .map((w: any) => ({
                    group: w.group,
                    label: w.label,
                    percent: w.percent,
                    resetAt: typeof w.resetAt === 'string' ? w.resetAt : undefined,
                  }))
              : undefined,
          });
        }

        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        const sessionProfile = sessionStore.sessions[targetSessionId]?.profile;

        // A poll for profile B must never write its number onto profile A's session.
        if (profile && sessionProfile && sessionProfile.toLowerCase() !== profile.toLowerCase()) {
          return;
        }

        sessionStore.updateSessionTelemetry(targetSessionId, { quotaPercent: pct });
      })
    );

    // 10b. Quota check finished (success or failure) — always clears the per-profile spinner.
    unsubs.push(
      webviewBridge.on('quota_check_completed', (data: any) => {
        const profile = typeof data.profile === 'string' && data.profile ? data.profile : null;
        const profileStore = useProfileStore.getState();

        if (profile && typeof data.quotaPercent === 'number' && data.quotaPercent >= 0) {
          profileStore.updateProfileQuota(profile, data.quotaPercent);
        }

        profileStore.settleQuotaCheck(profile);
      })
    );

    // 11. Dynamic Model Info Sync
    unsubs.push(
      webviewBridge.on('model_info', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        sessionStore.updateSessionTelemetry(targetSessionId, {
          model: data.model,
          reasoningEffort: data.reasoningEffort,
        });
      })
    );

    // 12. Dynamic Token Counter Sync
    unsubs.push(
      webviewBridge.on('token_usage', (data: any) => {
        const sessionStore = useSessionStore.getState();
        const targetSessionId = data.sessionId || sessionStore.activeSessionId;
        const usage = data.usage || data;

        // One event per completed turn; accumulate it instead of replacing the session total.
        sessionStore.recordTurnUsage(targetSessionId, {
          inputTokens: usage.inputTokens || 0,
          outputTokens: usage.outputTokens || 0,
          thinkingTokens: usage.thinkingTokens || 0,
          totalTokens: usage.totalTokens || (usage.inputTokens || 0) + (usage.outputTokens || 0),
        });
      })
    );

    // Initial query
    get().refreshSidecarStatus();
    get().hydrateInitialState();

    return () => {
      unsubs.forEach(unsub => unsub());
    };
  },
}));
