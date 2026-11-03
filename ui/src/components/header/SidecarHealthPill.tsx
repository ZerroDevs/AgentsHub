import React from 'react';
import { Cpu, Wifi, Activity } from 'lucide-react';
import { SidecarHealth } from '../../types/agent';
import { Tooltip } from '../common/Tooltip';

interface SidecarHealthPillProps {
  health: SidecarHealth;
  onRefresh?: () => void;
}

export const SidecarHealthPill: React.FC<SidecarHealthPillProps> = ({ health, onRefresh }) => {
  const isOnline = health.isOnline || health.status === 'Online';
  const isStarting = health.status === 'Starting' || health.status === 'Connecting';

  const dotColor = isOnline
    ? 'bg-emerald-400'
    : isStarting
    ? 'bg-amber-400 animate-pulse'
    : 'bg-rose-400';

  const tooltipContent = (
    <div className="space-y-1.5 p-1 font-mono text-[11px] leading-relaxed select-none">
      <div className="flex items-center justify-between gap-4 pb-1 border-b border-surface-border/80">
        <span className="font-semibold text-content-main">Bun Sidecar Engine</span>
        <span
          className={`font-bold ${
            isOnline ? 'text-state-success' : isStarting ? 'text-state-warning' : 'text-state-danger'
          }`}
        >
          {health.status}
        </span>
      </div>
      <div className="flex items-center justify-between gap-4 text-content-muted">
        <span>Port:</span>
        <span className="text-content-main">{health.port > 0 ? health.port : 'Auto (OS)'}</span>
      </div>
      <div className="flex items-center justify-between gap-4 text-content-muted">
        <span>Process ID (PID):</span>
        <span className="text-content-main">{health.pid > 0 ? health.pid : 'N/A'}</span>
      </div>
      <div className="flex items-center justify-between gap-4 text-content-muted">
        <span>Latency:</span>
        <span className="text-state-success font-semibold">{health.latencyMs}ms</span>
      </div>
      <div className="flex items-center justify-between gap-4 text-content-muted">
        <span>Active Sessions:</span>
        <span className="text-accent font-semibold">{health.activeSessions}</span>
      </div>
      <div className="flex items-center justify-between gap-4 text-content-muted pt-1 border-t border-surface-border">
        <span>Protocol:</span>
        <span className="text-content-muted">{health.version || `v${__APP_VERSION__}`}</span>
      </div>
    </div>
  );

  return (
    <Tooltip content={tooltipContent} position="bottom">
      <button
        type="button"
        onClick={onRefresh}
        className="flex items-center justify-center w-7 h-7 rounded-lg bg-surface-card border border-surface-border hover:border-surface-border hover:bg-surface-strong transition shadow-sm relative group"
        title="Sidecar Process Health"
      >
        <Cpu className="w-3.5 h-3.5 text-content-muted group-hover:text-content-main transition" />
        <span
          className={`absolute top-1 right-1 w-1.5 h-1.5 rounded-full ${dotColor} shadow-sm`}
        />
      </button>
    </Tooltip>
  );
};
