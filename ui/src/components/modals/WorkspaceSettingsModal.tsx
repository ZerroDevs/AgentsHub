import React, { useState, useEffect } from 'react';
import { Folder, Trash2, Check, Loader2 } from 'lucide-react';
import { actionIcons } from '../../lib/toolIcons';
import { useWorkspaceStore } from '../../store/useWorkspaceStore';
import { useProfileStore } from '../../store/useProfileStore';
import { ProjectWorkspace } from '../../types/agent';
import { webviewBridge } from '../../services/webviewBridge';

interface WorkspaceSettingsModalProps {
  isOpen: boolean;
  onClose: () => void;
}

const ACCENT_COLORS = [
  '#38BDF8', // Sky
  '#10B981', // Emerald
  '#06B6D4', // Cyan
  '#6366F1', // Indigo
  '#F59E0B', // Amber
  '#EC4899', // Pink
  '#8B5CF6', // Purple
  '#F43F5E', // Rose
];

export const WorkspaceSettingsModal: React.FC<WorkspaceSettingsModalProps> = ({ isOpen, onClose }) => {
  const { activeWorkspace, saveWorkspace, deleteWorkspace, loadWorkspaceFiles } = useWorkspaceStore();
  const { profiles } = useProfileStore();

  const [id, setId] = useState('');
  const [name, setName] = useState('');
  const [workingDirectory, setWorkingDirectory] = useState('');
  const [accentColor, setAccentColor] = useState('#38BDF8');
  const [antigravityProfile, setAntigravityProfile] = useState('');
  const [primaryCli, setPrimaryCli] = useState('AntigravityCli');
  const [secondaryCli, setSecondaryCli] = useState('none');
  const [splitMode, setSplitMode] = useState<'single' | 'horizontal' | 'vertical'>('single');
  const [splitRatio, setSplitRatio] = useState('0.5');
  const [isSaving, setIsSaving] = useState(false);
  const [isSaved, setIsSaved] = useState(false);

  useEffect(() => {
    if (activeWorkspace) {
      setId(activeWorkspace.id);
      setName(activeWorkspace.name);
      setWorkingDirectory(activeWorkspace.workingDirectory);
      setAccentColor(activeWorkspace.accentColor || '#38BDF8');
      setAntigravityProfile(activeWorkspace.antigravityProfile || profiles[0]?.name || '');
      setPrimaryCli(activeWorkspace.primaryCli || 'AntigravityCli');
      setSecondaryCli(activeWorkspace.secondaryCli || 'none');
      setSplitMode(activeWorkspace.splitMode || 'single');
      setSplitRatio(activeWorkspace.splitRatio || '0.5');
    } else {
      setId(`ws_${Date.now()}`);
      setName('New Workspace');
      setWorkingDirectory('C:\\Projects');
      setAccentColor('#38BDF8');
      setAntigravityProfile(profiles[0]?.name || '');
      setPrimaryCli('AntigravityCli');
      setSecondaryCli('none');
      setSplitMode('single');
      setSplitRatio('0.5');
    }
  }, [activeWorkspace, profiles, isOpen]);

  if (!isOpen) return null;

  const handleBrowse = async () => {
    try {
      const res = await webviewBridge.invoke('browse_editor_path');
      if (res && res.path) {
        setWorkingDirectory(res.path);
      }
    } catch {
      // Fallback in case host dialog is cancelled
    }
  };

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault();
    setIsSaving(true);

    const ws: ProjectWorkspace = {
      id: id || `ws_${Date.now()}`,
      name: name.trim() || 'Workspace',
      workingDirectory: workingDirectory.trim(),
      accentColor,
      antigravityProfile,
      primaryCli,
      secondaryCli,
      splitMode,
      splitRatio,
    };

    const ok = await saveWorkspace(ws);
    setIsSaving(false);
    if (ok) {
      setIsSaved(true);
      await loadWorkspaceFiles(ws.workingDirectory);
      setTimeout(() => {
        setIsSaved(false);
        onClose();
      }, 600);
    }
  };

  const handleDelete = async () => {
    if (!id || confirm(`Are you sure you want to delete workspace "${name}"?`)) {
      await deleteWorkspace(id);
      onClose();
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs select-none">
      <div className="w-full max-w-lg rounded-2xl bg-surface-card border border-surface-border shadow-2xl overflow-hidden font-sans text-xs">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-surface-border bg-surface-deep/40">
          <h3 className="font-semibold text-sm text-content-main">
            {activeWorkspace ? 'Edit Project Workspace' : 'New Project Workspace'}
          </h3>
          <button
            onClick={onClose}
            className="p-1 rounded text-content-muted hover:text-content-main hover:bg-surface-deep transition"
          >
            <img src={actionIcons.close} alt="Close" className="h-4 w-4 object-contain" />
          </button>
        </div>

        {/* Form Body */}
        <form onSubmit={handleSave} className="p-5 space-y-4">
          {/* Workspace Name */}
          <div className="space-y-1.5">
            <label className="text-content-muted font-medium">Workspace Name:</label>
            <input
              type="text"
              value={name}
              onChange={e => setName(e.target.value)}
              placeholder="e.g. AgentsHub Dev, ChessSenseV2"
              className="w-full px-3 py-2 rounded-lg bg-surface-input border border-surface-border text-content-main focus:outline-none focus:border-sky-500 font-sans shadow-sm"
              required
            />
          </div>

          {/* Accent Color Palette */}
          <div className="space-y-1.5">
            <label className="text-content-muted font-medium">Accent Color:</label>
            <div className="flex items-center gap-2 pt-1">
              {ACCENT_COLORS.map(color => (
                <button
                  key={color}
                  type="button"
                  onClick={() => setAccentColor(color)}
                  style={{ backgroundColor: color }}
                  className={`w-6 h-6 rounded-full transition-transform cursor-pointer ${
                    accentColor === color ? 'ring-2 ring-white ring-offset-2 ring-offset-slate-900 scale-110' : 'hover:scale-105 opacity-80 hover:opacity-100'
                  }`}
                />
              ))}
            </div>
          </div>

          {/* Project Working Directory */}
          <div className="space-y-1.5">
            <label className="text-content-muted font-medium">Project Working Directory:</label>
            <div className="flex items-center gap-2">
              <input
                type="text"
                value={workingDirectory}
                onChange={e => setWorkingDirectory(e.target.value)}
                placeholder="C:\Projects\MyProject"
                className="flex-1 px-3 py-2 rounded-lg bg-surface-input border border-surface-border text-content-main font-mono text-xs focus:outline-none focus:border-sky-500 shadow-sm"
                required
              />
              <button
                type="button"
                onClick={handleBrowse}
                className="px-3 py-2 rounded-lg bg-surface-deep border border-surface-border hover:border-sky-500 text-content-muted hover:text-content-main transition whitespace-nowrap flex items-center gap-1.5 font-medium"
              >
                <Folder className="w-3.5 h-3.5 text-amber-500" />
                <span>Browse...</span>
              </button>
            </div>
            <p className="text-[11px] text-content-dim">
              Terminals, git status, and CLI tools will launch inside this project root.
            </p>
          </div>

          {/* Associated Antigravity Profile */}
          <div className="space-y-1.5">
            <label className="text-content-muted font-medium">Associated Antigravity Profile:</label>
            <select
              value={antigravityProfile}
              onChange={e => setAntigravityProfile(e.target.value)}
              className="w-full px-3 py-2 rounded-lg bg-surface-input border border-surface-border text-content-main font-mono focus:outline-none focus:border-sky-500 shadow-sm cursor-pointer"
            >
              {profiles.map(p => (
                <option key={p.name} value={p.name} className="bg-slate-900">
                  {p.name} {p.email ? `(${p.email})` : ''}
                </option>
              ))}
            </select>
            <p className="text-[11px] text-content-dim">
              Auto-switches Google AI Pro credentials when activating this workspace.
            </p>
          </div>

          {/* Primary & Secondary CLI */}
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <label className="text-content-muted font-medium">Primary CLI Terminal:</label>
              <select
                value={primaryCli}
                onChange={e => setPrimaryCli(e.target.value)}
                className="w-full px-3 py-2 rounded-lg bg-surface-input border border-surface-border text-content-main focus:outline-none focus:border-sky-500 shadow-sm cursor-pointer"
              >
                <option value="AntigravityCli">Antigravity CLI</option>
                <option value="ClaudeCode">Claude Code</option>
                <option value="Hub">Hub CLI</option>
                <option value="powershell">PowerShell</option>
                <option value="cmd">Command Prompt</option>
              </select>
            </div>

            <div className="space-y-1.5">
              <label className="text-content-muted font-medium">Secondary CLI Terminal:</label>
              <select
                value={secondaryCli}
                onChange={e => setSecondaryCli(e.target.value)}
                className="w-full px-3 py-2 rounded-lg bg-surface-input border border-surface-border text-content-main focus:outline-none focus:border-sky-500 shadow-sm cursor-pointer"
              >
                <option value="none">None (Single Pane)</option>
                <option value="powershell">PowerShell</option>
                <option value="cmd">Command Prompt</option>
                <option value="Hub">Hub CLI</option>
                <option value="ClaudeCode">Claude Code</option>
                <option value="AntigravityCli">Antigravity CLI</option>
              </select>
            </div>
          </div>

          {/* Split Mode & Ratio */}
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <label className="text-content-muted font-medium">Split Mode:</label>
              <select
                value={splitMode}
                onChange={e => setSplitMode(e.target.value as any)}
                className="w-full px-3 py-2 rounded-lg bg-surface-input border border-surface-border text-content-main focus:outline-none focus:border-sky-500 shadow-sm cursor-pointer"
              >
                <option value="single">Single Pane</option>
                <option value="horizontal">Horizontal Split (Left / Right)</option>
                <option value="vertical">Vertical Split (Top / Bottom)</option>
              </select>
            </div>

            <div className="space-y-1.5">
              <label className="text-content-muted font-medium">Split Ratio:</label>
              <select
                value={splitRatio}
                onChange={e => setSplitRatio(e.target.value)}
                className="w-full px-3 py-2 rounded-lg bg-surface-input border border-surface-border text-content-main focus:outline-none focus:border-sky-500 shadow-sm cursor-pointer"
              >
                <option value="0.5">50% / 50% Even</option>
                <option value="0.6">60% Primary / 40% Secondary</option>
                <option value="0.7">70% Primary / 30% Secondary</option>
              </select>
            </div>
          </div>

          {/* Action buttons */}
          <div className="flex items-center justify-between pt-3 border-t border-surface-border">
            {activeWorkspace ? (
              <button
                type="button"
                onClick={handleDelete}
                className="px-3 py-1.5 rounded-lg text-rose-400 hover:bg-rose-500/10 border border-transparent hover:border-rose-500/30 transition flex items-center gap-1.5"
              >
                <Trash2 className="w-3.5 h-3.5" />
                <span>Delete Workspace</span>
              </button>
            ) : <div />}

            <div className="flex items-center space-x-2">
              <button
                type="button"
                onClick={onClose}
                className="px-3 py-1.5 rounded-lg bg-surface-deep border border-surface-border hover:bg-surface-deep/80 text-content-muted hover:text-content-main transition"
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={isSaving}
                className="px-4 py-1.5 rounded-lg bg-sky-600 hover:bg-sky-500 text-white font-medium transition shadow-sm flex items-center gap-1.5"
              >
                {isSaving ? (
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                ) : isSaved ? (
                  <Check className="w-3.5 h-3.5 text-white" />
                ) : null}
                <span>{isSaved ? 'Saved!' : 'Save Workspace'}</span>
              </button>
            </div>
          </div>
        </form>
      </div>
    </div>
  );
};
