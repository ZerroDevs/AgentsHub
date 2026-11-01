import React, { useState, useEffect } from 'react';
import { GitBranch, RefreshCw, FileText, Check, Plus, Minus, Folder } from 'lucide-react';
import { actionIcons } from '../../lib/toolIcons';
import { Button } from '../common/Button';
import { webviewBridge } from '../../services/webviewBridge';
import { useWorkspaceStore } from '../../store/useWorkspaceStore';

interface GitDiffModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export const GitDiffModal: React.FC<GitDiffModalProps> = ({ isOpen, onClose }) => {
  const { activeWorkspace } = useWorkspaceStore();
  const [diffText, setDiffText] = useState<string>('');
  const [isLoading, setIsLoading] = useState(false);
  const [changedFiles, setChangedFiles] = useState<string[]>([]);
  const [selectedFile, setSelectedFile] = useState<string | null>(null);

  const fetchDiff = async () => {
    try {
      setIsLoading(true);
      const repoPath = activeWorkspace?.workingDirectory || undefined;
      const res = await webviewBridge.invoke('get_git_status', { repoPath });
      if (res) {
        setDiffText(res.diff || res.rawDiff || 'No uncommitted changes detected in active workspace.');
        setChangedFiles(Array.isArray(res.files) ? res.files : []);
      } else {
        setDiffText('No uncommitted changes in active workspace.');
        setChangedFiles([]);
      }
    } catch {
      setDiffText('Workspace git status not available.');
      setChangedFiles([]);
    } finally {
      setIsLoading(false);
    }
  };

  useEffect(() => {
    if (isOpen) {
      setSelectedFile(null);
      fetchDiff();
    }
  }, [isOpen, activeWorkspace?.id]);

  if (!isOpen) return null;

  const renderDiffLines = (raw: string) => {
    const lines = raw.split('\n');
    return lines.map((line, idx) => {
      let lineCls = 'text-content-muted';
      let bgCls = '';
      if (line.startsWith('+') && !line.startsWith('+++')) {
        lineCls = 'text-emerald-400 font-semibold';
        bgCls = 'bg-emerald-500/10 px-1 rounded-xs';
      } else if (line.startsWith('-') && !line.startsWith('---')) {
        lineCls = 'text-rose-400';
        bgCls = 'bg-rose-500/10 px-1 rounded-xs';
      } else if (line.startsWith('@@')) {
        lineCls = 'text-sky-400 font-bold';
        bgCls = 'bg-sky-500/10 px-1 rounded-xs';
      } else if (line.startsWith('diff --git')) {
        lineCls = 'text-amber-400 font-bold';
      }

      return (
        <div key={idx} className={`leading-relaxed whitespace-pre font-mono ${lineCls} ${bgCls}`}>
          {line || ' '}
        </div>
      );
    });
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs select-none"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-label="Workspace git changes"
    >
      <div
        className="w-full max-w-4xl h-[82vh] flex flex-col rounded-2xl bg-surface-card border border-surface-border shadow-2xl overflow-hidden font-sans"
        onClick={event => event.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-surface-border bg-surface-deep/80">
          <div className="flex items-center space-x-2.5">
            <div className="w-7 h-7 rounded-lg bg-emerald-500/15 border border-emerald-500/30 flex items-center justify-center text-emerald-400">
              <GitBranch className="w-4 h-4" />
            </div>
            <div>
              <div className="flex items-center gap-2">
                <span className="text-sm font-bold text-content-main">Workspace Git Changes</span>
                {activeWorkspace && (
                  <span className="text-[10px] font-mono px-2 py-0.5 rounded bg-sky-500/10 text-sky-400 border border-sky-500/20">
                    {activeWorkspace.name}
                  </span>
                )}
              </div>
              <p className="text-[11px] text-content-dim font-mono truncate max-w-md">
                {activeWorkspace?.workingDirectory || 'Current Workspace'}
              </p>
            </div>
          </div>

          <div className="flex items-center space-x-2">
            <button
              onClick={fetchDiff}
              className="p-1.5 rounded-lg text-content-dim hover:text-content-main hover:bg-surface-deep transition"
              title="Refresh Workspace Git Status"
            >
              <RefreshCw className={`w-3.5 h-3.5 ${isLoading ? 'animate-spin' : ''}`} />
            </button>
            <button
              onClick={onClose}
              className="p-1.5 rounded-lg text-content-dim hover:text-content-main hover:bg-surface-deep transition"
              title="Close modal"
            >
              <img src={actionIcons.close} alt="Close" className="h-4 w-4 object-contain" />
            </button>
          </div>
        </div>

        {/* Content */}
        <div className="flex-1 overflow-y-auto p-4 space-y-3.5 font-mono text-xs">
          {changedFiles.length > 0 ? (
            <div className="space-y-1.5">
              <div className="flex items-center justify-between text-[11px] uppercase tracking-wider text-content-dim font-bold">
                <span>Modified Files ({changedFiles.length})</span>
                <span className="text-[10px] font-normal normal-case text-content-dim">
                  Click a file to highlight diff
                </span>
              </div>
              <div className="flex flex-wrap gap-1.5 max-h-28 overflow-y-auto p-1.5 rounded-xl bg-surface-deep/60 border border-surface-border">
                {changedFiles.map((f, i) => {
                  const isSel = selectedFile === f;
                  return (
                    <button
                      key={i}
                      type="button"
                      onClick={() => setSelectedFile(isSel ? null : f)}
                      className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-lg text-[11px] font-mono transition cursor-pointer border ${
                        isSel
                          ? 'bg-sky-500/20 text-sky-300 border-sky-500/50'
                          : 'bg-surface-card border-surface-border text-content-muted hover:text-content-main hover:border-surface-border/80'
                      }`}
                    >
                      <FileText className="w-3 h-3 text-sky-400" />
                      <span className="truncate max-w-[220px]">{f}</span>
                    </button>
                  );
                })}
              </div>
            </div>
          ) : (
            <div className="p-3 rounded-xl bg-surface-deep/40 border border-surface-border text-xs text-content-dim font-sans flex items-center gap-2">
              <Check className="w-4 h-4 text-emerald-400" />
              <span>Working tree is clean. No uncommitted modifications.</span>
            </div>
          )}

          {/* Diff Box */}
          <div className="border border-surface-border rounded-xl overflow-hidden bg-surface-deep/90 shadow-inner flex flex-col">
            <div className="px-3.5 py-2 bg-surface-deep border-b border-surface-border text-[11px] text-content-dim font-semibold flex items-center justify-between">
              <span>Diff Preview</span>
              {changedFiles.length > 0 && (
                <span className="font-mono text-[10px] text-emerald-400">
                  {changedFiles.length} file{changedFiles.length === 1 ? '' : 's'} altered
                </span>
              )}
            </div>
            <div className="p-4 text-[11.5px] font-mono leading-relaxed overflow-x-auto select-text max-h-[46vh]">
              {diffText ? renderDiffLines(diffText) : (
                <span className="text-content-dim">No diff content available.</span>
              )}
            </div>
          </div>
        </div>

        {/* Footer */}
        <div className="px-5 py-3 border-t border-surface-border flex items-center justify-between bg-surface-deep/80">
          <span className="text-[11px] font-mono text-content-dim">
            Git repository status synced with active theme
          </span>
          <Button variant="secondary" onClick={onClose}>
            Close
          </Button>
        </div>
      </div>
    </div>
  );
};
