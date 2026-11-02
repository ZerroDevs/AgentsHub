/**
 * Comprehensive Settings & Preferences Store
 * Synchronized with C# backend services:
 * - StartupHelper / General preferences
 * - SoundpackAudioService
 * - DiscordRpcService
 * - RemoteWebhookService
 * - PocketCommanderService
 * - DependencyCheckerService
 * - PreCommitSecurityService
 */

import { create } from 'zustand';
import { webviewBridge } from '../services/webviewBridge';

export interface AppSettings {
  autostart: boolean;
  startMinimized: boolean;
  minimizeToTray: boolean;
  notifications: boolean;
  enableArchitectureGraph: boolean;
  globalHotkey: boolean;
  globalHotkeyModifier: string;
  globalHotkeyKey: string;
  globalHudHotkey: boolean;
  hudHotkeyModifier: string;
  hudHotkeyKey: string;
  hudHotkeyTarget: string;
  maskShortcutPaths: boolean;
  embedShellsInTerminalWorkspace: boolean;
  hideMainOnCliOpen: boolean;
  enableHardwareMonitor: boolean;
  /** Written by the theme store; the host keeps these beside the other preferences. */
  theme: string;
  accentColor: string;
}

export interface EngineHealthItem {
  id: string;
  name: string;
  initialized: boolean;
  signedIn: boolean | null;
  primedIn: string | null;
  lastTurnAt: string | null;
  lastError: string | null;
}

export interface DependencyItem {
  id: string;
  name: string;
  category: 'runtime' | 'agent' | 'tool';
  isInstalled: boolean;
  version?: string;
  path?: string;
  commandPreview?: string;
  installKind: 'winget' | 'powershell' | 'browser';
  /** False means the tool is detected and launchable, but cannot drive a chat session yet. */
  chatSupported?: boolean;
}

export interface DiagnosticItem {
  id: string;
  title: string;
  status: 'Pass' | 'Warning' | 'Error';
  details: string;
  canAutoFix?: boolean;
  fixLabel?: string;
}

export interface LogEntry {
  id: string;
  timestamp: string;
  level: 'info' | 'warn' | 'error' | 'debug';
  source: string;
  message: string;
}

interface SettingsStoreState {
  settings: AppSettings;
  activeSettingsCategory: string;
  searchQuery: string;
  saveFeedback: string | null;

  // Dependencies
  dependencies: DependencyItem[];
  /** Passive engine state for the health panel. */
  engineHealth: EngineHealthItem[];
  engineHealthLoading: boolean;
  /** The dependency whose installer is running, if any. */
  installingId: string | null;
  /** Last progress line per dependency, so a row can say what it is doing. */
  installLines: Record<string, string>;
  dependenciesLoading: boolean;

  // Soundpack
  soundpackEnabled: boolean;
  soundpackTheme: 'modern' | 'arcade' | 'scifi' | 'vocal';
  soundpackVolume: number;
  soundpackEvents: {
    taskDone: boolean;
    approval: boolean;
    checkpoint: boolean;
    error: boolean;
  };

  // Remote Webhook
  webhookEnabled: boolean;
  webhookUrl: string;
  webhookMention: string;
  webhookEvents: {
    done: boolean;
    error: boolean;
    approval: boolean;
  };

  // Discord RPC
  discordRpcEnabled: boolean;
  discordPrivacy: 'default' | 'transparent' | 'project_only' | 'stealth';
  discordTime: boolean;
  discordParty: boolean;
  discordButtons: boolean;
  discordRpcStatusText: string;
  discordRpcConnected: boolean;

  // Pocket Commander
  pocketEnabled: boolean;
  pocketPlatform: 'telegram' | 'discord' | 'both';
  telegramToken: string;
  telegramPin: string;
  telegramPairedUser: string | null;
  discordToken: string;
  discordPin: string;
  discordPairedUser: string | null;
  pocketFeatures: {
    allowRun: boolean;
    allowActive: boolean;
    allowPrompts: boolean;
    allowScreen: boolean;
    allowDiff: boolean;
    allowCancel: boolean;
  };
  pocketNotifications: {
    notifyDone: boolean;
    notifyError: boolean;
    notifyApproval: boolean;
  };

  // Security & Health Doctor
  diagnostics: DiagnosticItem[];
  diagnosticsScanning: boolean;
  diagnosticsScore: string;
  securityGatekeeperEnabled: boolean;
  securityAuditResults: any;

  // Live Logs
  logs: LogEntry[];
  logsFilter: string;
  logsSearch: string;
  autoScroll: boolean;

  // Update Management
  updateStatus: 'idle' | 'checking' | 'available' | 'uptodate' | 'error' | 'downloading' | 'downloaded';
  updateStatusText: string;
  updateStatusSubtext: string;
  updateInfo: {
    isUpdateAvailable: boolean;
    latestVersion: string;
    currentVersion: string;
    releaseName?: string;
    releaseNotes?: string;
    downloadUrl?: string;
    assetSizeBytes?: number;
    publishedAtUtc?: string;
  } | null;
  downloadProgress: number;
  downloadProgressLabel: string;

