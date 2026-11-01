import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Check, ChevronDown, Copy, Loader2, Terminal } from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';
import { useSessionStore } from '../../store/useSessionStore';
import { copyToClipboard } from '../../lib/clipboard';

interface OpenCodeModelChipProps {
  sessionId: string;
  /** The model stored on the session, in the CLI's `provider/model` form. */
  model?: string;
}

interface AuthLogin {
  code: string;
  url: string;
  running: boolean;
  succeeded: boolean | null;
}

interface AuthState {
  signedIn: boolean;
  label: string | null;
  login: AuthLogin | null;
}

const PANEL_WIDTH = 340;
const VIEWPORT_MARGIN = 8;
const VISIBLE_ROWS = 11;

/** The free Zen models are the ones an account with no credit can still run. */
const isFree = (id: string) => /(^|[/-])free($|[-.])/i.test(id);

/**
 * OpenCode's model picker for the composer footer.
 *
 * The list comes from `opencode models`, so it is whatever the CLI can actually route to. Unlike
 * Vyce, nothing is driven in a menu: `--model` is a flag on each turn, so the choice is stored on the
 * session and sent with the next prompt.
 *
 * The panel is portalled because the composer card clips its own children.
 */
export const OpenCodeModelChip: React.FC<OpenCodeModelChipProps> = ({ sessionId, model }) => {
  const updateSessionTelemetry = useSessionStore(state => state.updateSessionTelemetry);

  const [models, setModels] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [anchor, setAnchor] = useState<{ bottom: number; left: number } | null>(null);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('all');
  const [auth, setAuth] = useState<AuthState | null>(null);
  const [authError, setAuthError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [copied, setCopied] = useState(false);

  const triggerRef = useRef<HTMLButtonElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const load = useCallback(async () => {
    setError(null);
    setModels(null);

    try {
      // The CLI is asked each time the panel opens; a cold run takes a moment.
      const res: any = await webviewBridge.invoke('opencode_models', {}, 60000);
      const list: string[] = Array.isArray(res?.models) ? res.models : [];

      setModels(list);

      // A new chat has no pick yet, and the CLI's default is the one model that is not routed
      // through the user's proxy. Showing it now means the first turn sends what the chip says.
      if (!model && typeof res?.defaultModel === 'string' && list.includes(res.defaultModel)) {
        updateSessionTelemetry(sessionId, { model: res.defaultModel });
      }
    } catch (err: any) {
      setModels([]);
      setError(String(err?.message || err || 'The model list could not be read.'));
    }
  }, [model, sessionId, updateSessionTelemetry]);

  const loadAuth = useCallback(async () => {
    try {
      const res: any = await webviewBridge.invoke('opencode_auth', {}, 60000);

      setAuth({
        signedIn: Boolean(res?.signedIn),
        label: typeof res?.label === 'string' ? res.label : null,
        login: res?.login ?? null,
      });
      setAuthError(null);
    } catch (err: any) {
      setAuth(null);
      setAuthError(String(err?.message || err || 'The sign-in state could not be read.'));
    }
  }, []);

  useEffect(() => {
    if (!anchor) return;

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setAnchor(null);
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [anchor]);

  // While the CLI waits for the code to be approved, this is the only thing being asked.
  const waiting = auth?.login?.running === true;

  useEffect(() => {
    if (!anchor || !waiting) return;

    const timer = window.setInterval(() => {
      void loadAuth();
    }, 2000);

    return () => window.clearInterval(timer);
  }, [anchor, waiting, loadAuth]);

  const toggle = () => {
    if (anchor) {
      setAnchor(null);
      return;
    }

    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;

    setQuery('');
    setCopied(false);
    setAnchor({
      bottom: Math.max(VIEWPORT_MARGIN, window.innerHeight - rect.top + 6),
      left: Math.max(VIEWPORT_MARGIN, Math.min(rect.left, window.innerWidth - PANEL_WIDTH - VIEWPORT_MARGIN)),
    });

    void load();
    void loadAuth();
    requestAnimationFrame(() => inputRef.current?.focus());
  };

  const choose = (id: string) => {
    updateSessionTelemetry(sessionId, { model: id });
    setAnchor(null);
  };

  const startLogin = async () => {
    setStarting(true);
    setAuthError(null);

    try {
      const res: any = await webviewBridge.invoke('opencode_login', {}, 60000);

      setAuth(prev => ({
        signedIn: prev?.signedIn ?? false,
        label: prev?.label ?? null,
        login: { code: res?.code ?? '', url: res?.url ?? '', running: true, succeeded: null },
      }));

      // The CLI would open this itself in a terminal; the panel does it instead.
      if (res?.url) void webviewBridge.invoke('open_url_external', { url: res.url });
    } catch (err: any) {
      setAuthError(String(err?.message || err || 'Sign-in could not start.'));
    } finally {
      setStarting(false);
    }
  };

  const cancelLogin = async () => {
    try {
      await webviewBridge.invoke('opencode_login_cancel', {});
    } catch {
      // The process is already gone, which is the same outcome.
    }

    setAuth(prev => (prev ? { ...prev, login: null } : prev));
  };

  const copyCode = async () => {
    const code = auth?.login?.code;
    if (!code) return;

    if (await copyToClipboard(code)) {
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    }
  };

  const providers = useMemo(() => {
    const counts = new Map<string, number>();

    for (const id of models ?? []) {
      const provider = id.split('/')[0];
      counts.set(provider, (counts.get(provider) ?? 0) + 1);
    }

    return [...counts.entries()].sort((a, b) => b[1] - a[1]);
  }, [models]);

  const freeCount = useMemo(() => (models ?? []).filter(isFree).length, [models]);

  const filtered = useMemo(() => {
    const needle = query.trim().toLowerCase();

    return (models ?? []).filter(id => {
      if (filter === 'free' && !isFree(id)) return false;
      if (filter !== 'all' && filter !== 'free' && !id.startsWith(`${filter}/`)) return false;

      return needle.length === 0 || id.toLowerCase().includes(needle);
    });
  }, [models, filter, query]);

  const label = model || 'Model';
  const login = auth?.login ?? null;
  const failedLogin = login !== null && !login.running && login.succeeded === false;

  const chipClass = (active: boolean) =>
    `rounded-md border px-1.5 py-0.5 font-mono text-[10px] transition-colors ${
      active
        ? 'border-accent/40 bg-accent-soft text-accent'
        : 'border-surface-border/70 text-content-dim hover:bg-surface-hover hover:text-content-main'
    }`;

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        title={`Model: ${label}`}
        onClick={toggle}
        aria-haspopup="listbox"
        aria-expanded={anchor !== null}
        className="flex max-w-[240px] items-center gap-1.5 rounded-lg border border-transparent px-1.5 py-1 font-mono text-[11px] text-content-muted transition-colors hover:border-surface-border hover:bg-surface-hover hover:text-content-main"
      >
        <Terminal className="h-3 w-3 flex-shrink-0 text-amber-400" />
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
                aria-label="OpenCode models"
              >
                <div className="flex items-center justify-between rounded-t-xl border-b border-surface-border/70 px-3 py-1.5">
                  <span className="text-[10.5px] font-medium text-content-dim">Model</span>
                  <span className="font-mono text-[10px] text-content-dim">
                    {models === null ? 'reading...' : `${filtered.length}`}
                  </span>
                </div>

                <div className="border-b border-surface-border/70 px-3 py-1.5">
                  <input
                    ref={inputRef}
                    value={query}
                    onChange={event => setQuery(event.target.value)}
                    placeholder="Filter provider or model"
                    className="w-full bg-transparent font-mono text-[11px] text-content-main placeholder:text-content-dim focus:outline-none"
                  />
                </div>

                {models !== null && models.length > 0 ? (
                  <div className="flex flex-wrap gap-1 border-b border-surface-border/70 px-3 py-1.5">
                    <button
                      type="button"
                      onClick={() => setFilter('all')}
                      aria-pressed={filter === 'all'}
                      className={chipClass(filter === 'all')}
                    >
                      All {models.length}
                    </button>
                    <button
                      type="button"
                      onClick={() => setFilter('free')}
                      aria-pressed={filter === 'free'}
                      className={chipClass(filter === 'free')}
                    >
                      Free {freeCount}
                    </button>
                    {providers.map(([provider, count]) => (
                      <button
                        key={provider}
                        type="button"
                        onClick={() => setFilter(provider)}
                        aria-pressed={filter === provider}
                        className={chipClass(filter === provider)}
                      >
                        {provider} {count}
                      </button>
                    ))}
                  </div>
                ) : null}

                <div className="overflow-y-auto py-1" style={{ maxHeight: VISIBLE_ROWS * 30 }}>
                  {models === null ? (
                    <p className="px-3 py-4 text-center font-mono text-[11px] text-content-dim">
                      Asking the CLI for its models...
                    </p>
                  ) : filtered.length === 0 ? (
                    <p className="px-3 py-4 text-center font-mono text-[11px] leading-relaxed text-content-dim">
                      {error ?? (models.length === 0 ? 'The CLI listed no models.' : 'Nothing matches that filter.')}
                    </p>
                  ) : (
                    filtered.map(id => {
                      const isActive = id === model;
                      const slash = id.indexOf('/');
                      const provider = slash > 0 ? id.slice(0, slash) : '';
                      const name = slash > 0 ? id.slice(slash + 1) : id;

                      return (
                        <button
                          key={id}
                          type="button"
                          role="option"
                          aria-selected={isActive}
                          onClick={() => choose(id)}
                          className={`flex w-full items-center gap-2 px-3 py-1.5 text-left transition-colors ${
                            isActive ? 'bg-accent-soft' : 'hover:bg-surface-hover'
                          }`}
                        >
                          <span className="w-3 flex-shrink-0">
                            {isActive ? <Check className="h-3 w-3 text-accent" /> : null}
                          </span>

                          <span className="min-w-0 flex-1">
                            <span className="block truncate font-mono text-[11.5px] text-content-main">{name}</span>
                            <span className="block truncate font-mono text-[9.5px] text-content-dim">{provider}</span>
                          </span>

                          {isFree(id) ? (
                            <span className="flex-shrink-0 rounded border border-emerald-500/20 bg-emerald-500/10 px-1 font-mono text-[9px] text-state-success">
                              free
                            </span>
                          ) : null}
                        </button>
                      );
                    })
                  )}
                </div>

                {error && models && models.length > 0 ? (
                  <p className="border-t border-surface-border/70 px-3 py-1.5 font-mono text-[10px] text-state-danger">
                    {error}
                  </p>
                ) : null}

                <div className="rounded-b-xl border-t border-surface-border/70 px-3 py-2">
                  {login ? (
                    <div className="space-y-1.5">
                      <p className="font-mono text-[10px] leading-relaxed text-content-muted">
                        {login.running
                          ? 'Enter this code on the page that just opened:'
                          : failedLogin
                            ? 'Sign-in did not finish. Try again.'
                            : 'Signed in.'}
                      </p>

                      {login.code ? (
                        <div className="flex items-center gap-1.5">
                          <code className="flex-1 rounded-md border border-surface-border bg-surface-hover px-2 py-1 font-mono text-[12.5px] tracking-[0.12em] text-content-main">
                            {login.code}
                          </code>
                          <button
                            type="button"
                            onClick={copyCode}
                            title="Copy code"
                            className="rounded-md border border-surface-border p-1 text-content-dim transition-colors hover:bg-surface-hover hover:text-content-main"
                          >
                            <Copy className="h-3 w-3" />
                          </button>
                        </div>
                      ) : null}

                      <div className="flex items-center gap-2">
                        {login.running ? (
                          <span className="flex items-center gap-1 font-mono text-[10px] text-content-dim">
                            <Loader2 className="h-3 w-3 animate-spin" />
                            Waiting for approval
                          </span>
                        ) : null}

                        {login.url ? (
                          <button
                            type="button"
                            onClick={() => webviewBridge.invoke('open_url_external', { url: login.url })}
                            className="font-mono text-[10px] text-accent hover:underline"
                          >
                            Open page
                          </button>
                        ) : null}

                        <button
                          type="button"
                          onClick={login.running ? cancelLogin : startLogin}
                          className="ml-auto font-mono text-[10px] text-content-dim hover:text-content-main"
                        >
                          {login.running ? 'Cancel' : 'Try again'}
                        </button>
                      </div>

                      {copied ? <p className="font-mono text-[10px] text-state-success">Code copied.</p> : null}
                    </div>
                  ) : auth?.signedIn ? (
                    <p className="flex items-center gap-1.5 font-mono text-[10px] text-content-dim">
                      <span className="h-1.5 w-1.5 flex-shrink-0 rounded-full bg-state-success" />
                      <span className="truncate">
                        Signed in{auth.label ? ` · ${auth.label}` : ''}
                      </span>
                      <button
                        type="button"
                        onClick={startLogin}
                        className="ml-auto flex-shrink-0 text-content-dim hover:text-content-main"
                      >
                        Switch
                      </button>
                    </p>
                  ) : (
                    <div className="flex items-center gap-2">
                      <p className="flex-1 font-mono text-[10px] leading-relaxed text-content-dim">
                        {authError ?? 'No OpenCode Console account is signed in.'}
                      </p>
                      <button
                        type="button"
                        onClick={startLogin}
                        disabled={starting}
                        className="flex-shrink-0 rounded-md border border-accent/40 bg-accent-soft px-2 py-0.5 font-mono text-[10px] text-accent transition-colors hover:bg-accent/15 disabled:opacity-50"
                      >
                        {starting ? 'Starting...' : 'Sign in'}
                      </button>
                    </div>
                  )}
                </div>
              </div>
            </>,
            document.body
          )
        : null}
    </>
  );
};
