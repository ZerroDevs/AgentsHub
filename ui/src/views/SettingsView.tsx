import React, { useState, useEffect, useRef } from 'react';
import {
  Palette,
  Sliders,
  Cpu,
  Boxes,
  Share2,
  ShieldCheck,
  Wifi,
  Wrench,
  Terminal,
  Info,
  Search,
  X,
  Moon,
  Sun,
  Check,
  RefreshCw,
  ExternalLink,
  Bell,
  Play,
  RotateCcw,
  Copy,
  Plus,
  Trash2,
  Send,
  MessageSquare,
  Radio,
  Lock,
  ChevronDown,
  Clock,
  Calendar,
  Layers,
  Activity,
  Download,
  HelpCircle,
  Folder,
  Power,
  Filter,
  KeyRound,
  Eye,
  EyeOff,
  Bot,
  Sparkles,
  Code2,
  Package,
  FolderOpen,
} from 'lucide-react';
import { actionIcons, sectionIcons } from '../lib/toolIcons';

/**
 * A mark per agent, so a list of four CLIs is readable at a glance instead of four identical rows.
 * Unknown ids fall back to a package icon rather than rendering nothing.
 */
function dependencyIcon(id: string) {
  const cls = 'w-3.5 h-3.5 flex-shrink-0';

  switch (id) {
    case 'antigravity':
      return <Sparkles className={cls} />;
    case 'claude':
      return <Bot className={cls} />;
    case 'codex':
      return <Code2 className={cls} />;
    case 'opencode':
      return <Terminal className={cls} />;
    default:
      return <Package className={cls} />;
  }
}
import { useSettingsStore } from '../store/useSettingsStore';
import { useThemeStore, PRESET_SWATCHES } from '../store/useThemeStore';
import { useProfileStore } from '../store/useProfileStore';
import { profileLabel } from '../lib/profileName';
import { webviewBridge } from '../services/webviewBridge';
import { useSessionStore } from '../store/useSessionStore';
import { useSidecarStore } from '../store/useSidecarStore';
import { useGuideStore } from '../store/useGuideStore';
import { LanSettings } from '../components/settings/LanSettings';
import { TelemetryPanel } from '../components/settings/TelemetryPanel';
import { DevToolsPanel } from '../components/settings/DevToolsPanel';
import { sendPrompt } from '../services/chatController';
import { Button } from '../components/common/Button';