  // Custom AI CLI Engines
  customClis: Array<{
    id: string;
    name: string;
    executablePath: string;
    arguments: string;
    workingDirectory?: string;
    colorTag?: string;
  }>;
  customClisLoading: boolean;

  // MCP Servers & Capabilities
  mcpServers: Array<{
    id: string;
    name: string;
    command?: string;
    args?: string[];
    env?: Record<string, string>;
    scope?: string;
    isEnabled?: boolean;
    description?: string;
    toolCount?: number;
    toolsCount?: number;
    requiresAuth?: boolean;
    authStatus?: string;
    authDetails?: string;
    authKeyName?: string;
    status?: string;
  }>;
  agentSkills: Array<{
    name: string;
    description: string;
    scope: 'project' | 'builtin';
    path?: string;
    tokenCount?: number;
  }>;
  agentRules: Array<{
    id: string;
    name: string;
    description: string;
    scope: 'Project' | 'Global';
    sourceFilePath: string;
    isEnabled: boolean;
    isExcludedByIsolation?: boolean;
    tokenFootprintApprox?: number;
  }>;
  ruleIsolation: boolean;
  rulesActiveWorkspaceName: string;

  // Agent Sessions & Trajectory Inspector
  agentSessions: Array<{
    sessionId: string;
    workspaceDir: string;
    transcriptPath: string;
    stepCount: number;
    createdAt?: string;
    status?: string;
  }>;
  selectedSession: any | null;
  selectedSessionSteps: Array<{
    stepIndex: number;
    type?: string;
    source?: string;
    content?: string;
    thinking?: string;
    timestamp?: string;
    status?: string;
    toolCalls?: Array<{ toolName: string; argumentsJson?: string }>;
  }>;
  loadingSessionSteps: boolean;

  // Automations
  automations: Array<{
    id: string;
    name: string;
    targetCli: string;
    scheduleExpression: string;
    promptToExecute: string;
    isEnabled: boolean;
    lastRunUtc?: string;
    lastRunStatus?: string;
    nextDueUtc?: string;
  }>;

  // Actions
  setActiveSettingsCategory: (cat: string) => void;
  setSearchQuery: (query: string) => void;
  updateSetting: <K extends keyof AppSettings>(key: K, value: AppSettings[K]) => void;
  /** Reads the preferences the host actually stored. Safe to call from several places. */
  loadGeneralSettings: () => Promise<void>;
  saveGeneralSettings: () => Promise<void>;
  
  // Dependencies actions
  loadDependencies: () => Promise<void>;
  runDependencyInstall: (id: string) => Promise<void>;
  loadEngineHealth: () => Promise<void>;
  noteInstallLine: (id: string, line: string) => void;
  finishInstall: (id: string, success: boolean, message: string) => void;
  installAllDependencies: () => Promise<void>;

  // Soundpack actions
  loadSoundpackConfig: () => Promise<void>;
  saveSoundpackConfig: (overrides?: Partial<any>) => Promise<void>;
  playSoundEvent: (eventName: string) => Promise<void>;

  // Webhook actions
  loadWebhookConfig: () => Promise<void>;
  saveWebhookConfig: () => Promise<void>;
  testWebhook: () => Promise<void>;

  // Discord RPC actions
  loadDiscordRpcStatus: () => Promise<void>;
  saveDiscordRpcSettings: () => Promise<void>;
  toggleDiscordRpc: () => Promise<void>;
  refreshDiscordRpc: () => Promise<void>;

  // Pocket Commander actions
  loadPocketCommanderConfig: () => Promise<void>;
  savePocketCommanderConfig: () => Promise<void>;
  generatePocketPin: (platform: 'telegram' | 'discord') => Promise<void>;
  unpairPocket: (platform: 'telegram' | 'discord' | 'all') => Promise<void>;
  testPocketBot: () => Promise<void>;

  // Diagnostics & Security actions
  runDiagnostics: () => Promise<void>;
  autoFixDiagnostic: (id: string) => Promise<void>;
  runSecurityAudit: () => Promise<void>;

  // Logs actions
  setLogsFilter: (f: string) => void;
  setLogsSearch: (s: string) => void;
  setAutoScroll: (scroll: boolean) => void;
  clearLogs: () => void;
  addLogEntry: (entry: Omit<LogEntry, 'id'>) => void;

  // Updates
  checkForUpdates: (manual?: boolean) => Promise<any>;
  installUpdate: (downloadUrl?: string) => Promise<void>;

  // Custom CLIs
  loadCustomClis: () => Promise<void>;
  saveCustomCli: (cli: any) => Promise<void>;
  deleteCustomCli: (id: string) => Promise<void>;
  launchCustomCli: (id: string) => Promise<void>;

  // MCP & Skills & Rules
  loadMcpAndSkills: () => Promise<void>;
  saveMcpServer: (server: any) => Promise<void>;
  deleteMcpServer: (id: string) => Promise<void>;
  testMcpAuth: (serverName: string, keyValue: string) => Promise<{ valid: boolean; message: string }>;
  saveMcpAuth: (serverName: string, keyName: string, keyValue: string) => Promise<{ success: boolean; message?: string }>;
  loadAgentRules: () => Promise<void>;
  saveAgentRule: (rule: any) => Promise<void>;
  toggleAgentRule: (id: string, enabled: boolean) => Promise<void>;
  setRuleIsolation: (isolated: boolean) => Promise<void>;
  deleteAgentRule: (id: string) => Promise<void>;

