import React, { useEffect, useState } from 'react';
import {
  X,
  FolderOpen,
  FilePlus2,
  FolderPlus,
  Search,
  Download,
  Play,
  Trash2,
  Loader2,
  RefreshCw,
  AlertCircle,
} from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';
import { Button } from '../common/Button';

interface LocalModelsModalProps {
  onClose: () => void;
}

interface Fit {
  where: 'gpu' | 'ram' | 'slow';
  needMb: number;
  haveMb: number;
}

interface LocalModel {
  path: string;
  name: string;
  size: number;
  origin: string;
  managed: boolean;
  fit?: Fit;
  capabilities?: string[];
}

interface HfResult {
  id: string;
  downloads: number;
  likes: number;
  paramLabel: string;
  uncensored: boolean;
  capabilities: string[];
}

interface HfFile {
  path: string;
  size: number;
  quant: string;
  partial: number;
  fit?: Fit;
}

interface DownloadState {
  percent: number;
  received: number;
  total: number;
  done: boolean;
  error?: string;
}

const BUCKETS = [
  { id: 'all', label: 'All sizes' },
  { id: 'small', label: '≤3B' },
  { id: '1-7b', label: '1–7B' },
  { id: '7-14b', label: '7–14B' },
  { id: '14-32b', label: '14–32B' },
  { id: '32b+', label: '32B+' },
];

function sizeLabel(bytes: number): string {
  if (!bytes) return 'unknown size';
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(2)} GB`;
  return `${Math.round(bytes / 1e6)} MB`;
}

function gb(mb: number): string {
  return `${(mb / 1024).toFixed(mb % 1024 === 0 ? 0 : 1)} GB`;
}

/**
 * Whether the machine can hold this file, and where. An estimate, so the wording stays a hint:
 * weights plus context never equal the download size exactly.
 */
const FitBadge: React.FC<{ fit?: Fit }> = ({ fit }) => {
  if (!fit || !fit.needMb) return null;

  const tone =
    fit.where === 'gpu'
      ? 'border-emerald-500/30 bg-emerald-500/10 text-state-success'
      : fit.where === 'ram'
        ? 'border-amber-500/30 bg-amber-500/10 text-state-warning'
        : 'border-rose-500/30 bg-rose-500/10 text-state-danger';

  const label =
    fit.where === 'gpu'
      ? `fits GPU · ${gb(fit.needMb)}`
      : fit.where === 'ram'
        ? `RAM only · ${gb(fit.needMb)}`
        : `won't fit · ${gb(fit.needMb)}`;

  const hint =
    fit.where === 'gpu'
      ? `Roughly ${gb(fit.needMb)} needed; this machine has ${gb(fit.haveMb)} of GPU memory.`
      : fit.where === 'ram'
        ? `Roughly ${gb(fit.needMb)} needed. Larger than the ${gb(fit.haveMb)} of GPU memory, so it runs from system RAM and answers slower.`
        : `Roughly ${gb(fit.needMb)} needed, more than the ${gb(fit.haveMb)} this machine has. It will likely fail to load.`;

  return (
    <span className={`rounded border px-1 text-[9.5px] ${tone}`} title={hint}>
      {label}
    </span>
  );
};

const CAPABILITY_LABEL: Record<string, string> = {
  vision: 'vision',
  image: 'image gen',
  tools: 'tools',
  thinking: 'thinking',
  text: 'text',
  uncensored: 'uncensored',
};

/**
 * The local model shelf. Two lists in one dialog: what is already on this machine, and what Hugging
 * Face has. Both end at the same action — run the file and make it the model a chat uses.
 */
