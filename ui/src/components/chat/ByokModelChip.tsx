import React, { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Check, ChevronUp, KeyRound, RefreshCw } from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';
import { useSessionStore } from '../../store/useSessionStore';
import { BYOK_PROVIDER_CHANGED } from './ByokProviderSwitcher';

/** What the bridge reports about the provider a BYOK session runs on. */
interface ByokState {
  providerId: string;
  providerName: string;
  models: string[];
  selectedModel: string;
  hasKey: boolean;
}

const chipClass =
  'flex items-center gap-1.5 rounded-lg border border-surface-border bg-surface-card px-2 py-1 font-mono text-[11px] text-content-muted transition-colors hover:border-amber-500/50 hover:text-content-main';

const menuClass =
  'fixed z-[121] w-64 -translate-y-full overflow-hidden rounded-lg border border-surface-border bg-surface-card py-1 shadow-xl';

/**
 * The model a bring-your-own-key session answers with.
 *
 * The list is whatever the provider listed when the key was added, not a table kept here: a gateway
 * adds and retires models on its own schedule, so a hard-coded list would be wrong within weeks. An
 * empty list is a setup problem, and the menu says where to fix it instead of offering nothing.
 */
export const ByokModelChip: React.FC<{ sessionId: string; model?: string }> = ({ sessionId, model }) => {
  const updateSessionTelemetry = useSessionStore(state => state.updateSessionTelemetry);

  const [state, setState] = useState<ByokState | null>(null);
  const [anchor, setAnchor] = useState<{ top: number; left: number } | null>(null);
  const [busy, setBusy] = useState(false);
  const triggerRef = useRef<HTMLButtonElement | null>(null);

  /**
   * The model this session will send, falling back to the picker's own default.
   *
   * A session opened from the engine picker carries the Antigravity default
   * ("Gemini 3.8 Flash (High)") until something replaces it, and that is not an id any gateway
   * here serves. Only a name the provider lists — or, before any fetch, one shaped like an id — is
   * shown as the choice; anything else is treated as unset.
   */
  const listed = state?.models ?? [];
  const isUsable = (value?: string): boolean =>
    Boolean(value) && (listed.length > 0 ? listed.includes(value!) : !/[\s()]/.test(value!));

  const current = (isUsable(model) ? model! : '') || (isUsable(state?.selectedModel) ? state!.selectedModel : '') || listed[0] || '';

  /** Reads the provider state, normalised: every field the chip uses is present or has a default. */
  const load = async (): Promise<ByokState | undefined> => {
    try {
      const res: any = await webviewBridge.invoke('byok_models');
      const next: ByokState = {
        providerId: res?.providerId ?? '',
        providerName: res?.providerName ?? 'Custom provider',
        models: Array.isArray(res?.models) ? res.models : [],
        selectedModel: res?.selectedModel ?? '',
        hasKey: Boolean(res?.hasKey),
      };

      setState(next);
      return next;
    } catch {
      setState({ providerId: '', providerName: 'Custom provider', models: [], selectedModel: '', hasKey: false });
      return undefined;
    }
  };

  /**
   * Replaces a model this provider cannot serve with one it listed.
   *
   * A session created from the engine picker still carries the Antigravity default in `model`
   * ("Gemini 3.8 Flash (High)"). That is not an id any gateway here knows, so anything the provider
   * did not list is replaced by its own choice — and when it has listed nothing yet, the stale label
   * is cleared rather than shown as if it were the answer's model.
   */
  const settle = (res: ByokState | undefined, current?: string) => {
    if (!res) return;

    const known = res.models.length > 0
      ? Boolean(current && res.models.includes(current))
      : Boolean(current) && !/[\s()]/.test(current!);
    const fallback = res.selectedModel || res.models[0] || '';

    if (!known && fallback !== current) {
      updateSessionTelemetry(sessionId, { model: fallback });

      if (fallback && res.providerId) {
        void webviewBridge
          .invoke('claude_gateway_set_model', { providerId: res.providerId, model: fallback })
          .catch(() => {});
      }
    }
  };

  // Once per session: settle the model the turn will carry, so the chip and the request agree even
  // when the picker was never touched.
  useEffect(() => {
    let alive = true;

    void load().then(res => {
      if (alive) settle(res, model);
    });

    return () => {
      alive = false;
    };
    // `model` is deliberately out of the deps: this only fills an empty choice once.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId]);

  // Switching provider in the header swaps the catalogue under this chip. The old model may not
  // exist on the new one, so the list is reread and the choice re-settled.
  useEffect(() => {
    const onProviderChanged = () => {
      void load().then(res => settle(res, useSessionStore.getState().sessions[sessionId]?.model));
    };

    window.addEventListener(BYOK_PROVIDER_CHANGED, onProviderChanged);
    return () => window.removeEventListener(BYOK_PROVIDER_CHANGED, onProviderChanged);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId]);

  useEffect(() => {
    if (!anchor) return;

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setAnchor(null);
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [anchor]);

  const openMenu = async () => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;

    setAnchor({
      top: Math.max(8, rect.top - 8),
      left: Math.max(8, Math.min(rect.left, window.innerWidth - 268)),
    });

    await load();
  };

  const choose = (entry: string) => {
    setAnchor(null);
    updateSessionTelemetry(sessionId, { model: entry });

    // Remembered on the provider too, so the next session starts on the model this one used.
    if (state?.providerId) {
      void webviewBridge.invoke('claude_gateway_set_model', { providerId: state.providerId, model: entry }).catch(() => {});
    }
  };

  /** Re-asks the provider. The stored key is reused, so nothing is retyped. */
  const refresh = async () => {
    if (!state?.providerId) return;

    setBusy(true);
    try {
      await webviewBridge.invoke('claude_gateway_fetch_models', { providerId: state.providerId });
      await load();
    } catch {
      // The menu keeps the previous list; a failed refresh changes nothing.
    } finally {
      setBusy(false);
    }
  };

  const label = current || state?.providerName || 'Custom provider';

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        title={`The model ${state?.providerName ?? 'this provider'} answers with`}
        onClick={() => (anchor ? setAnchor(null) : void openMenu())}
        className={chipClass}
      >
        <KeyRound className="h-3 w-3 flex-shrink-0 text-amber-400" />
        <span className="max-w-[160px] truncate">{label}</span>
        <ChevronUp className={`h-3 w-3 opacity-60 transition-transform ${anchor ? 'rotate-180' : ''}`} />
      </button>

      {anchor
        ? createPortal(
            <>
              <div className="fixed inset-0 z-[120]" onClick={() => setAnchor(null)} />
              <div style={{ top: anchor.top, left: anchor.left }} className={menuClass} role="menu">
                <div className="flex items-center justify-between gap-2 border-b border-surface-border px-3 py-1.5">
                  <span className="truncate text-[10px] text-content-dim">{state?.providerName ?? 'Custom provider'}</span>
                  <button
                    type="button"
                    onClick={() => void refresh()}
                    disabled={busy || !state?.models.length}
                    title="Ask the provider for its model list again"
                    className="flex items-center gap-1 rounded px-1 py-0.5 text-[10px] text-content-muted transition-colors hover:bg-surface-hover hover:text-content-main disabled:opacity-40"
                  >
                    <RefreshCw className={`h-2.5 w-2.5 ${busy ? 'animate-spin' : ''}`} />
                    refresh
                  </button>
                </div>

                {!state ? (
                  <p className="px-3 py-2 text-[10.5px] text-content-dim">Reading the model list...</p>
                ) : state.models.length === 0 ? (
                  <p className="px-3 py-2 text-[10.5px] leading-relaxed text-content-muted">
                    {state.hasKey
                      ? 'This provider listed no models yet. Press refresh, or fetch them from the API keys panel.'
                      : 'No key yet. Add one under AccountsHub → API keys, then press Fetch models.'}
                  </p>
                ) : (
                  <div className="max-h-64 overflow-y-auto">
                    {state.models.map(entry => (
                      <button
                        key={entry}
                        type="button"
                        role="menuitem"
                        onClick={() => choose(entry)}
                        className={`flex w-full items-center justify-between gap-2 px-3 py-1.5 text-left text-[11px] transition-colors hover:bg-surface-hover ${
                          entry === current ? 'text-amber-400' : 'text-content-muted'
                        }`}
                      >
                        <span className="truncate">{entry}</span>
                        {entry === current ? <Check className="h-3 w-3 flex-shrink-0" /> : null}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </>
          , document.body)
        : null}
    </>
  );
};
