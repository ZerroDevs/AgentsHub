import React, { useCallback, useEffect, useState } from 'react';
import { Check, ChevronDown, KeyRound, Loader2 } from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';
import { useSessionStore } from '../../store/useSessionStore';

interface ProviderSummary {
  id: string;
  name: string;
  models: number;
  selectedModel: string;
  firstModel: string;
  hasKey: boolean;
}

/** Announced so the model chip elsewhere in the composer rereads its list. */
export const BYOK_PROVIDER_CHANGED = 'byok-provider-changed';

/**
 * Which key answers this chat.
 *
 * A bring-your-own-key session has no Antigravity runner, so the runner dropdown would describe a
 * process the turn never touches. This takes its place and lists the saved providers instead, so
 * moving a conversation onto another gateway is one click rather than a trip to AccountsHub.
 */
export const ByokProviderSwitcher: React.FC<{ sessionId: string }> = ({ sessionId }) => {
  const updateSessionTelemetry = useSessionStore(state => state.updateSessionTelemetry);

  const [providers, setProviders] = useState<ProviderSummary[]>([]);
  const [activeId, setActiveId] = useState('');
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState('');

  const load = useCallback(async (): Promise<ProviderSummary[]> => {
    try {
      const res: any = await webviewBridge.invoke('get_claude_gateway_config');
      const list: ProviderSummary[] = Array.isArray(res?.providers)
        ? res.providers.map((raw: any) => {
            const models: string[] = Array.isArray(raw?.models) ? raw.models : [];
            const keys: string[] = Array.isArray(raw?.apiKeys) && raw.apiKeys.length > 0 ? raw.apiKeys : raw?.apiKey ? [raw.apiKey] : [];

            return {
              id: String(raw?.id ?? ''),
              name: String(raw?.name ?? 'Provider'),
              models: models.length,
              selectedModel: String(raw?.selectedModel ?? ''),
              firstModel: models[0] ?? '',
              hasKey: keys.filter(Boolean).length > 0,
            };
          })
        : [];

      setProviders(list);
      setActiveId(String(res?.activeId ?? ''));
      return list;
    } catch {
      setProviders([]);
      return [];
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open]);

  const active = providers.find(provider => provider.id === activeId);

  const choose = async (provider: ProviderSummary) => {
    if (provider.id === activeId) {
      setOpen(false);
      return;
    }

    setBusy(provider.id);
    try {
      await webviewBridge.invoke('select_claude_gateway_provider', { providerId: provider.id });

      // The old choice named a model this provider may not serve, so the session takes the new
      // provider's own default before the next turn is built.
      updateSessionTelemetry(sessionId, { model: provider.selectedModel || provider.firstModel || '' });

      setActiveId(provider.id);
      setOpen(false);
      window.dispatchEvent(new CustomEvent(BYOK_PROVIDER_CHANGED));
    } catch {
      // Nothing moved: the menu keeps showing the provider that answered last.
    } finally {
      setBusy('');
    }
  };

  return (
    <div className="relative">
      <button
        type="button"
        onClick={() => {
          setOpen(previous => !previous);
          void load();
        }}
        aria-haspopup="menu"
        aria-expanded={open}
        title="The provider this chat sends to. Its key and model live in AccountsHub → API keys."
        className="flex items-center gap-2 rounded-lg border border-surface-border bg-surface-card px-2.5 py-1 text-[11px] text-content-main transition-colors hover:border-amber-500/50"
      >
        <KeyRound className="h-3 w-3 text-amber-400" />
        <span className="max-w-[160px] truncate">Provider: {active?.name || 'none'}</span>
        <span
          aria-hidden="true"
          className={`h-1.5 w-1.5 rounded-full ${active?.hasKey ? 'bg-emerald-400' : 'bg-amber-400'}`}
        />
        <ChevronDown className={`h-3 w-3 text-content-dim transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>

      {open ? (
        <>
          <div className="fixed inset-0 z-40" onClick={() => setOpen(false)} />
          <div
            role="menu"
            className="absolute left-0 top-9 z-50 w-72 overflow-hidden rounded-xl border border-surface-border bg-surface-card p-1.5 shadow-2xl"
          >
            {providers.length === 0 ? (
              <p className="px-2 py-2 text-[11px] leading-relaxed text-content-dim">
                No providers saved yet. Add one in AccountsHub → API keys.
              </p>
            ) : (
              providers.map(provider => {
                const selected = provider.id === activeId;

                return (
                  <button
                    key={provider.id}
                    type="button"
                    role="menuitemradio"
                    aria-checked={selected}
                    onClick={() => void choose(provider)}
                    className={`flex w-full items-center justify-between gap-2 rounded-lg px-2 py-1.5 text-left transition-colors hover:bg-surface-hover ${
                      selected ? 'text-content-main' : 'text-content-muted'
                    }`}
                  >
                    <span className="min-w-0">
                      <span className="block truncate text-[11.5px]">{provider.name}</span>
                      <span className="block truncate text-[10px] text-content-dim">
                        {!provider.hasKey
                          ? 'No key yet'
                          : provider.models > 0
                            ? `${provider.models} models`
                            : 'No model list yet'}
                      </span>
                    </span>

                    {busy === provider.id ? (
                      <Loader2 className="h-3 w-3 flex-shrink-0 animate-spin" />
                    ) : selected ? (
                      <Check className="h-3 w-3 flex-shrink-0 text-accent" />
                    ) : null}
                  </button>
                );
              })
            )}

            <p className="mt-1 border-t border-surface-border px-2 pb-0.5 pt-1.5 text-[10px] leading-relaxed text-content-dim">
              Keys and models are managed in AccountsHub → API keys.
            </p>
          </div>
        </>
      ) : null}
    </div>
  );
};