export const LocalModelsModal: React.FC<LocalModelsModalProps> = ({ onClose }) => {
  const [tab, setTab] = useState<'shelf' | 'hub'>('shelf');
  const [models, setModels] = useState<LocalModel[]>([]);
  const [directory, setDirectory] = useState('');
  const [memory, setMemory] = useState<{ vramMb: number; ramMb: number } | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [query, setQuery] = useState('');
  const [bucket, setBucket] = useState('all');
  const [uncensored, setUncensored] = useState(false);
  const [results, setResults] = useState<HfResult[] | null>(null);
  const [searching, setSearching] = useState(false);

  const [openRepo, setOpenRepo] = useState<string | null>(null);
  const [files, setFiles] = useState<HfFile[]>([]);
  const [filesLoading, setFilesLoading] = useState(false);

  const [downloads, setDownloads] = useState<Record<string, DownloadState>>({});

  const loadModels = async () => {
    try {
      const res: any = await webviewBridge.invoke('local_models_list');
      setModels(res?.models ?? []);
      setDirectory(res?.directory ?? '');
      setMemory(res?.memory ?? null);
    } catch {
      setError('Could not read the model folder.');
    }
  };

  useEffect(() => {
    void loadModels();

    const offProgress = webviewBridge.on('local_model_download_progress', (payload: any) => {
      if (!payload) return;
      const key = `${payload.repo}/${payload.file}`;
      setDownloads(prev => ({
        ...prev,
        [key]: {
          percent: payload.percent ?? 0,
          received: payload.received ?? 0,
          total: payload.total ?? 0,
          done: false,
        },
      }));
    });

    const offDone = webviewBridge.on('local_model_download_done', (payload: any) => {
      if (!payload) return;
      const key = `${payload.repo}/${payload.file}`;
      setDownloads(prev => ({
        ...prev,
        [key]: {
          ...(prev[key] ?? { percent: 0, received: 0, total: 0 }),
          percent: payload.success ? 100 : prev[key]?.percent ?? 0,
          done: true,
          error: payload.success ? undefined : payload.error ?? 'Failed',
        },
      }));

      if (payload.success) {
        setNotice(`${payload.model?.name ?? 'Model'} downloaded.`);
        void loadModels();
      } else {
        setError(payload.error ?? 'The download failed.');
      }
    });

    return () => {
      offProgress?.();
      offDone?.();
    };
  }, []);

  const search = async () => {
    setSearching(true);
    setError(null);
    try {
      const res: any = await webviewBridge.invoke('local_models_search', { query, bucket, uncensored });
      setResults(res?.models ?? []);
      if ((res?.models ?? []).length === 0) setNotice('Nothing matched. Try a shorter search.');
    } catch (err: any) {
      setError(err?.message ?? 'Hugging Face could not be reached.');
    } finally {
      setSearching(false);
    }
  };

  const openFiles = async (repo: string) => {
    setOpenRepo(repo);
    setFiles([]);
    setFilesLoading(true);
    try {
      const res: any = await webviewBridge.invoke('local_models_files', { repo });
      setFiles(res?.files ?? []);
      if ((res?.files ?? []).length === 0) setError('That repository has no single-file GGUF models.');
    } catch (err: any) {
      setError(err?.message ?? `Could not list ${repo}.`);
    } finally {
      setFilesLoading(false);
    }
  };

  const download = async (repo: string, file: string) => {
    const key = `${repo}/${file}`;
    setDownloads(prev => ({ ...prev, [key]: { percent: 0, received: 0, total: 0, done: false } }));
    try {
      await webviewBridge.invoke('local_models_download', { repo, file });
    } catch (err: any) {
      setError(err?.message ?? 'The download could not be started.');
    }
  };

  const addFile = async () => {
    setBusy(true);
    try {
      const res: any = await webviewBridge.invoke('local_models_add_file');
      if (res?.success) {
        setNotice(res.message ?? 'Model added.');
        void loadModels();
      }
    } catch {
      // A cancelled picker is not a failure.
    } finally {
      setBusy(false);
    }
  };

  const addFolder = async () => {
    setBusy(true);
    try {
      const res: any = await webviewBridge.invoke('local_models_add_folder');
      if (res?.success) {
        setNotice(res.message ?? 'Folder added.');
        void loadModels();
      }
    } catch {
    } finally {
      setBusy(false);
    }
  };

  const runModel = async (model: LocalModel) => {
    setBusy(true);
    setError(null);
    try {
      const res: any = await webviewBridge.invoke('local_models_run', { path: model.path });
      if (res?.success) {
        setNotice(`${model.name} is running. Start a chat from the + menu to use it.`);
      } else {
        setError(res?.message ?? 'The server did not start.');
      }
    } catch (err: any) {
      setError(err?.message ?? 'The server did not start.');
    } finally {
      setBusy(false);
    }
  };

  const forget = async (model: LocalModel) => {
    setBusy(true);
    try {
      const res: any = await webviewBridge.invoke('local_models_forget', {
        path: model.path,
        delete: model.managed,
      });
      if (res?.success) {
        setModels(res.models ?? []);
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-surface-sunken/80 p-4 backdrop-blur-sm">
      <div className="flex h-[min(86vh,760px)] w-full max-w-3xl flex-col overflow-hidden rounded-2xl border border-surface-border bg-surface-card shadow-2xl">
        {/* Header */}
        <div className="flex items-start justify-between gap-3 border-b border-surface-border px-4 py-3">
          <div className="min-w-0">
            <h3 className="text-sm font-bold text-content-main">Local models</h3>
            <p className="mt-0.5 truncate font-mono text-[10.5px] text-content-dim" title={directory}>
              {directory || 'Reading model folder…'}
            </p>
            {memory ? (
              <p className="mt-0.5 text-[10px] text-content-dim">
                {memory.vramMb > 0 ? `${gb(memory.vramMb)} GPU` : 'No GPU memory reported'}
                {memory.ramMb > 0 ? ` · ${gb(memory.ramMb)} RAM` : ''}
              </p>
            ) : null}
          </div>

          <div className="flex items-center gap-1.5">
            <button
              onClick={() => void webviewBridge.invoke('local_models_open_folder')}
              className="flex h-7 items-center gap-1 rounded-lg border border-surface-border bg-surface-deep px-2 text-[11px] text-content-muted hover:text-content-main"
              title="Show the app's model folder"
            >
              <FolderOpen className="h-3.5 w-3.5" />
              Folder
            </button>
            <button
              onClick={onClose}
              className="flex h-7 w-7 items-center justify-center rounded-lg text-content-dim hover:bg-surface-hover hover:text-content-main"
              aria-label="Close"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        </div>

        {/* Tabs */}
        <div className="flex items-center gap-1 border-b border-surface-border px-4 py-2">
          {[
            { id: 'shelf', label: `On this machine (${models.length})` },
            { id: 'hub', label: 'From Hugging Face' },
          ].map(item => (
            <button
              key={item.id}
              onClick={() => setTab(item.id as 'shelf' | 'hub')}
              className={`rounded-lg px-2.5 py-1 text-[11px] transition ${
                tab === item.id
                  ? 'bg-sky-500/15 text-accent'
                  : 'text-content-muted hover:bg-surface-hover hover:text-content-main'
              }`}
            >
              {item.label}
            </button>
          ))}
        </div>

        {(notice || error) && (
          <div
            className={`flex items-start gap-2 px-4 py-2 text-[11px] ${
              error ? 'text-state-danger' : 'text-content-muted'
            }`}
          >
            {error ? <AlertCircle className="mt-0.5 h-3.5 w-3.5 flex-shrink-0" /> : null}
            <span className="min-w-0 flex-1 break-words">{error ?? notice}</span>
            <button
              onClick={() => {
                setError(null);
                setNotice(null);
              }}
              className="text-content-dim hover:text-content-main"
              aria-label="Dismiss"
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        )}

        {/* Shelf */}
        {tab === 'shelf' && (
          <div className="flex min-h-0 flex-1 flex-col">
            <div className="flex flex-wrap items-center gap-1.5 px-4 py-2">
              <Button variant="secondary" size="xs" disabled={busy} onClick={() => void addFile()} leftIcon={<FilePlus2 className="h-3 w-3" />}>
                Add .gguf
              </Button>
              <Button variant="secondary" size="xs" disabled={busy} onClick={() => void addFolder()} leftIcon={<FolderPlus className="h-3 w-3" />}>
                Add folder
              </Button>
              <Button variant="secondary" size="xs" onClick={() => void loadModels()} leftIcon={<RefreshCw className="h-3 w-3" />}>
                Refresh
              </Button>
            </div>

            <div className="min-h-0 flex-1 overflow-y-auto px-4 pb-4">
              {models.length === 0 ? (
                <div className="rounded-xl border border-dashed border-surface-border px-4 py-8 text-center">
                  <p className="text-xs text-content-main">No models on this machine yet.</p>
                  <p className="mt-1 text-[11px] text-content-dim">
                    Download one from Hugging Face, or add a .gguf you already have.
                  </p>
                </div>
              ) : (
                <div className="space-y-1.5">
                  {models.map(model => (
                    <div
                      key={model.path}
                      className="flex flex-wrap items-center gap-2 rounded-lg border border-surface-border bg-surface-deep px-3 py-2"
                    >
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center gap-2">
                          <span className="truncate text-xs text-content-main" title={model.path}>
                            {model.name}
                          </span>
                          <span className="rounded border border-surface-border px-1 text-[9px] uppercase tracking-wide text-content-dim">
                            {model.origin}
                          </span>
                          {(model.capabilities ?? []).map(capability => (
                            <span
                              key={capability}
                              className="rounded border border-sky-500/25 bg-sky-500/10 px-1 text-[9.5px] text-accent"
                              title="Read from the file name, since a local file carries no metadata"
                            >
                              {CAPABILITY_LABEL[capability] ?? capability}
                            </span>
                          ))}
                          <FitBadge fit={model.fit} />
                        </div>
                        <p className="truncate font-mono text-[10px] text-content-dim" title={model.path}>
                          {sizeLabel(model.size)} · {model.path}
                        </p>
                      </div>

                      <button
                        onClick={() => void runModel(model)}
                        disabled={busy}
                        className="flex h-7 items-center gap-1 rounded-lg bg-emerald-600 px-2 text-[11px] font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
                      >
                        <Play className="h-3 w-3" />
                        Run
                      </button>

                      <button
                        onClick={() => void forget(model)}
                        disabled={busy}
                        className="flex h-7 items-center gap-1 rounded-lg border border-surface-border px-2 text-[11px] text-content-muted hover:border-rose-500/40 hover:text-state-danger disabled:opacity-50"
                        title={model.managed ? 'Delete the downloaded file' : 'Stop tracking this file'}
                      >
                        <Trash2 className="h-3 w-3" />
                        {model.managed ? 'Delete' : 'Forget'}
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        )}

        {/* Hugging Face */}
        {tab === 'hub' && (
          <div className="flex min-h-0 flex-1 flex-col">
            <div className="space-y-2 border-b border-surface-border px-4 py-2">
              <div className="flex items-center gap-1.5">
                <div className="relative flex-1">
                  <Search className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-content-dim" />
                  <input
                    value={query}
                    onChange={e => setQuery(e.target.value)}
                    onKeyDown={e => {
                      if (e.key === 'Enter') void search();
                    }}
                    placeholder="Search GGUF models, e.g. qwen 7b"
                    className="w-full rounded-lg border border-surface-border bg-surface-deep py-1.5 pl-7 pr-2 text-xs text-content-main placeholder:text-content-dim focus:border-sky-500 focus:outline-none"
                  />
                </div>
                <Button variant="primary" size="xs" onClick={() => void search()} disabled={searching}>
                  {searching ? <Loader2 className="h-3 w-3 animate-spin" /> : 'Search'}
                </Button>
              </div>

              <div className="flex flex-wrap items-center gap-1">
                {BUCKETS.map(item => (
                  <button
                    key={item.id}
                    onClick={() => setBucket(item.id)}
                    className={`rounded px-1.5 py-0.5 text-[10.5px] ${
                      bucket === item.id
                        ? 'bg-sky-500/15 text-accent'
                        : 'border border-surface-border text-content-dim hover:text-content-main'
                    }`}
                  >
                    {item.label}
                  </button>
                ))}

                <button
                  onClick={() => setUncensored(!uncensored)}
                  className={`ml-1 rounded px-1.5 py-0.5 text-[10.5px] ${
                    uncensored
                      ? 'bg-amber-500/15 text-state-warning'
                      : 'border border-surface-border text-content-dim hover:text-content-main'
                  }`}
                >
                  Uncensored
                </button>
              </div>
            </div>

            <div className="min-h-0 flex-1 overflow-y-auto px-4 py-2">
              {results === null ? (
                <p className="py-8 text-center text-[11px] text-content-dim">
                  Search Hugging Face for GGUF models. Size tags come from the model name.
                </p>
              ) : results.length === 0 ? (
                <p className="py-8 text-center text-[11px] text-content-dim">
                  Nothing matched. Try a shorter search or a different size.
                </p>
              ) : (
                <div className="space-y-1.5">
                  {results.map(result => {
                    const isOpen = openRepo === result.id;

                    return (
                      <div key={result.id} className="rounded-lg border border-surface-border bg-surface-deep">
                        <div className="flex flex-wrap items-center gap-2 px-3 py-2">
                          <div className="min-w-0 flex-1">
                            <p className="truncate text-xs text-content-main" title={result.id}>
                              {result.id}
                            </p>
                            <p className="mt-0.5 flex flex-wrap items-center gap-1 text-[10px] text-content-dim">
                              {result.paramLabel ? (
                                <span className="rounded border border-surface-border px-1">{result.paramLabel}</span>
                              ) : null}
                              {(result.capabilities ?? []).filter(c => c !== 'uncensored').map(capability => (
                                <span
                                  key={capability}
                                  className="rounded border border-sky-500/25 bg-sky-500/10 px-1 text-accent"
                                  title="Read from the repository name and tags, not a guarantee"
                                >
                                  {CAPABILITY_LABEL[capability] ?? capability}
                                </span>
                              ))}
                              {result.uncensored ? (
                                <span className="rounded bg-amber-500/10 px-1 text-state-warning">uncensored</span>
                              ) : null}
                              <span>{result.downloads.toLocaleString()} downloads</span>
                            </p>
                          </div>

                          <Button variant="secondary" size="xs" onClick={() => void openFiles(result.id)}>
                            {isOpen ? 'Files' : 'Choose file'}
                          </Button>
                        </div>

                        {isOpen ? (
                          <div className="border-t border-surface-border px-3 py-2">
                            {filesLoading ? (
                              <p className="flex items-center gap-1.5 text-[11px] text-content-dim">
                                <Loader2 className="h-3 w-3 animate-spin" /> Listing files…
                              </p>
                            ) : files.length === 0 ? (
                              <p className="text-[11px] text-content-dim">No single-file GGUF models here.</p>
                            ) : (
                              <div className="space-y-1">
                                {files.map(file => {
                                  const key = `${result.id}/${file.path}`;
                                  const state = downloads[key];

                                  return (
                                    <div
                                      key={file.path}
                                      className="flex flex-wrap items-center gap-2 rounded border border-surface-border/60 px-2 py-1.5"
                                    >
                                      <span className="min-w-0 flex-1 truncate font-mono text-[10.5px] text-content-main" title={file.path}>
                                        {file.path}
                                      </span>
                                      <FitBadge fit={file.fit} />
                                      <span className="text-[10px] text-content-dim">{sizeLabel(file.size)}</span>

                                      {state && !state.done ? (
                                        <span className="w-24">
                                          <span className="block h-1.5 overflow-hidden rounded-full bg-surface-border">
                                            <span
                                              className="block h-full rounded-full bg-accent transition-all"
                                              style={{ width: `${Math.max(4, state.percent)}%` }}
                                            />
                                          </span>
                                          <span className="mt-0.5 block text-right text-[9.5px] tabular-nums text-content-dim">
                                            {state.percent}%
                                          </span>
                                        </span>
                                      ) : (
                                        <Button
                                          variant="secondary"
                                          size="xs"
                                          onClick={() => void download(result.id, file.path)}
                                          leftIcon={<Download className="h-3 w-3" />}
                                        >
                                          {file.partial > 0 && file.partial < file.size
                                            ? `Resume ${Math.round((file.partial / file.size) * 100)}%`
                                            : state?.error
                                              ? 'Retry'
                                              : 'Download'}
                                        </Button>
                                      )}
                                    </div>
                                  );
                                })}
                              </div>
                            )}
                          </div>
                        ) : null}
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
};
