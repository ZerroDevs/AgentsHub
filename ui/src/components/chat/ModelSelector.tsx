import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Check, ChevronDown, ChevronRight, RefreshCw, Sparkles } from 'lucide-react';
import { useModelStore, effortLabel, findBaseModel, resolveModelId } from '../../store/useModelStore';
import { useSessionStore } from '../../store/useSessionStore';
import { ModelInfo } from '../../types/agent';

interface ModelSelectorProps {
  sessionId: string;
  /** Active model id, already resolved against the session and the profile default. */
  model?: string;
  /** Active reasoning effort for this session. */
  effort?: string;
  /** Active CLI id, so the catalogue is fetched for the right engine. */
  cli?: string;
  disabled?: boolean;
}

const PANEL_WIDTH = 330;
const FLYOUT_WIDTH = 152;
const VIEWPORT_MARGIN = 8;

function shortLabel(label: string, max = 34): string {
  if (label.length <= max) return label;
  return `${label.slice(0, max - 1).trimEnd()}…`;
}

/**
 * Model + reasoning effort picker for the composer footer.
 *
 * The list is whatever the engine reported, so it stays correct as models ship and retire. Effort is
 * the engine's own scale (`--effort low|medium|high|xhigh|max`), shown as a flyout so the model list
 * stays the primary surface.
 *
 * The panel renders in a portal: the composer card clips its own children (`overflow-hidden` for the
 * rounded corners), so an absolutely positioned panel would be cut off at the card edge.
 */
