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
  Power,
  Pencil,
  Check,
  SlidersHorizontal,
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

interface ModelFlagSet {
  contextSize?: number | null;
  gpuLayers?: number | null;
  cacheTypeK?: string | null;
  cacheTypeV?: string | null;
  flashAttention?: boolean | null;
  extraArgs?: string | null;
  projector?: string | null;
}

interface LocalModel {
  path: string;
  name: string;
  size: number;
  origin: string;
  managed: boolean;
  fit?: Fit;
  capabilities?: string[];
  /** The mmproj this model would be launched with, found beside it or picked by hand. */
  projector?: string;
  /** False when the model looks multimodal but has no projector: the vision tag cannot be honoured. */
  visionReady?: boolean;
  context?: number;
  kvMb?: number;
  suggestedContext?: number;
  flags?: ModelFlagSet | null;
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
const FitBadge: React.FC<{ fit?: Fit; context?: number; kvMb?: number; gpuUnusable?: boolean }> = ({
  fit,
  context,
  kvMb,
  gpuUnusable,
}) => {
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

  // Weights alone never tell the story: the KV cache grows with context, and at a large one it can
  // be bigger than the file. Saying where the number comes from keeps the badge honest.
  const cache = kvMb && context ? ` (${gb(kvMb)} is KV cache at ${context.toLocaleString()} context)` : '';

  const hint =
    fit.where === 'gpu'
      ? `Roughly ${gb(fit.needMb)} needed${cache}; this machine has ${gb(fit.haveMb)} of GPU memory.`
      : fit.where === 'ram'
        ? gpuUnusable
          ? `Roughly ${gb(fit.needMb)} needed${cache}. The llama.cpp build installed here has no GPU backend, so every model runs on the CPU.`
          : `Roughly ${gb(fit.needMb)} needed${cache}. Larger than the ${gb(fit.haveMb)} of GPU memory, so it runs from system RAM and answers slower.`
        : `Roughly ${gb(fit.needMb)} needed${cache}, more than the ${gb(fit.haveMb)} this machine has. It will likely fail to load.`;

  return (
    <span className={`rounded border px-1 text-[9.5px] ${tone}`} title={hint}>
      {label}
    </span>
  );
};

const CACHE_TYPES = ['f16', 'bf16', 'q8_0', 'q5_1', 'q5_0', 'q4_1', 'q4_0', 'f32'];

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
  const [defaultDirectory, setDefaultDirectory] = useState('');
  const [usingDefaultDirectory, setUsingDefaultDirectory] = useState(true);
  const [memory, setMemory] = useState<{ vramMb: number; ramMb: number } | null>(null);
  const [server, setServer] = useState<any | null>(null);
  const [loadedPath, setLoadedPath] = useState('');
  const [runOnRequest, setRunOnRequest] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameDraft, setRenameDraft] = useState('');
  const [tuning, setTuning] = useState<string | null>(null);
  const [tuneDraft, setTuneDraft] = useState<ModelFlagSet>({});

  // A build with no GPU device has nothing to put layers on, so the field is not a choice.
  const cpuOnly = server?.backend === 'CPU only';

  // Text of the in-flight GPU build download, or null when nothing is running.
  const [gpuInstall, setGpuInstall] = useState<string | null>(null);
  const gpuBusy = gpuInstall !== null;

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
      setDefaultDirectory(res?.defaultDirectory ?? '');
      setUsingDefaultDirectory(res?.usingDefaultDirectory !== false);
      setMemory(res?.memory ?? null);
      setServer(res?.server ?? null);
      setLoadedPath(res?.loadedModel ?? '');
      setRunOnRequest(res?.runOnRequest !== false);
    } catch {
      setError('Could not read the model folder.');
    }
  };

  /** llama.cpp on or off. Starting it without a model leaves it in router mode: ready, nothing loaded. */
  const toggleServer = async () => {
    setBusy(true);
    setError(null);
    try {
      const res: any = await webviewBridge.invoke(server?.running ? 'llamacpp_stop' : 'llamacpp_start');
      setServer(res?.status ?? res);
      setLoadedPath((res?.status ?? res)?.loadedModel ?? '');
      void loadModels();
    } catch {
      setError('The local server did not change state.');
    } finally {
      setBusy(false);
    }
  };

  const unload = async () => {
    setBusy(true);
    try {
      const res: any = await webviewBridge.invoke('local_models_unload');
      setModels(res?.models ?? []);
      setServer(res?.server ?? null);
      setLoadedPath('');
      setNotice('Model unloaded. Its memory is free again.');
    } catch {
      setError('The model could not be unloaded.');
    } finally {
      setBusy(false);
    }
  };

  const commitRename = async (model: LocalModel) => {
    const name = renameDraft.trim();
    setRenaming(null);
    setRenameDraft('');

    try {
      const res: any = await webviewBridge.invoke('local_models_rename', { path: model.path, name });
      if (res?.models) setModels(res.models);
      if (!res?.success) setError('The model could not be renamed.');
    } catch {
      setError('The model could not be renamed.');
    }
  };

  const chooseDownloadFolder = async () => {
    try {
      const res: any = await webviewBridge.invoke('local_models_pick_download_folder');
      if (res?.chosen) {
        setNotice(`Downloads now go to ${res.directory}.`);
        void loadModels();
      }
    } catch {
      setError('The folder could not be changed.');
    }
  };

  const installGpuBuild = async () => {
    setGpuInstall('Looking for the newest GPU build…');
    setError(null);
    try {
      const res: any = await webviewBridge.invoke('llamacpp_install_gpu');
      if (!res?.started) setGpuInstall(null);
    } catch (err: any) {
      setGpuInstall(null);
      setError(err?.message ?? 'The GPU build could not be started.');
    }
  };

  const useDefaultDownloadFolder = async () => {
    try {
      const res: any = await webviewBridge.invoke('local_models_reset_download_folder');
      setNotice('Downloads go to the app\u2019s own folder again.');
      if (res?.models) setModels(res.models);
      void loadModels();
    } catch {
      setError('The folder could not be changed.');
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

    // A GPU build replaces the server binary, so its progress is the only thing to show while the
    // download runs; the final event carries the new status.
    const offGpu = webviewBridge.on('llama_gpu_progress', (payload: any) => {
      if (!payload) return;

      if (!payload.done) {
        setGpuInstall(payload.line ?? 'Downloading the GPU build…');
        return;
      }

      setGpuInstall(null);
      if (payload.success) setNotice(payload.line ?? 'The GPU build is installed.');
      else setError(payload.line ?? 'The GPU build could not be installed.');
      if (payload.status) setServer(payload.status);
      void loadModels();
    });

    // The server can be started from a chat or from Settings while this dialog is open, so the rows'
    // Load/Unload buttons follow it instead of waiting for Refresh.
    const offServer = webviewBridge.on('local_server_changed', () => void loadModels());

    return () => {
      offProgress?.();
      offDone?.();
      offGpu?.();
      offServer?.();
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
        setNotice(`${model.name} is loaded. Start a chat from the + menu to use it.`);
        await loadModels();
        // The chat's composer shows whether anything is serving; it rereads on this event.
        window.dispatchEvent(new CustomEvent('byok-provider-changed'));
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

  /** Opens the per-model launch flags. A closed row and a different row both start from what is saved. */
  const openTune = (model: LocalModel) => {
    if (tuning === model.path) {
      setTuning(null);
      return;
    }

    setTuning(model.path);
    setTuneDraft({ ...(model.flags ?? {}) });
  };

  const saveFlags = async (model: LocalModel) => {
    setBusy(true);
    setError(null);
    try {
      const res: any = await webviewBridge.invoke('local_models_set_flags', { path: model.path, ...tuneDraft });
      if (res?.models) setModels(res.models);
      if (res?.stopped) setLoadedPath('');
      setNotice(res?.message ?? 'Saved.');
      setTuning(null);
      void loadModels();
    } catch {
      setError('The flags could not be saved.');
    } finally {
      setBusy(false);
    }
  };

  const resetFlags = async (model: LocalModel) => {
    setBusy(true);
    try {
      const res: any = await webviewBridge.invoke('local_models_reset_flags', { path: model.path });
      if (res?.models) setModels(res.models);
      if (res?.stopped) setLoadedPath('');
      setNotice(`${model.name} uses the app-wide flags again.`);
      setTuning(null);
      void loadModels();
    } catch {
      setError('The flags could not be reset.');
    } finally {
      setBusy(false);
    }
  };

  const pickProjector = async (model: LocalModel) => {
    try {
      const res: any = await webviewBridge.invoke('local_models_pick_projector', { path: model.path });
      if (res?.chosen) {
        setTuneDraft(draft => ({ ...draft, projector: res.projector }));
        if (res?.models) setModels(res.models);
      }
    } catch {
      setError('The projector could not be changed.');
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
              <p
                className="mt-0.5 truncate text-[10px] text-content-dim"
                title={server?.backend ? `This llama.cpp build offloads to ${server.backend}` : 'The compute backend this llama.cpp build uses'}
              >
                {memory.vramMb > 0
                  ? `${gb(memory.vramMb)} GPU${server?.backend === 'CPU only' ? ' (this build cannot use it)' : ''}`
                  : 'No GPU memory reported'}
                {memory.ramMb > 0 ? ` · ${gb(memory.ramMb)} RAM` : ''}
                {server?.backend ? ` · ${server.backend}` : server?.installed ? ' · checking backend…' : ''}
              </p>
            ) : null}

            {gpuInstall ? (
              <p className="mt-0.5 truncate text-[10px] text-state-warning" title={gpuInstall}>
                {gpuInstall}
              </p>
            ) : null}
          </div>

          <div className="flex items-center gap-1.5">
            <span
              className={`rounded border px-1.5 py-0.5 text-[10px] font-mono ${
                server?.running
                  ? 'border-emerald-500/30 bg-emerald-500/10 text-state-success'
                  : server?.installed
                    ? 'border-surface-border bg-surface-deep text-content-dim'
                    : 'border-rose-500/30 bg-rose-500/10 text-state-danger'
              }`}
              title={server?.message ?? 'The local llama.cpp server'}
            >
              {server?.running ? 'llama.cpp on' : server?.installed ? 'llama.cpp off' : 'no llama.cpp'}
            </span>

            {cpuOnly ? (
              <button
                onClick={() => void installGpuBuild()}
                disabled={gpuBusy}
                className="flex h-7 items-center gap-1 rounded-lg border border-amber-500/40 bg-amber-500/10 px-2 text-[11px] text-state-warning transition-colors hover:bg-amber-500/20 disabled:opacity-60"
                title="This build has no GPU backend, so every model runs on the CPU. Downloads the official GPU-enabled build from GitHub into the app's own folder and switches the server to it. Nothing you placed by hand is overwritten."
              >
                {gpuBusy ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <Download className="h-3.5 w-3.5" />
                )}
                {gpuBusy ? 'GPU build…' : 'Switch to GPU build'}
              </button>
            ) : null}

            <button
              onClick={() => void toggleServer()}
              disabled={busy || !server?.installed}
              className="flex h-7 items-center gap-1 rounded-lg border border-surface-border bg-surface-deep px-2 text-[11px] text-content-muted transition-colors hover:text-content-main disabled:opacity-40"
              title={
                server?.running
                  ? 'Stop the llama.cpp server and free the memory it holds'
                  : 'Start the llama.cpp server without loading a model'
              }
            >
              <Power className="h-3.5 w-3.5" />
              {server?.running ? 'Stop' : 'Start'}
            </button>

            <button
              onClick={() => void webviewBridge.invoke('local_models_open_folder')}
              className="flex h-7 items-center gap-1 rounded-lg border border-surface-border bg-surface-deep px-2 text-[11px] text-content-muted hover:text-content-main"
              title="Show the folder downloads are saved in"
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
              <span
                className="rounded border border-surface-border px-1.5 py-0.5 text-[9.5px] text-content-dim"
                title={
                  runOnRequest
                    ? 'A chat that needs a local model loads the first one on the shelf. Change this in Settings \u2192 AI Engines & Runtimes.'
                    : 'A chat never loads a model on its own. Press Load on a row first. Change this in Settings \u2192 AI Engines & Runtimes.'
                }
              >
                {runOnRequest ? 'auto-load on' : 'auto-load off'}
              </span>
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
                    <div key={model.path} className="rounded-lg border border-surface-border bg-surface-deep">
                      <div className="flex flex-wrap items-center gap-2 px-3 py-2">
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center gap-2">
                          {renaming === model.path ? (
                            <input
                              autoFocus
                              value={renameDraft}
                              onChange={event => setRenameDraft(event.target.value)}
                              onBlur={() => void commitRename(model)}
                              onKeyDown={event => {
                                if (event.key === 'Enter') void commitRename(model);
                                if (event.key === 'Escape') {
                                  setRenaming(null);
                                  setRenameDraft('');
                                }
                              }}
                              className="min-w-0 flex-1 rounded border border-sky-500 bg-surface-deep px-1.5 py-0.5 text-xs text-content-main focus:outline-none"
                            />
                          ) : (
                            <>
                              <span className="truncate text-xs text-content-main" title={model.path}>
                                {model.name}
                              </span>
                              <button
                                type="button"
                                onClick={() => {
                                  setRenaming(model.path);
                                  setRenameDraft(model.name);
                                }}
                                title="Rename it here. The file on disk keeps its own name."
                                className="rounded p-0.5 text-content-dim transition-colors hover:bg-surface-hover hover:text-content-main"
                                aria-label={`Rename ${model.name}`}
                              >
                                <Pencil className="h-3 w-3" />
                              </button>
                            </>
                          )}
                          <span className="rounded border border-surface-border px-1 text-[9px] uppercase tracking-wide text-content-dim">
                            {model.origin}
                          </span>
                          {model.flags ? (
                            <span
                              className="rounded border border-violet-500/30 bg-violet-500/10 px-1 text-[9px] uppercase tracking-wide text-content-muted"
                              title="This model has its own launch flags"
                            >
                              custom
                            </span>
                          ) : null}
                          {(model.capabilities ?? []).map(capability => {
                            // A vision tag with no projector behind it promises what the launch cannot keep.
                            const blind = capability === 'vision' && !model.visionReady;

                            return (
                              <span
                                key={capability}
                                className={`rounded border px-1 text-[9.5px] ${
                                  blind
                                    ? 'border-amber-500/30 bg-amber-500/10 text-state-warning'
                                    : 'border-sky-500/25 bg-sky-500/10 text-accent'
                                }`}
                                title={
                                  blind
                                    ? 'Vision needs its mmproj projector beside the file. Until then an image is refused, not silently ignored.'
                                    : 'Read from the file name, since a local file carries no metadata'
                                }
                              >
                                {CAPABILITY_LABEL[capability] ?? capability}
                                {blind ? ' · no projector' : ''}
                              </span>
                            );
                          })}
                          <FitBadge
                            fit={model.fit}
                            context={model.context}
                            kvMb={model.kvMb}
                            gpuUnusable={server?.backend === 'CPU only'}
                          />
                        </div>
                        <p className="truncate font-mono text-[10px] text-content-dim" title={model.path}>
                          {sizeLabel(model.size)} · {model.path}
                        </p>
                      </div>

                      <button
                        onClick={() => openTune(model)}
                        className={`flex h-7 items-center gap-1 rounded-lg border px-2 text-[11px] transition-colors ${
                          tuning === model.path
                            ? 'border-sky-500/40 text-accent'
                            : 'border-surface-border text-content-muted hover:text-content-main'
                        }`}
                        title="Launch flags for this model only: context, cache, projector"
                        aria-expanded={tuning === model.path}
                      >
                        <SlidersHorizontal className="h-3 w-3" />
                        Flags
                      </button>

                      {loadedPath && loadedPath === model.path && server?.running ? (
                        <>
                          <span
                            className="rounded border border-emerald-500/30 bg-emerald-500/10 px-1.5 py-0.5 text-[9.5px] text-state-success"
                            title="This file is answering right now"
                          >
                            loaded
                          </span>
                          <button
                            onClick={() => void unload()}
                            disabled={busy}
                            className="flex h-7 items-center gap-1 rounded-lg border border-surface-border px-2 text-[11px] text-content-muted hover:border-rose-500/40 hover:text-state-danger disabled:opacity-50"
                            title="Stop the server and free the memory this model holds"
                          >
                            <Power className="h-3 w-3" />
                            Unload
                          </button>
                        </>
                      ) : (
                        <button
                          onClick={() => void runModel(model)}
                          disabled={busy}
                          className="flex h-7 items-center gap-1 rounded-lg bg-emerald-600 px-2 text-[11px] font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
                          title={server?.running ? 'Restart the server on this file' : 'Start the server on this file'}
                        >
                          <Play className="h-3 w-3" />
                          Load
                        </button>
                      )}

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

                      {tuning === model.path && (
                        <div className="grid grid-cols-2 gap-2 border-t border-surface-border px-3 py-2 text-[10.5px] sm:grid-cols-4">
                          <label className="flex flex-col gap-0.5">
                            <span className="text-content-dim">Context</span>
                            <input
                              type="number"
                              min={8192}
                              step={1024}
                              value={tuneDraft.contextSize ?? ''}
                              placeholder={model.context ? String(model.context) : 'app default'}
                              onChange={event =>
                                setTuneDraft(draft => ({
                                  ...draft,
                                  contextSize: event.target.value ? Number(event.target.value) : null,
                                }))
                              }
                              className="rounded border border-surface-border bg-surface-deep px-1.5 py-0.5 font-mono text-[10.5px] text-content-main focus:border-sky-500 focus:outline-none"
                            />
                          </label>

                          <label className="flex flex-col gap-0.5">
                            <span className="text-content-dim">GPU layers</span>
                            <input
                              type="number"
                              min={0}
                              max={512}
                              value={tuneDraft.gpuLayers ?? ''}
                              placeholder={cpuOnly ? 'no GPU device' : 'app default'}
                              disabled={cpuOnly}
                              onChange={event =>
                                setTuneDraft(draft => ({
                                  ...draft,
                                  gpuLayers: event.target.value ? Number(event.target.value) : null,
                                }))
                              }
                              className="rounded border border-surface-border bg-surface-deep px-1.5 py-0.5 font-mono text-[10.5px] text-content-main focus:border-sky-500 focus:outline-none disabled:opacity-40"
                            />
                          </label>

                          <label className="flex flex-col gap-0.5">
                            <span className="text-content-dim">Cache K</span>
                            <select
                              value={tuneDraft.cacheTypeK ?? ''}
                              onChange={event =>
                                setTuneDraft(draft => ({ ...draft, cacheTypeK: event.target.value || null }))
                              }
                              className="rounded border border-surface-border bg-surface-deep px-1 py-0.5 font-mono text-[10.5px] text-content-main focus:border-sky-500 focus:outline-none"
                            >
                              <option value="">app default</option>
                              {CACHE_TYPES.map(type => (
                                <option key={type} value={type}>
                                  {type}
                                </option>
                              ))}
                            </select>
                          </label>

                          <label className="flex flex-col gap-0.5">
                            <span className="text-content-dim">Cache V</span>
                            <select
                              value={tuneDraft.cacheTypeV ?? ''}
                              onChange={event =>
                                setTuneDraft(draft => ({ ...draft, cacheTypeV: event.target.value || null }))
                              }
                              className="rounded border border-surface-border bg-surface-deep px-1 py-0.5 font-mono text-[10.5px] text-content-main focus:border-sky-500 focus:outline-none"
                            >
                              <option value="">app default</option>
                              {CACHE_TYPES.map(type => (
                                <option key={type} value={type}>
                                  {type}
                                </option>
                              ))}
                            </select>
                          </label>

                          <label className="flex flex-col gap-0.5">
                            <span className="text-content-dim">Flash attention</span>
                            <select
                              value={
                                tuneDraft.flashAttention === null || tuneDraft.flashAttention === undefined
                                  ? ''
                                  : tuneDraft.flashAttention
                                    ? 'on'
                                    : 'off'
                              }
                              onChange={event =>
                                setTuneDraft(draft => ({
                                  ...draft,
                                  flashAttention: event.target.value === '' ? null : event.target.value === 'on',
                                }))
                              }
                              className="rounded border border-surface-border bg-surface-deep px-1 py-0.5 text-[10.5px] text-content-main focus:border-sky-500 focus:outline-none"
                            >
                              <option value="">app default</option>
                              <option value="on">on</option>
                              <option value="off">off</option>
                            </select>
                          </label>

                          <label className="col-span-2 flex flex-col gap-0.5 sm:col-span-3">
                            <span className="text-content-dim">Extra arguments</span>
                            <input
                              value={tuneDraft.extraArgs ?? ''}
                              placeholder="app default"
                              onChange={event =>
                                setTuneDraft(draft => ({ ...draft, extraArgs: event.target.value || null }))
                              }
                              className="rounded border border-surface-border bg-surface-deep px-1.5 py-0.5 font-mono text-[10.5px] text-content-main focus:border-sky-500 focus:outline-none"
                            />
                          </label>

                          <div className="col-span-2 flex flex-wrap items-center gap-1.5 sm:col-span-4">
                            <span className="text-content-dim">Projector</span>
                            <span
                              className="min-w-0 flex-1 truncate font-mono text-[10px] text-content-muted"
                              title={tuneDraft.projector ?? model.projector ?? 'No projector'}
                            >
                              {tuneDraft.projector ?? model.projector ?? 'none — a vision model needs one'}
                            </span>
                            <button
                              type="button"
                              onClick={() => void pickProjector(model)}
                              className="rounded border border-surface-border px-1.5 py-0.5 text-[10px] text-content-muted hover:text-content-main"
                            >
                              Choose…
                            </button>
                          </div>

                          <div className="col-span-2 flex flex-wrap items-center gap-1.5 sm:col-span-4">
                            {model.suggestedContext ? (
                              <button
                                type="button"
                                onClick={() =>
                                  setTuneDraft(draft => ({ ...draft, contextSize: model.suggestedContext ?? null }))
                                }
                                className="rounded border border-emerald-500/30 bg-emerald-500/10 px-1.5 py-0.5 text-[10px] text-state-success"
                                title="The largest context this model still fits in, counting the KV cache"
                              >
                                Use {model.suggestedContext.toLocaleString()}
                              </button>
                            ) : null}
                            <Button size="xs" disabled={busy} onClick={() => void saveFlags(model)}>
                              Save flags
                            </Button>
                            <button
                              type="button"
                              onClick={() => void resetFlags(model)}
                              className="text-[10px] text-content-dim underline decoration-dotted hover:text-content-main"
                            >
                              Use app defaults
                            </button>
                            {model.kvMb ? (
                              <span className="ml-auto font-mono text-[9.5px] text-content-dim">
                                KV cache ≈ {gb(model.kvMb)} at {(model.context ?? 0).toLocaleString()}
                              </span>
                            ) : null}
                          </div>
                        </div>
                      )}
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
              <div className="flex flex-wrap items-center gap-1.5 text-[10.5px]">
                <span className="text-content-dim">Download to</span>
                <span className="min-w-0 flex-1 truncate font-mono text-content-muted" title={directory}>
                  {directory || 'the app folder'}
                </span>
                <button
                  type="button"
                  onClick={() => void chooseDownloadFolder()}
                  className="rounded border border-surface-border px-1.5 py-0.5 text-content-muted transition-colors hover:text-content-main"
                >
                  Choose…
                </button>
                {usingDefaultDirectory ? (
                  <span className="text-content-dim" title="Downloads use the app's own models folder">
                    default
                  </span>
                ) : (
                  <button
                    type="button"
                    onClick={() => void useDefaultDownloadFolder()}
                    className="rounded border border-surface-border px-1.5 py-0.5 text-content-muted transition-colors hover:text-content-main"
                    title={`Go back to ${defaultDirectory}`}
                  >
                    Use default
                  </button>
                )}
              </div>

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
