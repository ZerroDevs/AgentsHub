import React, { useEffect, useRef } from 'react';
import { Loader2, RefreshCw } from 'lucide-react';
import { SlashCommand, SlashCommandSource } from '../../types/agent';

interface SlashCommandPaletteProps {
  commands: SlashCommand[];
  query: string;
  activeIndex: number;
  isLoading: boolean;
  error?: string;
  engineLabel?: string;
  onSelect: (command: SlashCommand) => void;
  onHover: (index: number) => void;
  onRefresh: () => void;
  onDismiss: () => void;
}

const SOURCE_LABEL: Record<SlashCommandSource, string> = {
  builtin: 'builtin',
  skill: 'skill',
  mcp: 'mcp',
  profile: 'profile',
};

const SOURCE_CLASS: Record<SlashCommandSource, string> = {
  builtin: 'border-surface-border text-content-muted',
  skill: 'border-sky-500/40 text-accent',
  mcp: 'border-emerald-500/40 text-state-success',
  profile: 'border-amber-500/40 text-state-warning',
};

/**
 * Floating command palette shown above the composer while the caret sits in a `/token`.
 *
 * Selection is driven from the composer's key handler so arrow keys and Enter keep working while
 * the textarea holds focus; this component is presentational plus scroll-into-view.
 */
export const SlashCommandPalette: React.FC<SlashCommandPaletteProps> = ({
  commands,
  query,
  activeIndex,
  isLoading,
  error,
  engineLabel,
  onSelect,
  onHover,
  onRefresh,
  onDismiss,
}) => {
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const container = listRef.current;
    if (!container) return;
    const active = container.querySelector<HTMLElement>('[data-active="true"]');
    active?.scrollIntoView({ block: 'nearest' });
  }, [activeIndex, commands.length]);

  return (
    <div
      className="absolute bottom-full left-0 right-0 z-40 mb-2 overflow-hidden rounded-xl border border-surface-border bg-surface-card shadow-2xl"
      role="listbox"
      aria-label="Slash commands"
    >
      <div className="flex items-center justify-between border-b border-surface-border/70 px-3 py-1.5">
        <div className="flex items-center gap-2">
          <span className="text-[10.5px] font-medium text-content-dim">
            Commands
          </span>
          {engineLabel ? (
            <span className="font-mono text-[10px] text-content-dim">· {engineLabel}</span>
          ) : null}
          {query ? (
            <span className="font-mono text-[10px] text-accent">/{query}</span>
          ) : null}
        </div>

        <div className="flex items-center gap-2">
          {isLoading ? <Loader2 className="h-3 w-3 animate-spin text-content-dim" /> : null}
          <span className="font-mono text-[10px] text-content-dim">{commands.length}</span>
          <button
            type="button"
            onClick={onRefresh}
            title="Re-scan commands"
            className="rounded p-0.5 text-content-dim transition-colors hover:bg-surface-strong hover:text-content-main"
          >
            <RefreshCw className="h-3 w-3" />
          </button>
        </div>
      </div>

      <div ref={listRef} className="max-h-[268px] overflow-y-auto py-1">
        {commands.length === 0 ? (
          <div className="px-3 py-4 text-center font-mono text-[11px] leading-relaxed text-content-dim">
            {isLoading
              ? 'Scanning installed commands…'
              : error
                ? `Discovery failed: ${error}`
                : `No command matches /${query}. Keep typing, or press Esc to write plain text.`}
          </div>
        ) : (
          commands.map((command, index) => {
            const isActive = index === activeIndex;
            return (
              <button
                key={`${command.source}_${command.name}`}
                type="button"
                role="option"
                aria-selected={isActive}
                data-active={isActive}
                onMouseEnter={() => onHover(index)}
                onMouseDown={event => {
                  // Keep focus in the textarea so the caret does not jump before insertion.
                  event.preventDefault();
                  onSelect(command);
                }}
                className={`flex w-full items-center gap-2 px-3 py-1.5 text-left transition-colors ${
                  isActive ? 'bg-sky-500/10' : 'hover:bg-surface-hover'
                }`}
              >
                <span className="flex-shrink-0 font-mono text-[11.5px] text-content-main">
                  /{command.name}
                </span>
                {command.hint ? (
                  <span className="flex-shrink-0 font-mono text-[10px] text-content-dim">{command.hint}</span>
                ) : null}
                <span className="min-w-0 flex-1 truncate text-[11px] text-content-dim">
                  {command.description}
                </span>
                <span
                  className={`flex-shrink-0 rounded border px-1.5 py-0.5 font-mono text-[9px] uppercase tracking-[0.05em] ${
                    SOURCE_CLASS[command.source]
                  }`}
                >
                  {SOURCE_LABEL[command.source]}
                </span>
              </button>
            );
          })
        )}
      </div>

      <div className="flex items-center justify-between border-t border-surface-border/70 px-3 py-1.5 font-mono text-[9.5px] text-content-dim">
        <span>↑↓ move · Tab insert · Enter run · Esc dismiss</span>
        <button type="button" onClick={onDismiss} className="hover:text-content-muted">
          close
        </button>
      </div>
    </div>
  );
};
