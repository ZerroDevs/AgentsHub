import React, { useState } from 'react';
import { createPortal } from 'react-dom';
import {
  MessageSquare,
  LayoutDashboard,
  GitFork,
  Users,
  Cpu,
  Sliders,
  PanelLeftClose,
} from 'lucide-react';
import { ActiveView } from '../../types/agent';
import { useSidecarStore } from '../../store/useSidecarStore';
import { brandLogo } from '../../lib/toolIcons';

export const PrimaryRail: React.FC = () => {
  const { activeView, setActiveView, toggleRail } = useSidecarStore();

  /**
   * The rail lives inside a width-animated, overflow-hidden wrapper, so a tooltip anchored inside it
   * would be clipped to 56px. Rendered into the body instead, positioned from the hovered button.
   */
  const [tooltip, setTooltip] = useState<{ label: string; top: number } | null>(null);

  const navItems: Array<{ id: ActiveView; label: string; icon: React.ReactNode }> = [
    { id: 'chat', label: 'AI Agent Workspace (Chat IDE)', icon: <MessageSquare className="w-5 h-5" /> },
    { id: 'dashboard', label: 'Dashboard & Running Ports', icon: <LayoutDashboard className="w-5 h-5" /> },
    { id: 'repositories', label: 'Visual Git & Repositories', icon: <GitFork className="w-5 h-5" /> },
    { id: 'profiles', label: 'AccountsHub', icon: <Users className="w-5 h-5" /> },
    { id: 'mcpskills', label: 'Model Context Protocol (MCP) & Skills', icon: <Cpu className="w-5 h-5" /> },
    { id: 'settings', label: 'Settings & Telemetry', icon: <Sliders className="w-5 h-5" /> },
  ];

  return (
    <nav
      id="primary-rail"
      className="w-14 bg-surface-sidebar border-r border-surface-border flex flex-col items-center py-3 justify-between z-20 flex-shrink-0 select-none"
    >
      {/* Top Group: Brand Badge & Nav Items */}
      <div className="flex flex-col items-center space-y-3 w-full">
        {/* The app's own mark, drawn by its own artwork. A gradient tile with a generic cat
            glyph was the loudest tell that this is not a designed product. */}
        <button
          onClick={() => setActiveView('chat')}
          className="mb-1 w-9 h-9 rounded-lg transition hover:brightness-110 active:scale-95"
          title="Agents Hub"
          aria-label="Agents Hub"
        >
          <img src={brandLogo} alt="" className="w-full h-full rounded-lg object-contain" />
        </button>

        {/* Primary View Switcher Buttons */}
        <div className="flex flex-col items-center space-y-1.5 w-full px-2">
          {navItems.map(item => {
            const isActive = activeView === item.id;
            return (
              <button
                key={item.id}
                onClick={() => setActiveView(item.id)}
                onMouseEnter={event =>
                  setTooltip({ label: item.label, top: event.currentTarget.getBoundingClientRect().top })
                }
                onMouseLeave={() => setTooltip(null)}
                onFocus={event =>
                  setTooltip({ label: item.label, top: event.currentTarget.getBoundingClientRect().top })
                }
                onBlur={() => setTooltip(null)}
                className={`w-10 h-10 rounded-xl flex items-center justify-center transition relative ${
                  isActive
                    ? 'bg-accent-soft text-accent border border-sky-500/30'
                    : 'text-content-muted hover:text-content-main hover:bg-surface-card'
                }`}
                title={item.label}
              >
                {item.icon}
              </button>
            );
          })}
        </div>

      </div>

      {/* Bottom: the rail's own collapse control. The rail is the one panel a user hides when they
          want the full width back without losing the sessions list. */}
      <div className="flex flex-col items-center space-y-2">
        <button
          type="button"
          onClick={toggleRail}
          className="w-9 h-9 rounded-xl flex items-center justify-center text-content-dim hover:text-content-main hover:bg-surface-card transition"
          title="Hide the navigation rail"
          aria-label="Hide the navigation rail"
        >
          <PanelLeftClose className="w-4 h-4" />
        </button>
      </div>

      {tooltip
        ? createPortal(
            <div
              style={{ top: tooltip.top + 6, left: 64 }}
              className="pointer-events-none fixed z-[60] whitespace-nowrap rounded-md border border-surface-border bg-surface-card px-2.5 py-1 text-xs text-content-main shadow-xl"
            >
              {tooltip.label}
            </div>,
            document.body
          )
        : null}
    </nav>
  );
};