  // Sessions
  loadAgentSessions: () => Promise<void>;
  selectAgentSession: (session: any) => Promise<void>;
  deleteAgentSession: (transcriptPath: string) => Promise<void>;

  // Automations
  loadAutomations: () => Promise<void>;
  saveAutomation: (task: any) => Promise<void>;
  deleteAutomation: (id: string) => Promise<void>;
  runAutomationNow: (id: string) => Promise<void>;
}

const defaultSettings: AppSettings = {
  autostart: true,
  startMinimized: false,
  minimizeToTray: false,
  notifications: true,
  enableArchitectureGraph: false,
  globalHotkey: true,
  globalHotkeyModifier: 'Ctrl + Alt',
  globalHotkeyKey: 'H',
  globalHudHotkey: false,
  hudHotkeyModifier: 'None (Key only)',
  hudHotkeyKey: 'F12',
  hudHotkeyTarget: 'Slide-Down HUD Overlay',
  maskShortcutPaths: true,
  embedShellsInTerminalWorkspace: true,
  hideMainOnCliOpen: false,
  enableHardwareMonitor: false,
  theme: 'dark',
  accentColor: '#4049B9',
};

// Deliberately empty. This used to hold a hardcoded sample list, which is why removed tools kept
// appearing in the panel and why installed ones showed invented version numbers. The real scan
// fills it; until it answers, the panel says nothing rather than something false.
const defaultDependencies: DependencyItem[] = [];

// Deliberately empty: the panel shows nothing until a real scan returns.
const defaultDiagnostics: DiagnosticItem[] = [];

// Module-level so the debounce survives re-renders.
let autoSaveTimer: number | null = null;

/**
 * The one in-flight (or finished) read of the host's preferences, and whether it succeeded.
 *
 * The store used to start from hardcoded defaults and only read the host when Settings was opened, so
 * anything that saved before that — the theme store, a toggle on a page that was already open — posted
 * the defaults over the stored values. One shared promise also means two views asking cannot fire two
 * requests.
 */
let settingsLoad: Promise<void> | null = null;
let settingsLoaded = false;

/** Keys the person already changed in this run, which the host's older value must not overwrite. */
const settingsTouched = new Set<string>();

