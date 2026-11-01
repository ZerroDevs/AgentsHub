import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertCircle, Check, Eye, EyeOff, Loader2, Plus, RefreshCw, Search, Trash2, X } from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';

/**
 * One gateway the person has a key for.
 *
 * A row is an endpoint, not a mode: a name, where it lives, the keys, and what it can run. "In use"
 * marks the one new chats on this engine start with; every other row stays saved so switching is one
 * click instead of a retype.
 */
interface Provider {
  id: string;
  name: string;
  baseUrl: string;
  protocol: 'anthropic' | 'openai';
  useBridge: boolean;
  keys: string[];
  models: string[];
  selectedModel: string;
}

interface ProviderKeyModalProps {
  open: boolean;
  onClose: () => void;
}

const TEXT_INPUT =
  'w-full rounded-lg border border-surface-border bg-surface-deep px-3 py-2 text-[12px] text-content-main outline-none transition-colors placeholder:text-content-dim focus:border-accent';

const LABEL = 'mb-1.5 block text-[11px] font-medium text-content-muted';

/** Endpoints people actually paste, so the field is not a blank stare. */
const PRESETS = [
  { name: 'OpenRouter', url: 'https://openrouter.ai/api/v1' },
  { name: 'unorouter', url: 'https://api.unorouter.com/v1' },
  { name: 'Anthropic', url: 'https://api.anthropic.com', protocol: 'anthropic' as const },
];

const EMPTY_DRAFT: Provider = {
  id: '',
  name: '',
  baseUrl: '',
  protocol: 'openai',
  useBridge: true,
  keys: [''],
  models: [],
  selectedModel: '',
};

function toProvider(raw: any): Provider {
  const keys =
    Array.isArray(raw?.apiKeys) && raw.apiKeys.length > 0 ? raw.apiKeys : raw?.apiKey ? [raw.apiKey] : [''];

  return {
    id: String(raw?.id ?? ''),
    name: String(raw?.name ?? 'Provider'),
    baseUrl: String(raw?.baseUrl ?? ''),
    protocol: raw?.protocol === 'anthropic' ? 'anthropic' : 'openai',
    useBridge: raw?.useBridge !== false,
    keys,
    models: Array.isArray(raw?.models) ? raw.models : [],
    selectedModel: String(raw?.selectedModel ?? ''),
  };
}

/** Compares what matters, so typing a space does not light up "unsaved". */
function sameDraft(a: Provider, b: Provider): boolean {
  return (
    a.id === b.id &&
    a.name.trim() === b.name.trim() &&
    a.baseUrl.trim() === b.baseUrl.trim() &&
    a.protocol === b.protocol &&
    a.useBridge === b.useBridge &&
    a.selectedModel === b.selectedModel &&
    a.keys.map(k => k.trim()).join('\n') === b.keys.map(k => k.trim()).join('\n') &&
    a.models.join('\n') === b.models.join('\n')
  );
}

/**
 * Model ids label themselves: `vendor/name` for a gateway catalogue, plus a `:free` suffix for the
 * no-charge ones. Both are read off the id, so a provider that renames its catalogue needs no table
 * here.
 */
function modelVendor(id: string): string {
  const slash = id.indexOf('/');
  return slash > 0 ? id.slice(0, slash) : 'direct';
}

function modelIsFree(id: string): boolean {
  return /:free$/i.test(id);
}

const FilterChip: React.FC<{
  active: boolean;
  onClick: () => void;
  tone?: 'free';
  children: React.ReactNode;
}> = ({ active, onClick, tone, children }) => (
  <button
    type="button"
    onClick={onClick}
    aria-pressed={active}
    className={`flex-shrink-0 whitespace-nowrap rounded-full border px-2 py-0.5 text-[10.5px] transition-colors ${
      active
        ? tone === 'free'
          ? 'border-emerald-500/40 bg-emerald-500/15 text-state-success'
          : 'border-accent/50 bg-accent/15 text-accent'
        : 'border-surface-border text-content-dim hover:border-surface-border hover:text-content-muted'
    }`}
  >
    {children}
  </button>
);

