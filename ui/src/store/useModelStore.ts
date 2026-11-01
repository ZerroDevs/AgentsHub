/**
 * Model catalogue cache.
 *
 * The list comes from the engine (`agy models` through the host bridge), not from a table in the
 * bundle: a model that ships after this build still shows up, and one that is retired disappears.
 * Selection itself lives on the session record, so two tabs can run different models.
 */

import { create } from 'zustand';
import { ModelCatalog, ModelInfo, ReasoningEffort } from '../types/agent';
import { webviewBridge } from '../services/webviewBridge';

const CACHE_TTL_MS = 5 * 60 * 1000;

/** Levels this build offers. Anything else the engine reports is dropped rather than rendered. */
const SUPPORTED_EFFORTS = new Set(['low', 'medium', 'high']);

/** Fallback effort scale, used only until the engine reports its own list. */
export const DEFAULT_EFFORTS: ReasoningEffort[] = ['low', 'medium', 'high'];

const EFFORT_LABELS: Record<string, string> = {
  low: 'Low',
  medium: 'Medium',
  high: 'High',
};

export function effortLabel(effort?: string | null): string {
  if (!effort) return '';
  return EFFORT_LABELS[effort.toLowerCase()] || effort;
}

/**
 * The model a stored value belongs to, accepting a base id, a concrete tier id, or a display label.
 *
 * Label matching exists because the host reports the profile default as a label
 * ("Gemini 3.8 Flash (High)"), and sessions created before that was fixed still carry one. Mapping
 * it back to the catalogue keeps those sessions usable instead of falling back to the engine default.
 */
export function findBaseModel(models: ModelInfo[], id?: string | null): ModelInfo | undefined {
  if (!id) return undefined;

  const exact =
    models.find(model => model.id === id) ||
    models.find(model => Object.values(model.tiers).includes(id));
  if (exact) return exact;

  const normalised = id.trim().toLowerCase();
  const withoutTier = normalised.replace(/\s*\((thinking|high|medium|low|xhigh|max)\)$/, '').trim();

  return (
    models.find(model => model.label.toLowerCase() === normalised) ||
    models.find(model => model.label.toLowerCase() === withoutTier)
  );
}

/**
 * Resolves the id to hand to the engine for a base model plus effort.
 *
 * Returns `undefined` when the input is not a model the catalog knows. Echoing an unresolved value
 * back is what shipped a display label (`"Gemini 3.8 Flash (High)"`) to the engine as `--model`,
 * which it rejects — better to send no model at all and let the engine use its default.
 *
 * Falls back to the engine's high tier, then to whatever tier exists, because a model that only ships
 * a subset of tiers must still launch something valid.
 */
export function resolveModelId(
  models: ModelInfo[],
  baseId?: string | null,
  effort?: string | null
): string | undefined {
  if (!baseId) return undefined;

  const base = findBaseModel(models, baseId);
  if (!base) return undefined;

  const level = effort?.toLowerCase();
  if (level && base.tiers[level]) return base.tiers[level];

  const tierIds = Object.values(base.tiers);
  if (tierIds.length === 0) return base.id;
  return base.tiers['high'] || tierIds[0];
}

interface ModelStoreState {
  models: ModelInfo[];
  efforts: string[];
  source: string;
  error?: string;
  isLoading: boolean;
  hasLoaded: boolean;
  loadedKey?: string;
  lastLoadedAt: number;

  load: (cli?: string, options?: { force?: boolean }) => Promise<void>;
}

function normaliseCatalog(raw: unknown): ModelCatalog | null {
  if (!raw || typeof raw !== 'object') return null;
  const record = raw as Record<string, unknown>;

  const models: ModelInfo[] = Array.isArray(record.models)
    ? record.models
        .map((entry): ModelInfo | null => {
          if (!entry || typeof entry !== 'object') return null;
          const item = entry as Record<string, unknown>;
          const id = typeof item.id === 'string' ? item.id.trim() : '';
          if (!id) return null;

          const rawTiers = item.tiers;
          const tiers: Record<string, string> = {};
          if (rawTiers && typeof rawTiers === 'object') {
            for (const [level, concreteId] of Object.entries(rawTiers as Record<string, unknown>)) {
              if (typeof concreteId === 'string' && concreteId.trim()) {
                tiers[level.toLowerCase()] = concreteId.trim();
              }
            }
          }

          return {
            id,
            label: typeof item.label === 'string' && item.label.trim() ? item.label.trim() : id,
            provider: typeof item.provider === 'string' ? item.provider : '',
            tiers,
          };
        })
        .filter((model): model is ModelInfo => model !== null)
    : [];

  const efforts = Array.isArray(record.efforts)
    ? record.efforts
        .filter((value): value is string => typeof value === 'string')
        .filter(value => SUPPORTED_EFFORTS.has(value.toLowerCase()))
    : [];

  return {
    models,
    efforts: efforts.length > 0 ? efforts : DEFAULT_EFFORTS,
    source: typeof record.source === 'string' ? record.source : 'unknown',
    error: typeof record.error === 'string' ? record.error : undefined,
  };
}

export const useModelStore = create<ModelStoreState>((set, get) => ({
  models: [],
  efforts: DEFAULT_EFFORTS,
  source: 'unknown',
  isLoading: false,
  hasLoaded: false,
  lastLoadedAt: 0,

  load: async (cli, options = {}) => {
    const state = get();
    if (state.isLoading) return;

    const key = cli || '';
    const isFresh =
      state.hasLoaded && state.loadedKey === key && Date.now() - state.lastLoadedAt < CACHE_TTL_MS;
    if (isFresh && !options.force) return;

    set({ isLoading: true, error: undefined });

    try {
      const response = await webviewBridge.invoke<unknown>(
        'get_model_catalog',
        { cli, force: options.force === true },
        30000
      );
      const catalog = normaliseCatalog(response);

      if (!catalog) {
        set({ isLoading: false, hasLoaded: true, error: 'The host returned no model catalogue.' });
        return;
      }

      set({
        models: catalog.models,
        efforts: catalog.efforts,
        source: catalog.source,
        error: catalog.error ?? undefined,
        isLoading: false,
        hasLoaded: true,
        loadedKey: key,
        lastLoadedAt: Date.now(),
      });
    } catch (err) {
      set({
        isLoading: false,
        hasLoaded: true,
        error: err instanceof Error ? err.message : 'Model discovery failed',
      });
    }
  },
}));
