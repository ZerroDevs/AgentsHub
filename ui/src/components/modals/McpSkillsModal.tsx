import React, { useState, useEffect } from 'react';
import { Cpu, Sparkles, ShieldCheck, KeyRound } from 'lucide-react';
import { actionIcons } from '../../lib/toolIcons';
import { Button } from '../common/Button';
import { useSettingsStore } from '../../store/useSettingsStore';

interface McpSkillsModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export const McpSkillsModal: React.FC<McpSkillsModalProps> = ({ isOpen, onClose }) => {
  const [activeTab, setActiveTab] = useState<'mcp' | 'skills'>('mcp');
  const { mcpServers, agentSkills, loadMcpAndSkills } = useSettingsStore();

  useEffect(() => {
    if (isOpen) {
      loadMcpAndSkills();
    }
  }, [isOpen, loadMcpAndSkills]);

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/60 backdrop-blur-xs select-none">
      <div className="w-full max-w-2xl rounded-2xl bg-surface-card border border-surface-border shadow-2xl overflow-hidden font-sans">
        <div className="flex items-center justify-between px-5 py-3.5 border-b border-surface-border bg-surface-deep/50">
          <div className="flex items-center space-x-2 text-content-main font-semibold text-sm">
            <Cpu className="w-4 h-4 text-sky-400" />
            <span>MCP Servers & Agent Skills</span>
          </div>
          <button
            onClick={onClose}
            className="p-1 rounded text-content-muted hover:text-content-main hover:bg-surface-deep transition"
          >
            <img src={actionIcons.close} alt="Close" className="h-4 w-4 object-contain" />
          </button>
        </div>

        {/* Tab switch */}
        <div className="flex px-4 pt-3 border-b border-surface-border space-x-2 text-xs font-mono">
          <button
            onClick={() => setActiveTab('mcp')}
            className={`px-3 py-1.5 border-b-2 font-medium transition ${
              activeTab === 'mcp'
                ? 'border-sky-400 text-sky-400 font-bold'
                : 'border-transparent text-content-muted hover:text-content-main'
            }`}
          >
            MCP Servers ({mcpServers.length})
          </button>
          <button
            onClick={() => setActiveTab('skills')}
            className={`px-3 py-1.5 border-b-2 font-medium transition ${
              activeTab === 'skills'
                ? 'border-sky-400 text-sky-400 font-bold'
                : 'border-transparent text-content-muted hover:text-content-main'
            }`}
          >
            Installed Skills ({agentSkills.length})
          </button>
        </div>

        {/* List */}
        <div className="p-4 max-h-[60vh] overflow-y-auto space-y-2">
          {activeTab === 'mcp' ? (
            mcpServers.length > 0 ? (
              mcpServers.map((s, i) => {
                const count = s.toolCount ?? s.toolsCount ?? 0;
                const isAuthed = s.authStatus === 'Authenticated';
                const isExpired = s.authStatus === 'TokenExpired';

                return (
                  <div
                    key={s.id || s.name || i}
                    className="flex items-center justify-between p-3 rounded-xl bg-surface-deep/60 border border-surface-border/80"
                  >
                    <div className="flex items-center space-x-3">
                      <span className={`w-2 h-2 rounded-full ${isAuthed ? 'bg-emerald-400' : isExpired ? 'bg-amber-400' : 'bg-sky-400'}`} />
                      <div>
                        <div className="font-mono text-xs font-semibold text-content-main flex items-center gap-2">
                          <span>{s.name}</span>
                          {s.scope && (
                            <span className="text-[10px] font-normal px-1.5 py-0.2 rounded bg-purple-500/10 text-purple-400 border border-purple-500/20">
                              {s.scope}
                            </span>
                          )}
                        </div>
                        <div className="text-[11px] text-content-muted font-mono">
                          {count} registered tool{count !== 1 ? 's' : ''}
                          {s.description ? ` · ${s.description}` : ''}
                        </div>
                      </div>
                    </div>
                    <div className="flex items-center gap-2">
                      {s.requiresAuth && (
                        <span
                          className={`px-2 py-0.5 rounded text-[10.5px] font-mono flex items-center gap-1 border ${
                            isAuthed
                              ? 'bg-emerald-500/10 text-emerald-400 border-emerald-500/20'
                              : isExpired
                              ? 'bg-amber-500/10 text-amber-400 border-amber-500/20'
                              : 'bg-rose-500/10 text-rose-400 border-rose-500/20'
                          }`}
                        >
                          <KeyRound className="w-3 h-3" />
                          {isAuthed ? 'Authenticated' : isExpired ? 'Token Expired' : 'Needs Auth'}
                        </span>
                      )}
                      <span className="px-2 py-0.5 rounded text-[10.5px] font-mono bg-emerald-500/10 text-emerald-400 border border-emerald-500/20">
                        {s.status || 'Active'}
                      </span>
                    </div>
                  </div>
                );
              })
            ) : (
              <div className="py-8 text-center text-xs text-content-muted font-mono">
                No MCP servers currently loaded.
              </div>
            )
          ) : agentSkills.length > 0 ? (
            agentSkills.map((sk, i) => (
              <div
                key={sk.name || i}
                className="p-3 rounded-xl bg-surface-deep/60 border border-surface-border/80 space-y-1"
              >
                <div className="flex items-center justify-between">
                  <span className="font-mono text-xs font-semibold text-sky-400 flex items-center gap-1.5">
                    <Sparkles className="w-3.5 h-3.5 text-sky-400" />
                    <span>{sk.name}</span>
                  </span>
                  <div className="flex items-center gap-2">
                    <span className="text-[10px] text-content-muted font-mono">
                      ~{sk.tokenCount || 300} tok
                    </span>
                    <span className="px-2 py-0.5 rounded text-[10.5px] font-mono bg-surface-card text-content-main border border-surface-border">
                      {sk.scope === 'project' ? 'Project' : 'Builtin'}
                    </span>
                  </div>
                </div>
                <div className="text-xs text-content-muted">{sk.description}</div>
                {sk.path && (
                  <div className="text-[10px] text-content-muted/70 font-mono truncate">
                    {sk.path}
                  </div>
                )}
              </div>
            ))
          ) : (
            <div className="py-8 text-center text-xs text-content-muted font-mono">
              No agent skills currently discovered.
            </div>
          )}
        </div>

        <div className="p-3 border-t border-surface-border flex justify-end bg-surface-deep/40">
          <Button variant="secondary" onClick={onClose}>
            Done
          </Button>
        </div>
      </div>
    </div>
  );
};