export const useSettingsStore = create<SettingsStoreState>((set, get) => ({
  settings: defaultSettings,
  activeSettingsCategory: 'appearance',
  searchQuery: '',
  saveFeedback: null,

  dependencies: defaultDependencies,
  engineHealth: [],
  engineHealthLoading: false,
  installingId: null,
  installLines: {},
  dependenciesLoading: false,

  soundpackEnabled: false,
  soundpackTheme: 'modern',
  soundpackVolume: 70,
  soundpackEvents: {
    taskDone: true,
    approval: true,
    checkpoint: true,
    error: true,
  },

  webhookEnabled: false,
  webhookUrl: '',
  webhookMention: '',
  webhookEvents: {
    done: true,
    error: true,
    approval: true,
  },

  discordRpcEnabled: true,
  discordPrivacy: 'default',
  discordTime: true,
  discordParty: true,
  discordButtons: true,
  discordRpcStatusText: 'Active',
  discordRpcConnected: true,

  pocketEnabled: false,
  pocketPlatform: 'telegram',
  telegramToken: '',
  telegramPin: '---',
  telegramPairedUser: null,
  discordToken: '',
  discordPin: '---',
  discordPairedUser: null,
  pocketFeatures: {
    allowRun: true,
    allowActive: true,
    allowPrompts: true,
    allowScreen: true,
    allowDiff: true,
    allowCancel: true,
  },
  pocketNotifications: {
    notifyDone: true,
    notifyError: true,
    notifyApproval: true,
  },

  diagnostics: defaultDiagnostics,
  diagnosticsScanning: false,
  diagnosticsScore: '',
  securityGatekeeperEnabled: true,
  securityAuditResults: null,

  // Starts empty and fills from real events: the host's live_log_entry stream plus the front-end
  // capture in services/uiLogger.ts.
  logs: [],
  logsFilter: 'all',
  logsSearch: '',
  autoScroll: true,

  // Updates Initial State
  updateStatus: 'idle',
  updateStatusText: 'Check for updates',
  updateStatusSubtext: 'Target: github.com/ZerroDevs/AgentsHub',
  updateInfo: null,
  downloadProgress: 0,
  downloadProgressLabel: 'Downloading installer...',

  // Custom CLIs Initial State
  customClis: [
    { id: 'aider', name: 'Aider AI Pair Programmer', executablePath: 'aider', arguments: '--gui', colorTag: '#38bdf8' },
    { id: 'goose', name: 'Goose Autonomous Agent', executablePath: 'goose', arguments: 'session', colorTag: '#a855f7' },
    { id: 'copilot', name: 'GitHub Copilot CLI', executablePath: 'gh', arguments: 'copilot', colorTag: '#10b981' },
    { id: 'gemini', name: 'Gemini CLI Developer', executablePath: 'gemini', arguments: 'run', colorTag: '#06b6d4' },
  ],
  customClisLoading: false,

  // MCP Servers & Capabilities (dynamic from backend)
  mcpServers: [],
  agentSkills: [],
  agentRules: [],
  ruleIsolation: false,
  rulesActiveWorkspaceName: '',

  agentSessions: [],
  selectedSession: null,
  selectedSessionSteps: [],
  loadingSessionSteps: false,

  automations: [
    { id: 'auto-1', name: 'Daily Repository Health Check', targetCli: 'AntigravityCli', scheduleExpression: '0 9 * * *', promptToExecute: 'Run git status, scan for security issues, and check dependencies.', isEnabled: true, lastRunStatus: 'Success', lastRunUtc: new Date(Date.now() - 3600000).toISOString() },
    { id: 'auto-2', name: 'Hourly Test Suite Runner', targetCli: 'Hub', scheduleExpression: '0 * * * *', promptToExecute: 'Run unit test suite and report failing tests.', isEnabled: false, lastRunStatus: 'Pending' },
  ],

  setActiveSettingsCategory: cat => set({ activeSettingsCategory: cat }),
  setSearchQuery: query => set({ searchQuery: query }),

  updateSetting: (key, value) => {
    settingsTouched.add(key);

    set(state => {
      const updated = { ...state.settings, [key]: value };
      return { settings: updated };
    });

    // Auto-save: a toggle used to sit in memory until the Save button was pressed, so anything the
    // user did not explicitly save was silently lost on the next launch. Debounced so dragging a
    // slider or typing in a field does not fire a write per keystroke.
    if (autoSaveTimer !== null) window.clearTimeout(autoSaveTimer);
    autoSaveTimer = window.setTimeout(() => {
      autoSaveTimer = null;
      void get().saveGeneralSettings();
    }, 600);
  },

  loadGeneralSettings: async () => {
    settingsLoad ??= (async () => {
      try {
        const res: any = await webviewBridge.invoke('get_initial_state');
        if (res?.settings) {
          // A change made while this request was in flight keeps its value: the answer describes the
          // host as it was before that click.
          const patch = Object.fromEntries(
            Object.entries(res.settings).filter(([key]) => !settingsTouched.has(key))
          );

          set(state => ({ settings: { ...state.settings, ...patch } }));
          settingsLoaded = true;
        }
      } catch {
        // Keep the defaults rather than blanking the page if the host cannot answer, and let the next
        // call try again instead of caching the failure.
        settingsLoad = null;
      }
    })();

    await settingsLoad;
  },

  saveGeneralSettings: async () => {
    // A save before the host has been read would post this page's defaults over every stored
    // preference, which is how flipping one switch used to reset the rest.
    if (!settingsLoaded) await get().loadGeneralSettings();

    try {
      // The bridge action is `save_general_settings`; `save_settings` was never implemented, so every
      // save failed and the catch reported success anyway — preferences reset on the next launch.
      await webviewBridge.invoke('save_general_settings', { settings: get().settings });
      set({ saveFeedback: 'Preferences saved successfully!' });
    } catch (err) {
      set({ saveFeedback: `Could not save preferences: ${err instanceof Error ? err.message : 'unknown error'}` });
    }
    setTimeout(() => set({ saveFeedback: null }), 4000);
  },

  loadDependencies: async () => {
    set({ dependenciesLoading: true });
    try {
      const res = await webviewBridge.invoke('get_dependencies_status');
      if (res && res.dependencies) {
        set({ dependencies: res.dependencies });
      }
    } catch {}
    set({ dependenciesLoading: false });
  },

  loadEngineHealth: async () => {
    set({ engineHealthLoading: true });
    try {
      const res = await webviewBridge.invoke('engine_health');
      if (res && Array.isArray(res.engines)) {
        set({ engineHealth: res.engines });
      }
    } catch {}
    set({ engineHealthLoading: false });
  },

  runDependencyInstall: async (id: string) => {
    // The installer streams for minutes, so the row has to show that it started. Before this, the
    // button looked inert: the call worked and nothing on screen ever changed.
    set({ installingId: id, installLines: { ...get().installLines, [id]: 'Starting...' } });

    // A safety net: the completion event normally clears this, but a crashed installer would leave
    // the button disabled forever.
    window.setTimeout(() => {
      if (get().installingId === id) {
        set({ installingId: null, installLines: { ...get().installLines, [id]: 'No response from the installer.' } });
      }
    }, 180000);

    try {
      await webviewBridge.invoke('run_dependency_install', { id });
    } catch (err) {
      console.warn('Failed to run dependency install:', err);
      set({
        installingId: null,
        installLines: { ...get().installLines, [id]: 'Could not start the installer.' },
      });
    }
  },

  noteInstallLine: (id: string, line: string) => {
    set({ installLines: { ...get().installLines, [id]: line } });
  },

  finishInstall: (id: string, success: boolean, message: string) => {
    set({
      installingId: get().installingId === id ? null : get().installingId,
      installLines: { ...get().installLines, [id]: success ? message || 'Done.' : message || 'Failed.' },
    });
    get().loadDependencies();
  },

  installAllDependencies: async () => {
    try {
      await webviewBridge.invoke('install_all_dependencies');
    } catch (err) {
      console.warn('Failed to install all dependencies:', err);
    }
  },

  loadSoundpackConfig: async () => {
    try {
      const res = await webviewBridge.invoke('get_soundpack_config');
      if (res) {
        set({
          soundpackEnabled: res.isEnabled ?? res.IsEnabled ?? false,
          soundpackTheme: res.theme || res.Theme || 'modern',
          soundpackVolume: res.volume ?? res.Volume ?? 70,
          // The host stores each trigger as its own flat flag, so they have to be mapped back
          // individually. Reading only the three fields above is what made the checkboxes reset.
          soundpackEvents: {
            taskDone: res.notifyTaskDone ?? res.NotifyTaskDone ?? true,
            approval: res.notifyApproval ?? res.NotifyApproval ?? true,
            checkpoint: res.notifyCheckpoint ?? res.NotifyCheckpoint ?? true,
            error: res.notifyError ?? res.NotifyError ?? true,
          },
        });
      }
    } catch {}
  },

  saveSoundpackConfig: async (overrides = {}) => {
    const { soundpackEnabled, soundpackTheme, soundpackVolume, soundpackEvents } = get();
    const events = overrides.soundpackEvents ?? soundpackEvents;
    const cfg = {
      isEnabled: overrides.soundpackEnabled ?? soundpackEnabled,
      theme: overrides.soundpackTheme ?? soundpackTheme,
      volume: overrides.soundpackVolume ?? soundpackVolume,
      notifyTaskDone: events.taskDone,
      notifyApproval: events.approval,
      notifyCheckpoint: events.checkpoint,
      notifyError: events.error,
    };
    try {
      await webviewBridge.invoke('save_soundpack_config', { config: cfg });
      set({
        soundpackEnabled: cfg.isEnabled,
        soundpackTheme: cfg.theme,
        soundpackVolume: cfg.volume,
        soundpackEvents: events,
      });
    } catch (err) {
      console.warn('Failed to save soundpack config:', err);
    }
  },

  playSoundEvent: async (eventName: string) => {
    const { soundpackTheme, soundpackVolume } = get();
    try {
      await webviewBridge.invoke('play_sound_event', {
        eventName,
        theme: soundpackTheme,
        volume: soundpackVolume,
        force: true,
      });
    } catch (err) {
      console.warn('Failed to play sound event:', err);
    }
  },

  loadWebhookConfig: async () => {
    try {
      const res = await webviewBridge.invoke('get_remote_webhook_config');
      if (res) {
        set({
          webhookEnabled: res.isEnabled ?? res.IsEnabled ?? false,
          webhookUrl: res.webhookUrl || res.WebhookUrl || '',
          webhookMention: res.mentionRole || res.MentionRole || '',
        });
      }
    } catch {}
  },

  saveWebhookConfig: async () => {
    const { webhookEnabled, webhookUrl, webhookMention, webhookEvents } = get();
    try {
      await webviewBridge.invoke('save_remote_webhook_config', {
        config: {
          isEnabled: webhookEnabled,
          webhookUrl,
          mentionRole: webhookMention,
          notifyDone: webhookEvents.done,
          notifyError: webhookEvents.error,
          notifyApproval: webhookEvents.approval,
        },
      });
    } catch (err) {
      console.warn('Failed to save webhook config:', err);
    }
  },

  testWebhook: async () => {
    try {
      await webviewBridge.invoke('test_remote_webhook', {
        title: 'AgentsHub Test Alert',
        description: 'Webhook integration successfully configured and validated.',
      });
    } catch (err) {
      console.warn('Failed to test webhook:', err);
    }
  },

  loadDiscordRpcStatus: async () => {
    try {
      const res = await webviewBridge.invoke('get_discord_rpc_status');
      if (res) {
        set({
          discordRpcConnected: !!res.isConnected,
          discordRpcStatusText: res.isConnected ? 'Connected' : 'Connecting...',
          discordRpcEnabled: res.settings?.enabled ?? true,
          discordPrivacy: res.settings?.privacyPreset || 'default',
        });
      }
    } catch {}
  },

  saveDiscordRpcSettings: async () => {
    const { discordRpcEnabled, discordPrivacy, discordTime, discordParty, discordButtons } = get();
    try {
      await webviewBridge.invoke('save_discord_rpc_settings', {
        settings: {
          enabled: discordRpcEnabled,
          privacyPreset: discordPrivacy,
          showElapsedTime: discordTime,
          showPartySize: discordParty,
          showActionButtons: discordButtons,
        },
      });
    } catch (err) {
      console.warn('Failed to save Discord RPC settings:', err);
    }
  },

  toggleDiscordRpc: async () => {
    const next = !get().discordRpcEnabled;
    set({ discordRpcEnabled: next });
    try {
      await webviewBridge.invoke('toggle_discord_rpc', { enabled: next });
    } catch {}
  },

  loadPocketCommanderConfig: async () => {
    try {
      const res = await webviewBridge.invoke('get_pocket_commander_config');
      if (res) {
        set({
          pocketEnabled: res.isEnabled ?? res.IsEnabled ?? false,
          pocketPlatform: res.platform || res.Platform || 'telegram',
          telegramToken: res.telegramBotToken || res.TelegramBotToken || '',
          telegramPin: res.telegramPairingPin || res.TelegramPairingPin || '382914',
          telegramPairedUser: res.telegramChatId ? `@user_${res.telegramChatId}` : null,
          discordToken: res.discordBotToken || res.DiscordBotToken || '',
          discordPin: res.discordPairingPin || res.DiscordPairingPin || '849102',
          discordPairedUser: res.discordUserId ? `@user_${res.discordUserId}` : null,
        });
      }
    } catch {}
  },

  savePocketCommanderConfig: async () => {
    const {
      pocketEnabled,
      pocketPlatform,
      telegramToken,
      discordToken,
      pocketFeatures,
      pocketNotifications,
    } = get();
    try {
      await webviewBridge.invoke('save_pocket_commander_config', {
        config: {
          isEnabled: pocketEnabled,
          platform: pocketPlatform,
          telegramBotToken: telegramToken,
          discordBotToken: discordToken,
          allowRun: pocketFeatures.allowRun,
          allowActive: pocketFeatures.allowActive,
          allowPrompts: pocketFeatures.allowPrompts,
          allowScreen: pocketFeatures.allowScreen,
          allowDiff: pocketFeatures.allowDiff,
          allowCancel: pocketFeatures.allowCancel,
          notifyDone: pocketNotifications.notifyDone,
          notifyError: pocketNotifications.notifyError,
          notifyApproval: pocketNotifications.notifyApproval,
        },
      });
    } catch (err) {
      console.warn('Failed to save pocket commander config:', err);
    }
  },

  generatePocketPin: async (platform: 'telegram' | 'discord') => {
    try {
      const res = await webviewBridge.invoke('generate_pocket_pairing_pin', { platform });
      if (res && res.pin) {
        if (platform === 'telegram') set({ telegramPin: res.pin });
        else set({ discordPin: res.pin });
      }
    } catch {
      const randomPin = Math.floor(100000 + Math.random() * 900000).toString();
      if (platform === 'telegram') set({ telegramPin: randomPin });
      else set({ discordPin: randomPin });
    }
  },

  unpairPocket: async (platform: 'telegram' | 'discord' | 'all') => {
    try {
      await webviewBridge.invoke('unpair_pocket_commander', { platform });
      if (platform === 'telegram' || platform === 'all') set({ telegramPairedUser: null });
      if (platform === 'discord' || platform === 'all') set({ discordPairedUser: null });
    } catch {}
  },

  testPocketBot: async () => {
    const { pocketPlatform, telegramToken, discordToken } = get();
    const token = pocketPlatform === 'discord' ? discordToken : telegramToken;
    try {
      await webviewBridge.invoke('test_pocket_commander_token', { token, platform: pocketPlatform });
    } catch (err) {
      console.warn('Failed to test pocket bot token:', err);
    }
  },

  runDiagnostics: async () => {
    set({ diagnosticsScanning: true });
    try {
      const res = await webviewBridge.invoke('run_system_diagnostics');
      if (res && Array.isArray(res.diagnostics)) {
        const items: DiagnosticItem[] = res.diagnostics;
        const passed = items.filter(item => item.status === 'Pass').length;
        const percent = items.length > 0 ? Math.round((passed / items.length) * 100) : 0;
        const verdict = percent >= 90 ? 'Healthy' : percent >= 70 ? 'Warning' : 'Critical';

        set({
          diagnostics: items,
          diagnosticsScore: items.length > 0 ? `Score: ${percent}% (${verdict})` : '',
        });
      }
    } catch {}
    set({ diagnosticsScanning: false });
  },

  autoFixDiagnostic: async (id: string) => {
    try {
      await webviewBridge.invoke('autofix_diagnostic_issue', { issueId: id });
    } catch (err) {
      // Surfaced rather than swallowed: a cancelled UAC prompt used to look like nothing happened.
      set({
        saveFeedback: `Fix failed: ${err instanceof Error ? err.message : 'the host refused the change'}`,
      });
      setTimeout(() => set({ saveFeedback: null }), 4000);
      return;
    }
    await get().runDiagnostics();
  },

  runSecurityAudit: async () => {
    try {
      const res = await webviewBridge.invoke('audit_workspace_security');
      if (res) {
        set({ securityAuditResults: res });
      }
    } catch (err) {
      console.warn('Failed to run security audit:', err);
    }
  },

  setLogsFilter: filter => set({ logsFilter: filter }),
  setLogsSearch: search => set({ logsSearch: search }),
  setAutoScroll: scroll => set({ autoScroll: scroll }),

  clearLogs: () => set({ logs: [] }),

  addLogEntry: entry => {
    set(state => {
      const next = [...state.logs, { ...entry, id: Math.random().toString(36).substring(7) }];
      // An append-only console is a leak in a session that runs for hours; the oldest lines go
      // first and the file on disk keeps the full history.
      if (next.length > 500) next.splice(0, next.length - 500);
      return { logs: next };
    });
  },

  refreshDiscordRpc: async () => {
    try {
      await webviewBridge.invoke('test_discord_rpc');
      get().loadDiscordRpcStatus();
    } catch {}
  },

  checkForUpdates: async (manual = true) => {
    set({
      updateStatus: 'checking',
      updateStatusText: 'Checking GitHub for releases...',
      updateStatusSubtext: 'Target: github.com/ZerroDevs/AgentsHub',
    });

    try {
      const res = await webviewBridge.invoke('check_for_updates');
      if (!res) {
        set({
          updateStatus: 'error',
          updateStatusText: 'Failed to check GitHub releases',
          updateStatusSubtext: 'Check your network connection and retry',
        });
        return null;
      }

      const isAvail = !!(res.isUpdateAvailable ?? res.IsUpdateAvailable);
      const latestVer = (res.latestVersion || res.LatestVersion || '').replace(/^v/i, '');
      const currentVer = (res.currentVersion || res.CurrentVersion || '1.0.5').replace(/^v/i, '');
      const resultKind = res.result ?? res.Result;

      const info = {
        isUpdateAvailable: isAvail,
        latestVersion: latestVer,
        currentVersion: currentVer,
        releaseName: res.releaseName || res.ReleaseName || `Release v${latestVer}`,
        releaseNotes: res.releaseNotes || res.ReleaseNotes || 'No release notes provided.',
        downloadUrl: res.downloadUrl || res.DownloadUrl,
        assetSizeBytes: res.assetSizeBytes || res.AssetSizeBytes,
        publishedAtUtc: res.publishedAtUtc || res.PublishedAtUtc,
      };

      if (isAvail) {
        set({
          updateStatus: 'available',
          updateStatusText: `Update Available: v${latestVer}`,
          updateStatusSubtext: `Current: v${currentVer} · Latest: v${latestVer}`,
          updateInfo: info,
        });
      } else if (resultKind === 'UpToDate' || (latestVer && latestVer === currentVer) || !isAvail) {
        set({
          updateStatus: 'uptodate',
          updateStatusText: 'Agents Hub is up to date',
          updateStatusSubtext: `Version v${currentVer} · Checked just now`,
          updateInfo: info,
        });
      }

      return info;
    } catch (err: any) {
      set({
        updateStatus: 'error',
        updateStatusText: 'Update check error',
        updateStatusSubtext: err?.message || 'Network request failed',
      });
      return null;
    }
  },

  installUpdate: async (downloadUrl?: string) => {
    const url = downloadUrl || get().updateInfo?.downloadUrl;
    if (!url) return;

    set({
      updateStatus: 'downloading',
      downloadProgress: 0,
      downloadProgressLabel: 'Downloading installer...',
    });

    try {
      await webviewBridge.invoke('install_update', { downloadUrl: url });
    } catch (err: any) {
      set({
        updateStatus: 'error',
        updateStatusText: 'Update download failed',
        updateStatusSubtext: err?.message || 'Download error',
      });
    }
  },

  // Custom CLIs
  loadCustomClis: async () => {
    set({ customClisLoading: true });
    try {
      const res = await webviewBridge.invoke('get_custom_clis');
      if (res && res.clis) {
        set({ customClis: res.clis });
      }
    } catch {}
    set({ customClisLoading: false });
  },

  saveCustomCli: async (cli: any) => {
    try {
      await webviewBridge.invoke('save_custom_cli', { cli });
      get().loadCustomClis();
    } catch (err) {
      console.warn('Failed to save custom cli:', err);
    }
  },

  deleteCustomCli: async (id: string) => {
    try {
      await webviewBridge.invoke('delete_custom_cli', { id });
      get().loadCustomClis();
    } catch (err) {
      console.warn('Failed to delete custom cli:', err);
    }
  },

  launchCustomCli: async (id: string) => {
    try {
      await webviewBridge.invoke('start_cli', { cli: id });
    } catch (err) {
      console.warn('Failed to launch custom cli:', err);
    }
  },

  // MCP & Skills
  loadMcpAndSkills: async () => {
    try {
      const res = await webviewBridge.invoke('get_mcp_and_skills');
      if (res) {
        if (res.servers) set({ mcpServers: res.servers });
        if (res.skills) set({ agentSkills: res.skills });
      }
    } catch {}
    get().loadAgentRules();
  },

  saveMcpServer: async (server: any) => {
    try {
      await webviewBridge.invoke('save_mcp_server', { server });
      get().loadMcpAndSkills();
    } catch (err) {
      console.warn('Failed to save mcp server:', err);
    }
  },

  deleteMcpServer: async (id: string) => {
    try {
      await webviewBridge.invoke('delete_mcp_server', { id });
      get().loadMcpAndSkills();
    } catch (err) {
      console.warn('Failed to delete mcp server:', err);
    }
  },

  testMcpAuth: async (serverName: string, keyValue: string) => {
    try {
      const res = await webviewBridge.invoke('test_mcp_auth', { serverName, keyValue });
      return res || { valid: false, message: 'No response from bridge' };
    } catch (err: any) {
      return { valid: false, message: err?.message || 'Error executing auth test' };
    }
  },

  saveMcpAuth: async (serverName: string, keyName: string, keyValue: string) => {
    try {
      const res = await webviewBridge.invoke('save_mcp_auth', { serverName, keyName, keyValue });
      await get().loadMcpAndSkills();
      return res || { success: true };
    } catch (err: any) {
      return { success: false, message: err?.message || 'Error saving auth key' };
    }
  },

  // Agent Rules
  loadAgentRules: async () => {
    try {
      const res = await webviewBridge.invoke('get_agent_rules');
      if (res) {
        if (res.rules) set({ agentRules: res.rules });
        if (res.activeWorkspaceName) set({ rulesActiveWorkspaceName: res.activeWorkspaceName });
        if (res.isIsolated !== undefined) set({ ruleIsolation: res.isIsolated });
      }
    } catch {}
  },

  saveAgentRule: async (rule: any) => {
    try {
      await webviewBridge.invoke('save_agent_rule', { rule });
      get().loadAgentRules();
    } catch (err) {
      console.warn('Failed to save agent rule:', err);
    }
  },

  toggleAgentRule: async (id: string, enabled: boolean) => {
    try {
      await webviewBridge.invoke('toggle_agent_rule', { id, enabled });
      set(state => ({
        agentRules: state.agentRules.map(r => r.id === id ? { ...r, isEnabled: enabled } : r),
      }));
    } catch {}
  },

  setRuleIsolation: async (isolated: boolean) => {
    try {
      await webviewBridge.invoke('set_workspace_rule_isolation', { isIsolated: isolated });
      set({ ruleIsolation: isolated });
      get().loadAgentRules();
    } catch {}
  },

  deleteAgentRule: async (id: string) => {
    try {
      await webviewBridge.invoke('delete_agent_rule', { id });
      get().loadAgentRules();
    } catch {}
  },

  // Agent Sessions
  loadAgentSessions: async () => {
    try {
      const res = await webviewBridge.invoke('get_agent_sessions');
      if (res && res.sessions) {
        set({ agentSessions: res.sessions });
      }
    } catch {}
  },

  selectAgentSession: async (session: any) => {
    set({ selectedSession: session, loadingSessionSteps: true, selectedSessionSteps: [] });
    try {
      const res = await webviewBridge.invoke('get_transcript_steps', { transcriptPath: session.transcriptPath });
      if (res && res.steps) {
        set({ selectedSessionSteps: res.steps });
      }
    } catch {}
    set({ loadingSessionSteps: false });
  },

  deleteAgentSession: async (transcriptPath: string) => {
    try {
      await webviewBridge.invoke('delete_agent_session', { transcriptPath });
      set(state => ({
        agentSessions: state.agentSessions.filter(s => s.transcriptPath !== transcriptPath),
        selectedSession: state.selectedSession?.transcriptPath === transcriptPath ? null : state.selectedSession,
      }));
    } catch {}
  },

  // Automations
  loadAutomations: async () => {
    try {
      const res = await webviewBridge.invoke('get_scheduled_automations');
      if (res && res.tasks) {
        set({ automations: res.tasks });
      }
    } catch {}
  },

  saveAutomation: async (task: any) => {
    try {
      await webviewBridge.invoke('save_scheduled_automation', { task });
      get().loadAutomations();
    } catch (err) {
      console.warn('Failed to save automation:', err);
    }
  },

  deleteAutomation: async (id: string) => {
    try {
      await webviewBridge.invoke('delete_scheduled_automation', { id });
      get().loadAutomations();
    } catch (err) {
      console.warn('Failed to delete automation:', err);
    }
  },

  runAutomationNow: async (id: string) => {
    try {
      await webviewBridge.invoke('run_scheduled_automation_now', { id });
    } catch (err) {
      console.warn('Failed to run automation:', err);
    }
  },
}));

