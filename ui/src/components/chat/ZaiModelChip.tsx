import React, { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { ChevronUp, Lock, Sparkles } from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';
import { useSessionStore } from '../../store/useSessionStore';

interface ZaiModel {
  id: string;
  name: string;
  /** True for the models the site withholds until you sign in. */
  locked: boolean;
}

const chipClass =
  'flex items-center gap-1.5 rounded-lg border border-surface-border bg-surface-card px-2 py-1 font-mono text-[11px] text-content-muted transition-colors hover:border-sky-500/50 hover:text-content-main';

const menuClass =
  'fixed z-[121] w-56 -translate-y-full overflow-hidden rounded-lg border border-surface-border bg-surface-card py-1 shadow-xl';

/**
 * The model Z.ai answers with.
 *
 * The list is read from the page, not copied from here: the site decides which models exist and,
 * more importantly, which ones a guest may use. A guest sees GLM-5.3 and GLM-5.2 disabled, so the
 * menu says so and offers the sign-in instead of sending on a model that was never applied.
 */
export const ZaiModelChip: React.FC<{ sessionId: string; model?: string }> = ({ sessionId, model }) => {
  const updateSessionTelemetry = useSessionStore(state => state.updateSessionTelemetry);

  /**
   * A Z.ai session only ever answers with a GLM model. Sessions created before Z.ai was picked still
   * carry the agy default in `model` ("Gemini 3.8 Flash (High)"), and showing that in this chip was
   * both wrong and a lie about what the turn would use.
   */
  const asGlm = (value?: string) => (value && /^GLM/i.test(value) ? value : 'GLM-5.3-Flash');
  const [current, setCurrent] = useState(asGlm(model));

  useEffect(() => {
    const resolved = asGlm(model);
    setCurrent(resolved);
    if (resolved !== model) updateSessionTelemetry(sessionId, { model: resolved });
  }, [model, sessionId, updateSessionTelemetry]);
  const [models, setModels] = useState<ZaiModel[] | null>(null);
  const [anchor, setAnchor] = useState<{ top: number; left: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);

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

    setAnchor({ top: Math.max(8, rect.top - 8), left: Math.max(8, Math.min(rect.left, window.innerWidth - 240)) });
    setModels(null);

    try {
      const res: any = await webviewBridge.invoke('zai_models');
      setModels(Array.isArray(res?.models) ? res.models : []);
    } catch {
      setModels([]);
    }
  };

  const choose = (entry: ZaiModel) => {
    if (entry.locked) {
      // The page refuses this selection for a guest. Opening the window is the only honest answer.
      void webviewBridge.invoke('zai_login').catch(() => {});
      setAnchor(null);
      return;
    }

    setCurrent(entry.name);
    setAnchor(null);
    // Session, not local state alone: the turn carries this name so the page is switched before it
    // sends, and a reloaded tab still shows the model it will use.
    updateSessionTelemetry(sessionId, { model: entry.name });
    void webviewBridge.invoke('zai_model', { name: entry.name }).catch(() => {});
  };

  const anyLocked = models?.some(entry => entry.locked) ?? false;

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        title="The model Z.ai answers with"
        onClick={() => (anchor ? setAnchor(null) : void openMenu())}
        className={chipClass}
      >
        <Sparkles className="h-3 w-3 flex-shrink-0 text-accent" />
        <span className="max-w-[140px] truncate">{current}</span>
        <ChevronUp className={`h-3 w-3 opacity-60 transition-transform ${anchor ? 'rotate-180' : ''}`} />
      </button>

      {anchor
        ? createPortal(
            <>
              <div className="fixed inset-0 z-[120]" onClick={() => setAnchor(null)} />
              <div style={{ top: anchor.top, left: anchor.left }} className={menuClass} role="menu">
                {models === null ? (
                  <p className="px-3 py-2 text-[10.5px] text-content-dim">Reading the model list...</p>
                ) : models.length === 0 ? (
                  <button
                    type="button"
                    onClick={() => {
                      setAnchor(null);
                      void webviewBridge.invoke('zai_open').catch(() => {});
                    }}
                    className="w-full px-3 py-2 text-left text-[10.5px] text-content-muted transition-colors hover:bg-surface-hover hover:text-content-main"
                  >
                    No list available. Open the Z.ai window
                  </button>
                ) : (
                  models.map(entry => (
                    <button
                      key={entry.id}
                      type="button"
                      role="menuitem"
                      onClick={() => choose(entry)}
                      className={`flex w-full items-center justify-between gap-2 px-3 py-1.5 text-left text-[11px] transition-colors hover:bg-surface-hover ${
                        entry.name === current ? 'text-accent' : 'text-content-muted'
                      }`}
                    >
                      <span className="truncate">{entry.name}</span>
                      {entry.locked ? (
                        <span className="flex flex-shrink-0 items-center gap-1 text-[9.5px] text-content-dim">
                          <Lock className="h-2.5 w-2.5" />
                          sign in
                        </span>
                      ) : entry.name === current ? (
                        <span className="text-[9px]">current</span>
                      ) : null}
                    </button>
                  ))
                )}

                {anyLocked ? (
                  <button
                    type="button"
                    onClick={() => {
                      setAnchor(null);
                      void webviewBridge.invoke('zai_login').catch(() => {});
                    }}
                    className="mt-1 w-full border-t border-surface-border px-3 py-2 text-left text-[10.5px] leading-relaxed text-content-muted transition-colors hover:bg-surface-hover hover:text-content-main"
                  >
                    Sign in to Z.ai for GLM-5.3, GLM-5.2 and a larger allowance.
                  </button>
                ) : null}
              </div>
            </>
          , document.body)
        : null}
    </>
  );
};
