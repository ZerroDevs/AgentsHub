import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Loader2, ShieldAlert, ShieldCheck } from 'lucide-react';
import { useProfileStore } from '../../store/useProfileStore';
import { isUnsetProfile } from '../../lib/profileName';

interface PermissionModeButtonProps {
  /** Profile that owns the setting. Empty means the active runner. */
  profileName?: string;
  /**
   * Which engine the copy is written for. The setting is the same profile flag either way, but a
   * DeepSeek turn has no shell policy: it has an approval card for writes and commands.
   */
  engine?: 'agy' | 'deepseek' | 'qwen' | 'zai';
}

const PANEL_WIDTH = 296;
const VIEWPORT_MARGIN = 8;

/**
 * Full-access switch for the composer footer.
 *
 * The value lives on the profile, not the session: the terminal tab has its own checkbox backed by
 * the same flag, and two surfaces disagreeing about whether a command may run is worse than either
 * default. What "off" means is not what it sounds like — headless `agy` cannot answer a permission
 * prompt, so gated tools are denied outright and the composer offers to allow them afterwards. The
 * copy says so rather than implying the agent will ask.
 */
export const PermissionModeButton: React.FC<PermissionModeButtonProps> = ({ profileName, engine }) => {
  // Z.ai uses the same approval card as DeepSeek, so the copy describes the card rather than a
  // shell policy.
  const isDeepSeek = engine === 'deepseek' || engine === 'zai';
  const profiles = useProfileStore(state => state.profiles);
  const activeProfileName = useProfileStore(state => state.activeProfileName);
  const setSkipPermissions = useProfileStore(state => state.setSkipPermissions);

  const targetName = isUnsetProfile(profileName) ? activeProfileName : (profileName as string);
  const profile = profiles.find(p => p.name.toLowerCase() === (targetName || '').toLowerCase());
  const isAllowed = profile?.skipPermissions !== false;

  const [isOpen, setIsOpen] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [anchor, setAnchor] = useState<{ bottom: number; left: number } | null>(null);

  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  const measure = useCallback(() => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;
    setAnchor({
      bottom: Math.max(VIEWPORT_MARGIN, window.innerHeight - rect.top + VIEWPORT_MARGIN),
      left: Math.max(
        VIEWPORT_MARGIN,
        Math.min(rect.left, window.innerWidth - PANEL_WIDTH - VIEWPORT_MARGIN)
      ),
    });
  }, []);

  useLayoutEffect(() => {
    if (isOpen) measure();
  }, [isOpen, measure]);

  useEffect(() => {
    if (!isOpen) return;

    const handlePointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (panelRef.current?.contains(target)) return;
      if (triggerRef.current?.contains(target)) return;
      setIsOpen(false);
    };
    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setIsOpen(false);
    };
    // Re-measure rather than close: a streaming reply scrolls the list constantly, and closing the
    // panel on every token would make the switch unusable mid-turn.
    const handleReposition = () => {
      const rect = triggerRef.current?.getBoundingClientRect();
      if (!rect) return;
      if (rect.bottom < 0 || rect.top > window.innerHeight) {
        setIsOpen(false);
        return;
      }
      measure();
    };

    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('keydown', handleEscape);
    window.addEventListener('resize', handleReposition);
    window.addEventListener('scroll', handleReposition, true);

    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('keydown', handleEscape);
      window.removeEventListener('resize', handleReposition);
      window.removeEventListener('scroll', handleReposition, true);
    };
  }, [isOpen, measure]);

  const toggle = async () => {
    if (isSaving || !targetName) return;
    setError(null);
    setIsSaving(true);
    const ok = await setSkipPermissions(targetName, !isAllowed);
    setIsSaving(false);
    if (!ok) setError('The host refused the change. Is the runner still online?');
  };

  const Icon = isAllowed ? ShieldCheck : ShieldAlert;

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setIsOpen(prev => !prev)}
        aria-expanded={isOpen}
        className={`flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] font-mono transition hover:bg-surface-hover ${
          isAllowed ? 'text-content-muted hover:text-content-main' : 'text-state-warning'
        }`}
        title={
          isAllowed
            ? 'Full access is on: tools run without asking'
            : 'Full access is off: gated tools are blocked'
        }
      >
        <Icon className={`h-3 w-3 ${isAllowed ? 'text-accent' : 'text-state-warning'}`} />
        <span>Full access</span>
      </button>

      {isOpen && anchor
        ? createPortal(
            <div
              ref={panelRef}
              style={{ bottom: anchor.bottom, left: anchor.left, width: PANEL_WIDTH }}
              className="fixed z-50 rounded-lg border border-surface-border bg-surface-card p-3 shadow-xl"
            >
              <p className="text-[11.5px] leading-relaxed text-content-main">
                {isDeepSeek
                  ? isAllowed
                    ? 'Writes, deletes and commands run without asking.'
                    : 'Writes, deletes and commands ask first. You allow each one from the composer.'
                  : isAllowed
                    ? 'Every tool runs without asking, including shell commands.'
                    : 'Shell commands are blocked. The agent lists the ones it needs and you allow them from the composer.'}
              </p>
              <p className="mt-1.5 text-[11px] leading-relaxed text-content-dim">
                {isDeepSeek
                  ? isAllowed
                    ? 'Only keep this on for work you trust.'
                    : 'Reads and searches still run.'
                  : isAllowed
                    ? 'Only keep this on for work you trust.'
                    : 'File reads and edits still run.'}
              </p>

              <div className="mt-3 flex items-center justify-between gap-3 border-t border-surface-border/60 pt-2.5">
                <span className="text-[11.5px] font-medium text-content-main">Allow full access</span>
                <button
                  type="button"
                  role="switch"
                  aria-checked={isAllowed}
                  aria-label="Allow full access"
                  onClick={toggle}
                  disabled={isSaving || !targetName}
                  className={`relative inline-flex h-[18px] w-8 flex-shrink-0 items-center rounded-full transition disabled:opacity-50 ${
                    isAllowed ? 'bg-accent' : 'bg-surface-strong'
                  }`}
                >
                  <span
                    className={`inline-block h-3.5 w-3.5 rounded-full bg-surface-card shadow transition-transform ${
                      isAllowed ? 'translate-x-[15px]' : 'translate-x-[2px]'
                    }`}
                  />
                </button>
              </div>

              {isSaving ? (
                <p className="mt-2 flex items-center gap-1.5 text-[10.5px] text-content-dim">
                  <Loader2 className="h-3 w-3 animate-spin" /> Saving…
                </p>
              ) : null}
              {error ? (
                <p className="mt-2 text-[10.5px] leading-relaxed text-state-danger">{error}</p>
              ) : null}
              {!error && !isSaving ? (
                <p className="mt-2 text-[10.5px] leading-relaxed text-content-dim">
                  {isDeepSeek
                    ? `Saved for ${targetName || 'this profile'}. DeepSeek chats use it too.`
                    : `Saved for ${targetName || 'this profile'}. The terminal tab shows the same setting.`}
                </p>
              ) : null}
            </div>,
            document.body
          )
        : null}
    </>
  );
};