// Set up event listeners for updates on webviewBridge
if (typeof window !== 'undefined') {
  webviewBridge.on('update_available', (payload: any) => {
    if (payload) {
      const latestVer = (payload.latestVersion || payload.LatestVersion || '').replace(/^v/i, '');
      const currentVer = (payload.currentVersion || payload.CurrentVersion || '1.0.5').replace(/^v/i, '');
      useSettingsStore.setState({
        updateStatus: 'available',
        updateStatusText: `Update Available: v${latestVer}`,
        updateStatusSubtext: `Current: v${currentVer} · Latest: v${latestVer}`,
        updateInfo: {
          isUpdateAvailable: true,
          latestVersion: latestVer,
          currentVersion: currentVer,
          releaseName: payload.releaseName || payload.ReleaseName || `Release v${latestVer}`,
          releaseNotes: payload.releaseNotes || payload.ReleaseNotes || '',
          downloadUrl: payload.downloadUrl || payload.DownloadUrl,
        },
      });
    }
  });

  webviewBridge.on('update_download_progress', (payload: any) => {
    const progress = payload?.progress ?? 0;
    const pct = Math.round(progress * 100);
    useSettingsStore.setState({
      updateStatus: 'downloading',
      downloadProgress: pct,
      downloadProgressLabel: pct >= 100 ? 'Download complete. Launching installer...' : `Downloading update... (${pct}%)`,
    });
  });

  webviewBridge.on('update_download_failed', (payload: any) => {
    useSettingsStore.setState({
      updateStatus: 'error',
      updateStatusText: 'Update download failed',
      updateStatusSubtext: payload?.error || 'Network error occurred during download',
    });
  });
}
