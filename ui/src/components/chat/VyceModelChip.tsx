import React, { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Check, ChevronDown, Cpu, Loader2 } from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';

interface VyceModelRow {
  name: string;
  note: string;
  available: boolean;
}

interface VyceModelChipProps {
  /** The CLI reads its session and its model per workspace, so the list is asked for in this one. */
  workingDirectory?: string;
}

const PANEL_WIDTH = 330;
const VIEWPORT_MARGIN = 8;

/**
 * Vyce's model picker for the composer footer.
 *
 * The names come from the CLI's own menu, which is the only place they exist. That menu is driven
 * behind the scenes: this chip asks the host to open it, read it and close it again, so a model
 * added by the provider shows up here the same day.
 *
 * The panel is portalled and measured from the trigger, because the composer card clips its own
 * children and an absolutely positioned panel would be cut off at its edge.
 */
export const VyceModelChip: React.FC<VyceModelChipProps> = ({ workingDirectory }) => {
  const [current, setCurrent] = useState<string | null>(null);
  const [rows, setRows] = useState<VyceModelRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [anchor, setAnchor] = useState<{ bottom: number; left: number } | null>(null);

  const triggerRef = useRef<HTMLButtonElement>(null);

  // The label comes from the session file, so naming the model does not start a console.
  useEffect(() => {
    let active = true;

    void webviewBridge
      .invoke('vyce_model_current', {})
      .then((res: any) => {
        if (!active) return;
        if (typeof res?.model === 'string' && res.model.length > 0) setCurrent(res.model);
      })
      .catch(() => {
        // A missing label is not an error: the chip still opens the list.
      });

    return () => {
      active = false;
    };
  }, []);

  const loadList = useCallback(async () => {
    setError(null);
    setRows(null);

    try {
      // The first open may have to start the CLI and draw its menu, which is slower than a bridge
      // call is usually allowed to take.
      const res: any = await webviewBridge.invoke('vyce_models', { workingDirectory }, 60000);
      const list = Array.isArray(res?.models) ? (res.models as VyceModelRow[]) : [];
      setRows(list);
      if (typeof res?.current === 'string' && res.current.length > 0) setCurrent(res.current);
    } catch (err: any) {
      setRows([]);
      setError(String(err?.message || err || 'The model list could not be read.'));
    }
  }, [workingDirectory]);

  useEffect(() => {
    if (!anchor) return;

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setAnchor(null);
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [anchor]);

  const toggle = () => {
    if (anchor) {
      setAnchor(null);
      return;
    }

    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;

    setAnchor({
      bottom: Math.max(VIEWPORT_MARGIN, window.innerHeight - rect.top + 6),
      left: Math.max(VIEWPORT_MARGIN, Math.min(rect.left, window.innerWidth - PANEL_WIDTH - VIEWPORT_MARGIN)),
    });

    void loadList();
  };

  const choose = async (row: VyceModelRow) => {
    if (!row.available || busy) return;

    // Picking the model that is already answering should not drive the menu again.
    if (row.name === current) {
      setAnchor(null);
      return;
    }

    setBusy(true);
    setError(null);

    try {
      const res: any = await webviewBridge.invoke('vyce_model', { model: row.name, workingDirectory }, 60000);

      if (res?.switched === false) {
        // The CLI accepted no change: say so instead of showing a name it is not using.
        setError(`Vyce did not switch to ${row.name}.`);
      } else {
        setCurrent(row.name);
        setAnchor(null);
      }
    } catch (err: any) {
      setError(String(err?.message || err || 'The switch failed.'));
    } finally {
      setBusy(false);
    }
  };

  const label = current ?? 'Model';

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        title={`Model: ${label}`}
        onClick={toggle}
        aria-haspopup="listbox"
        aria-expanded={anchor !== null}
        className="flex max-w-[240px] items-center gap-1.5 rounded-md border border-surface-border/70 bg-surface-card/50 px-2 py-1 font-mono text-[11px] text-content-muted transition-colors hover:border-surface-border hover:bg-surface-hover hover:text-content-main focus-visible:border-accent focus-visible:outline-none"
      >
        <Cpu className="h-3 w-3 flex-shrink-0 text-emerald-400" />
        <span className="truncate">{label}</span>
        <ChevronDown
          className={`h-3 w-3 flex-shrink-0 text-content-dim transition-transform ${anchor ? 'rotate-180' : ''}`}
        />
      </button>

      {anchor
        ? createPortal(
            <>
              <div className="fixed inset-0 z-[90]" onClick={() => setAnchor(null)} />
              <div
                style={{ bottom: anchor.bottom, left: anchor.left, width: PANEL_WIDTH }}
                className="fixed z-[91] rounded-xl border border-surface-border bg-surface-card shadow-2xl"
                role="listbox"
                aria-label="Vyce models"
              >
                <div className="flex items-center justify-between rounded-t-xl border-b border-surface-border/70 px-3 py-1.5">
                  <span className="text-[10.5px] font-medium text-content-dim">
                    Model
                  </span>
                  <span className="flex items-center gap-1.5 font-mono text-[10px] text-content-dim">
                    {busy || rows === null ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                    {rows && rows.length > 0 ? <span>{rows.length}</span> : null}
                  </span>
                </div>

                <div className="max-h-[300px] overflow-y-auto py-1">
                  {rows === null ? (
                    <p className="px-3 py-4 text-center font-mono text-[11px] text-content-dim">
                      Opening Vyce's model menu…
                    </p>
                  ) : rows.length === 0 ? (
                    <p className="px-3 py-4 text-center font-mono text-[11px] leading-relaxed text-content-dim">
                      {error ?? 'Vyce listed no models.'}
                    </p>
                  ) : (
                    rows.map(row => {
                      const isActive = row.name === current;

                      return (
                        <button
                          key={row.name}
                          type="button"
                          role="option"
                          aria-selected={isActive}
                          aria-disabled={!row.available}
                          disabled={!row.available || busy}
                          onClick={() => void choose(row)}
                          className={`flex w-full items-center gap-2 px-3 py-1.5 text-left transition-colors ${
                            isActive ? 'bg-accent-soft' : row.available ? 'hover:bg-surface-hover' : ''
                          } ${row.available ? '' : 'cursor-default opacity-55'}`}
                        >
                          <span className="w-3 flex-shrink-0">
                            {isActive ? <Check className="h-3 w-3 text-accent" /> : null}
                          </span>

                          <span className="min-w-0 flex-1">
                            <span className="block truncate font-mono text-[11.5px] text-content-main">
                              {row.name}
                            </span>
                            <span className="block truncate font-mono text-[9.5px] text-content-dim">
                              {row.available ? row.note : `Coming soon${row.note ? ` · ${row.note}` : ''}`}
                            </span>
                          </span>
                        </button>
                      );
                    })
                  )}
                </div>

                {error && rows && rows.length > 0 ? (
                  <p className="border-t border-surface-border/70 px-3 py-1.5 font-mono text-[10px] text-state-danger">
                    {error}
                  </p>
                ) : null}
              </div>
            </>,
            document.body
          )
        : null}
    </>
  );
};
