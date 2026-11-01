import React, { useEffect, useState } from 'react';
import { RefreshCw, Copy, Check, ExternalLink, AlertTriangle, ShieldCheck } from 'lucide-react';
import { Button } from '../common/Button';
import { webviewBridge } from '../../services/webviewBridge';
import { useLanStore } from '../../store/useLanStore';
import { useProfileStore } from '../../store/useProfileStore';
import { lanIcons } from '../../lib/toolIcons';

/**
 * LAN access lives in Settings now: it is a network-exposure switch, not a workspace view. Nothing
 * listens until it is turned on, and only the ports ticked here get a socket.
 */
export const LanSettings: React.FC = () => {
  const { running, lanIp, gatewayPort, bindError, forwarders, available, selected, hasLoaded, isBusy, error, load, setEnabled, setPorts } =
    useLanStore();
  const activeProfileName = useProfileStore(state => state.activeProfileName);

  const [copied, setCopied] = useState(false);
  const [qr, setQr] = useState('');

  const url = lanIp && gatewayPort ? `http://${lanIp}:${gatewayPort}/` : '';

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!running || !url) {
      setQr('');
      return;
    }

    let cancelled = false;
    (async () => {
      try {
        const res: any = await webviewBridge.invoke('generate_qr_code', { url });
        if (!cancelled && res?.qrBase64) setQr(res.qrBase64);
      } catch {
        // A missing QR leaves the URL, which is the part that actually matters.
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [running, url]);

  const copyUrl = () => {
    if (!url) return;
    navigator.clipboard?.writeText(url);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  const togglePort = (port: number) => {
    const next = selected.includes(port) ? selected.filter(p => p !== port) : [...selected, port];
    void setPorts(next);
  };

  const boundPorts = new Map(forwarders.map(forwarder => [forwarder.port, forwarder]));

  return (
    <div className="max-w-3xl space-y-6">
      <div className="space-y-4 rounded-xl border border-[var(--border-subtle,#1E293B)] bg-[var(--bg-card,#0F172A)] p-5">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <img
                src={running ? lanIcons.on : lanIcons.off}
                alt=""
                className="h-5 w-5 flex-shrink-0 object-contain"
              />
              <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">LAN access</h3>
            </div>
            <p className="mt-0.5 text-xs text-[var(--text-dim,#64748B)]">
              Reach the workspace and the ports you pick from another device on this network. Off means nothing
              is listening.
            </p>
          </div>

          <button
            type="button"
            onClick={() => void setEnabled(!running)}
            disabled={isBusy}
            className={`flex flex-shrink-0 items-center gap-2 rounded-lg border px-3 py-1.5 font-mono text-[11px] font-medium transition-colors disabled:opacity-60 ${
              running
                ? 'border-emerald-500/30 bg-emerald-500/15 text-state-success'
                : 'border-[var(--border-subtle,#1E293B)] bg-[var(--bg-input,#0D1322)] text-[var(--text-dim,#64748B)]'
            }`}
          >
            <span className={`h-2 w-2 rounded-full ${running ? 'animate-pulse bg-emerald-400' : 'bg-content-dim'}`} />
            {isBusy ? 'Working…' : running ? 'LAN on' : 'LAN off'}
          </button>
        </div>

        {!running ? (
          <p className="rounded-lg border border-[var(--border-subtle,#1E293B)] bg-[var(--bg-input,#0D1322)] px-3 py-2 font-mono text-[11px] text-[var(--text-dim,#64748B)]">
            No ports are bound. Turn it on to serve this workspace on {lanIp || 'your LAN address'}.
          </p>
        ) : (
          <>
            <div className="flex items-center justify-between gap-2 rounded-lg border border-[var(--border-subtle,#1E293B)] bg-[var(--bg-input,#0D1322)] px-3 py-2 font-mono text-xs">
              <span className="truncate text-accent">{url}</span>
              <div className="flex flex-shrink-0 items-center gap-1">
                <button
                  type="button"
                  onClick={copyUrl}
                  title="Copy URL"
                  className="rounded p-1 text-[var(--text-dim,#64748B)] transition-colors hover:text-[var(--text-main,#F1F5F9)]"
                >
                  {copied ? <Check className="h-3.5 w-3.5 text-state-success" /> : <Copy className="h-3.5 w-3.5" />}
                </button>
                <button
                  type="button"
                  onClick={() => void webviewBridge.invoke('open_url_external', { url })}
                  title="Open in browser"
                  className="rounded p-1 text-[var(--text-dim,#64748B)] transition-colors hover:text-[var(--text-main,#F1F5F9)]"
                >
                  <ExternalLink className="h-3.5 w-3.5" />
                </button>
              </div>
            </div>

            <div className="flex items-start gap-4">
              <div className="grid flex-1 grid-cols-2 gap-2 font-mono text-[11px]">
                <div className="rounded-lg border border-[var(--border-subtle,#1E293B)] bg-[var(--bg-input,#0D1322)] p-2.5">
                  <span className="block text-[9.5px] uppercase text-[var(--text-dim,#64748B)]">Host address</span>
                  <span className="mt-0.5 block text-[var(--text-main,#F1F5F9)]">{lanIp || 'unknown'}</span>
                </div>
                <div className="rounded-lg border border-[var(--border-subtle,#1E293B)] bg-[var(--bg-input,#0D1322)] p-2.5">
                  <span className="block text-[9.5px] uppercase text-[var(--text-dim,#64748B)]">Gateway port</span>
                  <span className="mt-0.5 block text-[var(--text-main,#F1F5F9)]">{gatewayPort}</span>
                </div>
                <div className="col-span-2 flex items-center gap-1.5 rounded-lg border border-[var(--border-subtle,#1E293B)] bg-[var(--bg-input,#0D1322)] p-2.5">
                  <ShieldCheck className="h-3 w-3 text-state-success" />
                  <span className="text-[var(--text-muted,#94A3B8)]">
                    Token guard · profile {activeProfileName || 'default'}
                  </span>
                </div>
              </div>

              {qr ? (
                <div className="flex-shrink-0 rounded-xl border border-[var(--border-subtle,#1E293B)] bg-white p-2">
                  <img
                    src={qr.startsWith('data:') ? qr : `data:image/png;base64,${qr}`}
                    alt="LAN access QR code"
                    className="h-24 w-24 object-contain"
                  />
                </div>
              ) : null}
            </div>
          </>
        )}

        {bindError ? (
          <p className="flex items-start gap-1.5 text-[11px] text-state-warning">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 flex-shrink-0" />
            {bindError}
          </p>
        ) : null}

        {error ? (
          <p className="flex items-start gap-1.5 text-[11px] text-state-danger">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 flex-shrink-0" />
            {error}
          </p>
        ) : null}

        <div className="flex justify-end">
          <Button
            variant="secondary"
            size="xs"
            onClick={() => void load()}
            leftIcon={<RefreshCw className={`h-3.5 w-3.5 ${isBusy ? 'animate-spin' : ''}`} />}
          >
            Refresh
          </Button>
        </div>
      </div>

      <div className="space-y-3 rounded-xl border border-[var(--border-subtle,#1E293B)] bg-[var(--bg-card,#0F172A)] p-5">
        <div>
          <h3 className="text-sm font-bold text-[var(--text-main,#F1F5F9)]">Ports to expose</h3>
          <p className="mt-0.5 text-xs text-[var(--text-dim,#64748B)]">
            Each ticked port gets its own socket on the LAN. Untick anything you do not need open.
          </p>
        </div>

        <div className="space-y-1.5">
          {available.length === 0 ? (
            <p className="rounded-lg border border-dashed border-[var(--border-subtle,#1E293B)] px-3 py-4 text-center font-mono text-[11px] text-[var(--text-dim,#64748B)]">
              {hasLoaded ? 'The host reported no exposable ports.' : 'Reading ports…'}
            </p>
          ) : (
            available.map(entry => {
              const chosen = selected.includes(entry.port);
              const bound = boundPorts.get(entry.port);

              return (
                <button
                  key={entry.port}
                  type="button"
                  onClick={() => togglePort(entry.port)}
                  disabled={isBusy}
                  className={`flex w-full items-center justify-between gap-3 rounded-lg border px-3 py-2 text-left transition-colors disabled:opacity-60 ${
                    chosen
                      ? 'border-sky-500/40 bg-sky-500/10'
                      : 'border-[var(--border-subtle,#1E293B)] bg-[var(--bg-input,#0D1322)] hover:border-sky-500/30'
                  }`}
                >
                  <span className="flex min-w-0 items-center gap-2.5">
                    <span
                      className={`flex h-3.5 w-3.5 flex-shrink-0 items-center justify-center rounded border ${
                        chosen ? 'border-sky-500 bg-sky-500 text-white' : 'border-content-dim/50'
                      }`}
                    >
                      {chosen ? <Check className="h-2.5 w-2.5" /> : null}
                    </span>
                    <span className="truncate text-xs text-[var(--text-main,#F1F5F9)]">{entry.label}</span>
                  </span>

                  <span className="flex flex-shrink-0 items-center gap-2 font-mono text-[10.5px]">
                    <span className="text-accent">:{entry.port}</span>
                    {bound ? (
                      bound.bound ? (
                        <span className="rounded border border-emerald-500/30 bg-emerald-500/10 px-1.5 py-0.5 text-state-success">
                          listening
                        </span>
                      ) : (
                        <span className="rounded border border-rose-500/30 bg-rose-500/10 px-1.5 py-0.5 text-state-danger">
                          busy
                        </span>
                      )
                    ) : null}
                  </span>
                </button>
              );
            })
          )}
        </div>

        {!running ? (
          <p className="text-[11px] text-[var(--text-dim,#64748B)]">
            Choices are saved now and applied the next time LAN access starts.
          </p>
        ) : null}
      </div>
    </div>
  );
};