export const SettingsView: React.FC = () => {
  const { autoRunDefaultProfile, defaultProfileName, setAutoRunDefaultProfile } = useProfileStore();
  // The log console is the only scroll container on this page. Auto-Scroll used to flip a label
  // without touching it, so new lines landed below the fold.
  const logConsoleRef = useRef<HTMLDivElement | null>(null);

  const {
    settings,
    updateSetting,
    saveGeneralSettings,
    searchQuery,
    setSearchQuery,
    saveFeedback,
    dependencies,
    dependenciesLoading,
    engineHealth,
    engineHealthLoading,
    loadEngineHealth,
    installingId,
    installLines,
    noteInstallLine,
    finishInstall,
    loadDependencies,
    runDependencyInstall,
    installAllDependencies,
    soundpackEnabled,
    soundpackTheme,
    soundpackVolume,
    soundpackEvents,
    loadSoundpackConfig,
    saveSoundpackConfig,
    playSoundEvent,
    webhookEnabled,
    webhookUrl,
    webhookMention,
    webhookEvents,
    loadWebhookConfig,
    saveWebhookConfig,
    testWebhook,
    discordRpcEnabled,
    discordPrivacy,
    discordTime,
    discordParty,
    discordButtons,
    discordRpcStatusText,
    discordRpcConnected,
    loadDiscordRpcStatus,
    saveDiscordRpcSettings,
    toggleDiscordRpc,
    refreshDiscordRpc,
    pocketEnabled,
    pocketPlatform,
    telegramToken,
    telegramPin,
    telegramPairedUser,
    discordToken,
    discordPin,
    discordPairedUser,
    pocketFeatures,
    pocketNotifications,
    loadPocketCommanderConfig,
    savePocketCommanderConfig,
    generatePocketPin,
    unpairPocket,
    testPocketBot,
    diagnostics,
    diagnosticsScanning,
    diagnosticsScore,
    runDiagnostics,
    autoFixDiagnostic,
    securityGatekeeperEnabled,
    runSecurityAudit,
    securityAuditResults,
    logs,
    logsFilter,
    logsSearch,
    autoScroll,
    setLogsFilter,
    setLogsSearch,
    setAutoScroll,
    clearLogs,
    updateStatus,
    updateStatusText,
    updateStatusSubtext,
    updateInfo,
    downloadProgress,
    downloadProgressLabel,
    checkForUpdates,
    installUpdate,
    customClis,
    customClisLoading,
    loadCustomClis,
    saveCustomCli,
    deleteCustomCli,
    launchCustomCli,
    mcpServers,
    agentSkills,
    agentRules,
    ruleIsolation,
    rulesActiveWorkspaceName,
    loadMcpAndSkills,
    saveMcpServer,
    deleteMcpServer,
    testMcpAuth,
    saveMcpAuth,
    loadAgentRules,
    saveAgentRule,
    toggleAgentRule,
    setRuleIsolation,
    deleteAgentRule,
    agentSessions,
    selectedSession,
    selectedSessionSteps,
    loadingSessionSteps,
    loadAgentSessions,
    selectAgentSession,
    deleteAgentSession,
    automations,
    loadAutomations,
    saveAutomation,
    deleteAutomation,
    runAutomationNow,
  } = useSettingsStore();

  /** Where the last log bundle was written, so the path is visible rather than assumed. */
  const [bundlePath, setBundlePath] = useState<string | null>(null);

  // The installer streams progress for minutes. Without these two subscriptions the button looked
  // dead: the call worked, the install ran, and the row never changed.
  useEffect(() => {
    const offProgress = webviewBridge.on('dependency_install_progress', (data: any) => {
      if (data?.id) noteInstallLine(String(data.id), String(data.line ?? ''));
    });

    const offComplete = webviewBridge.on('dependency_install_complete', (data: any) => {
      if (data?.id) finishInstall(String(data.id), Boolean(data.success), String(data.message ?? ''));
    });

    return () => {
      offProgress?.();
      offComplete?.();
    };
  }, [noteInstallLine, finishInstall]);

  const {
    theme,
    accentColor,
    themeIntensity,
    backgroundImage,
    backgroundDim,
    setTheme,
    setAccentColor,
    setThemeIntensity,
    setBackgroundImage,
    setBackgroundDim,
  } = useThemeStore();

  const [wallpaperError, setWallpaperError] = useState<string | null>(null);

  /** Asks the host for an image; it copies the file somewhere the webview can read and returns a URL. */
  const chooseImage = async () => {
    setWallpaperError(null);
    try {
      const res: any = await webviewBridge.invoke('browse_theme_image', {});
      if (res?.url) setBackgroundImage(String(res.url));
    } catch (err) {
      // Closing the picker is reported as a failed action, so only a real error is worth showing.
      const message = err instanceof Error ? err.message : String(err);
      if (!/cancel/i.test(message)) setWallpaperError(message);
    }
  };

  const wallpaperName = (url: string) => {
    try {
      return decodeURIComponent(url.split('/').pop() || url);
    } catch {
      return url;
    }
  };

  // The section lives in the shared store so the guide can land directly on the dependency list.
  const activeCategory = useSidecarStore(state => state.settingsCategory);
  const setActiveCategory = useSidecarStore(state => state.setSettingsCategory);
  const openGuide = useGuideStore(state => state.open);
  // Shows what the host actually stored, so a UI/host disagreement is visible instead of guessed.
  const [prefDebug, setPrefDebug] = useState<string | null>(null);

  // The store was never hydrated from the host: `settings` stayed at its hardcoded defaults, so every
  // checkbox showed the default rather than the saved value — the app honoured the registry while the
  // UI displayed the opposite. Load once when Settings opens.
  useEffect(() => {
    void (async () => {
      try {
        const res: any = await webviewBridge.invoke('get_initial_state');
        if (res?.settings) {
          useSettingsStore.setState(state => ({ settings: { ...state.settings, ...res.settings } }));
        }
      } catch {
        // Keep the defaults rather than blanking the page if the host cannot answer.
      }
    })();
  }, []);

  /**
   * Hands the findings to a real agent session instead of inventing a second AI path: the audit is
   * deterministic, the agent is what decides whether a finding matters and what to change.
   */
  const reviewAuditWithAgent = async () => {
    const audit = securityAuditResults;
    if (!audit) return;

    const lines = (audit.issues ?? []).slice(0, 40).map((issue: any) =>
      `- [${issue.severity}] ${issue.type}: ${issue.filePath}:${issue.lineNumber}\n  ${issue.matchSnippet}`
    );

    const prompt = [
      'Review this pre-commit security audit for the current workspace and tell me what actually needs fixing.',
      '',
      `Score: ${audit.score}/100 · ${audit.scannedFilesCount} files scanned · ${audit.issuesCount} issues`,
      '',
      lines.length > 0 ? lines.join('\n') : 'No issues were reported.',
      '',
      'For each real problem: say why it matters, then propose the minimal fix. Ignore anything that is a false positive and say so.',
    ].join('\n');

    const sessions = useSessionStore.getState();
    const id = sessions.createSession();
    sessions.switchSession(id);
    useSidecarStore.getState().setActiveView('chat');
    await sendPrompt(id, prompt);
  };
  const [customHex, setCustomHex] = useState<string>(accentColor);
  const [testHotkeyFeedback, setTestHotkeyFeedback] = useState<string | null>(null);
  const [mcpSubTab, setMcpSubTab] = useState<'mcp' | 'skills' | 'rules' | 'sessions' | 'automations'>('mcp');
  const [updateFeedback, setUpdateFeedback] = useState<string | null>(null);
  const [copiedLog, setCopiedLog] = useState<boolean>(false);

  // Pocket Commander platform active tab
  const [pocketTab, setPocketTab] = useState<'telegram' | 'discord' | 'both'>(pocketPlatform || 'telegram');

  // Skills filter state
  const [skillSearch, setSkillSearch] = useState('');
  const [skillFilter, setSkillFilter] = useState<'all' | 'project' | 'builtin'>('all');

  // Modals state
  const [showAddCliModal, setShowAddCliModal] = useState(false);
  const [newCliName, setNewCliName] = useState('');
  const [newCliExe, setNewCliExe] = useState('');
  const [newCliArgs, setNewCliArgs] = useState('');
  const [newCliDir, setNewCliDir] = useState('');
  const [newCliColor, setNewCliColor] = useState('#38bdf8');

  const [showAddMcpModal, setShowAddMcpModal] = useState(false);
  const [newMcpName, setNewMcpName] = useState('');
  const [newMcpCommand, setNewMcpCommand] = useState('node');
  const [newMcpArgs, setNewMcpArgs] = useState('');
  const [newMcpDesc, setNewMcpDesc] = useState('');

  // MCP Auth Modal state
  const [showMcpAuthModal, setShowMcpAuthModal] = useState(false);
  const [authModalServer, setAuthModalServer] = useState<any>(null);
  const [authKeyValue, setAuthKeyValue] = useState('');
  const [authKeyVisible, setAuthKeyVisible] = useState(false);
  const [authTesting, setAuthTesting] = useState(false);
  const [authSaving, setAuthSaving] = useState(false);
  const [authFeedback, setAuthFeedback] = useState<{ valid?: boolean; message: string } | null>(null);

  const openAuthModal = (server: any) => {
    setAuthModalServer(server);
    setAuthKeyValue('');
    setAuthKeyVisible(false);
    setAuthFeedback(null);
    setShowMcpAuthModal(true);
  };

  const [showAddRuleModal, setShowAddRuleModal] = useState(false);
  const [newRuleName, setNewRuleName] = useState('');
  const [newRuleScope, setNewRuleScope] = useState<'Project' | 'Global'>('Project');
  const [newRuleDesc, setNewRuleDesc] = useState('');
  const [newRulePath, setNewRulePath] = useState('');

  const [showAddAutoModal, setShowAddAutoModal] = useState(false);
  const [newAutoName, setNewAutoName] = useState('');
  const [newAutoCli, setNewAutoCli] = useState('AntigravityCli');
  const [newAutoInterval, setNewAutoInterval] = useState('*/30 * * * *');
  const [newAutoCron, setNewAutoCron] = useState('*/30 * * * *');
  const [newAutoPrompt, setNewAutoPrompt] = useState('');


  // Live ticking timer for Discord Live Profile Card Simulator
  const [elapsedSeconds, setElapsedSeconds] = useState(872); // 00:14:32 initial
  useEffect(() => {
    const timer = setInterval(() => {
      setElapsedSeconds(prev => prev + 1);
    }, 1000);
    return () => clearInterval(timer);
  }, []);

  const formatElapsed = (sec: number) => {
    const h = Math.floor(sec / 3600);
    const m = Math.floor((sec % 3600) / 60);
    const s = sec % 60;
    return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')} elapsed`;
  };

  useEffect(() => {
    loadDependencies();
    // Passive, so it is safe to ask on mount: it reads state the engines already hold.
    loadEngineHealth();
    loadSoundpackConfig();
    loadWebhookConfig();
    loadDiscordRpcStatus();
    loadPocketCommanderConfig();
    loadCustomClis();
    loadAgentSessions();
    loadAutomations();
  }, [
    loadDependencies,
    loadSoundpackConfig,
    loadWebhookConfig,
    loadDiscordRpcStatus,
    loadPocketCommanderConfig,
    loadCustomClis,
    loadMcpAndSkills,
    loadAgentSessions,
    loadAutomations,
  ]);

  const categories = [
    { id: 'appearance', label: 'Appearance & Theme', icon: Palette },
    { id: 'general', label: 'General & System', icon: Sliders },
    { id: 'engines', label: 'AI Engines & Runtimes', icon: Cpu },
    { id: 'integrations', label: 'Webhooks & Discord', icon: Share2 },
    { id: 'devtools', label: 'AI Dev Tools', icon: Wrench },
    { id: 'lan', label: 'Remote Access', icon: Wifi },
    { id: 'security', label: 'Security & Health', icon: ShieldCheck },
    { id: 'logs', label: 'Live Logs', icon: Terminal },
    { id: 'about', label: 'About & Updates', icon: Info },
  ];

  const filteredCategories = categories.filter(c =>
    !searchQuery || c.label.toLowerCase().includes(searchQuery.toLowerCase())
  );

  const handleApplyCustomHex = () => {
    let hex = customHex.trim();
    if (!hex.startsWith('#')) hex = '#' + hex;
    if (/^#[0-9A-Fa-f]{6}$/.test(hex)) {
      setAccentColor(hex.toUpperCase());
    }
  };

  const testHotkey = async (name: string) => {
    try {
      await webviewBridge.invoke('test_hotkey', { hotkey: name });
      setTestHotkeyFeedback(`Triggered ${name} successfully!`);
      setTimeout(() => setTestHotkeyFeedback(null), 3000);
    } catch {
      setTestHotkeyFeedback(`Hotkey ${name} triggered`);
      setTimeout(() => setTestHotkeyFeedback(null), 3000);
    }
  };

  const handleCheckUpdates = async () => {
    const res = await checkForUpdates();
    setUpdateFeedback(res);
    setTimeout(() => setUpdateFeedback(null), 4000);
  };

  const handleCopyLogs = () => {
    const logText = logs.map(l => `${l.timestamp} [${l.level.toUpperCase()}] (${l.source}): ${l.message}`).join('\n');
    navigator.clipboard.writeText(logText);
    setCopiedLog(true);
    setTimeout(() => setCopiedLog(false), 2000);
  };

  const filteredLogs = logs.filter(l => {
    if (logsFilter !== 'all' && l.source.toLowerCase() !== logsFilter.toLowerCase() && l.level.toLowerCase() !== logsFilter.toLowerCase()) {
      return false;
    }
    if (logsSearch) {
      const q = logsSearch.toLowerCase();
      return l.message.toLowerCase().includes(q) || l.source.toLowerCase().includes(q);
    }
    return true;
  });

  // Follow the tail of the log console while Auto-Scroll is on. Running here instead of on every
  // log write keeps the scroll tied to what is actually rendered.
  useEffect(() => {
    if (!autoScroll) return;
    const el = logConsoleRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [filteredLogs, autoScroll]);

  return (
    <div className="flex-1 flex flex-col h-full overflow-hidden bg-[var(--bg-app,#090D16)] text-[var(--text-main,#F1F5F9)] select-none">
      {/* Top Header */}
      <div className="px-6 py-4 border-b border-[var(--border-subtle,#1E293B)] flex items-center justify-between bg-[var(--bg-header,#0B0F19)]">
        <div>
          <h1 className="text-lg font-bold tracking-tight text-[var(--text-main,#F1F5F9)] flex items-center gap-2">
            <span>Settings & Preferences</span>
          </h1>
          <p className="text-xs text-[var(--text-dim,#64748B)] mt-0.5">
            Manage themes, system preferences, runtimes, MCP servers, audio, and integrations
          </p>
        </div>

        {/* Search */}
        <div className="relative w-80">
          <img src={actionIcons.search} alt="" className="h-3.5 w-3.5 object-contain absolute left-3 top-1/2 -translate-y-1/2" />
          <input
            type="text"
            value={searchQuery}
            onChange={e => setSearchQuery(e.target.value)}
            placeholder="Search settings (e.g. tray, theme, hotkey, mcp)..."
            className="w-full pl-8 pr-8 py-1.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)] placeholder-[var(--text-dim,#64748B)] focus:outline-none focus:border-sky-500 font-sans"
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery('')}
              className="absolute right-2.5 top-1/2 -translate-y-1/2 text-[var(--text-dim,#64748B)] hover:text-white"
            >
              <X className="w-3.5 h-3.5" />
            </button>
          )}
        </div>
      </div>

      {/* Main Master-Detail Layout */}
      <div className="flex-1 flex overflow-hidden">
        {/* Left Nav Sidebar (~240px) */}
        <nav className="w-60 border-r border-[var(--border-subtle,#1E293B)] bg-[var(--bg-sidebar,#0A0E1A)] p-3 space-y-1 overflow-y-auto">
          {filteredCategories.map(cat => {
            const sectionIcon = sectionIcons[cat.id] ?? sectionIcons.general;
            const isActive = activeCategory === cat.id;
            return (
              <button
                key={cat.id}
                onClick={() => setActiveCategory(cat.id)}
                className={`w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-xs font-medium transition-all ${
                  isActive
                    ? 'bg-sky-500/15 text-accent font-semibold border border-sky-500/30'
                    : 'text-[var(--text-muted,#94A3B8)] hover:bg-[var(--bg-card-hover,#111827)] hover:text-[var(--text-main,#F1F5F9)]'
                }`}
              >
                <img src={sectionIcon} alt="" className="h-4 w-4 flex-shrink-0 object-contain" />
                <span className="truncate">{cat.label}</span>
              </button>
            );
          })}
        </nav>

        {/* Right Active Content Pane */}
        <div className="flex-1 p-6 overflow-y-auto space-y-6">
          {/* ======================================================== */}
          {/* 1. APPEARANCE & THEME */}
          {/* ======================================================== */}
          {activeCategory === 'appearance' && (
            <div className="max-w-3xl space-y-6">
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-5">
                <div>
                  <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">Theme & Appearance</h3>
                  <p className="text-xs text-[var(--text-dim,#64748B)] mt-0.5">
                    Customize user interface style, color accents, and contrast levels
                  </p>
                </div>

                {/* Theme Mode */}
                <div className="space-y-2">
                  <label className="text-xs font-semibold text-[var(--text-muted,#94A3B8)]">Theme Mode:</label>
                  <div className="grid grid-cols-2 gap-3 max-w-md">
                    <button
                      onClick={() => setTheme('dark')}
                      className={`flex items-center justify-center gap-2.5 p-3 rounded-lg border text-xs font-medium transition-all ${
                        theme === 'dark'
                          ? 'bg-sky-500/15 border-sky-500 text-accent font-bold'
                          : 'bg-[var(--bg-input,#0D1322)] border-[var(--border-subtle,#1E293B)] text-[var(--text-muted,#94A3B8)] hover:border-slate-600'
                      }`}
                    >
                      <Moon className="w-4 h-4" />
                      <span>Dark Mode (Default)</span>
                    </button>
                    <button
                      onClick={() => setTheme('light')}
                      className={`flex items-center justify-center gap-2.5 p-3 rounded-lg border text-xs font-medium transition-all ${
                        theme === 'light'
                          ? 'bg-sky-500/15 border-sky-500 text-accent font-bold'
                          : 'bg-[var(--bg-input,#0D1322)] border-[var(--border-subtle,#1E293B)] text-[var(--text-muted,#94A3B8)] hover:border-slate-600'
                      }`}
                    >
                      <Sun className="w-4 h-4" />
                      <span>Light Mode</span>
                    </button>
                  </div>
                </div>

                {/* Discord Adaptive Theme */}
                <div className="space-y-3 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
                  <div>
                    <label className="text-xs font-semibold text-[var(--text-muted,#94A3B8)]">
                      Discord Adaptive Theme (Select Preset or Enter #Hex Code):
                    </label>
                    <p className="text-[11px] text-[var(--text-dim,#64748B)] mt-0.5">
                      Dynamically adapts app background, sidebar, cards, inputs, and borders for high contrast.
                    </p>
                  </div>

                  {/* Swatches */}
                  <div className="flex flex-wrap items-center gap-2">
                    {PRESET_SWATCHES.map(swatch => {
                      const isSelected = accentColor.toUpperCase() === swatch.hex.toUpperCase();
                      return (
                        <button
                          key={swatch.hex}
                          onClick={() => {
                            setAccentColor(swatch.hex);
                            setCustomHex(swatch.hex);
                          }}
                          title={swatch.label}
                          style={{ backgroundColor: swatch.hex }}
                          className={`w-7 h-7 rounded-full flex items-center justify-center transition-transform hover:scale-110 ${
                            isSelected ? 'ring-2 ring-white ring-offset-2 ring-offset-[#090D16] scale-110 shadow-lg' : ''
                          }`}
                        >
                          {isSelected && <Check className="w-3.5 h-3.5 text-white drop-shadow" />}
                        </button>
                      );
                    })}

                    <div className="flex items-center gap-2 ml-2 pl-3 border-l border-[var(--border-subtle,#1E293B)]">
                      <input
                        type="color"
                        value={customHex.startsWith('#') ? customHex : '#' + customHex}
                        onChange={e => {
                          setCustomHex(e.target.value.toUpperCase());
                          setAccentColor(e.target.value.toUpperCase());
                        }}
                        className="w-7 h-7 rounded cursor-pointer border-0 bg-transparent p-0"
                      />
                      <input
                        type="text"
                        value={customHex}
                        onChange={e => setCustomHex(e.target.value)}
                        placeholder="#RRGGBB"
                        className="w-24 px-2.5 py-1 text-xs font-mono uppercase rounded bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-[var(--text-main,#F1F5F9)] focus:outline-none focus:border-sky-500"
                      />
                      <Button variant="secondary" size="xs" onClick={handleApplyCustomHex}>
                        Apply Code #
                      </Button>
                    </div>
                  </div>

                  {/* Intensity Slider */}
                  <div className="max-w-sm pt-2 space-y-1.5">
                    <div className="flex items-center justify-between text-xs">
                      <span className="font-medium text-[var(--text-muted,#94A3B8)]">Color Intensity:</span>
                      <span className="font-mono font-bold text-accent">{themeIntensity}%</span>
                    </div>
                    <input
                      type="range"
                      min={10}
                      max={100}
                      value={themeIntensity}
                      onChange={e => setThemeIntensity(Number(e.target.value))}
                      className="w-full accent-sky-500 cursor-pointer"
                    />
                  </div>
                </div>

                {/* Wallpaper */}
                <div className="space-y-3 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
                  <div>
                    <label className="text-xs font-semibold text-[var(--text-muted,#94A3B8)]">Background image:</label>
                    <p className="text-[11px] text-[var(--text-dim,#64748B)] mt-0.5">
                      A picture sits behind the chrome. Panels turn translucent so it stays visible, and the dim slider keeps text readable.
                    </p>
                  </div>

                  <div className="flex flex-wrap items-center gap-2">
                    <Button variant="secondary" size="xs" onClick={() => void chooseImage()}>
                      {backgroundImage ? 'Replace image' : 'Choose image'}
                    </Button>
                    {backgroundImage ? (
                      <Button variant="secondary" size="xs" onClick={() => setBackgroundImage('')}>
                        Remove
                      </Button>
                    ) : null}
                    <span className="max-w-[240px] truncate font-mono text-[11px] text-[var(--text-dim,#64748B)]">
                      {backgroundImage ? wallpaperName(backgroundImage) : 'None'}
                    </span>
                  </div>

                  {backgroundImage ? (
                    <div className="max-w-sm space-y-1.5">
                      <div className="flex items-center justify-between text-xs">
                        <span className="font-medium text-[var(--text-muted,#94A3B8)]">Image dim:</span>
                        <span className="font-mono font-bold text-accent">{backgroundDim}%</span>
                      </div>
                      <input
                        type="range"
                        min={0}
                        max={90}
                        value={backgroundDim}
                        onChange={e => setBackgroundDim(Number(e.target.value))}
                        className="w-full accent-sky-500 cursor-pointer"
                      />
                    </div>
                  ) : null}

                  {wallpaperError ? <p className="text-[11px] text-state-danger">{wallpaperError}</p> : null}
                </div>
              </div>
            </div>
          )}

          {/* ======================================================== */}
          {/* 2. GENERAL & SYSTEM PREFERENCES */}
          {/* ======================================================== */}
          {activeCategory === 'general' && (
            <div className="max-w-3xl space-y-6">
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-5">
                <div>
                  <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">Startup & System Preferences</h3>
                  <p className="text-xs text-[var(--text-dim,#64748B)] mt-0.5">
                    Configure window behaviors, global hotkeys, notifications, and tray actions
                  </p>
                </div>

                <div className="space-y-3">
                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.autostart}
                      onChange={e => updateSetting('autostart', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Launch Agents Hub on Windows Startup</span>
                  </label>

                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.startMinimized}
                      onChange={e => updateSetting('startMinimized', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Start Minimized to System Tray on Launch</span>
                  </label>

                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.minimizeToTray}
                      onChange={e => updateSetting('minimizeToTray', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Enable Minimize to System Tray (Keep running in background when closed or minimized)</span>
                  </label>

                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.notifications}
                      onChange={e => updateSetting('notifications', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Enable Windows Notifications (Balloon Tips & System Tray Alerts)</span>
                  </label>

                  {/* Architecture Graph Toggle */}
                  <div className="p-3.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-1">
                    <label className="flex items-center gap-3 cursor-pointer text-xs font-semibold text-[var(--text-main,#F1F5F9)]">
                      <input
                        type="checkbox"
                        checked={settings.enableArchitectureGraph}
                        onChange={e => updateSetting('enableArchitectureGraph', e.target.checked)}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Enable Codebase Architecture & Blast-Radius Graph</span>
                    </label>
                    <p className="text-[11px] text-[var(--text-dim,#64748B)] pl-7">
                      Interactive 2D dependency graph visualizer. Maps file callers, calculates refactor blast radius, detects unused files, and generates agent prompt shields.
                    </p>
                  </div>

                  {/* Global Window Hotkey */}
                  <div className="p-3.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-3">
                    <label className="flex items-center gap-3 cursor-pointer text-xs font-semibold text-[var(--text-main,#F1F5F9)]">
                      <input
                        type="checkbox"
                        checked={settings.globalHotkey}
                        onChange={e => updateSetting('globalHotkey', e.target.checked)}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Enable Global Hotkey to Show or Hide Window</span>
                    </label>

                    <div className="grid grid-cols-2 gap-3 pl-7">
                      <div>
                        <label className="text-[11px] text-[var(--text-dim,#64748B)] mb-1 block">Hotkey Modifier:</label>
                        <select
                          value={settings.globalHotkeyModifier}
                          onChange={e => updateSetting('globalHotkeyModifier', e.target.value)}
                          className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)]"
                        >
                          <option value="Ctrl + Alt">Ctrl + Alt</option>
                          <option value="Alt">Alt</option>
                          <option value="Ctrl">Ctrl</option>
                          <option value="Shift">Shift</option>
                          <option value="Win">Win</option>
                          <option value="None">None (Key only)</option>
                        </select>
                      </div>

                      <div>
                        <label className="text-[11px] text-[var(--text-dim,#64748B)] mb-1 block">Hotkey Key:</label>
                        <select
                          value={settings.globalHotkeyKey}
                          onChange={e => updateSetting('globalHotkeyKey', e.target.value)}
                          className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)]"
                        >
                          <option value="H">H</option>
                          <option value="F12">F12</option>
                          <option value="F11">F11</option>
                          <option value="F10">F10</option>
                          <option value="Space">Space</option>
                        </select>
                      </div>
                    </div>

                    {prefDebug ? (
                      <p className="ml-7 mt-1 break-all font-mono text-[10px] text-[var(--text-dim,#64748B)]">
                        {prefDebug}
                      </p>
                    ) : null}

                    <div className="flex items-center gap-3 pl-7 pt-1">
                      <Button
                        variant="secondary"
                        size="xs"
                        onClick={async () => {
                          testHotkey('window');
                          try {
                            const res: any = await webviewBridge.invoke('get_preference_debug');
                            const v = res?.values ?? {};
                            setPrefDebug(
                              `stored: GlobalHotkey=${v.GlobalHotkey} · Modifier="${v.GlobalHotkeyModifier}" · Key="${v.GlobalHotkeyKey}" · StartMinimized=${v.StartMinimized} · MinimizeToTray=${v.MinimizeToTray}`
                            );
                          } catch (err) {
                            setPrefDebug(`could not read preferences: ${err instanceof Error ? err.message : 'error'}`);
                          }
                        }}
                      >
                        Test Hotkey Action Now
                      </Button>
                      <span className="text-[11px] font-mono text-accent">
                        Active Hotkey: {settings.globalHotkeyModifier} + {settings.globalHotkeyKey}
                      </span>
                    </div>
                  </div>

                  {/* Quake HUD Hotkey */}
                  <div className="p-3.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-3">
                    <label className="flex items-center gap-3 cursor-pointer text-xs font-semibold text-[var(--text-main,#F1F5F9)]">
                      <input
                        type="checkbox"
                        checked={settings.globalHudHotkey}
                        onChange={e => updateSetting('globalHudHotkey', e.target.checked)}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Enable Global Quake Hotkey Toggle</span>
                    </label>

                    <div className="grid grid-cols-3 gap-3 pl-7">
                      <div>
                        <label className="text-[11px] text-[var(--text-dim,#64748B)] mb-1 block">Hotkey Modifier:</label>
                        <select
                          value={settings.hudHotkeyModifier}
                          onChange={e => updateSetting('hudHotkeyModifier', e.target.value)}
                          className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)]"
                        >
                          <option value="None (Key only)">None (Key only)</option>
                          <option value="Alt">Alt</option>
                          <option value="Ctrl">Ctrl</option>
                          <option value="Ctrl+Alt">Ctrl + Alt</option>
                        </select>
                      </div>

                      <div>
                        <label className="text-[11px] text-[var(--text-dim,#64748B)] mb-1 block">Hotkey Key:</label>
                        <select
                          value={settings.hudHotkeyKey}
                          onChange={e => updateSetting('hudHotkeyKey', e.target.value)}
                          className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)]"
                        >
                          <option value="F12">F12</option>
                          <option value="F11">F11</option>
                          <option value="Oem3">~ (Tilde)</option>
                        </select>
                      </div>

                      <div>
                        <label className="text-[11px] text-[var(--text-dim,#64748B)] mb-1 block">Trigger Target:</label>
                        <select
                          value={settings.hudHotkeyTarget}
                          onChange={e => updateSetting('hudHotkeyTarget', e.target.value)}
                          className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)]"
                        >
                          <option value="Slide-Down HUD Overlay">Slide-Down HUD Overlay</option>
                          <option value="Standalone Terminals">Standalone Terminals</option>
                          <option value="Both">Both</option>
                        </select>
                      </div>
                    </div>

                    <div className="flex items-center gap-3 pl-7 pt-1">
                      <Button variant="secondary" size="xs" onClick={() => testHotkey('quake')}>
                        Test Hotkey Action Now
                      </Button>
                      <span className="text-[11px] font-mono text-accent">
                        {settings.globalHudHotkey ? `Active Hotkey: ${settings.hudHotkeyKey}` : 'Hotkey Disabled'}
                      </span>
                    </div>
                  </div>

                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.maskShortcutPaths}
                      onChange={e => updateSetting('maskShortcutPaths', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Hide / Mask File Target Paths on Dashboard (Show as **)</span>
                  </label>

                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.embedShellsInTerminalWorkspace}
                      onChange={e => updateSetting('embedShellsInTerminalWorkspace', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Open CMD & PowerShell Terminals inside CLI Workspace Window</span>
                  </label>

                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.hideMainOnCliOpen}
                      onChange={e => updateSetting('hideMainOnCliOpen', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Hide Main Hub Window to Tray when Opening CLI Terminal Workspace (Prevents 2 windows in Taskbar)</span>
                  </label>

                  {/* Runner behaviour on send */}
                  <div className="p-3.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-2">
                    <label className="flex items-center gap-3 cursor-pointer text-xs font-semibold text-[var(--text-main,#F1F5F9)]">
                      <input
                        type="checkbox"
                        checked={autoRunDefaultProfile}
                        onChange={e => setAutoRunDefaultProfile(e.target.checked)}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Auto-run default profile on request</span>
                    </label>
                    <p className="text-[11px] text-[var(--text-dim,#64748B)] pl-7 leading-relaxed">
                      Off: sending with no running runner is refused and you are told to start one.
                      On: the default runner is started automatically before the message is sent.
                    </p>
                    <p className="text-[11px] text-[var(--text-dim,#64748B)] pl-7 font-mono">
                      Default runner: {defaultProfileName ? profileLabel(defaultProfileName) : 'not set. Pick one with the star in Multi-Profile Agents'}
                    </p>
                  </div>

                  <label className="flex items-center gap-3 cursor-pointer text-xs text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={settings.enableHardwareMonitor}
                      onChange={e => updateSetting('enableHardwareMonitor', e.target.checked)}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Enable System & Hardware Telemetry Monitor (Real-time network & RAM resource charts)</span>
                  </label>
                </div>

                <div className="flex items-center justify-end gap-3 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
                  {saveFeedback && <span className="text-xs font-mono text-state-success">{saveFeedback}</span>}
                  <Button variant="primary" size="xs" onClick={saveGeneralSettings}>
                    Save Preferences
                  </Button>
                </div>
              </div>

              {/* Soundpack is one preference, not a page of its own, so it belongs with the
                  other system toggles. It saves as you change it. */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div>
                  <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">SOUNDPACK & AUDIO CHIMES</h3>
                  <p className="text-xs text-[var(--text-dim,#64748B)] mt-0.5">
                    Cues for agent events, checkpoints, and approvals. Silent until you switch it on.
                  </p>
                </div>

                <div className="flex flex-wrap items-center gap-3">
                  <label className="flex items-center gap-2 cursor-pointer text-xs font-semibold text-[var(--text-main,#F1F5F9)]">
                    <input
                      type="checkbox"
                      checked={soundpackEnabled}
                      onChange={e => saveSoundpackConfig({ soundpackEnabled: e.target.checked })}
                      className="w-4 h-4 rounded accent-sky-500"
                    />
                    <span>Audio: {soundpackEnabled ? 'On' : 'Off'}</span>
                  </label>

                  <Button variant="secondary" size="xs" leftIcon={<Play className="w-3 h-3" />} onClick={() => playSoundEvent('task_done')}>
                    Test Audio
                  </Button>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-2 gap-4 p-4 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)]">
                  <div>
                    <label className="text-xs font-semibold text-[var(--text-muted,#94A3B8)] block mb-1">Soundpack Theme:</label>
                    <select
                      value={soundpackTheme}
                      onChange={e => saveSoundpackConfig({ soundpackTheme: e.target.value as any })}
                      className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)]"
                    >
                      <option value="modern">Modern Synthesizer (Clean Chimes)</option>
                      <option value="arcade">Arcade 8-Bit (Retro Chiptune)</option>
                      <option value="scifi">Sci-Fi Ambient (Subtle Sonar)</option>
                      <option value="vocal">Vocal Synthesis (Speech Cue)</option>
                    </select>
                  </div>

                  <div>
                    <div className="flex items-center justify-between text-xs mb-1">
                      <span className="font-semibold text-[var(--text-muted,#94A3B8)]">Master Volume:</span>
                      <span className="font-mono text-accent font-bold">{soundpackVolume}%</span>
                    </div>
                    <input
                      type="range"
                      min={0}
                      max={100}
                      value={soundpackVolume}
                      onChange={e => saveSoundpackConfig({ soundpackVolume: Number(e.target.value) })}
                      className="w-full accent-sky-500 cursor-pointer"
                    />
                  </div>
                </div>

                <div className="space-y-2">
                  <div className="text-xs font-semibold text-[var(--text-muted,#94A3B8)]">Trigger audio on:</div>
                  <div className="flex flex-wrap items-center gap-4 text-xs text-[var(--text-main,#F1F5F9)]">
                    <label className="flex items-center gap-2 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={soundpackEvents.taskDone}
                        onChange={e => saveSoundpackConfig({ soundpackEvents: { ...soundpackEvents, taskDone: e.target.checked } })}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Task completed</span>
                    </label>
                    <label className="flex items-center gap-2 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={soundpackEvents.approval}
                        onChange={e => saveSoundpackConfig({ soundpackEvents: { ...soundpackEvents, approval: e.target.checked } })}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Approval needed</span>
                    </label>
                    <label className="flex items-center gap-2 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={soundpackEvents.checkpoint}
                        onChange={e => saveSoundpackConfig({ soundpackEvents: { ...soundpackEvents, checkpoint: e.target.checked } })}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Checkpoint created</span>
                    </label>
                    <label className="flex items-center gap-2 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={soundpackEvents.error}
                        onChange={e => saveSoundpackConfig({ soundpackEvents: { ...soundpackEvents, error: e.target.checked } })}
                        className="w-4 h-4 rounded accent-sky-500"
                      />
                      <span>Task failed</span>
                    </label>
                  </div>
                </div>
              </div>

            </div>
          )}

          {/* ======================================================== */}
          {/* 3. AI ENGINES & RUNTIMES */}
          {/* ======================================================== */}
          {activeCategory === 'engines' && (
            <div className="max-w-3xl space-y-6">
              {/* Dependencies & Core Runtimes */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div>
                    <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">DEPENDENCIES & REQUIRED RUNTIMES</h3>
                    <p className="text-xs text-[var(--text-dim,#64748B)]">
                      Verify installed development runtimes, AI CLI engines, and network utilities
                    </p>
                  </div>

                  <div className="flex items-center gap-2">
                    <Button
                      variant="primary"
                      size="xs"
                      onClick={installAllDependencies}
                      disabled={dependencies.every(d => d.isInstalled)}
                    >
                      {dependencies.every(d => d.isInstalled) ? 'Nothing missing' : 'Install Missing'}
                    </Button>
                    <Button
                      variant="secondary"
                      size="xs"
                      leftIcon={<img src={actionIcons.refresh} alt="" className={`h-3.5 w-3.5 object-contain ${dependenciesLoading ? 'animate-spin' : ''}`} />}
                      onClick={loadDependencies}
                    >
                      Recheck All
                    </Button>
                  </div>
                </div>

                {/* Engine health: passive state only. Nothing here loads a page, because a
                    health check that can hang is worse than no health check. */}
                <div className="space-y-2 pb-1">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      <div className="text-xs font-semibold text-accent">Engine health</div>
                      <Button
                        variant="secondary"
                        size="xs"
                        onClick={async () => {
                          try {
                            const res: any = await webviewBridge.invoke('export_log_bundle');
                            setBundlePath(res?.path ?? null);
                          } catch {
                            setBundlePath(null);
                          }
                        }}
                      >
                        Export logs
                      </Button>
                    </div>
                    <Button variant="secondary" size="xs" onClick={() => loadEngineHealth()}>
                      <img src={actionIcons.refresh} alt="" className={`h-3.5 w-3.5 object-contain ${engineHealthLoading ? 'animate-spin' : ''}`} />
                    </Button>
                  </div>

                  {engineHealth.length === 0 ? (
                    <p className="text-[11px] text-content-dim">Not checked yet. Press refresh.</p>
                  ) : (
                    <div className="space-y-1.5">
                      {engineHealth.map(engine => (
                        <div
                          key={engine.id}
                          className="p-3 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)]"
                        >
                          <div className="flex items-center gap-2 flex-wrap text-xs font-bold text-[var(--text-main,#F1F5F9)]">
                            <span className="text-accent">{dependencyIcon(engine.id)}</span>
                            <span>{engine.name}</span>

                            <span
                              className={`px-1.5 rounded text-[10px] font-mono border ${
                                engine.initialized
                                  ? 'bg-emerald-500/10 text-state-success border-emerald-500/20'
                                  : 'bg-slate-500/10 text-content-dim border-slate-500/20'
                              }`}
                            >
                              {engine.initialized ? 'window ready' : 'not started'}
                            </span>

                            {engine.signedIn !== null ? (
                              <span
                                className={`px-1.5 rounded text-[10px] font-mono border ${
                                  engine.signedIn
                                    ? 'bg-emerald-500/10 text-state-success border-emerald-500/20'
                                    : 'bg-amber-500/10 text-state-warning border-amber-500/20'
                                }`}
                              >
                                {engine.signedIn ? 'signed in' : 'guest'}
                              </span>
                            ) : null}

                            <span
                              className={`px-1.5 rounded text-[10px] font-mono border ${
                                engine.primedIn
                                  ? 'bg-emerald-500/10 text-state-success border-emerald-500/20'
                                  : 'bg-slate-500/10 text-content-dim border-slate-500/20'
                              }`}
                              title={engine.primedIn ?? 'No primer in the current conversation'}
                            >
                              {engine.primedIn ? 'tools primed' : 'no primer'}
                            </span>
                          </div>

                          {engine.lastError ? (
                            <p className="text-[11px] font-mono text-state-danger mt-1 break-words">{engine.lastError}</p>
                          ) : (
                            <p className="text-[11px] font-mono text-[var(--text-dim,#64748B)] mt-1">
                              {engine.lastTurnAt ? `last turn ${new Date(engine.lastTurnAt).toLocaleTimeString()}` : 'no turn yet'}
                            </p>
                          )}
                        </div>
                      ))}
                    </div>
                  )}

                  {bundlePath ? (
                    <div className="flex items-start gap-1.5">
                      <p className="flex-1 text-[10.5px] font-mono text-content-dim break-all">Saved to {bundlePath}</p>
                      <button
                        type="button"
                        title="Show this file in its folder"
                        onClick={() => void webviewBridge.invoke('open_log_bundle_location', { path: bundlePath })}
                        className="mt-0.5 flex-shrink-0 rounded p-0.5 text-content-dim transition-colors hover:text-accent"
                      >
                        <FolderOpen className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  ) : null}
                </div>

                {/* Group 1: Core System & Dev Runtimes */}
                <div className="space-y-2 pt-2">
                  <div className="text-xs font-semibold text-accent">1. Core Dev Runtimes (Required by CLI Agents)</div>
                  <div className="space-y-1.5">
                    {dependencies
                      .filter(d => d.category === 'runtime')
                      .map(dep => (
                        <div
                          key={dep.id}
                          className="p-3 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] flex items-center justify-between gap-3"
                        >
                          <div>
                            <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)] flex items-center gap-2">
                              <span className="text-accent">{dependencyIcon(dep.id)}</span>
                              <span>{dep.name}</span>
                              <span
                                className={`px-1.5 py-0.2 rounded text-[10px] font-mono ${
                                  dep.isInstalled
                                    ? 'bg-emerald-500/10 text-state-success border border-emerald-500/20'
                                    : 'bg-rose-500/10 text-state-danger border border-rose-500/20'
                                }`}
                              >
                                {dep.isInstalled ? 'Installed' : 'Not Installed'}
                              </span>
                            </div>
                            <div className="text-[11px] font-mono text-[var(--text-dim,#64748B)] mt-0.5">
                              {dep.version || dep.path || 'Not detected on PATH'}
                            </div>
                            {installLines[dep.id] ? (
                              <div className="text-[10.5px] font-mono text-accent mt-0.5 break-all">
                                {installLines[dep.id]}
                              </div>
                            ) : null}
                          </div>

                          <Button
                            variant="secondary"
                            size="xs"
                            disabled={installingId === dep.id}
                            onClick={() => runDependencyInstall(dep.id)}
                          >
                            {installingId === dep.id
                              ? 'Installing...'
                              : dep.isInstalled
                                ? 'Reinstall'
                                : 'Install'}
                          </Button>
                        </div>
                      ))}
                  </div>
                </div>

                {/* Group 2: AI Agents & Network Tools */}
                <div className="space-y-2 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
                  <div>
                    <div className="text-xs font-semibold text-state-success">2. AI Agents & Network Tools</div>
                    <p className="text-[10.5px] text-content-dim mt-0.5">
                      Optional. Only the runtimes above are required for the app to run. Antigravity,
                      DeepSeek, Qwen, Vyce and OpenCode drive chat sessions; the rest are detected and
                      can be launched in a terminal.
                    </p>
                  </div>
                  <div className="space-y-1.5">
                    {dependencies
                      .filter(d => d.category !== 'runtime')
                      .map(dep => (
                        <div
                          key={dep.id}
                          className="p-3 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] flex items-center justify-between gap-3"
                        >
                          <div>
                            <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)] flex items-center gap-2">
                              <span className="text-accent">{dependencyIcon(dep.id)}</span>
                              <span>{dep.name}</span>
                              <span
                                className={`px-1.5 py-0.2 rounded text-[10px] font-mono ${
                                  dep.isInstalled
                                    ? 'bg-emerald-500/10 text-state-success border border-emerald-500/20'
                                    : 'bg-rose-500/10 text-state-danger border border-rose-500/20'
                                }`}
                              >
                                {dep.isInstalled ? 'Ready' : 'Missing'}
                              </span>

                              {dep.chatSupported === false ? (
                                <span
                                  className="px-1.5 py-0.2 rounded text-[10px] font-mono bg-slate-500/10 text-content-dim border border-slate-500/20"
                                  title="Detected and launchable, but not wired into chat yet"
                                >
                                  Not used
                                </span>
                              ) : null}
                            </div>
                            <div className="text-[11px] font-mono text-[var(--text-dim,#64748B)] mt-0.5">
                              {dep.version || dep.commandPreview || 'Available as agent terminal'}
                            </div>
                            {installLines[dep.id] ? (
                              <div className="text-[10.5px] font-mono text-accent mt-0.5 break-all">
                                {installLines[dep.id]}
                              </div>
                            ) : null}
                          </div>

                          <div className="flex items-center gap-2">
                            {dep.isInstalled ? (
                              <Button
                                variant="secondary"
                                size="xs"
                                title={`Open a terminal running ${dep.name}`}
                                onClick={() => void webviewBridge.invoke('run_dependency_cli', { id: dep.id })}
                              >
                                Run
                              </Button>
                            ) : null}

                            <Button
                              variant="secondary"
                              size="xs"
                              disabled={installingId === dep.id}
                              onClick={() => runDependencyInstall(dep.id)}
                            >
                              {installingId === dep.id
                                ? 'Installing...'
                                : dep.isInstalled
                                  ? 'Update / Check'
                                  : 'Run Installer'}
                            </Button>
                          </div>
                        </div>
                      ))}
                  </div>
                </div>
              </div>

              {/* Custom AI CLI Engines Card */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div className="flex items-center gap-2.5">
                    <div className="w-8 h-8 rounded-lg bg-sky-500/15 border border-sky-500/30 flex items-center justify-center text-accent">
                      <Terminal className="w-4 h-4" />
                    </div>
                    <div>
                      <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">CUSTOM AI CLI ENGINES</h3>
                      <p className="text-xs text-[var(--text-dim,#64748B)]">
                        Run Aider, Goose, Copilot CLI, Gemini CLI, or custom scripts as interactive tabs
                      </p>
                    </div>
                  </div>

                  <Button
                    variant="primary"
                    size="xs"
                    leftIcon={<img src={actionIcons.add} alt="" className="h-3.5 w-3.5 object-contain" />}
                    onClick={() => {
                      setNewCliName('');
                      setNewCliExe('');
                      setNewCliArgs('');
                      setNewCliDir('');
                      setNewCliColor('#38bdf8');
                      setShowAddCliModal(true);
                    }}
                  >
                    Add CLI Engine
                  </Button>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-2 gap-3 pt-1">
                  {customClis.map(c => (
                    <div
                      key={c.id}
                      className="p-3.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] flex flex-col justify-between gap-3"
                    >
                      <div>
                        <div className="flex items-center justify-between mb-1.5">
                          <div className="flex items-center gap-2">
                            <span className="w-2.5 h-2.5 rounded-full" style={{ background: c.colorTag || '#38bdf8' }}></span>
                            <span className="text-xs font-bold text-[var(--text-main,#F1F5F9)]">{c.name}</span>
                          </div>
                          <button
                            type="button"
                            onClick={() => deleteCustomCli(c.id)}
                            className="text-[11px] text-state-danger hover:text-state-danger font-medium cursor-pointer"
                          >
                            Delete
                          </button>
                        </div>
                        <div
                          className="text-[11px] font-mono text-[var(--text-dim,#64748B)] truncate"
                          title={`${c.executablePath} ${c.arguments}`}
                        >
                          {c.executablePath} {c.arguments}
                        </div>
                      </div>

                      <div className="flex justify-end gap-2 pt-1 border-t border-[var(--border-subtle,#1E293B)]/40">
                        <Button
                          variant="secondary"
                          size="xs"
                          onClick={() => launchCustomCli(c.id)}
                        >
                          Run in Terminal
                        </Button>
                      </div>
                    </div>
                  ))}

                  {customClis.length === 0 && (
                    <div className="col-span-full py-8 text-center text-xs text-[var(--text-dim,#64748B)]">
                      No custom CLI engines configured. Click "Add CLI Engine" to integrate tools like Aider, Goose, or Copilot.
                    </div>
                  )}
                </div>
              </div>
            </div>
          )}


          {/* ======================================================== */}
          {/* 6. WEBHOOKS & DISCORD */}
          {/* ======================================================== */}
          {activeCategory === 'integrations' && (
            <div className="max-w-4xl space-y-6">
              {/* Discord Webhooks */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div className="flex items-center gap-2.5">
                    <div className="w-8 h-8 rounded-lg bg-indigo-500/15 border border-indigo-500/30 flex items-center justify-center text-indigo-400">
                      <Share2 className="w-4 h-4" />
                    </div>
                    <div>
                      <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">REMOTE AGENT WEBHOOK & DISCORD BOT</h3>
                      <p className="text-xs text-[var(--text-dim,#64748B)]">
                        Dispatch remote status embeds to Discord channel webhooks (Toggleable & Secure)
                      </p>
                    </div>
                  </div>

                  <div className="flex items-center gap-3">
                    <label className="flex items-center gap-2 cursor-pointer text-xs font-semibold text-[var(--text-main,#F1F5F9)]">
                      <input
                        type="checkbox"
                        checked={webhookEnabled}
                        onChange={e => saveWebhookConfig()}
                        className="w-4 h-4 rounded accent-indigo-500"
                      />
                      <span>Enable Discord Alerts</span>
                    </label>

                    <Button variant="secondary" size="xs" leftIcon={<Send className="w-3 h-3" />} onClick={testWebhook}>
                      Send Test Webhook
                    </Button>
                    <Button variant="primary" size="xs" onClick={saveWebhookConfig}>
                      Save Webhook
                    </Button>
                  </div>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-3 p-4 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)]">
                  <div className="md:col-span-2">
                    <label className="text-xs font-semibold text-[var(--text-muted,#94A3B8)] block mb-1">Discord Webhook URL:</label>
                    <input
                      type="password"
                      value={webhookUrl}
                      onChange={e => updateSetting('webhookUrl' as any, e.target.value)}
                      placeholder="https://discord.com/api/webhooks/..."
                      className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)] font-mono focus:outline-none"
                    />
                  </div>
                  <div>
                    <label className="text-xs font-semibold text-[var(--text-muted,#94A3B8)] block mb-1">Mention Role or User ID (optional):</label>
                    <input
                      type="text"
                      value={webhookMention}
                      onChange={e => updateSetting('webhookMention' as any, e.target.value)}
                      placeholder="e.g. <@&123456789>"
                      className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)] font-mono focus:outline-none"
                    />
                  </div>
                </div>

                <div className="flex flex-wrap items-center gap-4 text-xs text-[var(--text-main,#F1F5F9)] pt-1">
                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={webhookEvents.done}
                      onChange={e => useSettingsStore.setState(s => ({ webhookEvents: { ...s.webhookEvents, done: e.target.checked } }))}
                      className="w-4 h-4 rounded accent-indigo-500"
                    />
                    <span>Notify on Completed Tasks</span>
                  </label>

                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={webhookEvents.error}
                      onChange={e => useSettingsStore.setState(s => ({ webhookEvents: { ...s.webhookEvents, error: e.target.checked } }))}
                      className="w-4 h-4 rounded accent-indigo-500"
                    />
                    <span>Notify on Errors & Failures</span>
                  </label>

                  <label className="flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={webhookEvents.approval}
                      onChange={e => useSettingsStore.setState(s => ({ webhookEvents: { ...s.webhookEvents, approval: e.target.checked } }))}
                      className="w-4 h-4 rounded accent-indigo-500"
                    />
                    <span>Notify on Approval Needed</span>
                  </label>
                </div>
              </div>

              {/* Discord Rich Presence (RPC) */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div className="flex items-center gap-2.5">
                    <div className="w-8 h-8 rounded-lg bg-[#5865f2]/15 border border-[#5865f2]/30 flex items-center justify-center text-[#5865f2]">
                      <MessageSquare className="w-4 h-4" />
                    </div>
                    <div>
                      <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">DISCORD RICH PRESENCE (RPC)</h3>
                      <p className="text-xs text-[var(--text-dim,#64748B)]">
                        Broadcast active AgentsHub project, git branch, and AI agents live to your Discord profile
                      </p>
                    </div>
                  </div>

                  <div className="flex items-center gap-3">
                    <span className="px-2 py-0.5 rounded text-[11px] font-mono bg-emerald-500/10 text-state-success border border-emerald-500/20 font-semibold">
                      {discordRpcConnected ? 'Connected' : 'Connecting...'}
                    </span>
                    <label className="flex items-center gap-2 cursor-pointer text-xs font-semibold text-[var(--text-main,#F1F5F9)]">
                      <input
                        type="checkbox"
                        checked={discordRpcEnabled}
                        onChange={toggleDiscordRpc}
                        className="w-4 h-4 rounded accent-[#5865f2]"
                      />
                      <span>Enable Rich Presence</span>
                    </label>
                    <Button variant="secondary" size="xs" onClick={refreshDiscordRpc}>
                      Refresh
                    </Button>
                  </div>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
                  {/* Controls */}
                  <div className="space-y-3">
                    <div className="p-3.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-2">
                      <label className="text-xs font-semibold text-[var(--text-muted,#94A3B8)] block">Privacy Preset:</label>
                      <select
                        value={discordPrivacy}
                        onChange={e => updateSetting('discordPrivacy' as any, e.target.value)}
                        className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)]"
                      >
                        <option value="default">Default (Coding with AgentsHub & Buttons)</option>
                        <option value="transparent">Full Transparency (Show Repo, Branch & Agents)</option>
                        <option value="project_only">Project Only (Show Repo Name & Agent)</option>
                        <option value="stealth">Stealth Mode (Developing in Private Codebase)</option>
                      </select>
                    </div>

                    <div className="grid grid-cols-2 gap-3 p-3 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-xs font-mono">
                      <div>
                        <div className="text-[11px] text-[var(--text-dim,#64748B)] mb-1 flex items-center gap-1">
                          <img src={actionIcons.lock} alt="" className="h-3 w-3 object-contain inline-block align-[-2px]" /> Application ID:
                        </div>
                        <span className="text-accent text-[11px]">1562567704941174834</span>
                      </div>
                      <div>
                        <div className="text-[11px] text-[var(--text-dim,#64748B)] mb-1 flex items-center gap-1">
                          <img src={actionIcons.lock} alt="" className="h-3 w-3 object-contain inline-block align-[-2px]" /> Official Assets:
                        </div>
                        <div className="flex gap-1.5">
                          <span className="px-1.5 py-0.2 rounded bg-white/5 border border-white/10 text-[10px] text-content-muted">app</span>
                          <span className="px-1.5 py-0.2 rounded bg-white/5 border border-white/10 text-[10px] text-content-muted">cli</span>
                        </div>
                      </div>
                    </div>

                    <div className="p-3 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-2 text-xs">
                      <label className="flex items-center gap-2 cursor-pointer text-[var(--text-main,#F1F5F9)]">
                        <input
                          type="checkbox"
                          checked={discordTime}
                          onChange={e => updateSetting('discordTime' as any, e.target.checked)}
                          className="w-3.5 h-3.5 rounded accent-[#5865f2]"
                        />
                        <span>Show Elapsed Session Time</span>
                      </label>
                      <label className="flex items-center gap-2 cursor-pointer text-[var(--text-main,#F1F5F9)]">
                        <input
                          type="checkbox"
                          checked={discordParty}
                          onChange={e => updateSetting('discordParty' as any, e.target.checked)}
                          className="w-3.5 h-3.5 rounded accent-[#5865f2]"
                        />
                        <span>Show Active Agents Party Size</span>
                      </label>
                      <label className="flex items-center gap-2 cursor-pointer text-[var(--text-main,#F1F5F9)]">
                        <input
                          type="checkbox"
                          checked={discordButtons}
                          onChange={e => updateSetting('discordButtons' as any, e.target.checked)}
                          className="w-3.5 h-3.5 rounded accent-[#5865f2]"
                        />
                        <span>Show Action Buttons</span>
                      </label>
                    </div>

                    <div className="flex justify-end gap-2 pt-1">
                      <Button variant="primary" size="xs" onClick={saveDiscordRpcSettings}>
                        Save RPC Settings
                      </Button>
                    </div>
                  </div>

                  {/* Authentic Discord Live Profile Card Simulator */}
                  <div className="space-y-2">
                    <span className="text-[10px] font-bold text-content-muted uppercase tracking-wider block">Live Profile Card Preview</span>
                    
                    <div className="rounded-2xl bg-[#232428] border border-[#1e1f22] overflow-hidden shadow-2xl text-white">
                      {/* Top banner gradient */}
                      <div className="h-16 bg-gradient-to-r from-[#5865f2] to-[#404eed] w-full"></div>

                      <div className="px-4 pb-4 space-y-3">
                        {/* Avatar row overlapping banner */}
                        <div className="relative -mt-8 flex items-end justify-between">
                          <div className="relative">
                            <div className="w-16 h-16 rounded-full bg-[#111214] p-1 shadow-md">
                              <div className="w-full h-full rounded-full bg-[#5865f2] flex items-center justify-center font-bold text-white text-base">
                                <svg viewBox="0 0 24 24" className="w-8 h-8 fill-white">
                                  <path d="M12 12c2.21 0 4-1.79 4-4s-1.79-4-4-4-4 1.79-4 4 1.79 4 4 4zm0 2c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z"/>
                                </svg>
                              </div>
                            </div>
                            <span className="w-4 h-4 rounded-full bg-emerald-500 border-[3px] border-[#232428] absolute bottom-0.5 right-0.5"></span>
                          </div>
                        </div>

                        {/* User Identity */}
                        <div>
                          <div className="text-sm font-bold flex items-center gap-1.5 leading-tight">
                            <span>Developer</span>
                            <span className="px-1 py-0.2 bg-[#5865f2] text-white text-[9px] font-black rounded uppercase tracking-wider">APP</span>
                          </div>
                          <div className="text-[11px] text-content-muted">@developer</div>
                        </div>

                        {/* Playing Activity Box */}
                        <div className="p-3.5 rounded-xl bg-[#111214] space-y-2 border border-white/5">
                          <div className="text-[10px] font-black uppercase text-content-muted tracking-wider">Playing a Game</div>
                          <div className="flex items-start gap-3">
                            <div className="relative shrink-0">
                              <div className="w-12 h-12 rounded-xl bg-gradient-to-br from-[#06b6d4] to-[#3b82f6] flex items-center justify-center text-white font-mono font-black text-lg shadow-inner">
                                A&gt;
                              </div>
                              <div className="w-5 h-5 rounded-full bg-[#1e1f22] border-2 border-[#111214] flex items-center justify-center text-[10px] font-mono font-bold text-accent absolute -bottom-1 -right-1 shadow">
                                &gt;_
                              </div>
                            </div>

                            <div className="space-y-0.5 min-w-0 flex-1">
                              <div className="font-bold text-xs text-white leading-tight">AgentsHub</div>
                              <div className="text-[11px] text-content-muted truncate">Coding with AgentsHub</div>
                              <div className="text-[10px] text-content-muted font-mono">⚡ AI Agent Station</div>
                              {discordTime && (
                                <div className="text-[10px] text-content-muted font-mono pt-0.5">{formatElapsed(elapsedSeconds)}</div>
                              )}
                            </div>
                          </div>

                          {discordButtons && (
                            <button
                              type="button"
                              className="w-full py-1.5 rounded-lg bg-[#2b2d31] hover:bg-[#35373c] text-white font-semibold text-xs transition-colors border border-white/5 shadow-sm text-center"
                            >
                              Get AgentsHub
                            </button>
                          )}
                        </div>
                      </div>
                    </div>

                    <div className="text-[10.5px] text-[var(--text-dim,#64748B)] text-center leading-relaxed">
                      Discord hides action buttons on your <strong>own</strong> desktop profile popout; all other users see the clickable button.
                    </div>
                  </div>
                </div>
              </div>

              {/* Pocket Commander (Mobile Remote Bot: Telegram & Discord) */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div className="flex items-center gap-2.5">
                    <div className="w-8 h-8 rounded-lg bg-[#0088cc]/15 border border-[#0088cc]/30 flex items-center justify-center text-[#0088cc]">
                      <Radio className="w-4 h-4" />
                    </div>
                    <div>
                      <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">POCKET COMMANDER (MOBILE REMOTE BOT)</h3>
                      <p className="text-xs text-[var(--text-dim,#64748B)]">
                        Control AgentsHub, inject prompts, inspect diffs, and receive push alerts from your phone
                      </p>
                    </div>
                  </div>

                  <div className="flex items-center gap-3">
                    <div className="flex items-center gap-2">
                      <span className="text-xs font-bold text-[var(--text-main,#F1F5F9)]">Master Switch:</span>
                      <span className={`text-[10px] font-bold px-1.5 py-0.2 rounded font-mono ${pocketEnabled ? 'bg-emerald-500/10 text-state-success border border-emerald-500/20' : 'bg-slate-500/10 text-content-muted border border-slate-500/20'}`}>
                        {pocketEnabled ? 'ON' : 'OFF'}
                      </span>
                      <label className="cursor-pointer">
                        <input
                          type="checkbox"
                          checked={pocketEnabled}
                          onChange={e => useSettingsStore.setState({ pocketEnabled: e.target.checked })}
                          className="w-4 h-4 rounded accent-sky-500"
                        />
                      </label>
                    </div>

                    <Button variant="secondary" size="xs" onClick={testPocketBot}>
                      Test Active Bot
                    </Button>
                    <Button variant="primary" size="xs" onClick={savePocketCommanderConfig}>
                      Save Pocket Commander
                    </Button>
                  </div>
                </div>

                {/* Platform Selector Chips */}
                <div className="flex items-center gap-2 pt-1 border-b border-[var(--border-subtle,#1E293B)] pb-3">
                  <span className="text-xs font-bold text-[var(--text-muted,#94A3B8)] mr-1">Choose Platform:</span>
                  {(['telegram', 'discord', 'both'] as const).map(p => (
                    <button
                      key={p}
                      onClick={() => {
                        setPocketTab(p);
                        useSettingsStore.setState({ pocketPlatform: p });
                      }}
                      className={`px-3 py-1 rounded text-xs font-semibold capitalize transition-all ${
                        pocketTab === p
                          ? p === 'telegram'
                            ? 'bg-[#0088cc]/20 text-[#0088cc] border border-[#0088cc]/40'
                            : p === 'discord'
                            ? 'bg-[#5865f2]/20 text-[#5865f2] border border-[#5865f2]/40'
                            : 'bg-emerald-500/20 text-state-success border border-emerald-500/40'
                          : 'bg-[var(--bg-input,#0D1322)] text-[var(--text-dim,#64748B)] hover:text-white border border-[var(--border-subtle,#1E293B)]'
                      }`}
                    >
                      {p === 'telegram' ? 'Telegram Bot' : p === 'discord' ? 'Discord Bot' : 'Dual Mode (Both)'}
                    </button>
                  ))}
                </div>

                {/* Pairing Cards */}
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  {/* Telegram */}
                  {(pocketTab === 'telegram' || pocketTab === 'both') && (
                    <div className="p-4 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-3">
                      <div className="flex items-center justify-between">
                        <span className="text-xs font-bold text-accent">Telegram Bot</span>
                        <a href="https://t.me/BotFather" target="_blank" rel="noreferrer" className="text-[11px] text-accent hover:underline">
                          Get from @BotFather &rarr;
                        </a>
                      </div>
                      <input
                        type="password"
                        value={telegramToken}
                        onChange={e => updateSetting('telegramToken' as any, e.target.value)}
                        placeholder="Telegram Bot Token..."
                        className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)] font-mono focus:outline-none"
                      />

                      <div className="flex items-center justify-between text-xs pt-1">
                        <span className="text-[var(--text-dim,#64748B)]">Pairing PIN:</span>
                        <div className="flex items-center gap-2">
                          <span className="px-2.5 py-0.5 rounded bg-emerald-500/10 text-state-success font-mono font-bold tracking-widest border border-emerald-500/20">
                            {telegramPin}
                          </span>
                          <Button variant="secondary" size="xs" onClick={() => generatePocketPin('telegram')}>
                            New PIN
                          </Button>
                          {telegramPairedUser && (
                            <Button variant="secondary" size="xs" onClick={() => unpairPocket('telegram')}>
                              Unpair
                            </Button>
                          )}
                        </div>
                      </div>

                      <div className="p-2.5 rounded bg-[#0088cc]/10 border border-[#0088cc]/20 text-[11px] text-sky-200 flex items-center justify-between gap-2">
                        <span>Telegram Pairing: Start chat with your bot and reply with your 6-digit PIN.</span>
                        {telegramPairedUser && (
                          <span className="px-1.5 py-0.2 rounded bg-emerald-500/20 text-state-success font-mono text-[10px] shrink-0">
                            Paired: {telegramPairedUser}
                          </span>
                        )}
                      </div>
                    </div>
                  )}

                  {/* Discord */}
                  {(pocketTab === 'discord' || pocketTab === 'both') && (
                    <div className="p-4 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-3">
                      <div className="flex items-center justify-between">
                        <span className="text-xs font-bold text-[#5865f2]">Discord Bot</span>
                        <a href="https://discord.com/developers/applications" target="_blank" rel="noreferrer" className="text-[11px] text-[#5865f2] hover:underline">
                          Developer Portal &rarr;
                        </a>
                      </div>
                      <input
                        type="password"
                        value={discordToken}
                        onChange={e => updateSetting('discordToken' as any, e.target.value)}
                        placeholder="Discord Bot Token..."
                        className="w-full px-2.5 py-1.5 rounded bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] text-xs text-[var(--text-main,#F1F5F9)] font-mono focus:outline-none"
                      />

                      <div className="flex items-center justify-between text-xs pt-1">
                        <span className="text-[var(--text-dim,#64748B)]">Pairing PIN:</span>
                        <div className="flex items-center gap-2">
                          <span className="px-2.5 py-0.5 rounded bg-emerald-500/10 text-state-success font-mono font-bold tracking-widest border border-emerald-500/20">
                            {discordPin}
                          </span>
                          <Button variant="secondary" size="xs" onClick={() => generatePocketPin('discord')}>
                            New PIN
                          </Button>
                          {discordPairedUser && (
                            <Button variant="secondary" size="xs" onClick={() => unpairPocket('discord')}>
                              Unpair
                            </Button>
                          )}
                        </div>
                      </div>

                      <div className="p-2.5 rounded bg-[#5865f2]/10 border border-[#5865f2]/20 text-[11px] text-indigo-200 flex items-center justify-between gap-2">
                        <span>Discord Pairing: Direct message (DM) your bot with your 6-digit PIN.</span>
                        {discordPairedUser && (
                          <span className="px-1.5 py-0.2 rounded bg-emerald-500/20 text-state-success font-mono text-[10px] shrink-0">
                            Paired: {discordPairedUser}
                          </span>
                        )}
                      </div>
                    </div>
                  )}
                </div>

                {/* 6 Remote Commands & Control Feature Switches */}
                <div className="space-y-2 pt-2 border-t border-[var(--border-subtle,#1E293B)]">
                  <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)] uppercase tracking-wider">
                    Remote Commands & Interactive Control
                  </div>
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-2.5">
                    {[
                      { key: 'allowRun', title: 'Start Profiles (/run)', desc: 'Launch synced CLI profiles & agent sessions', icon: Play, color: 'text-blue-400' },
                      { key: 'allowActive', title: 'Active Switcher (/active)', desc: 'Select active target among running agents', icon: RefreshCw, color: 'text-accent' },
                      { key: 'allowPrompts', title: 'Prompt Injection (/prompt)', desc: 'Send prompts into running agent stdin', icon: Send, color: 'text-state-success' },
                      { key: 'allowScreen', title: 'Desktop Screenshot (/screen)', desc: 'Capture & send live desktop screen JPEG', icon: Activity, color: 'text-state-warning' },
                      { key: 'allowDiff', title: 'Git Diff (/diff)', desc: 'Inspect modified files and commit diff stats', icon: Share2, color: 'text-purple-400' },
                      { key: 'allowCancel', title: 'Stop Agent (/stop)', desc: 'Send Ctrl+C SIGINT to halt active session', icon: Power, color: 'text-state-danger' },
                    ].map(feat => {
                      const FeatIcon = feat.icon;
                      const isChecked = (pocketFeatures as any)[feat.key] ?? true;
                      return (
                        <div
                          key={feat.key}
                          className="p-3 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] flex items-center justify-between gap-3"
                        >
                          <div className="flex items-center gap-2.5 min-w-0">
                            <FeatIcon className={`w-4 h-4 shrink-0 ${feat.color}`} />
                            <div className="min-w-0">
                              <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)] truncate">{feat.title}</div>
                              <div className="text-[11px] text-[var(--text-dim,#64748B)] truncate">{feat.desc}</div>
                            </div>
                          </div>

                          <div className="flex items-center gap-2 shrink-0">
                            <span className={`text-[10px] font-bold font-mono px-1.5 py-0.2 rounded ${isChecked ? 'bg-emerald-500/10 text-state-success border border-emerald-500/20' : 'bg-slate-500/10 text-content-muted'}`}>
                              {isChecked ? 'ON' : 'OFF'}
                            </span>
                            <input
                              type="checkbox"
                              checked={isChecked}
                              onChange={e => useSettingsStore.setState(s => ({ pocketFeatures: { ...s.pocketFeatures, [feat.key]: e.target.checked } }))}
                              className="w-4 h-4 rounded accent-sky-500 cursor-pointer"
                            />
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>

                {/* 3 Push Notification Alerts Switches */}
                <div className="space-y-2 pt-2 border-t border-[var(--border-subtle,#1E293B)]">
                  <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)] uppercase tracking-wider">
                    Push Notification Alerts
                  </div>
                  <div className="grid grid-cols-1 md:grid-cols-3 gap-2.5">
                    {[
                      { key: 'notifyDone', title: 'Task Finished Alert', desc: 'Push message when an agent finishes work', color: 'text-state-success' },
                      { key: 'notifyError', title: 'Error & Failure Alert', desc: 'Immediate alert if an agent throws error', color: 'text-state-danger' },
                      { key: 'notifyApproval', title: 'Approval Needed Alert', desc: 'Notify when agent pauses for permission', color: 'text-state-warning' },
                    ].map(ntf => {
                      const isChecked = (pocketNotifications as any)[ntf.key] ?? true;
                      return (
                        <div
                          key={ntf.key}
                          className="p-3 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] flex items-center justify-between gap-2"
                        >
                          <div>
                            <div className={`text-xs font-bold ${ntf.color}`}>{ntf.title}</div>
                            <div className="text-[10px] text-[var(--text-dim,#64748B)] mt-0.5">{ntf.desc}</div>
                          </div>

                          <div className="flex items-center gap-1.5 shrink-0">
                            <span className={`text-[10px] font-bold font-mono px-1.5 py-0.2 rounded ${isChecked ? 'bg-emerald-500/10 text-state-success' : 'bg-slate-500/10 text-content-muted'}`}>
                              {isChecked ? 'ON' : 'OFF'}
                            </span>
                            <input
                              type="checkbox"
                              checked={isChecked}
                              onChange={e => useSettingsStore.setState(s => ({ pocketNotifications: { ...s.pocketNotifications, [ntf.key]: e.target.checked } }))}
                              className="w-4 h-4 rounded accent-sky-500 cursor-pointer"
                            />
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              </div>
            </div>
          )}

          {/* ======================================================== */}
          {/* 7. SECURITY & HEALTH */}
          {/* ======================================================== */}
          {activeCategory === 'devtools' && <DevToolsPanel />}

          {activeCategory === 'lan' && <LanSettings />}

          {activeCategory === 'security' && (
            <div className="max-w-3xl space-y-6">
              {/* Diagnostics Doctor */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div>
                    <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">SYSTEM HEALTH & DIAGNOSTICS DOCTOR</h3>
                    <p className="text-xs text-[var(--text-dim,#64748B)]">
                      Automated diagnostics for developer runtimes, PATH variables, network bindings, and firewall
                    </p>
                  </div>

                  <div className="flex items-center gap-2">
                    {diagnosticsScore ? (
                      <span className="px-2 py-0.5 rounded text-[11px] font-mono bg-emerald-500/10 text-state-success border border-emerald-500/20 font-bold">
                        {diagnosticsScore}
                      </span>
                    ) : null}
                    <Button
                      variant="primary"
                      size="xs"
                      leftIcon={<img src={actionIcons.refresh} alt="" className={`h-3.5 w-3.5 object-contain ${diagnosticsScanning ? 'animate-spin' : ''}`} />}
                      onClick={runDiagnostics}
                    >
                      Run Scan
                    </Button>
                  </div>
                </div>

                {diagnostics.length === 0 ? (
                  <p className="rounded-lg border border-dashed border-[var(--border-subtle,#1E293B)] px-4 py-8 text-center text-[11px] text-[var(--text-dim,#64748B)]">
                    No scan has run yet. Press Run Scan to measure this machine.
                  </p>
                ) : (
                <div className="grid grid-cols-1 md:grid-cols-2 gap-3 pt-2">
                  {diagnostics.map(item => (
                    <div
                      key={item.id}
                      className="p-3.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] flex flex-col justify-between gap-2"
                    >
                      <div>
                        <div className="flex items-center justify-between text-xs font-semibold">
                          <span className="text-[var(--text-main,#F1F5F9)]">{item.title}</span>
                          <span
                            className={`px-1.5 py-0.2 rounded text-[10px] font-bold ${
                              item.status === 'Pass'
                                ? 'bg-emerald-500/10 text-state-success'
                                : item.status === 'Warning'
                                ? 'bg-amber-500/10 text-state-warning'
                                : 'bg-rose-500/10 text-state-danger'
                            }`}
                          >
                            {item.status}
                          </span>
                        </div>
                        <p className="text-[11px] text-[var(--text-dim,#64748B)] mt-1">{item.details}</p>
                      </div>

                      {item.canAutoFix && (
                        <div className="flex justify-end pt-1">
                          <Button variant="secondary" size="xs" onClick={() => autoFixDiagnostic(item.id)}>
                            {item.fixLabel || 'Fix'}
                          </Button>
                        </div>
                      )}
                    </div>
                  ))}
                </div>
                )}
              </div>

              {/* Pre-Commit Security Gatekeeper */}
              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-3">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div>
                    <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">PRE-COMMIT AI SECURITY GATEKEEPER</h3>
                    <p className="text-xs text-[var(--text-dim,#64748B)]">
                      Real-time working tree secret detection, dangerous command blocker & safety scoring
                    </p>
                  </div>

                  <Button variant="secondary" size="xs" onClick={runSecurityAudit}>
                    Audit Workspace Now
                  </Button>
                </div>

                {!securityAuditResults ? (
                  <p className="text-xs text-[var(--text-dim,#64748B)] leading-relaxed">
                    Scans uncommitted files for API tokens (OpenAI, Anthropic, AWS, GitHub PATs), private keys, and shell injection hazards before changes get checked in.
                  </p>
                ) : (
                  <div className="space-y-2">
                    <div className="flex flex-wrap items-center gap-2 font-mono text-[11px]">
                      <span
                        className={`rounded border px-1.5 py-0.5 font-bold ${
                          securityAuditResults.score >= 90
                            ? 'border-emerald-500/30 bg-emerald-500/10 text-state-success'
                            : securityAuditResults.score >= 70
                              ? 'border-amber-500/30 bg-amber-500/10 text-state-warning'
                              : 'border-rose-500/30 bg-rose-500/10 text-state-danger'
                        }`}
                      >
                        {securityAuditResults.score}/100
                      </span>
                      <span className="text-[var(--text-dim,#64748B)]">
                        {securityAuditResults.scannedFilesCount} files scanned · {securityAuditResults.issuesCount} issues
                      </span>
                      <Button variant="secondary" size="xs" onClick={() => void reviewAuditWithAgent()}>
                        Review with agent
                      </Button>
                    </div>

                    {(securityAuditResults.issues ?? []).length === 0 ? (
                      <p className="text-[11px] text-state-success">No secrets or hazards found in the working tree.</p>
                    ) : (
                      <div className="max-h-56 space-y-1.5 overflow-y-auto">
                        {(securityAuditResults.issues ?? []).slice(0, 40).map((issue: any, index: number) => (
                          <div
                            key={`${issue.filePath}-${issue.lineNumber}-${index}`}
                            className="rounded-lg border border-[var(--border-subtle,#1E293B)] px-3 py-1.5"
                          >
                            <div className="flex items-center gap-2 font-mono text-[10.5px]">
                              <span
                                className={`rounded border px-1.5 py-0.5 text-[9px] font-bold ${
                                  issue.severity === 'Critical'
                                    ? 'border-rose-500/30 bg-rose-500/10 text-state-danger'
                                    : 'border-amber-500/30 bg-amber-500/10 text-state-warning'
                                }`}
                              >
                                {issue.severity}
                              </span>
                              <span className="text-[var(--text-main,#F1F5F9)]">{issue.type}</span>
                              <span className="truncate text-[var(--text-dim,#64748B)]">
                                {issue.filePath}:{issue.lineNumber}
                              </span>
                            </div>
                            {issue.matchSnippet ? (
                              <div className="mt-1 truncate font-mono text-[10px] text-[var(--text-muted,#94A3B8)]">
                                {issue.matchSnippet}
                              </div>
                            ) : null}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                )}
              </div>
            </div>
          )}

          {/* ======================================================== */}
          {/* 8. LIVE LOGS */}
          {/* ======================================================== */}
          {activeCategory === 'logs' && (
            <div className="max-w-4xl space-y-4">
              <TelemetryPanel />

              <div className="p-5 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-4">
                <div className="flex items-center justify-between flex-wrap gap-2">
                  <div className="flex items-center gap-2">
                    <span className="w-2.5 h-2.5 rounded-full bg-emerald-400 animate-pulse"></span>
                    <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">System & CLI Execution Logs</h3>
                    <span className="text-[11px] font-mono text-accent">({filteredLogs.length} entries)</span>
                  </div>

                  <div className="flex items-center gap-2">
                    <div className="relative">
                      <img src={actionIcons.search} alt="" className="h-3 w-3 object-contain absolute left-2.5 top-1/2 -translate-y-1/2" />
                      <input
                        type="text"
                        value={logsSearch}
                        onChange={e => setLogsSearch(e.target.value)}
                        placeholder="Search logs..."
                        className="pl-7 pr-2 py-1 text-xs rounded bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none w-36"
                      />
                    </div>
                    <Button variant="secondary" size="xs" onClick={() => setAutoScroll(!autoScroll)}>
                      {autoScroll ? 'Auto-Scroll: ON' : 'Auto-Scroll: OFF'}
                    </Button>
                    <Button variant="secondary" size="xs" onClick={handleCopyLogs}>
                      {copiedLog ? 'Copied!' : 'Copy'}
                    </Button>
                    <Button variant="secondary" size="xs" onClick={clearLogs}>
                      Clear
                    </Button>
                  </div>
                </div>

                {/* Filter pills */}
                <div className="flex items-center gap-1.5 overflow-x-auto text-xs">
                  {['all', 'error', 'warn', 'system', 'clis'].map(f => (
                    <button
                      key={f}
                      onClick={() => setLogsFilter(f)}
                      className={`px-2.5 py-1 rounded text-[11px] font-medium capitalize transition-all ${
                        logsFilter === f
                          ? 'bg-sky-500/20 text-accent font-bold border border-sky-500/30'
                          : 'bg-[var(--bg-input,#0D1322)] text-[var(--text-dim,#64748B)] hover:text-white'
                      }`}
                    >
                      {f}
                    </button>
                  ))}
                </div>

                {/* Console view */}
                <div
                  ref={logConsoleRef}
                  className="p-2.5 rounded-xl bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] font-mono text-[11px] text-content-muted h-80 overflow-y-auto select-text"
                >
                  {/* Fixed columns: a wrapped source name used to push the message onto separate
                      lines and make two adjacent entries look like four. */}
                  {filteredLogs.map(l => (
                    <div
                      key={l.id}
                      className="grid grid-cols-[58px_40px_132px_1fr] items-baseline gap-2 rounded px-1 py-[3px] leading-relaxed hover:bg-white/[0.03]"
                    >
                      <span className="tabular-nums text-[10px] text-[var(--text-dim,#64748B)]">{l.timestamp}</span>
                      <span
                        className={`text-center text-[9px] font-bold uppercase tracking-wide rounded ${
                          l.level === 'error' ? 'text-state-danger bg-rose-500/10' : l.level === 'warn' ? 'text-state-warning bg-amber-500/10' : 'text-state-success bg-emerald-500/10'
                        }`}
                      >
                        {l.level}
                      </span>
                      <span className="truncate text-accent" title={l.source}>{l.source}</span>
                      <span className="break-words text-content-main">{l.message}</span>
                    </div>
                  ))}

                  {filteredLogs.length === 0 && (
                    <div className="text-center text-xs text-[var(--text-dim,#64748B)] py-8">
                      No logs matching current filter.
                    </div>
                  )}
                </div>
              </div>
            </div>
          )}

          {/* ======================================================== */}
          {/* 9. ABOUT & UPDATES */}
          {/* ======================================================== */}
          {activeCategory === 'about' && (
            <div className="max-w-3xl space-y-6">
              {/* Main About & Updates Card */}
              <div className="p-6 rounded-xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] space-y-5">
                {/* Header with AH Gradient Avatar */}
                <div className="flex items-center gap-4 pb-4 border-b border-[var(--border-subtle,#1E293B)]">
                  <div className="w-14 h-14 rounded-2xl bg-gradient-to-br from-sky-500 via-indigo-600 to-emerald-500 flex items-center justify-center text-white font-black text-xl shadow-xl shadow-sky-500/20 shrink-0">
                    AH
                  </div>
                  <div className="min-w-0">
                    <div className="flex items-center gap-2.5 flex-wrap">
                      <h3 className="text-base font-black tracking-wide text-[var(--text-main,#F1F5F9)]">
                        ABOUT AGENTS HUB
                      </h3>
                      <span className="px-2 py-0.5 rounded-full text-xs font-mono font-bold bg-sky-500/10 text-accent border border-sky-500/20">
                        v{updateInfo?.currentVersion || '1.0.5'}
                      </span>
                    </div>
                    <p className="text-xs text-[var(--text-dim,#64748B)] font-mono mt-0.5 truncate">
                      Build 2026.10 • .NET 10.0-windows • ConPTY Engine • WebView2 • React 19
                    </p>
                  </div>
                </div>

                {/* Summary Paragraph */}
                <p className="text-xs text-[var(--text-muted,#94A3B8)] leading-relaxed">
                  Agents Hub runs Hub, Claude Code and Antigravity CLI from one unified window. Each agent gets a real
                  terminal session with its own credentials, and a port you can open in a browser or hand to a phone
                  on the same network. It ships as a single portable executable built on .NET 10, ConPTY and WebView2.
                </p>

                {/* Auto Update Management Section */}
                <div className="p-4 rounded-xl bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-3">
                  <div className="flex items-center justify-between flex-wrap gap-3">
                    <div className="flex items-center gap-3">
                      <div
                        className={`w-2.5 h-2.5 rounded-full transition-all duration-300 ${
                          updateStatus === 'checking'
                            ? 'bg-cyan-400 animate-pulse ring-4 ring-cyan-500/20'
                            : updateStatus === 'available' || updateStatus === 'uptodate' || updateStatus === 'downloaded'
                            ? 'bg-emerald-400 ring-4 ring-emerald-500/20'
                            : updateStatus === 'error'
                            ? 'bg-rose-500 ring-4 ring-rose-500/20'
                            : 'bg-slate-500'
                        }`}
                      />
                      <div>
                        <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)]">
                          {updateStatusText || 'Check for updates'}
                        </div>
                        <div className="text-[11px] text-[var(--text-dim,#64748B)] mt-0.5">
                          {updateStatusSubtext || 'Target: github.com/ZerroDevs/AgentsHub'}
                        </div>
                      </div>
                    </div>

                    <div className="flex items-center gap-2">
                      <Button
                        variant="secondary"
                        size="xs"
                        disabled={updateStatus === 'checking' || updateStatus === 'downloading'}
                        leftIcon={<img src={actionIcons.refresh} alt="" className={`h-3.5 w-3.5 object-contain ${updateStatus === 'checking' ? 'animate-spin' : ''}`} />}
                        onClick={() => checkForUpdates(true)}
                      >
                        {updateStatus === 'checking' ? 'Checking...' : 'Check Now'}
                      </Button>

                      {updateInfo?.isUpdateAvailable && (
                        <button
                          type="button"
                          disabled={updateStatus === 'downloading'}
                          onClick={() => installUpdate(updateInfo?.downloadUrl)}
                          className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-bold bg-emerald-500 hover:bg-emerald-600 text-slate-950 transition-all shadow-md shadow-emerald-500/20 disabled:opacity-50 cursor-pointer"
                        >
                          <img src={actionIcons.download} alt="" className={`h-3.5 w-3.5 object-contain ${updateStatus === 'downloading' ? 'animate-bounce' : ''}`} />
                          <span>{updateStatus === 'downloading' ? 'Downloading...' : 'Download & Install'}</span>
                        </button>
                      )}
                    </div>
                  </div>

                  {/* Download Progress Bar */}
                  {(updateStatus === 'downloading' || downloadProgress > 0) && (
                    <div className="space-y-1.5 pt-2 border-t border-[var(--border-subtle,#1E293B)]">
                      <div className="flex items-center justify-between text-xs text-[var(--text-dim,#64748B)]">
                        <span>{downloadProgressLabel || 'Downloading installer package...'}</span>
                        <span className="font-mono font-bold text-state-success">{downloadProgress}%</span>
                      </div>
                      <div className="w-full h-2 rounded-full bg-[var(--bg-app,#080D1A)] border border-[var(--border-subtle,#1E293B)] overflow-hidden">
                        <div
                          className="h-full bg-gradient-to-r from-emerald-500 to-teal-400 transition-all duration-200"
                          style={{ width: `${downloadProgress}%` }}
                        />
                      </div>
                    </div>
                  )}

                  {/* Release Notes Preview Container */}
                  {updateInfo?.isUpdateAvailable && (
                    <div className="p-3.5 rounded-xl bg-[var(--bg-app,#080D1A)] border border-emerald-500/30 space-y-2 mt-2">
                      <div className="flex items-center justify-between">
                        <span className="text-[11px] font-bold tracking-wider text-state-success uppercase flex items-center gap-1.5">
                          <span className="w-2 h-2 rounded-full bg-emerald-400 animate-ping"></span>
                          New Release Available
                        </span>
                        <span className="text-xs font-mono text-accent font-bold px-2 py-0.5 rounded bg-cyan-500/10 border border-cyan-500/20">
                          v{updateInfo.latestVersion}
                        </span>
                      </div>
                      <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)]">
                        {updateInfo.releaseName || `Release v${updateInfo.latestVersion}`}
                      </div>
                      <div className="text-xs font-mono text-[var(--text-dim,#64748B)] max-h-36 overflow-y-auto whitespace-pre-wrap leading-relaxed p-2.5 rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)]">
                        {updateInfo.releaseNotes || 'No release notes provided.'}
                      </div>
                    </div>
                  )}
                </div>

                {/* Quick-Start User Guide & Tour Launcher Card */}
                <div className="p-4 rounded-xl bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] flex items-center justify-between flex-wrap gap-3">
                  <div className="flex items-center gap-3">
                    <div className="w-10 h-10 rounded-xl bg-emerald-500/10 border border-emerald-500/20 flex items-center justify-center text-state-success shrink-0">
                      <HelpCircle className="w-5 h-5" />
                    </div>
                    <div>
                      <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)]">
                        User Guide & Quick-Start Tour
                      </div>
                      <div className="text-[11px] text-[var(--text-dim,#64748B)] mt-0.5">
                        How the app works, the chat engines, runtime installation and shortcuts (F1, any view)
                      </div>
                    </div>
                  </div>
                  <Button
                    variant="primary"
                    size="xs"
                    leftIcon={<HelpCircle className="w-3.5 h-3.5" />}
                    onClick={openGuide}
                  >
                    Open User Guide
                  </Button>
                </div>

                {/* External Links 4-card Grid */}
                <div className="grid grid-cols-1 md:grid-cols-2 gap-2.5">
                  {[
                    {
                      icon: HelpCircle,
                      label: 'User Guide',
                      value: 'Launch Interactive Guide (F1)',
                      action: openGuide,
                    },
                    {
                      icon: Terminal,
                      label: 'Repository',
                      value: 'github.com/ZerroDevs/AgentsHub',
                      action: () => webviewBridge.invoke('open_url_external', { url: 'https://github.com/ZerroDevs/AgentsHub' }),
                    },
                    {
                      icon: ShieldCheck,
                      label: 'License',
                      value: 'Source-Available',
                      action: () => webviewBridge.invoke('open_url_external', { url: 'https://github.com/ZerroDevs/AgentsHub/blob/main/LICENSE' }),
                    },
                    {
                      icon: Info,
                      label: 'Author',
                      value: 'ZerroDevs',
                      action: () => webviewBridge.invoke('open_url_external', { url: 'https://github.com/ZerroDevs' }),
                    },
                  ].map((link, i) => {
                    const Icon = link.icon;
                    return (
                      <button
                        key={i}
                        type="button"
                        onClick={link.action}
                        className="p-3.5 rounded-xl bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] hover:border-sky-500/30 hover:bg-[var(--bg-card,#0F172A)] transition-all flex items-center justify-between text-left group cursor-pointer"
                      >
                        <div className="flex items-center gap-3 min-w-0">
                          <div className="w-8 h-8 rounded-lg bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] flex items-center justify-center text-accent group-hover:scale-105 transition-transform shrink-0">
                            <Icon className="w-4 h-4" />
                          </div>
                          <div className="min-w-0">
                            <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)]">{link.label}</div>
                            <div className="text-[11px] text-[var(--text-dim,#64748B)] truncate">{link.value}</div>
                          </div>
                        </div>
                        <img src={actionIcons.external} alt="" className="h-3.5 w-3.5 object-contain shrink-0 ml-2" />
                      </button>
                    );
                  })}
                </div>

                {/* System Diagnostics & Specifications Grid */}
                <div className="p-4 rounded-xl bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-3">
                  <div className="text-xs font-bold text-[var(--text-main,#F1F5F9)] uppercase tracking-wider flex items-center gap-2">
                    <Cpu className="w-4 h-4 text-accent" />
                    System Diagnostics & Specifications
                  </div>
                  <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-2.5">
                    {[
                      { label: '.NET Runtime', value: '.NET 10.0-windows (CoreCLR)' },
                      { label: 'Terminal Engine', value: 'Windows ConPTY Subsystem' },
                      { label: 'WebView Runtime', value: 'Microsoft WebView2 (Evergreen)' },
                      { label: 'Platform Architecture', value: 'x64 (Windows NT 10.0+)' },
                      { label: 'Local Virtual Host', value: 'https://app.agentshub.local' },
                      { label: 'Agent Engine Daemon', value: 'Bun / TypeScript Standalone' },
                    ].map((spec, i) => (
                      <div key={i} className="p-2.5 rounded-lg bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)]">
                        <div className="text-[10px] text-[var(--text-dim,#64748B)] uppercase tracking-wider">{spec.label}</div>
                        <div className="text-xs font-mono font-semibold text-[var(--text-main,#F1F5F9)] mt-0.5 truncate">{spec.value}</div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            </div>
          )}
        </div>
      </div>

      {/* ======================================================== */}
      {/* MODAL 1: ADD CUSTOM CLI ENGINE */}
      {/* ======================================================== */}
      {showAddCliModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-surface-sunken backdrop-blur-sm animate-in fade-in">
          <div className="w-full max-w-md p-6 rounded-2xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] shadow-2xl space-y-4">
            <div className="flex items-center justify-between pb-3 border-b border-[var(--border-subtle,#1E293B)]">
              <div className="flex items-center gap-2.5">
                <Terminal className="w-5 h-5 text-accent" />
                <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">Add Custom AI CLI Engine</h3>
              </div>
              <button onClick={() => setShowAddCliModal(false)} className="text-[var(--text-dim,#64748B)] hover:text-white">
                <X className="w-4 h-4" />
              </button>
            </div>

            <div className="space-y-3">
              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Engine Name</label>
                <input
                  type="text"
                  placeholder="e.g. Ollama Local, Cursor CLI, Aider..."
                  value={newCliName}
                  onChange={e => setNewCliName(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Executable Path / Command</label>
                <input
                  type="text"
                  placeholder="e.g. aider, C:\ollama\ollama.exe..."
                  value={newCliExe}
                  onChange={e => setNewCliExe(e.target.value)}
                  className="w-full px-3 py-2 text-xs font-mono rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Default Arguments (Optional)</label>
                <input
                  type="text"
                  placeholder="e.g. --model deepseek-r1:7b"
                  value={newCliArgs}
                  onChange={e => setNewCliArgs(e.target.value)}
                  className="w-full px-3 py-2 text-xs font-mono rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Working Directory (Optional)</label>
                <input
                  type="text"
                  placeholder="e.g. C:\projects\workspace"
                  value={newCliDir}
                  onChange={e => setNewCliDir(e.target.value)}
                  className="w-full px-3 py-2 text-xs font-mono rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Color Tag</label>
                <div className="flex items-center gap-2">
                  {['#38bdf8', '#10b981', '#8b5cf6', '#f59e0b', '#ec4899'].map(c => (
                    <button
                      key={c}
                      type="button"
                      onClick={() => setNewCliColor(c)}
                      className={`w-6 h-6 rounded-full transition-transform ${newCliColor === c ? 'scale-125 ring-2 ring-white' : 'opacity-70 hover:opacity-100'}`}
                      style={{ backgroundColor: c }}
                    />
                  ))}
                </div>
              </div>
            </div>

            <div className="flex justify-end gap-2 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
              <Button variant="secondary" size="xs" onClick={() => setShowAddCliModal(false)}>
                Cancel
              </Button>
              <Button
                variant="primary"
                size="xs"
                disabled={!newCliName.trim() || !newCliExe.trim()}
                onClick={async () => {
                  if (!newCliName.trim() || !newCliExe.trim()) return;
                  await saveCustomCli({
                    id: `cli_${Date.now()}`,
                    name: newCliName.trim(),
                    executablePath: newCliExe.trim(),
                    arguments: newCliArgs.trim(),
                    workingDirectory: newCliDir.trim() || undefined,
                    colorTag: newCliColor,
                  });
                  setShowAddCliModal(false);
                  setNewCliName('');
                  setNewCliExe('');
                  setNewCliArgs('');
                  setNewCliDir('');
                }}
              >
                Save CLI Engine
              </Button>
            </div>
          </div>
        </div>
      )}

      {/* ======================================================== */}
      {/* MODAL 2: ADD MCP SERVER */}
      {/* ======================================================== */}
      {showAddMcpModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-surface-sunken backdrop-blur-sm animate-in fade-in">
          <div className="w-full max-w-md p-6 rounded-2xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] shadow-2xl space-y-4">
            <div className="flex items-center justify-between pb-3 border-b border-[var(--border-subtle,#1E293B)]">
              <div className="flex items-center gap-2.5">
                <Boxes className="w-5 h-5 text-accent" />
                <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">Register MCP Server</h3>
              </div>
              <button onClick={() => setShowAddMcpModal(false)} className="text-[var(--text-dim,#64748B)] hover:text-white">
                <X className="w-4 h-4" />
              </button>
            </div>

            <div className="space-y-3">
              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Server Identifier Name</label>
                <input
                  type="text"
                  placeholder="e.g. filesystem, github, fetch, puppeteer..."
                  value={newMcpName}
                  onChange={e => setNewMcpName(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Command Runner</label>
                <select
                  value={newMcpCommand}
                  onChange={e => setNewMcpCommand(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                >
                  <option value="node">node</option>
                  <option value="npx">npx</option>
                  <option value="python">python</option>
                  <option value="uvx">uvx</option>
                  <option value="bun">bun</option>
                </select>
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Command Arguments</label>
                <input
                  type="text"
                  placeholder="e.g. -y @modelcontextprotocol/server-filesystem C:\projects"
                  value={newMcpArgs}
                  onChange={e => setNewMcpArgs(e.target.value)}
                  className="w-full px-3 py-2 text-xs font-mono rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Description (Optional)</label>
                <input
                  type="text"
                  placeholder="File system access MCP bridge"
                  value={newMcpDesc}
                  onChange={e => setNewMcpDesc(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>
            </div>

            <div className="flex justify-end gap-2 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
              <Button variant="secondary" size="xs" onClick={() => setShowAddMcpModal(false)}>
                Cancel
              </Button>
              <Button
                variant="primary"
                size="xs"
                disabled={!newMcpName.trim()}
                onClick={async () => {
                  if (!newMcpName.trim()) return;
                  await saveMcpServer({
                    id: `mcp_${Date.now()}`,
                    name: newMcpName.trim(),
                    command: newMcpCommand.trim(),
                    args: newMcpArgs.trim() ? newMcpArgs.trim().split(' ') : [],
                    description: newMcpDesc.trim(),
                  });
                  setShowAddMcpModal(false);
                  setNewMcpName('');
                  setNewMcpCommand('node');
                  setNewMcpArgs('');
                  setNewMcpDesc('');
                }}
              >
                Add MCP Server
              </Button>
            </div>
          </div>
        </div>
      )}

      {/* ======================================================== */}
      {/* MODAL: CONFIGURE MCP AUTHENTICATION */}
      {/* ======================================================== */}
      {showMcpAuthModal && authModalServer && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-surface-sunken backdrop-blur-sm animate-in fade-in select-none">
          <div className="w-full max-w-lg p-6 rounded-2xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] shadow-2xl space-y-4">
            <div className="flex items-center justify-between pb-3 border-b border-[var(--border-subtle,#1E293B)]">
              <div className="flex items-center gap-2.5">
                <img src={actionIcons.key} alt="" className="h-5 w-5 object-contain" />
                <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">
                  Configure Authentication: {authModalServer.name}
                </h3>
              </div>
              <button
                onClick={() => setShowMcpAuthModal(false)}
                className="text-[var(--text-dim,#64748B)] hover:text-white transition"
              >
                <X className="w-4 h-4" />
              </button>
            </div>

            {/* Server Status Header Box */}
            <div className="p-3 rounded-xl bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] space-y-1.5">
              <div className="flex items-center justify-between">
                <span className="font-mono text-xs font-bold text-[var(--text-main,#F1F5F9)]">
                  {authModalServer.name}
                </span>
                <span
                  className={`px-2 py-0.5 rounded text-[10px] font-mono font-semibold border ${
                    authModalServer.authStatus === 'Authenticated'
                      ? 'bg-emerald-500/10 text-state-success border-emerald-500/20'
                      : authModalServer.authStatus === 'TokenExpired'
                      ? 'bg-amber-500/10 text-state-warning border-amber-500/20'
                      : 'bg-rose-500/10 text-state-danger border-rose-500/20'
                  }`}
                >
                  {authModalServer.authStatus === 'Authenticated'
                    ? '✓ Authenticated'
                    : authModalServer.authStatus === 'TokenExpired'
                    ? '⚠ Token Expired'
                    : '✗ Needs Auth'}
                </span>
              </div>
              <div className="text-[11.5px] text-[var(--text-dim,#64748B)] leading-relaxed">
                {authModalServer.authDetails || `Provide personal access token or API credentials for ${authModalServer.name}.`}
              </div>
            </div>

            {/* Token / Key Input */}
            <div className="space-y-2">
              <label className="block text-xs font-medium text-[var(--text-dim,#64748B)]">
                {authModalServer.name.toLowerCase().includes('supabase')
                  ? 'Supabase Personal Access Token (or Service Role Key):'
                  : authModalServer.name.toLowerCase().includes('firebase')
                  ? 'Firebase Token / Service Account Key:'
                  : `${authModalServer.authKeyName || 'API Key / Token'}:`}
              </label>
              <div className="flex gap-2">
                <input
                  type={authKeyVisible ? 'text' : 'password'}
                  placeholder={
                    authModalServer.name.toLowerCase().includes('supabase')
                      ? 'sbp_... or ey...'
                      : 'Enter personal access token or secret...'
                  }
                  value={authKeyValue}
                  onChange={e => {
                    setAuthKeyValue(e.target.value);
                    setAuthFeedback(null);
                  }}
                  className="flex-1 px-3 py-2 text-xs font-mono rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
                <Button
                  variant="secondary"
                  size="xs"
                  onClick={() => setAuthKeyVisible(!authKeyVisible)}
                  className="px-2.5"
                >
                  {authKeyVisible ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
                </Button>
              </div>

              {/* Helpful Link */}
              {authModalServer.name.toLowerCase().includes('supabase') && (
                <div className="pt-1">
                  <button
                    type="button"
                    onClick={() =>
                      webviewBridge.invoke('open_url_external', {
                        url: 'https://supabase.com/dashboard/account/tokens',
                      })
                    }
                    className="text-[11px] text-accent hover:text-accent transition inline-flex items-center gap-1.5 cursor-pointer"
                  >
                    <span>Generate Personal Access Token in Supabase Dashboard</span>
                    <img src={actionIcons.external} alt="" className="h-3 w-3 object-contain" />
                  </button>
                </div>
              )}
            </div>

            {/* Feedback message */}
            {authFeedback && (
              <div
                className={`p-2.5 rounded-lg text-xs font-mono border ${
                  authFeedback.valid
                    ? 'bg-emerald-500/10 text-state-success border-emerald-500/20'
                    : 'bg-rose-500/10 text-state-danger border-rose-500/20'
                }`}
              >
                {authFeedback.message}
              </div>
            )}

            {/* Actions */}
            <div className="flex items-center justify-between pt-3 border-t border-[var(--border-subtle,#1E293B)]">
              <Button
                variant="secondary"
                size="xs"
                disabled={!authKeyValue.trim() || authTesting}
                onClick={async () => {
                  if (!authKeyValue.trim()) return;
                  setAuthTesting(true);
                  setAuthFeedback(null);
                  try {
                    const res = await testMcpAuth(authModalServer.name, authKeyValue.trim());
                    setAuthFeedback({ valid: res?.valid, message: res?.message || (res?.valid ? 'Key verified successfully!' : 'Invalid key.') });
                  } catch (err: any) {
                    setAuthFeedback({ valid: false, message: err?.message || 'Verification failed.' });
                  } finally {
                    setAuthTesting(false);
                  }
                }}
              >
                {authTesting ? 'Testing...' : 'Test Key'}
              </Button>

              <div className="flex gap-2">
                <Button variant="secondary" size="xs" onClick={() => setShowMcpAuthModal(false)}>
                  Cancel
                </Button>
                <Button
                  variant="primary"
                  size="xs"
                  disabled={!authKeyValue.trim() || authSaving}
                  onClick={async () => {
                    if (!authKeyValue.trim()) return;
                    setAuthSaving(true);
                    try {
                      const keyName = authModalServer.authKeyName || (authModalServer.name.toLowerCase().includes('supabase') ? 'SUPABASE_ACCESS_TOKEN' : 'API_KEY');
                      const res = await saveMcpAuth(authModalServer.name, keyName, authKeyValue.trim());
                      if (res && res.success !== false) {
                        setShowMcpAuthModal(false);
                      } else {
                        setAuthFeedback({ valid: false, message: res?.message || 'Failed to save credentials.' });
                      }
                    } catch (err: any) {
                      setAuthFeedback({ valid: false, message: err?.message || 'Save error.' });
                    } finally {
                      setAuthSaving(false);
                    }
                  }}
                >
                  {authSaving ? 'Saving...' : 'Save & Apply'}
                </Button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* ======================================================== */}
      {/* MODAL 3: ADD AGENT RULE */}
      {/* ======================================================== */}
      {showAddRuleModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-surface-sunken backdrop-blur-sm animate-in fade-in">
          <div className="w-full max-w-md p-6 rounded-2xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] shadow-2xl space-y-4">
            <div className="flex items-center justify-between pb-3 border-b border-[var(--border-subtle,#1E293B)]">
              <div className="flex items-center gap-2.5">
                <ShieldCheck className="w-5 h-5 text-accent" />
                <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">Add Agent System Rule</h3>
              </div>
              <button onClick={() => setShowAddRuleModal(false)} className="text-[var(--text-dim,#64748B)] hover:text-white">
                <X className="w-4 h-4" />
              </button>
            </div>

            <div className="space-y-3">
              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Rule Name</label>
                <input
                  type="text"
                  placeholder="e.g. antislop-code, strict-types, security-audit..."
                  value={newRuleName}
                  onChange={e => setNewRuleName(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Scope</label>
                <select
                  value={newRuleScope}
                  onChange={e => setNewRuleScope(e.target.value as any)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                >
                  <option value="Project">Project Workspace Rule</option>
                  <option value="Global">Global User Rule</option>
                </select>
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Source File / Relative Path</label>
                <input
                  type="text"
                  placeholder="e.g. .agents/rules/custom.md or AGENTS.md"
                  value={newRulePath}
                  onChange={e => setNewRulePath(e.target.value)}
                  className="w-full px-3 py-2 text-xs font-mono rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Description / Summary</label>
                <textarea
                  rows={3}
                  placeholder="Summarize the behavioral constraints and prompt injection rules..."
                  value={newRuleDesc}
                  onChange={e => setNewRuleDesc(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>
            </div>

            <div className="flex justify-end gap-2 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
              <Button variant="secondary" size="xs" onClick={() => setShowAddRuleModal(false)}>
                Cancel
              </Button>
              <Button
                variant="primary"
                size="xs"
                disabled={!newRuleName.trim()}
                onClick={async () => {
                  if (!newRuleName.trim()) return;
                  await saveAgentRule({
                    id: `rule_${Date.now()}`,
                    name: newRuleName.trim(),
                    scope: newRuleScope,
                    sourceFilePath: newRulePath.trim() || `${newRuleName.trim()}.md`,
                    description: newRuleDesc.trim(),
                    isEnabled: true,
                  });
                  setShowAddRuleModal(false);
                  setNewRuleName('');
                  setNewRuleScope('Project');
                  setNewRulePath('');
                  setNewRuleDesc('');
                }}
              >
                Save Rule
              </Button>
            </div>
          </div>
        </div>
      )}

      {/* ======================================================== */}
      {/* MODAL 4: SCHEDULE AUTOMATION */}
      {/* ======================================================== */}
      {showAddAutoModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-surface-sunken backdrop-blur-sm animate-in fade-in">
          <div className="w-full max-w-md p-6 rounded-2xl bg-[var(--bg-card,#0F172A)] border border-[var(--border-subtle,#1E293B)] shadow-2xl space-y-4">
            <div className="flex items-center justify-between pb-3 border-b border-[var(--border-subtle,#1E293B)]">
              <div className="flex items-center gap-2.5">
                <Clock className="w-5 h-5 text-accent" />
                <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">Schedule Agent Automation</h3>
              </div>
              <button onClick={() => setShowAddAutoModal(false)} className="text-[var(--text-dim,#64748B)] hover:text-white">
                <X className="w-4 h-4" />
              </button>
            </div>

            <div className="space-y-3">
              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Task Title</label>
                <input
                  type="text"
                  placeholder="e.g. Nightly Git Hygiene, Code Audit, Dependency Check..."
                  value={newAutoName}
                  onChange={e => setNewAutoName(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Target AI Agent</label>
                <select
                  value={newAutoCli}
                  onChange={e => setNewAutoCli(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                >
                  <option value="AntigravityCli">Google Antigravity CLI (agy)</option>
                  <option value="Claude">Claude Code CLI</option>
                </select>
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Schedule (Cron Expression)</label>
                <input
                  type="text"
                  placeholder="e.g. */30 * * * * or 0 9 * * *"
                  value={newAutoInterval}
                  onChange={e => setNewAutoInterval(e.target.value)}
                  className="w-full px-3 py-2 text-xs font-mono rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
                <div className="text-[10px] text-[var(--text-dim,#64748B)] mt-1">
                  Standard 5-part cron syntax (minute, hour, day of month, month, day of week).
                </div>
              </div>

              <div>
                <label className="block text-xs font-medium text-[var(--text-dim,#64748B)] mb-1">Prompt / Instructions</label>
                <textarea
                  rows={3}
                  placeholder="Review modified files and run test suites..."
                  value={newAutoPrompt}
                  onChange={e => setNewAutoPrompt(e.target.value)}
                  className="w-full px-3 py-2 text-xs rounded-lg bg-[var(--bg-input,#0D1322)] border border-[var(--border-subtle,#1E293B)] text-white focus:outline-none focus:border-sky-500"
                />
              </div>
            </div>

            <div className="flex justify-end gap-2 pt-3 border-t border-[var(--border-subtle,#1E293B)]">
              <Button variant="secondary" size="xs" onClick={() => setShowAddAutoModal(false)}>
                Cancel
              </Button>
              <Button
                variant="primary"
                size="xs"
                disabled={!newAutoName.trim() || !newAutoPrompt.trim()}
                onClick={async () => {
                  if (!newAutoName.trim() || !newAutoPrompt.trim()) return;
                  await saveAutomation({
                    id: `task_${Date.now()}`,
                    name: newAutoName.trim(),
                    targetCli: newAutoCli,
                    scheduleExpression: newAutoInterval.trim() || '*/30 * * * *',
                    promptToExecute: newAutoPrompt.trim(),
                    isEnabled: true,
                  });
                  setShowAddAutoModal(false);
                  setNewAutoName('');
                  setNewAutoPrompt('');
                }}
              >
                Schedule Task
              </Button>
            </div>
          </div>
        </div>
      )}

    </div>
  );
};