export const ModelSelector: React.FC<ModelSelectorProps> = ({
  sessionId,
  model,
  effort,
  cli,
  disabled = false,
}) => {
  const { models, efforts, isLoading, error, load } = useModelStore();
  const updateSessionTelemetry = useSessionStore(state => state.updateSessionTelemetry);

  const [isOpen, setIsOpen] = useState(false);
  const [isEffortOpen, setIsEffortOpen] = useState(false);
  const [anchor, setAnchor] = useState<{ bottom: number; left: number; flyoutRight: boolean } | null>(null);

  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    void load(cli);
  }, [cli, load]);

  const measure = useCallback(() => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;

    const left = Math.max(
      VIEWPORT_MARGIN,
      Math.min(rect.left, window.innerWidth - PANEL_WIDTH - VIEWPORT_MARGIN)
    );
    // The flyout opens to the right unless that would leave the viewport.
    const flyoutRight = left + PANEL_WIDTH + FLYOUT_WIDTH + VIEWPORT_MARGIN > window.innerWidth;

    setAnchor({
      bottom: Math.max(VIEWPORT_MARGIN, window.innerHeight - rect.top + VIEWPORT_MARGIN),
      left,
      flyoutRight,
    });
  }, []);

  useLayoutEffect(() => {
    if (isOpen) measure();
  }, [isOpen, measure]);

  useEffect(() => {
    if (!isOpen) return;

    const handlePointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (panelRef.current?.contains(target)) return;
      if (triggerRef.current?.contains(target)) return;
      setIsOpen(false);
      setIsEffortOpen(false);
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      setIsOpen(false);
      setIsEffortOpen(false);
    };

    /**
     * Scrolling the message list fires this too, and closing on every token would make the panel
     * unusable while a turn streams. Re-measure instead, and only close once the trigger itself has
     * left the viewport.
     */
    const handleReposition = () => {
      const rect = triggerRef.current?.getBoundingClientRect();
      if (!rect) return;

      if (rect.bottom < 0 || rect.top > window.innerHeight) {
        setIsOpen(false);
        setIsEffortOpen(false);
        return;
      }

      measure();
    };

    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('keydown', handleEscape);
    window.addEventListener('resize', handleReposition);
    window.addEventListener('scroll', handleReposition, true);

    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('keydown', handleEscape);
      window.removeEventListener('resize', handleReposition);
      window.removeEventListener('scroll', handleReposition, true);
    };
  }, [isOpen, measure]);

  // `model` is stored as a base id; older sessions may still hold a concrete tier id, which
  // findBaseModel also resolves.
  const activeModel: ModelInfo | undefined = findBaseModel(models, model);
  const activeLabel = activeModel?.label || model || 'Default model';
  const activeEffortLabel = effortLabel(effort);
  const dispatchModelId = resolveModelId(models, activeModel?.id || model, effort);
  // A model with no tier map has no effort levels; the picker must not offer them.
  const supportsEffort = Object.keys(activeModel?.tiers ?? {}).length > 0;

  /**
   * Stores the *concrete* engine id for the current effort, so dispatch never has to guess from a
   * base id it might not be able to resolve yet.
   */
  const selectModel = (entry: ModelInfo) => {
    const concreteId = resolveModelId(models, entry.id, effort) || entry.id;
    updateSessionTelemetry(sessionId, { model: concreteId });
    setIsOpen(false);
    setIsEffortOpen(false);
  };

  /**
   * Switching effort re-points the model at the matching tier of the same base model. With no model
   * chosen yet it falls back to the engine's first catalogue entry, so picking an effort on a fresh
   * session actually applies instead of leaving the request on the engine default.
   */
  const selectEffort = (level: string) => {
    const baseId = activeModel?.id || models[0]?.id;
    const concreteId = resolveModelId(models, baseId, level);
    updateSessionTelemetry(sessionId, {
      reasoningEffort: level,
      ...(concreteId ? { model: concreteId } : {}),
    });
    setIsEffortOpen(false);
  };

  const panel =
    isOpen && anchor ? (
      <div
        ref={panelRef}
        style={{ bottom: anchor.bottom, left: anchor.left, width: PANEL_WIDTH }}
        className="fixed z-[80] rounded-xl border border-surface-border bg-surface-card shadow-2xl"
      >
        <div className="flex items-center justify-between rounded-t-xl border-b border-surface-border/70 px-3 py-1.5">
          <span className="text-[10.5px] font-medium text-content-dim">Model</span>
          <span className="flex items-center gap-1.5 font-mono text-[10px] text-content-dim">
            {models.length > 0 ? <span>{models.length}</span> : null}
            {/* The catalogue is cached for five minutes and the probe can come back empty, so a
                manual re-read has to survive that cache. */}
            <button
              type="button"
              onClick={() => void load(cli, { force: true })}
              disabled={isLoading}
              aria-label="Refresh model list"
              title={isLoading ? 'Reading the model list…' : 'Read the model list again'}
              className="-mr-1 rounded p-1 transition-colors hover:text-content-main focus-visible:text-accent focus-visible:outline-none disabled:opacity-60"
            >
              <RefreshCw className={`h-3 w-3 ${isLoading ? 'animate-spin' : ''}`} />
            </button>
          </span>
        </div>

        <div className="max-h-[340px] overflow-y-auto py-1 pr-1" role="listbox" aria-label="Available models">
          {models.length === 0 ? (
            <p className="px-3 py-4 text-center font-mono text-[11px] leading-relaxed text-content-dim">
              {isLoading
                ? 'Querying the engine for its model list…'
                : error
                  ? `Could not read the model list: ${error}`
                  : 'The engine reported no models.'}
            </p>
          ) : (
            models.map(entry => {
              const isActive = entry.id === activeModel?.id;
              return (
                <button
                  key={entry.id}
                  type="button"
                  role="option"
                  aria-selected={isActive}
                  onClick={() => selectModel(entry)}
                  className={`flex w-full items-center gap-2 px-3 py-1.5 text-left transition-colors ${
                    isActive ? 'bg-accent-soft' : 'hover:bg-surface-hover'
                  }`}
                >
                  <span className="w-3 flex-shrink-0">
                    {isActive ? <Check className="h-3 w-3 text-accent" /> : null}
                  </span>

                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[11.5px] text-content-main">{entry.label}</span>
                    <span className="block truncate font-mono text-[9.5px] text-content-dim">
                      {isActive ? dispatchModelId || entry.id : entry.id}
                    </span>
                  </span>

                  {entry.provider ? (
                    <span className="flex-shrink-0 rounded border border-surface-border px-1.5 py-0.5 font-mono text-[9px] uppercase tracking-[0.05em] text-content-dim">
                      {entry.provider}
                    </span>
                  ) : null}
                </button>
              );
            })
          )}
        </div>

        {/* Effort only exists for models the engine publishes tiers for. Offering Low/Medium/High on
            a flat model produced "Effort changing isn't supported for this model". */}
        <div className={`relative rounded-b-xl border-t border-surface-border/70 ${supportsEffort ? '' : 'hidden'}`}>
          <button
            type="button"
            onClick={() => setIsEffortOpen(prev => !prev)}
            onMouseEnter={() => supportsEffort && setIsEffortOpen(true)}
            className={`flex w-full items-center justify-between rounded-b-xl px-3 py-2 text-left transition-colors ${
              isEffortOpen ? 'bg-surface-hover' : 'hover:bg-surface-hover'
            }`}
            aria-haspopup="listbox"
            aria-expanded={isEffortOpen}
          >
            <span className="text-[11px] text-content-muted">Thinking effort</span>
            <span className="flex items-center gap-1.5 font-mono text-[10.5px] text-content-main">
              <span>{activeEffortLabel || 'Default'}</span>
              <ChevronRight className="h-3 w-3 text-content-dim" />
            </span>
          </button>

          {isEffortOpen ? (
            <div
              style={{ width: FLYOUT_WIDTH }}
              className={`absolute bottom-0 z-[81] overflow-hidden rounded-xl border border-surface-border bg-surface-card shadow-2xl ${
                anchor.flyoutRight ? 'right-full mr-1.5' : 'left-full ml-1.5'
              }`}
              role="listbox"
              aria-label="Reasoning effort"
              onMouseLeave={() => setIsEffortOpen(false)}
            >
              <div className="border-b border-surface-border/70 px-3 py-1.5 text-[10.5px] font-medium text-content-dim">
                Thinking effort
              </div>
              <div className="py-1">
                {efforts.map(level => {
                  const isActive = level === effort;
                  return (
                    <button
                      key={level}
                      type="button"
                      role="option"
                      aria-selected={isActive}
                      onClick={() => selectEffort(level)}
                      className={`flex w-full items-center justify-between px-3 py-1.5 text-left text-[11px] transition-colors ${
                        isActive ? 'text-content-main' : 'text-content-muted hover:bg-surface-hover'
                      }`}
                    >
                      <span>{effortLabel(level)}</span>
                      {isActive ? <Check className="h-3 w-3 text-accent" /> : null}
                    </button>
                  );
                })}
              </div>
            </div>
          ) : null}
        </div>
      </div>
    ) : null;

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setIsOpen(prev => !prev)}
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={isOpen}
        data-testid="model-selector-trigger"
        title={`Model: ${activeLabel}${activeEffortLabel ? ` · effort ${activeEffortLabel}` : ''}`}
        className="flex max-w-[240px] items-center gap-1.5 rounded-md border border-surface-border/70 bg-surface-card/50 px-2 py-1 font-mono text-[11px] text-content-muted transition-colors hover:border-surface-border hover:bg-surface-hover hover:text-content-main focus-visible:border-accent focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-60"
      >
        <Sparkles className="h-3 w-3 flex-shrink-0 text-accent" />
        <span className="truncate">{shortLabel(activeLabel)}</span>
        {activeEffortLabel ? (
          <>
            <span className="text-content-dim">·</span>
            <span className="flex-shrink-0 text-content-dim">{activeEffortLabel}</span>
          </>
        ) : null}
        <ChevronDown
          className={`h-3 w-3 flex-shrink-0 text-content-dim transition-transform ${isOpen ? 'rotate-180' : ''}`}
        />
      </button>

      {panel ? createPortal(panel, document.body) : null}
    </>
  );
};
