import React from 'react';
import { brandLogo } from '../../lib/toolIcons';

interface MascotStatusProps {
  isRunning?: boolean;
  statusText?: string;
  onClick?: () => void;
}

export const MascotStatus: React.FC<MascotStatusProps> = ({
  isRunning = false,
  statusText = 'Ready',
  onClick,
}) => {
  return (
    <div
      onClick={onClick}
      className="flex items-center space-x-2.5 cursor-pointer select-none group px-2 py-1 rounded-lg hover:bg-surface-hover transition"
      title="Agents Hub - Desktop AI Workspace"
    >
      <div className="relative w-7 h-7 rounded-lg overflow-hidden bg-surface-card border border-surface-border/80 flex items-center justify-center p-0.5 shadow-sm group-hover:border-sky-500/50 transition-colors">
        <img
          src={brandLogo}
          alt="Agents Hub Mascot"
          className="w-full h-full object-contain filter group-hover:brightness-110 transition"
        />
        <span
          className={`absolute bottom-0 right-0 w-2 h-2 rounded-full border border-surface-card ${
            isRunning ? 'bg-emerald-400 animate-pulse' : 'bg-content-dim'
          }`}
        />
      </div>

      <div className="flex flex-col">
        <span className="text-xs font-bold text-content-main tracking-tight flex items-center gap-1.5">
          <span>Agents Hub</span>
          <span className="px-1 py-0.2 rounded text-[9px] font-mono font-semibold bg-sky-500/15 text-accent border border-sky-500/30">
            v{__APP_VERSION__}
          </span>
        </span>
        <span className="text-[10px] font-mono text-content-muted">
          {isRunning ? (
            <span className="text-state-success font-medium">Agent Active</span>
          ) : (
            statusText
          )}
        </span>
      </div>
    </div>
  );
};