export const ProviderKeyModal: React.FC<ProviderKeyModalProps> = ({ open, onClose }) => {
  const [providers, setProviders] = useState<Provider[]>([]);
  const [activeId, setActiveId] = useState('');
  const [draft, setDraft] = useState<Provider>(EMPTY_DRAFT);
  const [saved, setSaved] = useState<Provider>(EMPTY_DRAFT);
  const [keyVisible, setKeyVisible] = useState(false);
  const [busy, setBusy] = useState<'save' | 'fetch' | 'delete' | null>(null);
  const [note, setNote] = useState<{ tone: 'ok' | 'bad'; text: string } | null>(null);
  const [filter, setFilter] = useState('');
  const [freeOnly, setFreeOnly] = useState(false);
  const [vendor, setVendor] = useState('');
  const [injection, setInjection] = useState(true);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const load = useCallback(async (preferId?: string) => {
    try {
      const res: any = await webviewBridge.invoke('get_claude_gateway_config');
      const list: Provider[] = Array.isArray(res?.providers) ? res.providers.map(toProvider) : [];

      setProviders(list);
      setInjection(Boolean(res?.injectionEnabled ?? true));

      const selected = preferId || res?.activeId || list[0]?.id || '';
      setActiveId(String(res?.activeId ?? ''));

      const match = list.find(p => p.id === selected) ?? list[0];
      const next = match ? { ...match, keys: [...match.keys] } : { ...EMPTY_DRAFT };

      setDraft(next);
      setSaved(next);
      setConfirmingDelete(false);
    } catch (err: any) {
      setNote({ tone: 'bad', text: err?.message ? String(err.message) : 'Could not read the provider list.' });
    }
  }, []);

  useEffect(() => {
    if (!open) return;
    setNote(null);
    setFilter('');
    setVendor('');
    setFreeOnly(false);
    setKeyVisible(false);
    void load();
  }, [open, load]);

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  const patch = (changes: Partial<Provider>) => {
    setDraft(previous => ({ ...previous, ...changes }));
    setConfirmingDelete(false);
  };

  const dirty = !sameDraft(draft, saved);

  const choose = (provider: Provider) => {
    const next = { ...provider, keys: [...provider.keys] };
    setDraft(next);
    setSaved(next);
    setNote(null);
    setFilter('');
    setKeyVisible(false);
    setConfirmingDelete(false);
  };

  const startNew = () => {
    const next = { ...EMPTY_DRAFT, keys: [''] };
    setDraft(next);
    setSaved(next);
    setNote(null);
    setFilter('');
    setConfirmingDelete(false);
  };

  const save = async (): Promise<Provider | null> => {
    if (!draft.name.trim()) {
      setNote({ tone: 'bad', text: 'Give the provider a name — it is what the picker shows.' });
      return null;
    }

    const url = draft.baseUrl.trim();
    if (!/^https?:\/\/\S+/i.test(url)) {
      setNote({ tone: 'bad', text: 'The base URL needs to start with http:// or https://.' });
      return null;
    }

    setBusy('save');
    try {
      const keys = draft.keys.map(k => k.trim()).filter(Boolean);

      const res: any = await webviewBridge.invoke('save_claude_gateway_config', {
        providerId: draft.id,
        name: draft.name.trim(),
        baseUrl: url,
        apiKey: keys[0] || '',
        apiKeys: keys,
        models: draft.models,
        selectedModel: draft.selectedModel,
        protocol: draft.protocol,
        useBridge: draft.useBridge,
        makeActive: !draft.id || draft.id === activeId,
      });

      const stored = toProvider(res?.provider);
      setNote({ tone: 'ok', text: 'Saved. New chats on this engine can use it.' });
      await load(stored.id);
      return stored;
    } catch (err: any) {
      setNote({ tone: 'bad', text: err?.message ? String(err.message) : 'The host refused the change.' });
      return null;
    } finally {
      setBusy(null);
    }
  };

  /** Asks the provider for its own list. Nothing is saved first: a draft can be inspected freely. */
  const fetchModels = async () => {
    if (!draft.baseUrl.trim() || !draft.keys[0]?.trim()) {
      setNote({ tone: 'bad', text: 'Paste the base URL and a key, then fetch.' });
      return;
    }

    setBusy('fetch');
    setNote(null);

    try {
      const res: any = await webviewBridge.invoke(
        'claude_gateway_fetch_models',
        {
          providerId: draft.id,
          baseUrl: draft.baseUrl.trim(),
          apiKey: draft.keys[0].trim(),
          protocol: draft.protocol,
        },
        45000
      );

      const models: string[] = Array.isArray(res?.models) ? res.models : [];
      const keepSelection = models.includes(draft.selectedModel);

      patch({ models, selectedModel: keepSelection ? draft.selectedModel : (models[0] ?? '') });
      setNote({ tone: 'ok', text: `${models.length} models. Pick the one chats should use.` });
    } catch (err: any) {
      setNote({ tone: 'bad', text: err?.message ? String(err.message) : 'The provider did not answer.' });
    } finally {
      setBusy(null);
    }
  };

  const removeProvider = async () => {
    if (!draft.id) {
      startNew();
      return;
    }

    if (!confirmingDelete) {
      setConfirmingDelete(true);
      return;
    }

    setBusy('delete');
    try {
      await webviewBridge.invoke('delete_claude_gateway_provider', { providerId: draft.id });
      setNote({ tone: 'ok', text: `Removed ${draft.name}. Its key went with it.` });
      await load();
    } catch (err: any) {
      setNote({ tone: 'bad', text: err?.message ? String(err.message) : 'Could not remove it.' });
    } finally {
      setBusy(null);
      setConfirmingDelete(false);
    }
  };

  const useForNewChats = async () => {
    if (!draft.id || draft.id === activeId) return;
    if (dirty && !(await save())) return;

    try {
      await webviewBridge.invoke('select_claude_gateway_provider', { providerId: draft.id });
      setNote({ tone: 'ok', text: `${draft.name} is now the provider new chats start with.` });
      await load(draft.id);
    } catch (err: any) {
      setNote({ tone: 'bad', text: err?.message ? String(err.message) : 'Could not switch provider.' });
    }
  };

  const selectModel = (model: string) => patch({ selectedModel: model === draft.selectedModel ? '' : model });

  const toggleInjection = async (enabled: boolean) => {
    setInjection(enabled);
    try {
      await webviewBridge.invoke('toggle_claude_injection', { enabled });
    } catch {
      setInjection(!enabled);
    }
  };

  /** Model ids carry their own labels: `vendor/name` and the `:free` suffix most gateways use. */
  const tagged = useMemo(
    () =>
      draft.models.map(id => ({
        id,
        vendor: modelVendor(id),
        free: modelIsFree(id),
      })),
    [draft.models]
  );

  const vendors = useMemo(() => {
    const counts = new Map<string, number>();
    for (const model of tagged) counts.set(model.vendor, (counts.get(model.vendor) ?? 0) + 1);
    return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  }, [tagged]);

  const visibleModels = useMemo(() => {
    const needle = filter.trim().toLowerCase();

    return tagged.filter(model => {
      if (freeOnly && !model.free) return false;
      if (vendor && model.vendor !== vendor) return false;
      if (!needle) return true;
      return model.id.toLowerCase().includes(needle);
    });
  }, [tagged, filter, freeOnly, vendor]);

  const freeCount = useMemo(() => tagged.filter(model => model.free).length, [tagged]);

  const inUse = draft.id !== '' && draft.id === activeId;
  const canDelete = providers.length > 1 && Boolean(draft.id);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/55" onClick={onClose} />

      <div
        role="dialog"
        aria-modal="true"
        aria-label="Providers"
        className="relative flex h-[min(720px,92vh)] w-[min(920px,95vw)] flex-col overflow-hidden rounded-xl border border-surface-border bg-surface-card shadow-2xl"
      >
        <header className="flex items-start justify-between gap-4 border-b border-surface-border px-5 py-3">
          <div className="min-w-0">
            <h2 className="text-[13px] font-semibold text-content-main">Providers</h2>
            <p className="mt-0.5 text-[11px] leading-relaxed text-content-muted">
              Bring a key from any gateway. Chats on “Use your own key” run on the one in use, with the
              CLI’s tools and thinking.
            </p>
          </div>

          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="rounded-lg p-1.5 text-content-dim transition-colors hover:bg-surface-hover hover:text-content-main"
          >
            <X className="h-4 w-4" />
          </button>
        </header>

        <div className="flex min-h-0 flex-1 flex-col sm:flex-row">
          {/* Provider list */}
          <div className="flex flex-shrink-0 flex-col border-b border-surface-border sm:w-[196px] sm:border-b-0 sm:border-r">
            <div className="flex gap-1 overflow-x-auto p-2 sm:min-h-0 sm:flex-1 sm:flex-col sm:gap-0.5 sm:overflow-y-auto">
              {providers.map(provider => {
                const selected = provider.id === draft.id;

                return (
                  <button
                    key={provider.id}
                    type="button"
                    onClick={() => choose(provider)}
                    aria-current={selected}
                    className={`relative min-w-[132px] flex-1 rounded-lg px-2.5 py-1.5 text-left transition-colors sm:w-full sm:flex-none ${
                      selected ? 'bg-surface-hover text-content-main' : 'text-content-muted hover:bg-surface-hover/60'
                    }`}
                  >
                    {selected ? (
                      <span className="absolute left-0 top-1/2 hidden h-4 w-[2px] -translate-y-1/2 rounded-r bg-accent sm:block" />
                    ) : null}

                    <span className="flex items-center gap-1.5">
                      <span className="truncate text-[11.5px] font-medium">{provider.name || 'Untitled'}</span>
                      {provider.id === activeId ? (
                        <span
                          className="flex-shrink-0 rounded bg-accent/15 px-1 py-px text-[9px] font-medium uppercase tracking-wide text-accent"
                          title="New chats start here"
                        >
                          In use
                        </span>
                      ) : null}
                    </span>

                    <span className="mt-0.5 block truncate font-mono text-[10px] text-content-dim">
                      {provider.models.length > 0
                        ? `${provider.models.length} models`
                        : provider.keys[0]
                          ? 'No models yet'
                          : 'No key yet'}
                    </span>
                  </button>
                );
              })}

              {providers.length === 0 ? (
                <p className="px-2 py-3 text-[11px] leading-relaxed text-content-dim">Nothing saved yet.</p>
              ) : null}
            </div>

            <div className="border-t border-surface-border p-2">
              <button
                type="button"
                onClick={startNew}
                className="flex w-full items-center justify-center gap-1.5 rounded-lg border border-dashed border-surface-border px-2 py-1.5 text-[11px] text-content-muted transition-colors hover:border-accent/60 hover:text-content-main"
              >
                <Plus className="h-3 w-3" />
                New provider
              </button>
            </div>
          </div>

          {/* Editor */}
          <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto px-5 py-3.5">
            <section className="space-y-3">
                <div className="grid gap-3 sm:grid-cols-2">
                  <div>
                    <label className={LABEL} htmlFor="provider-name">
                      Name
                    </label>
                    <input
                      id="provider-name"
                      value={draft.name}
                      onChange={event => patch({ name: event.target.value })}
                      placeholder="Work gateway"
                      className={TEXT_INPUT}
                    />
                  </div>

                  <div>
                    <label className={LABEL} htmlFor="provider-base">
                      Base URL
                    </label>
                    <input
                      id="provider-base"
                      value={draft.baseUrl}
                      onChange={event => patch({ baseUrl: event.target.value })}
                      placeholder="https://api.unorouter.com/v1"
                      spellCheck={false}
                      autoComplete="off"
                      className={`${TEXT_INPUT} font-mono text-[11.5px]`}
                    />
                  </div>
                </div>

                <div className="flex flex-wrap items-center gap-1.5">
                  <span className="text-[10.5px] text-content-dim">Use:</span>
                  {PRESETS.map(preset => (
                    <button
                      key={preset.url}
                      type="button"
                      onClick={() =>
                        patch({
                          baseUrl: preset.url,
                          name: draft.name || preset.name,
                          protocol: preset.protocol ?? draft.protocol,
                        })
                      }
                      className="rounded border border-surface-border px-1.5 py-0.5 text-[10.5px] text-content-muted transition-colors hover:border-accent/60 hover:text-content-main"
                      title={preset.url}
                    >
                      {preset.name}
                    </button>
                  ))}
                  <span className="text-[10.5px] text-content-dim">— or paste any endpoint.</span>
                </div>
              </section>

              <section>
                <label className={LABEL} htmlFor="provider-key">
                  API key
                </label>
                <div className="flex items-center gap-1.5">
                  <input
                    id="provider-key"
                    type={keyVisible ? 'text' : 'password'}
                    value={draft.keys[0] ?? ''}
                    onChange={event => patch({ keys: [event.target.value, ...draft.keys.slice(1)] })}
                    placeholder="sk-…"
                    spellCheck={false}
                    autoComplete="off"
                    className={`${TEXT_INPUT} font-mono text-[11.5px]`}
                  />
                  <button
                    type="button"
                    onClick={() => setKeyVisible(previous => !previous)}
                    aria-label={keyVisible ? 'Hide key' : 'Show key'}
                    className="rounded-lg border border-surface-border p-2 text-content-dim transition-colors hover:text-content-main"
                  >
                    {keyVisible ? <EyeOff className="h-3.5 w-3.5" /> : <Eye className="h-3.5 w-3.5" />}
                  </button>
                </div>
                <p className="mt-1 text-[10.5px] leading-relaxed text-content-dim">
                  Stored on this machine. It only leaves as a request to this provider.
                </p>
              </section>

              <section>
                <span className={LABEL}>Endpoint speaks</span>

                <div className="inline-flex rounded-lg border border-surface-border bg-surface-deep p-0.5">
                  {[
                    { value: 'openai' as const, label: 'OpenAI-compatible' },
                    { value: 'anthropic' as const, label: 'Anthropic' },
                  ].map(option => (
                    <button
                      key={option.value}
                      type="button"
                      onClick={() => patch({ protocol: option.value })}
                      aria-pressed={draft.protocol === option.value}
                      className={`rounded-md px-2.5 py-1 text-[11px] transition-colors ${
                        draft.protocol === option.value
                          ? 'bg-surface-card text-content-main shadow-sm'
                          : 'text-content-dim hover:text-content-muted'
                      }`}
                    >
                      {option.label}
                    </button>
                  ))}
                </div>

                {draft.protocol === 'openai' ? (
                  <label className="mt-2 flex cursor-pointer items-start gap-2 rounded-lg border border-surface-border px-2.5 py-2">
                    <input
                      type="checkbox"
                      checked={draft.useBridge}
                      onChange={event => patch({ useBridge: event.target.checked })}
                      className="mt-0.5 h-3.5 w-3.5 accent-sky-400"
                    />
                    <span className="min-w-0">
                      <span className="block text-[11.5px] text-content-main">Translate through the bridge</span>
                      <span className="block text-[10.5px] leading-relaxed text-content-dim">
                        Claude Code only speaks Anthropic, so OpenAI-style gateways are translated here on
                        this machine. Leave it on.
                      </span>
                    </span>
                  </label>
                ) : (
                  <p className="mt-2 text-[10.5px] leading-relaxed text-content-dim">
                    Sent straight to the endpoint. Use this for Anthropic itself, or a gateway that serves
                    the Messages API.
                  </p>
                )}
              </section>

              <section className="flex min-h-[176px] flex-1 flex-col rounded-xl border border-surface-border">
                <div className="flex items-center justify-between gap-2 border-b border-surface-border px-3 py-1.5">
                  <span className="flex items-baseline gap-1.5 text-[11.5px] font-medium text-content-main">
                    Models
                    <span className="text-[10.5px] font-normal text-content-dim">
                      {draft.models.length > 0
                        ? `${visibleModels.length} of ${draft.models.length}`
                        : 'not fetched'}
                    </span>
                  </span>

                  <button
                    type="button"
                    onClick={fetchModels}
                    disabled={busy === 'fetch' || busy === 'save'}
                    className="flex items-center gap-1.5 rounded-lg px-2 py-1 text-[11px] text-content-muted transition-colors hover:bg-surface-hover hover:text-content-main disabled:opacity-50"
                  >
                    {busy === 'fetch' ? (
                      <Loader2 className="h-3 w-3 animate-spin" />
                    ) : (
                      <RefreshCw className="h-3 w-3" />
                    )}
                    {busy === 'fetch' ? 'Fetching' : 'Fetch models'}
                  </button>
                </div>

                {draft.models.length === 0 ? (
                  <p className="px-3 py-3 text-[11px] leading-relaxed text-content-dim">
                    Fetch to see what this key can reach. The list comes from the provider, not this app.
                  </p>
                ) : (
                  <>
                    <div className="flex flex-shrink-0 items-center gap-1.5 border-b border-surface-border px-3 py-1.5">
                      <Search className="h-3 w-3 flex-shrink-0 text-content-dim" />
                      <input
                        value={filter}
                        onChange={event => setFilter(event.target.value)}
                        placeholder={`Search ${draft.models.length} models`}
                        className="w-full bg-transparent text-[11px] text-content-main outline-none placeholder:text-content-dim"
                      />
                      {filter ? (
                        <button
                          type="button"
                          onClick={() => setFilter('')}
                          aria-label="Clear search"
                          className="text-content-dim transition-colors hover:text-content-main"
                        >
                          <X className="h-3 w-3" />
                        </button>
                      ) : null}
                    </div>

                    <div className="flex flex-shrink-0 items-center gap-1 overflow-x-auto border-b border-surface-border px-3 py-1.5">
                      <FilterChip active={!vendor && !freeOnly} onClick={() => { setVendor(''); setFreeOnly(false); }}>
                        All
                      </FilterChip>

                      {freeCount > 0 ? (
                        <FilterChip
                          active={freeOnly}
                          onClick={() => setFreeOnly(previous => !previous)}
                          tone="free"
                        >
                          Free {freeCount}
                        </FilterChip>
                      ) : null}

                      {vendors.slice(0, 10).map(([name, count]) => (
                        <FilterChip
                          key={name}
                          active={vendor === name}
                          onClick={() => setVendor(previous => (previous === name ? '' : name))}
                        >
                          {name} {count}
                        </FilterChip>
                      ))}
                    </div>

                    <div className="min-h-0 flex-1 overflow-y-auto py-1">
                      {visibleModels.map(model => {
                        const chosen = model.id === draft.selectedModel;

                        return (
                          <button
                            key={model.id}
                            type="button"
                            onClick={() => selectModel(model.id)}
                            aria-pressed={chosen}
                            className={`flex w-full items-center gap-2 px-3 py-1 text-left text-[11px] transition-colors hover:bg-surface-hover ${
                              chosen ? 'text-accent' : 'text-content-muted'
                            }`}
                          >
                            <span className="w-[68px] flex-shrink-0 truncate text-[10px] text-content-dim">
                              {model.vendor}
                            </span>
                            <span className="truncate font-mono">{model.id}</span>
                            {model.free ? (
                              <span className="flex-shrink-0 rounded bg-emerald-500/15 px-1 py-px text-[9px] font-medium uppercase tracking-wide text-state-success">
                                free
                              </span>
                            ) : null}
                            {chosen ? <Check className="ml-auto h-3 w-3 flex-shrink-0" /> : null}
                          </button>
                        );
                      })}

                      {visibleModels.length === 0 ? (
                        <p className="px-3 py-2 text-[11px] text-content-dim">
                          Nothing matches. Try a shorter search or clear the filters.
                        </p>
                      ) : null}
                    </div>
                  </>
                )}

                <div className="flex-shrink-0 border-t border-surface-border px-3 py-1.5 text-[10.5px] text-content-dim">
                  {draft.selectedModel ? (
                    <span className="flex items-center gap-1.5">
                      <Check className="h-3 w-3 text-accent" />
                      Chats use <span className="truncate font-mono text-content-muted">{draft.selectedModel}</span>
                    </span>
                  ) : (
                    <span>Nothing picked — the CLI’s own default applies.</span>
                  )}
                </div>
              </section>
          </div>
        </div>

        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-surface-border px-5 py-2">
          <div className="flex min-w-0 flex-1 flex-wrap items-center gap-x-3 gap-y-1 text-[10.5px] text-content-dim">
            {note ? (
              <p
                role="status"
                className={`flex min-w-0 items-center gap-1.5 text-[11px] ${
                  note.tone === 'bad' ? 'text-state-danger' : 'text-state-success'
                }`}
              >
                {note.tone === 'bad' ? (
                  <AlertCircle className="h-3 w-3 flex-shrink-0" />
                ) : (
                  <Check className="h-3 w-3 flex-shrink-0" />
                )}
                <span className="truncate">{note.text}</span>
              </p>
            ) : null}

            <button
              type="button"
              onClick={() => void toggleInjection(!injection)}
              className={`underline decoration-dotted underline-offset-2 transition-colors hover:text-content-main ${note ? 'hidden sm:inline' : ''}`}
              title="Also write this endpoint into Claude Code's own settings file"
            >
              {injection ? 'Claude Code settings: synced' : 'Claude Code settings: left alone'}
            </button>

            {draft.id && !inUse ? (
              <button
                type="button"
                onClick={() => void useForNewChats()}
                className="text-accent underline decoration-dotted underline-offset-2 transition-colors hover:text-content-main"
              >
                Use for new chats
              </button>
            ) : null}

            {dirty ? <span className="text-state-warning">Unsaved changes</span> : null}
          </div>

          <div className="flex items-center gap-2">
            {canDelete ? (
              <button
                type="button"
                onClick={() => void removeProvider()}
                disabled={busy === 'delete'}
                className={`flex items-center gap-1 rounded-lg px-2 py-1.5 text-[11px] transition-colors disabled:opacity-50 ${
                  confirmingDelete
                    ? 'bg-rose-500/15 text-state-danger'
                    : 'text-content-dim hover:bg-rose-500/10 hover:text-state-danger'
                }`}
              >
                <Trash2 className="h-3 w-3" />
                {confirmingDelete ? 'Confirm remove' : 'Remove'}
              </button>
            ) : null}

            <button
              type="button"
              onClick={() => void save()}
              disabled={busy === 'save' || busy === 'fetch' || (!dirty && Boolean(draft.id))}
              className="flex items-center gap-1.5 rounded-lg bg-emerald-600 px-3.5 py-1.5 text-[11px] font-medium text-white transition-colors hover:bg-emerald-500 disabled:opacity-45"
            >
              {busy === 'save' ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
              {draft.id ? 'Save' : 'Add provider'}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
};
