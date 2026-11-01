import React, { useState, useEffect } from 'react';
import { DeepSeekAccountsCard } from '../components/profiles/DeepSeekAccountsCard';
import {
  Users,
  Plus,
  RefreshCw,
  Shield,
  Folder,
  FolderOpen,
  ChevronDown,
  ChevronUp,
  Play,
  Square,
  Eye,
  EyeOff,
  Check,
  Edit2,
  Trash2,
  ExternalLink,
  Layers,
  Sparkles,
  LogOut,
  Sliders,
  CheckCircle2,
  Star,
} from 'lucide-react';
import { useProfileStore } from '../store/useProfileStore';
import { useWorkspaceStore } from '../store/useWorkspaceStore';
import { webviewBridge } from '../services/webviewBridge';
import { actionIcons, sticker } from '../lib/toolIcons';
import { ProjectWorkspace } from '../types/agent';
import { quotaPillClass } from '../lib/quota';
import { QuotaPopover } from '../components/common/QuotaPopover';

interface ProfilesViewProps {
  onOpenAddProfile?: () => void;
}

export const ProfilesView: React.FC<ProfilesViewProps> = () => {
  const {
    profiles,
    activeProfileName,
    switchActiveProfile,
    startProfile,
    stopProfile,
    quotaPending,
    checkQuota,
    defaultProfileName,
    setDefaultProfile,
  } = useProfileStore();

  const {
    workspaces,
    activeWorkspaceId,
    activeWorkspace,
    loadWorkspaces,
    switchWorkspace,
    saveWorkspace,
    deleteWorkspace,
  } = useWorkspaceStore();

  // 1. Claude Gateway state
  const [isGatewayExpanded, setIsGatewayExpanded] = useState(false);
  const [isInjectionEnabled, setIsInjectionEnabled] = useState(true);
  const [gatewayProvider, setGatewayProvider] = useState('preset_local_proxy');
  const [gatewayBaseUrl, setGatewayBaseUrl] = useState('http://127.0.1.1:9230');
  const [apiKeys, setApiKeys] = useState<string[]>(['']);
  const [visibleKeyIndices, setVisibleKeyIndices] = useState<Set<number>>(new Set());
  const [keyHealthStatus, setKeyHealthStatus] = useState<string>('Ready');
  const [freeModelsCount, setFreeModelsCount] = useState<string>('');
  const [isFetchingModels, setIsFetchingModels] = useState(false);
  const [isSavingProvider, setIsSavingProvider] = useState(false);

  // 2. Workspaces card state
  const [isWorkspacesExpanded, setIsWorkspacesExpanded] = useState(true);

  // 3. Email visibility per profile (keyed by profile name)
  const [visibleEmails, setVisibleEmails] = useState<Record<string, boolean>>({});

  // 4. Modals state
  const [isNewProfileModalOpen, setIsNewProfileModalOpen] = useState(false);
  const [newProfileName, setNewProfileName] = useState('');

  const [isRenameModalOpen, setIsRenameModalOpen] = useState(false);
  const [renameOldName, setRenameOldName] = useState('');
  const [renameNewName, setRenameNewName] = useState('');

  const [isWorkspaceModalOpen, setIsWorkspaceModalOpen] = useState(false);
  const [editingWorkspace, setEditingWorkspace] = useState<Partial<ProjectWorkspace> | null>(null);


  useEffect(() => {
    loadWorkspaces();
  }, [loadWorkspaces]);

  // Read once on mount. This used to hang off the workspaces effect, so every render re-asked the
  // bridge, and an action the bridge does not implement filled the log with a warning each time.
  useEffect(() => {
    void loadClaudeGatewayConfig();
  }, []);

  const loadClaudeGatewayConfig = async () => {
    try {
      // The bridge answers `get_claude_gateway_config`; the base url and keys live on the active
      // provider, not at the top level.
      const res = await webviewBridge.invoke('get_claude_gateway_config');
      if (res) {
        if (res.selectedGatewayId) setGatewayProvider(res.selectedGatewayId);

        const provider = res.activeProvider;
        if (provider?.baseUrl) setGatewayBaseUrl(provider.baseUrl);
        if (Array.isArray(provider?.apiKeys) && provider.apiKeys.length > 0) {
          setApiKeys(provider.apiKeys);
        } else if (provider?.apiKey) {
          setApiKeys([provider.apiKey]);
        }

        if (res.injectionEnabled !== undefined) {
          setIsInjectionEnabled(Boolean(res.injectionEnabled));
        }
      }
    } catch {
      // Default fallback
    }
  };

  const toggleEmailVisibility = (name: string) => {
    setVisibleEmails(prev => ({
      ...prev,
      [name]: !prev[name],
    }));
  };

  const handleOpenProfileFolder = async (name: string) => {
    try {
      await webviewBridge.invoke('open_profile_folder', { profileName: name });
    } catch {}
  };

  const handleOpenFolder = async (path: string) => {
    try {
      await webviewBridge.invoke('open_folder', { path });
    } catch {}
  };

  // --- Profile Actions ---
  const handleOpenCreateProfile = () => {
    setNewProfileName(`Account_${profiles.length + 1}`);
    setIsNewProfileModalOpen(true);
  };

  const handleConfirmCreateProfile = async () => {
    if (!newProfileName.trim()) return;
    try {
      await webviewBridge.invoke('create_antigravity_profile', { profileName: newProfileName.trim() });
    } catch {}
    setIsNewProfileModalOpen(false);
  };

  const handleOpenRename = (oldName: string) => {
    setRenameOldName(oldName);
    setRenameNewName(oldName);
    setIsRenameModalOpen(true);
  };

  const handleConfirmRename = async () => {
    if (!renameNewName.trim() || renameNewName.trim() === renameOldName) {
      setIsRenameModalOpen(false);
      return;
    }
    try {
      await webviewBridge.invoke('rename_antigravity_profile', {
        oldName: renameOldName,
        newName: renameNewName.trim(),
      });
    } catch {}
    setIsRenameModalOpen(false);
  };

  const handleDeleteProfile = async (name: string) => {
    if (confirm(`Delete Antigravity profile "${name}"?`)) {
      try {
        await webviewBridge.invoke('delete_antigravity_profile', { profileName: name });
      } catch {}
    }
  };

  // --- Claude Gateway Actions ---
  const handleToggleInjection = async (checked: boolean) => {
    setIsInjectionEnabled(checked);
    try {
      await webviewBridge.invoke('toggle_claude_injection', { enabled: checked });
    } catch {}
  };

  const handleAddApiKeyRow = () => {
    setApiKeys(prev => [...prev, '']);
  };

  const handleRemoveApiKeyRow = (index: number) => {
    if (apiKeys.length <= 1) {
      setApiKeys(['']);
    } else {
      setApiKeys(prev => prev.filter((_, i) => i !== index));
    }
  };

  const handleApiKeyChange = (index: number, val: string) => {
    setApiKeys(prev => {
      const next = [...prev];
      next[index] = val;
      return next;
    });
  };

  const toggleKeyReveal = (index: number) => {
    setVisibleKeyIndices(prev => {
      const next = new Set(prev);
      if (next.has(index)) next.delete(index);
      else next.add(index);
      return next;
    });
  };

  const handleSaveClaudeConfig = async () => {
    setIsSavingProvider(true);
    const validKeys = apiKeys.map(k => k.trim()).filter(Boolean);
    try {
      await webviewBridge.invoke('save_claude_gateway_config', {
        providerId: gatewayProvider,
        name: gatewayProvider === 'preset_local_proxy' ? 'Local Proxy' : 'OpenRouter Direct',
        baseUrl: gatewayBaseUrl.trim(),
        apiKey: validKeys[0] || '',
        apiKeys: validKeys,
      });
      setKeyHealthStatus(validKeys.length > 0 ? `${validKeys.length} key(s) configured` : 'Ready (No key set)');
    } catch {}
    setIsSavingProvider(false);
  };

  const handleFetchFreeModels = async () => {
    setIsFetchingModels(true);
    try {
      const res = await webviewBridge.invoke('refresh_openrouter_free_models');
      if (res && res.models) {
        setFreeModelsCount(`${res.models.length} free models available`);
      }
    } catch {}
    setIsFetchingModels(false);
  };

  const handleClaudeLogout = async () => {
    if (confirm('Clear stored Anthropic login tokens to ensure gateway proxy operates cleanly?')) {
      try {
        await webviewBridge.invoke('claude_logout');
      } catch {}
    }
  };

  // --- Workspaces Modal Actions ---
  const handleOpenNewWorkspace = () => {
    setEditingWorkspace({
      id: `ws_${Date.now()}`,
      name: `Workspace ${workspaces.length + 1}`,
      workingDirectory: '',
      antigravityProfile: activeProfileName,
      colorTag: '#06b6d4',
      primaryCli: 'AntigravityCli',
      secondaryCli: 'ClaudeCode',
      splitMode: 'single',
    });
    setIsWorkspaceModalOpen(true);
  };

  const handleOpenEditWorkspace = (ws: ProjectWorkspace) => {
    setEditingWorkspace({ ...ws });
    setIsWorkspaceModalOpen(true);
  };

  const handleBrowseWorkspaceDir = async () => {
    try {
      // `pick_directory` never existed, and the empty catch hid the failure: the button did
      // nothing at all. The folder chooser is `browse_directory`, which returns { path }.
      const res: any = await webviewBridge.invoke('browse_directory');
      if (res?.path) {
        // The folder name is almost always the name the user wants, so it fills the field when the
        // name is still a placeholder. A name they typed is never overwritten.
        const folder = String(res.path).replace(/[\\/]+$/, '').split(/[\\/]/).pop() || '';

        setEditingWorkspace(prev => {
          if (!prev) return prev;

          const current = (prev.name || '').trim();
          const isPlaceholder = current.length === 0
            || /^workspace\s*\d*$/i.test(current)
            || /^untitled workspace$/i.test(current);

          return {
            ...prev,
            workingDirectory: res.path,
            name: isPlaceholder && folder ? folder : prev.name,
          };
        });
      }
    } catch {
      // Cancelling the picker rejects; that is not an error worth reporting.
    }
  };

  const handleSaveWorkspaceModal = async () => {
    if (!editingWorkspace || !editingWorkspace.id) return;
    const wsToSave: ProjectWorkspace = {
      id: editingWorkspace.id,
      name: editingWorkspace.name || 'Untitled Workspace',
      workingDirectory: editingWorkspace.workingDirectory || '',
      antigravityProfile: editingWorkspace.antigravityProfile || activeProfileName,
      colorTag: editingWorkspace.colorTag || '#06b6d4',
      primaryCli: editingWorkspace.primaryCli || 'AntigravityCli',
      secondaryCli: editingWorkspace.secondaryCli || 'ClaudeCode',
      splitMode: editingWorkspace.splitMode || 'single',
      isDefault: Boolean(editingWorkspace.isDefault),
    };
    await saveWorkspace(wsToSave);
    setIsWorkspaceModalOpen(false);
  };

  return (
    <div className="flex-1 overflow-y-auto p-6 bg-surface-deep text-content-main select-none font-sans">
      <div className="max-w-6xl mx-auto space-y-6 w-full">
        {/* Header Bar */}
        <div className="flex items-center justify-between flex-wrap gap-4 pb-4 border-b border-surface-border">
          <div>
            <h2 className="text-base font-bold text-content-main flex items-center space-x-2">
              <span>AccountsHub</span>
            </h2>
            <p className="text-xs text-content-muted mt-0.5">
              Switch Google AI Pro accounts instantly without touching credentials
            </p>
          </div>

          <div className="flex items-center space-x-2.5">
            <details className="relative">
              <summary
                className="flex h-8 w-8 cursor-pointer list-none items-center justify-center rounded-lg border border-surface-border bg-surface-card font-mono text-xs text-content-muted transition hover:text-content-main"
                title="Where credentials and ports live"
                aria-label="Isolation details"
              >
                ?
              </summary>
              <div className="absolute right-0 top-9 z-40 w-72 space-y-2 rounded-xl border border-surface-border bg-surface-card p-3 shadow-xl">
                <p className="font-mono text-[9.5px] uppercase tracking-wide text-content-dim">Credential store</p>
                <p className="text-[11px] text-content-main">Windows Credential Manager <span className="text-state-success">Encrypted</span></p>
                <p className="font-mono text-[9.5px] uppercase tracking-wide text-content-dim">VirtualHome paths</p>
                <p className="font-mono text-[11px] text-content-main">~/.gemini/antigravity-ide <span className="text-accent">Isolated</span></p>
                <p className="font-mono text-[9.5px] uppercase tracking-wide text-content-dim">Port range</p>
                <p className="font-mono text-[11px] text-content-main">127.0.0.1:9339 - 9350 <span className="text-content-dim">Auto-alloc</span></p>
              </div>
            </details>

            <button
              onClick={() => setIsGatewayExpanded(!isGatewayExpanded)}
              title="Claude Code Gateway engine"
              aria-label="Claude Code Gateway engine"
              className={`flex h-8 w-8 items-center justify-center rounded-lg border transition ${
                isInjectionEnabled
                  ? 'border-emerald-500/40 bg-emerald-500/10 text-state-success'
                  : 'border-surface-border bg-surface-card text-content-muted hover:text-content-main'
              }`}
            >
              <Shield className="h-4 w-4" />
            </button>

            <button
              onClick={handleOpenCreateProfile}
              className="px-3.5 py-1.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded-lg text-xs font-medium flex items-center space-x-1.5 transition shadow-sm"
            >
              <img src={actionIcons.add} alt="" className="h-3.5 w-3.5 object-contain" />
              <span>New Profile</span>
            </button>
          </div>
        </div>

        {/* 1. Claude Code Gateway Engine: a floating panel, not a permanent card in the page. */}
        {isGatewayExpanded ? (
          <div
            className="fixed inset-0 z-[79] bg-black/40 backdrop-blur-[1px]"
            onClick={() => setIsGatewayExpanded(false)}
          />
        ) : null}

        <div
          className={
            isGatewayExpanded
              ? 'fixed left-1/2 top-16 z-[80] max-h-[78vh] w-[min(780px,92vw)] -translate-x-1/2 overflow-y-auto rounded-2xl border border-surface-border bg-surface-card p-4 shadow-2xl'
              : 'hidden'
          }
        >
          <div
            className="flex items-center justify-between cursor-pointer flex-wrap gap-2"
            onClick={() => setIsGatewayExpanded(!isGatewayExpanded)}
          >
            <div className="flex items-center gap-3">
              <div className="w-8 h-8 rounded-lg bg-emerald-500/15 border border-emerald-500/30 flex items-center justify-center text-state-success">
                <Shield className="w-4 h-4" />
              </div>
              <div className="flex items-center gap-2.5 flex-wrap">
                <span className="text-xs font-bold text-content-main">Claude Code Gateway Engine</span>
                <span
                  className={`text-[10px] px-2 py-0.5 rounded font-mono font-semibold border ${
                    isInjectionEnabled
                      ? 'bg-emerald-500/15 text-state-success border-emerald-500/30'
                      : 'bg-slate-800 text-content-muted border-surface-border'
                  }`}
                >
                  {isInjectionEnabled ? 'Configured' : 'Injection Disabled'}
                </span>

                <label
                  className="flex items-center gap-1.5 text-xs text-content-dim cursor-pointer ml-2"
                  onClick={e => e.stopPropagation()}
                >
                  <input
                    type="checkbox"
                    checked={isInjectionEnabled}
                    onChange={e => handleToggleInjection(e.target.checked)}
                    className="rounded accent-emerald-500 cursor-pointer"
                  />
                  <span>Enable Env Injection</span>
                </label>
              </div>
            </div>

            <button
              type="button"
              className="px-3 py-1 rounded-lg bg-surface-deep border border-surface-border text-xs text-content-muted flex items-center gap-1.5 hover:border-slate-500"
            >
              <span>{isGatewayExpanded ? 'Close Config' : 'Open Config'}</span>
              {isGatewayExpanded ? <ChevronUp className="w-3.5 h-3.5" /> : <ChevronDown className="w-3.5 h-3.5" />}
            </button>
          </div>

          {isGatewayExpanded && (
            <div className="mt-4 border-t border-surface-border pt-4 space-y-4 text-xs">
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3.5">
                <div>
                  <label className="block text-xs font-semibold text-content-muted mb-1">Gateway Provider</label>
                  <select
                    value={gatewayProvider}
                    onChange={e => setGatewayProvider(e.target.value)}
                    className="w-full bg-surface-deep border border-surface-border rounded-lg px-2.5 py-1.5 text-xs font-mono text-content-main focus:outline-none focus:border-sky-500"
                  >
                    <option value="preset_local_proxy">Local Proxy (Port 9230 - Recommended)</option>
                    <option value="preset_openrouter">OpenRouter Direct</option>
                  </select>
                </div>

                <div>
                  <label className="block text-xs font-semibold text-content-muted mb-1">
                    Base URL (ANTHROPIC_BASE_URL)
                  </label>
                  <input
                    type="text"
                    value={gatewayBaseUrl}
                    onChange={e => setGatewayBaseUrl(e.target.value)}
                    className="w-full bg-surface-deep border border-surface-border rounded-lg px-2.5 py-1.5 text-xs font-mono text-content-main focus:outline-none focus:border-sky-500"
                    placeholder="http://127.0.0.1:9230"
                  />
                </div>
              </div>

              {/* API Keys Failover Pool */}
              <div className="space-y-2">
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-2">
                    <span className="text-xs font-semibold text-content-main">OpenRouter API Keys (Failover Pool)</span>
                    <span className="text-[10px] px-1.5 py-0.5 rounded-full bg-sky-500/15 text-accent font-mono font-semibold">
                      {apiKeys.length} {apiKeys.length === 1 ? 'key' : 'keys'}
                    </span>
                  </div>
                  <div className="flex items-center gap-3">
                    <span className="text-[11px] text-content-dim">Auto-switches on 429</span>
                    <button
                      type="button"
                      onClick={handleAddApiKeyRow}
                      className="px-2.5 py-1 rounded bg-surface-deep border border-surface-border text-xs text-accent font-semibold hover:border-sky-500 transition"
                    >
                      + Add Key
                    </button>
                  </div>
                </div>

                <div className="space-y-2">
                  {apiKeys.map((key, idx) => {
                    const isVisible = visibleKeyIndices.has(idx);
                    return (
                      <div key={idx} className="flex items-center gap-2">
                        <span className="w-6 text-center text-xs font-mono text-content-dim font-bold">
                          #{idx + 1}
                        </span>
                        <input
                          type={isVisible ? 'text' : 'password'}
                          value={key}
                          onChange={e => handleApiKeyChange(idx, e.target.value)}
                          placeholder={`OpenRouter API key #${idx + 1} (sk-or-v1-...)`}
                          className="flex-1 bg-surface-deep border border-surface-border rounded-lg px-3 py-1.5 text-xs font-mono text-content-main focus:outline-none focus:border-sky-500"
                        />
                        <button
                          type="button"
                          onClick={() => toggleKeyReveal(idx)}
                          className="p-1.5 rounded border border-surface-border bg-surface-deep text-content-dim hover:text-content-main"
                          title={isVisible ? 'Hide key' : 'Show key'}
                        >
                          {isVisible ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
                        </button>
                        <button
                          type="button"
                          onClick={() => handleRemoveApiKeyRow(idx)}
                          className="p-1.5 rounded border border-rose-500/30 bg-rose-500/10 text-state-danger hover:bg-rose-500/20"
                          title="Remove key"
                        >
                          <img src={actionIcons.trash} alt="" className="h-3.5 w-3.5 object-contain" />
                        </button>
                      </div>
                    );
                  })}
                </div>

                <div className="flex items-center justify-between pt-1">
                  <span className="text-[11px] text-content-dim">
                    Add as many free keys as you want. When one hits a rate limit, the proxy automatically rotates to the next.
                  </span>
                  <button
                    type="button"
                    onClick={handleSaveClaudeConfig}
                    disabled={isSavingProvider}
                    className="px-4 py-1.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded-lg text-xs font-medium transition"
                  >
                    {isSavingProvider ? 'Saving...' : 'Save Provider'}
                  </button>
                </div>
              </div>

              {/* Utilities Bar */}
              <div className="flex items-center justify-between flex-wrap gap-2 pt-3 border-t border-surface-border text-xs">
                <div className="flex items-center gap-3">
                  <span className="font-mono text-content-dim">{keyHealthStatus}</span>
                  {freeModelsCount && <span className="text-state-success font-semibold">{freeModelsCount}</span>}
                </div>

                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={handleFetchFreeModels}
                    disabled={isFetchingModels}
                    className="px-3 py-1.5 bg-surface-deep border border-surface-border hover:border-slate-500 text-content-main rounded-lg flex items-center gap-1.5 text-xs"
                  >
                    <img src={actionIcons.refresh} alt="" className={`h-3.5 w-3.5 object-contain ${isFetchingModels ? 'animate-spin' : ''}`} />
                    <span>Fetch Free Models</span>
                  </button>

                  <button
                    type="button"
                    onClick={handleClaudeLogout}
                    className="px-3 py-1.5 bg-rose-500/15 border border-rose-500/30 hover:bg-rose-500/25 text-state-danger rounded-lg flex items-center gap-1.5 text-xs font-medium"
                    title="Clears stored Anthropic login tokens to ensure OpenRouter token works cleanly"
                  >
                    <LogOut className="w-3.5 h-3.5" />
                    <span>Claude /logout</span>
                  </button>
                </div>
              </div>
            </div>
          )}
        </div>

        {/* 2. Project Workspaces & Context Profiles Card (Collapsible) */}
        <div className="bg-surface-card border border-surface-border rounded-xl p-3 transition shadow-sm">
          <div
            className="flex items-center justify-between cursor-pointer flex-wrap gap-2"
            onClick={() => setIsWorkspacesExpanded(!isWorkspacesExpanded)}
          >
            <div className="flex items-center gap-3">
              <div className="w-8 h-8 rounded-lg bg-cyan-500/15 border border-cyan-500/30 flex items-center justify-center text-accent">
                <Folder className="w-4 h-4" />
              </div>
              <div className="flex items-center gap-2.5 flex-wrap">
                <span className="text-xs font-bold text-content-main">Project Workspaces & Context Profiles</span>
                <span className="text-[10px] px-2 py-0.5 rounded bg-cyan-500/15 text-accent border border-cyan-500/30 font-mono font-semibold">
                  {activeWorkspace?.name || 'Default Workspace'}
                </span>
                <span className="text-[10px] px-2 py-0.5 rounded bg-surface-deep text-content-dim font-medium border border-surface-border">
                  {workspaces.length} {workspaces.length === 1 ? 'Workspace' : 'Workspaces'}
                </span>
              </div>
            </div>

            <div className="flex items-center gap-2" onClick={e => e.stopPropagation()}>
              <button
                type="button"
                onClick={handleOpenNewWorkspace}
                className="px-3 py-1 bg-surface-deep border border-surface-border hover:border-cyan-500 text-accent rounded-lg text-xs font-medium flex items-center gap-1"
              >
                <img src={actionIcons.add} alt="" className="h-3 w-3 object-contain" />
                <span>New Workspace</span>
              </button>

              <button
                type="button"
                onClick={() => setIsWorkspacesExpanded(!isWorkspacesExpanded)}
                className="px-2.5 py-1 rounded-lg bg-surface-deep border border-surface-border text-xs text-content-muted flex items-center gap-1 hover:border-slate-500"
              >
                <span>{isWorkspacesExpanded ? 'Close' : 'Open'}</span>
                {isWorkspacesExpanded ? <ChevronUp className="w-3.5 h-3.5" /> : <ChevronDown className="w-3.5 h-3.5" />}
              </button>
            </div>
          </div>

          {isWorkspacesExpanded && (
            <div className="mt-4 border-t border-surface-border pt-4">
              {workspaces.length === 0 ? (
                <div className="text-xs text-content-dim py-4 text-center">
                  No workspaces created yet. Click "New Workspace" to create one.
                </div>
              ) : (
                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
                  {workspaces.map(ws => {
                    const isActive = ws.id === activeWorkspaceId;
                    const color = ws.colorTag || '#06b6d4';
                    const hasCustomDir = Boolean(ws.workingDirectory);
                    const cleanPath = ws.workingDirectory?.replace(/[\\/]+$/, '') || '';
                    const dirBaseName = cleanPath ? cleanPath.split(/[\\/]/).pop() : 'VirtualHome';

                    return (
                      <div
                        key={ws.id}
                        onClick={() => !isActive && switchWorkspace(ws.id)}
                        className={`p-2.5 rounded-xl border bg-surface-deep/60 transition cursor-pointer flex flex-col justify-between space-y-2 ${
                          isActive
                            ? 'border-cyan-500/70 ring-1 ring-cyan-500/40 bg-surface-deep/90'
                            : 'border-surface-border hover:border-surface-border'
                        }`}
                      >
                        <div>
                          {/* Title & Badge */}
                          <div className="flex items-center justify-between pb-1.5">
                            <div className="flex items-center gap-2 min-w-0">
                              <Folder className="w-4 h-4 flex-shrink-0" style={{ color }} />
                              <span className="text-xs font-bold text-content-main truncate">
                                {ws.name}
                              </span>
                            </div>
                            {isActive && (
                              <span className="text-[9px] px-1.5 py-0.5 rounded font-mono font-bold bg-cyan-500/15 text-accent border border-cyan-500/30 flex items-center gap-1">
                                <span className="w-1.5 h-1.5 rounded-full bg-cyan-400 animate-pulse" />
                                ACTIVE
                              </span>
                            )}
                          </div>

                          {/* Directory Box */}
                          <div
                            className="p-2 rounded-lg bg-surface-card border border-surface-border text-xs flex items-center justify-between gap-2 mt-2"
                            title={ws.workingDirectory || 'Default VirtualHome'}
                          >
                            <div className="truncate flex-1">
                              <span className="font-semibold text-content-main block text-[11px] truncate">
                                {dirBaseName}
                              </span>
                              <span className="text-[10px] font-mono text-content-dim truncate block">
                                {hasCustomDir ? ws.workingDirectory : 'Default (VirtualHome)'}
                              </span>
                            </div>

                            {hasCustomDir && (
                              <button
                                type="button"
                                onClick={e => {
                                  e.stopPropagation();
                                  handleOpenFolder(ws.workingDirectory);
                                }}
                                className="p-1 rounded text-content-dim hover:text-content-main hover:bg-slate-800"
                                title="Open in File Explorer"
                              >
                                <img src={actionIcons.external} alt="" className="h-3.5 w-3.5 object-contain" />
                              </button>
                            )}
                          </div>

                          {/* Meta chips */}
                          <div className="flex items-center gap-2 mt-2.5 text-[10px] font-mono text-content-dim flex-wrap">
                            <span className="px-1.5 py-0.5 rounded bg-surface-card border border-surface-border flex items-center gap-1">
                              <Users className="w-2.5 h-2.5 text-accent" />
                              <span>{ws.antigravityProfile || 'Current Profile'}</span>
                            </span>
                            <span className="px-1.5 py-0.5 rounded bg-surface-card border border-surface-border flex items-center gap-1">
                              <Layers className="w-2.5 h-2.5 text-content-muted" />
                              <span>{ws.primaryCli === 'Hub' || !ws.primaryCli ? 'Default engine' : ws.primaryCli}</span>
                            </span>
                          </div>
                        </div>

                        {/* Actions */}
                        <div className="flex items-center justify-end gap-1.5 pt-2 border-t border-surface-border/60" onClick={e => e.stopPropagation()}>
                          {!isActive && (
                            <button
                              type="button"
                              onClick={() => switchWorkspace(ws.id)}
                              className="px-2 py-0.5 rounded text-[10.5px] font-medium bg-surface-card border border-surface-border text-content-main hover:border-cyan-500"
                            >
                              Activate
                            </button>
                          )}
                          <button
                            type="button"
                            onClick={() => handleOpenEditWorkspace(ws)}
                            className="px-2 py-0.5 rounded text-[10.5px] font-medium bg-surface-card border border-surface-border text-content-main hover:border-slate-500"
                          >
                            Edit
                          </button>
                          {!ws.isDefault && (
                            <button
                              type="button"
                              onClick={() => deleteWorkspace(ws.id)}
                              className="px-2 py-0.5 rounded text-[10.5px] font-medium bg-rose-500/10 border border-rose-500/30 text-state-danger hover:bg-rose-500/20"
                            >
                              Delete
                            </button>
                          )}
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          )}
        </div>

        {/* Accounts: DeepSeek first, because its login is the one people change most often. */}
        <DeepSeekAccountsCard />

        {/* 3. Antigravity Multi-Account Profiles Grid */}
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-content-dim font-mono">
              Antigravity accounts ({profiles.length})
            </h3>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
            {profiles.map(p => {
              const isActive = p.name.toLowerCase() === activeProfileName.toLowerCase();
              const isRunning = Boolean(p.isRunning);
              const isEmailVisible = Boolean(visibleEmails[p.name]);

              return (
                <div
                  key={p.name}
                  className={`p-4 rounded-xl border bg-surface-card transition shadow-sm flex flex-col justify-between space-y-3.5 ${
                    isRunning || isActive
                      ? 'border-emerald-500/60 ring-1 ring-emerald-500/20'
                      : 'border-surface-border hover:border-surface-border'
                  }`}
                >
                  <div className="space-y-2">
                    <div className="flex items-start justify-between">
                      <div className="flex items-start space-x-3 min-w-0">
                        <div
                          className={`w-9 h-9 rounded-xl flex items-center justify-center flex-shrink-0 border ${
                            isRunning || isActive
                              ? 'bg-emerald-500/15 text-state-success border-emerald-500/30'
                              : 'bg-slate-800 text-content-muted border-surface-border'
                          }`}
                        >
                          <img src={sticker.sparkle} alt="" className="h-6 w-6 object-contain" />
                        </div>
                        <div className="min-w-0">
                          <h4 className="text-sm font-bold text-content-main font-mono truncate">{p.name}</h4>
                          <span className="text-xs text-content-dim block truncate">
                            {isRunning
                              ? `Running (Port: ${p.port || '9339'})`
                              : isActive
                              ? 'Active Session (Stopped)'
                              : 'Stopped'}
                          </span>
                        </div>
                      </div>

                      <div className="flex items-center space-x-1.5 flex-shrink-0">
                        {isActive && (
                          <span className="px-1.5 py-0.5 rounded text-[9.5px] font-mono font-bold bg-sky-500/15 text-accent border border-sky-500/30">
                            ACTIVE
                          </span>
                        )}
                        <span
                          className={`px-1.5 py-0.5 rounded text-[9.5px] font-mono font-bold border ${
                            isRunning
                              ? 'bg-emerald-500/15 text-state-success border-emerald-500/30'
                              : 'bg-slate-800 text-content-muted border-surface-border'
                          }`}
                        >
                          {isRunning ? `RUNNING${p.port ? ` :${p.port}` : ''}` : 'STOPPED'}
                        </span>
                        {(() => {
                          const isDefault = defaultProfileName.toLowerCase() === p.name.toLowerCase();
                          return (
                            <button
                              type="button"
                              onClick={() => setDefaultProfile(isDefault ? '' : p.name)}
                              title={
                                isDefault
                                  ? `${p.name} is the default profile. Click to clear`
                                  : `Set ${p.name} as the default profile`
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

                        {typeof p.quotaPercent === 'number' && p.quotaPercent >= 0 ? (
                          <QuotaPopover profile={p} placement="bottom">
                            <span
                              className={`px-1.5 py-0.5 rounded text-[9.5px] font-mono font-bold border tabular-nums ${quotaPillClass(p.quotaPercent)}`}
                            >
                              {Math.round(p.quotaPercent)}%
                            </span>
                          </QuotaPopover>
                        ) : null}
                        <button
                          type="button"
                          onClick={() => handleOpenProfileFolder(p.name)}
                          className="p-1 rounded text-content-dim hover:text-content-main hover:bg-surface-hover"
                          title="Open profile folder in Explorer"
                        >
                          <FolderOpen className="w-4 h-4" />
                        </button>
                      </div>
                    </div>

                    {/* Email row with eye reveal toggle */}
                    <div className="flex items-center justify-between p-2 rounded-lg bg-surface-deep border border-surface-border text-xs font-mono">
                      <span className="truncate text-content-muted select-text">
                        {isEmailVisible && p.email ? p.email : '••••••••••••••••'}
                      </span>
                      {p.email && (
                        <button
                          type="button"
                          onClick={() => toggleEmailVisibility(p.name)}
                          className="ml-2 text-content-dim hover:text-content-main"
                          title={isEmailVisible ? 'Hide email' : 'Show email'}
                        >
                          {isEmailVisible ? <EyeOff className="w-3.5 h-3.5" /> : <Eye className="w-3.5 h-3.5" />}
                        </button>
                      )}
                    </div>
                  </div>

                  {/* Actions Bar */}
                  <div className="flex items-center gap-2 pt-2 border-t border-surface-border flex-wrap">
                    {isRunning ? (
                      <button
                        type="button"
                        onClick={() => stopProfile(p.name)}
                        className="flex-1 px-3 py-1.5 bg-rose-500/15 border border-rose-500/30 hover:bg-rose-500/25 text-state-danger rounded-lg text-xs font-medium transition flex items-center justify-center gap-1.5"
                      >
                        <Square className="w-3.5 h-3.5 fill-current" />
                        <span>Stop Instance</span>
                      </button>
                    ) : (
                      <button
                        type="button"
                        onClick={() => startProfile(p.name)}
                        className="flex-1 px-3 py-1.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded-lg text-xs font-medium transition flex items-center justify-center gap-1.5"
                      >
                        <Play className="w-3.5 h-3.5 fill-current" />
                        <span>Start Instance</span>
                      </button>
                    )}

                    {!isActive && (
                      <button
                        type="button"
                        onClick={() => switchActiveProfile(p.name)}
                        className="px-2.5 py-1.5 bg-surface-deep border border-surface-border hover:border-sky-500 text-content-main rounded-lg text-xs font-medium transition"
                      >
                        Set Active
                      </button>
                    )}

                    <button
                      type="button"
                      onClick={() => void checkQuota(p.name)}
                      disabled={Boolean(quotaPending[p.name.toLowerCase()])}
                      className="flex items-center gap-1.5 px-2.5 py-1.5 bg-surface-deep border border-surface-border hover:border-sky-500 text-content-main rounded-lg text-xs font-medium transition disabled:cursor-wait disabled:opacity-70"
                      title={`Read quota for ${p.name} only`}
                    >
                      <RefreshCw
                        className={`w-3.5 h-3.5 ${
                          quotaPending[p.name.toLowerCase()] ? 'animate-spin text-accent' : 'text-content-muted'
                        }`}
                      />
                      <span>{quotaPending[p.name.toLowerCase()] ? 'Checking…' : 'Check Quota'}</span>
                    </button>

                    <button
                      type="button"
                      onClick={() => handleOpenRename(p.name)}
                      className="px-2.5 py-1.5 bg-surface-deep border border-surface-border hover:border-surface-borderLight text-content-main rounded-lg text-xs font-medium transition"
                    >
                      Rename
                    </button>

                    {!isActive && !isRunning && (
                      <button
                        type="button"
                        onClick={() => handleDeleteProfile(p.name)}
                        className="p-1.5 bg-rose-500/10 border border-rose-500/30 hover:bg-rose-500/20 text-state-danger rounded-lg text-xs transition"
                        title="Delete Profile"
                      >
                        <img src={actionIcons.trash} alt="" className="h-3.5 w-3.5 object-contain" />
                      </button>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>

      {/* Modal: New Profile */}
      {isNewProfileModalOpen && (
        <div className="fixed inset-0 bg-surface-sunken backdrop-blur-sm flex items-center justify-center z-50 p-4">
          <div className="bg-surface-card border border-surface-border rounded-2xl p-5 max-w-sm w-full space-y-4 shadow-2xl">
            <h3 className="text-sm font-bold text-content-main">Create Antigravity Profile</h3>
            <div>
              <label className="block text-xs font-semibold text-content-muted mb-1">Profile Name</label>
              <input
                type="text"
                value={newProfileName}
                onChange={e => setNewProfileName(e.target.value)}
                autoFocus
                className="w-full bg-surface-deep border border-surface-border rounded-lg px-3 py-2 text-xs font-mono text-content-main focus:outline-none focus:border-sky-500"
              />
            </div>
            <div className="flex justify-end gap-2 pt-2">
              <button
                type="button"
                onClick={() => setIsNewProfileModalOpen(false)}
                className="px-3 py-1.5 rounded-lg bg-surface-deep border border-surface-border text-xs text-content-main"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleConfirmCreateProfile}
                className="px-3.5 py-1.5 rounded-lg bg-emerald-600 hover:bg-emerald-500 text-white text-xs font-medium"
              >
                Create Profile
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Modal: Rename Profile */}
      {isRenameModalOpen && (
        <div className="fixed inset-0 bg-surface-sunken backdrop-blur-sm flex items-center justify-center z-50 p-4">
          <div className="bg-surface-card border border-surface-border rounded-2xl p-5 max-w-sm w-full space-y-4 shadow-2xl">
            <h3 className="text-sm font-bold text-content-main">Rename Profile: {renameOldName}</h3>
            <div>
              <label className="block text-xs font-semibold text-content-muted mb-1">New Name</label>
              <input
                type="text"
                value={renameNewName}
                onChange={e => setRenameNewName(e.target.value)}
                autoFocus
                className="w-full bg-surface-deep border border-surface-border rounded-lg px-3 py-2 text-xs font-mono text-content-main focus:outline-none focus:border-sky-500"
              />
            </div>
            <div className="flex justify-end gap-2 pt-2">
              <button
                type="button"
                onClick={() => setIsRenameModalOpen(false)}
                className="px-3 py-1.5 rounded-lg bg-surface-deep border border-surface-border text-xs text-content-main"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleConfirmRename}
                className="px-3.5 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white text-xs font-medium"
              >
                Rename
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Modal: Edit / New Workspace */}
      {isWorkspaceModalOpen && editingWorkspace && (
        <div className="fixed inset-0 bg-surface-sunken backdrop-blur-sm flex items-center justify-center z-50 p-4">
          <div className="bg-surface-card border border-surface-border rounded-2xl p-5 max-w-md w-full space-y-4 shadow-2xl">
            <h3 className="text-sm font-bold text-content-main">
              {editingWorkspace.isDefault ? 'Edit Default Workspace' : 'Configure Project Workspace'}
            </h3>

            <div className="space-y-3 text-xs">
              <div>
                <label className="block font-semibold text-content-muted mb-1">Workspace Name</label>
                <input
                  type="text"
                  value={editingWorkspace.name || ''}
                  onChange={e => setEditingWorkspace(prev => (prev ? { ...prev, name: e.target.value } : prev))}
                  className="w-full bg-surface-deep border border-surface-border rounded-lg px-3 py-2 font-mono text-content-main focus:outline-none focus:border-cyan-500"
                  placeholder="e.g. AgentsHub Dev, Client Portal"
                />
              </div>

              <div>
                <label className="block font-semibold text-content-muted mb-1">Working Directory</label>
                <div className="flex gap-2">
                  <input
                    type="text"
                    value={editingWorkspace.workingDirectory || ''}
                    onChange={e =>
                      setEditingWorkspace(prev => (prev ? { ...prev, workingDirectory: e.target.value } : prev))
                    }
                    className="flex-1 bg-surface-deep border border-surface-border rounded-lg px-3 py-2 font-mono text-content-main focus:outline-none focus:border-cyan-500 text-[11px]"
                    placeholder="C:\Projects\MyProject"
                  />
                  <button
                    type="button"
                    onClick={handleBrowseWorkspaceDir}
                    className="px-3 py-2 bg-surface-deep border border-surface-border hover:border-slate-500 text-content-main rounded-lg font-medium whitespace-nowrap"
                  >
                    Browse...
                  </button>
                </div>
              </div>

              <div>
                <label className="block font-semibold text-content-muted mb-1">Colour tag</label>
                <div className="flex items-center gap-2">
                  <input
                    type="color"
                    value={editingWorkspace.colorTag || '#06b6d4'}
                    onChange={e =>
                      setEditingWorkspace(prev => (prev ? { ...prev, colorTag: e.target.value } : prev))
                    }
                    className="h-8 w-12 cursor-pointer rounded-lg border border-surface-border bg-surface-deep"
                  />
                  <span className="font-mono text-[11px] text-content-dim">
                    {editingWorkspace.colorTag || '#06b6d4'}
                  </span>
                </div>
              </div>
            </div>

            <div className="flex justify-end gap-2 pt-2 border-t border-surface-border">
              <button
                type="button"
                onClick={() => setIsWorkspaceModalOpen(false)}
                className="px-3 py-1.5 rounded-lg bg-surface-deep border border-surface-border text-xs text-content-main"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleSaveWorkspaceModal}
                className="px-3.5 py-1.5 rounded-lg bg-cyan-600 hover:bg-cyan-500 text-white text-xs font-medium"
              >
                Save Workspace
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};
