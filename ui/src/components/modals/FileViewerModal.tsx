import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, Check, FileCode2, Loader2, Save, X } from 'lucide-react';
import { webviewBridge } from '../../services/webviewBridge';
import { highlightCode } from '../../lib/highlight';
import { useWorkspaceStore } from '../../store/useWorkspaceStore';

interface FileViewerModalProps {
  /** Workspace-relative path, or null when the modal is closed. */
  path: string | null;
  onClose: () => void;
}

/**
 * Reads a file, colours it with the same Prism grammars as the chat's code blocks, and writes it
 * back.
 *
 * The editor is a transparent textarea over the highlighted copy rather than a second view mode:
 * colours stay while typing, and the two layers share one font metric so the caret lands on the
 * character it looks like it is on. Saving goes through the host, which resolves the path against
 * the workspace root and refuses anything outside it.
 */
export const FileViewerModal: React.FC<FileViewerModalProps> = ({ path, onClose }) => {
  const root = useWorkspaceStore(state => state.activeWorkspace?.workingDirectory);

  const [text, setText] = useState('');
  const [savedText, setSavedText] = useState('');
  const [extension, setExtension] = useState('');
  const [sizeBytes, setSizeBytes] = useState(0);
  const [truncated, setTruncated] = useState(false);
  const [binary, setBinary] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [scroll, setScroll] = useState({ top: 0, left: 0 });

  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const load = useCallback(async () => {
    if (!path) return;

    setIsLoading(true);
    setError(null);
    setNote(null);
    setTruncated(false);
    setBinary(false);
    setScroll({ top: 0, left: 0 });

    try {
      const res = await webviewBridge.invoke('read_workspace_file', { path, root });
      if (!res) {
        setError('The host did not answer.');
        return;
      }

      const content = typeof res.text === 'string' ? res.text : '';
      setText(content);
      setSavedText(content);
      setExtension(typeof res.extension === 'string' ? res.extension : '');
      setSizeBytes(typeof res.sizeBytes === 'number' ? res.sizeBytes : 0);
      setTruncated(Boolean(res.truncated));
      setBinary(Boolean(res.binary));
    } catch (err: any) {
      setError(err?.message || 'Could not read that file.');
    } finally {
      setIsLoading(false);
    }
  }, [path, root]);

  useEffect(() => {
    if (!path) return;
    void load();
  }, [path, load]);

  // Escape closes, matching what the backdrop click does.
  useEffect(() => {
    if (!path) return;

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
      }
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [path, onClose]);

  const highlighted = useMemo(() => highlightCode(text, extension), [text, extension]);
  const isDirty = text !== savedText;
  const lines = useMemo(() => text.split('\n').length, [text]);

  // Only the first megabyte of a big file was read, so the buffer is not the whole file: editing it
  // would write a truncated copy back. Same rule the host enforces, said here first.
  const readOnly = binary || truncated;

  const save = useCallback(async () => {
    if (!path || !isDirty || isSaving || readOnly) return;

    setIsSaving(true);
    setError(null);
    setNote(null);

    try {
      const res = await webviewBridge.invoke('save_workspace_file', { path, root, text });
      if (!res || res.saved === false) {
        setError('The host refused the write.');
        return;
      }

      setSavedText(text);
      setNote(`Saved ${typeof res.sizeBytes === 'number' ? res.sizeBytes.toLocaleString() : ''} bytes.`.replace('  ', ' '));
    } catch (err: any) {
      setError(err?.message || 'Could not save that file.');
    } finally {
      setIsSaving(false);
    }
  }, [path, root, text, isDirty, isSaving, readOnly]);

  if (!path) return null;

  const fileName = path.split('/').pop() || path;
  const editorFont = 'font-mono text-[12px] leading-[1.55]';
  // Same value on both layers, or the caret drifts from the glyphs under it.
  const editorPadding = 'p-4';
  const tabSize = { tabSize: 2, MozTabSize: 2 } as React.CSSProperties;

  // Portalled to the body. Mounted inside the sidebar, `fixed inset-0` resolved against an ancestor
  // box instead of the window: the dialog opened part-way down the screen, slid under the tab strip,
  // and its backdrop only dimmed that box. The body has no such ancestor.
  return createPortal(
    <div
      className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 p-4 backdrop-blur-xs"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-label={`File ${path}`}
    >
      <div
        className="flex h-[86vh] w-full max-w-5xl flex-col overflow-hidden rounded-2xl border border-surface-border bg-surface-card font-sans shadow-2xl"
        onClick={event => event.stopPropagation()}
      >
        <div className="flex items-center justify-between gap-3 border-b border-surface-border bg-surface-deep/80 px-5 py-3.5">
          <div className="flex min-w-0 items-center gap-2.5">
            <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg border border-sky-500/30 bg-sky-500/15 text-sky-400">
              <FileCode2 className="h-4 w-4" />
            </div>
            <div className="min-w-0">
              <div className="flex items-center gap-2">
                <span className="truncate text-sm font-bold text-content-main">{fileName}</span>
                {highlighted.language && (
                  <span className="rounded border border-sky-500/20 bg-sky-500/10 px-1.5 py-0.5 text-[10px] font-mono text-sky-400">
                    {highlighted.language}
                  </span>
                )}
                {isDirty && (
                  <span className="rounded border border-amber-500/30 bg-amber-500/10 px-1.5 py-0.5 text-[10px] font-mono text-amber-400">
                    unsaved
                  </span>
                )}
              </div>
              <p className="truncate text-[11px] font-mono text-content-dim" title={path}>
                {path}
              </p>
            </div>
          </div>

          <div className="flex shrink-0 items-center gap-2">
            <button
              type="button"
              onClick={() => void save()}
              disabled={!isDirty || isSaving || readOnly}
              className="inline-flex items-center gap-1.5 rounded-lg bg-sky-500 px-2.5 py-1.5 text-[11px] font-semibold text-white transition-colors hover:bg-sky-400 disabled:cursor-not-allowed disabled:opacity-40"
              title={readOnly ? 'This file cannot be edited here' : isDirty ? 'Save the file (Ctrl+S)' : 'No changes to save'}
            >
              {isSaving ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />}
              <span>Save</span>
            </button>

            <button
              type="button"
              onClick={onClose}
              className="rounded-lg p-1.5 text-content-dim transition hover:bg-surface-deep hover:text-content-main"
              title="Close (Esc)"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        </div>

        {error && (
          <div className="flex items-center gap-2 border-b border-rose-500/30 bg-rose-500/10 px-5 py-2 text-[11px] text-rose-300">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
            <span className="truncate">{error}</span>
          </div>
        )}

        <div className="relative min-h-0 flex-1 bg-surface-deep">
          {isLoading ? (
            <div className="flex h-full items-center justify-center gap-2 text-xs text-content-dim">
              <Loader2 className="h-4 w-4 animate-spin text-sky-400" />
              <span>Reading {fileName}...</span>
            </div>
          ) : binary ? (
            <div className="flex h-full items-center justify-center px-8 text-center text-xs text-content-dim">
              This looks like a binary file, so it has no text to show or edit.
            </div>
          ) : (
            <>
              {/* The coloured copy sits under the textarea; both scroll together through `scroll`. */}
              <pre
                aria-hidden="true"
                className={`pointer-events-none absolute inset-0 m-0 overflow-hidden ${editorFont} ${editorPadding}`}
                style={tabSize}
              >
                <code
                  className={`code-highlight block whitespace-pre ${editorFont}`}
                  style={{ transform: `translate(${-scroll.left}px, ${-scroll.top}px)` }}
                  dangerouslySetInnerHTML={{ __html: highlighted.html + '\n' }}
                />
              </pre>

              <textarea
                ref={textareaRef}
                value={text}
                onChange={event => setText(event.target.value)}
                onScroll={event =>
                  setScroll({ top: event.currentTarget.scrollTop, left: event.currentTarget.scrollLeft })
                }
                onKeyDown={event => {
                  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
                    event.preventDefault();
                    void save();
                  }
                }}
                spellCheck={false}
                wrap="off"
                readOnly={readOnly}
                aria-label={`Contents of ${path}`}
                className={`absolute inset-0 h-full w-full resize-none overflow-auto border-0 bg-transparent text-transparent caret-sky-300 outline-none selection:bg-sky-500/30 ${editorFont} ${editorPadding}`}
                style={tabSize}
              />
            </>
          )}
        </div>

        <div className="flex items-center justify-between gap-3 border-t border-surface-border bg-surface-deep/80 px-5 py-2.5 text-[11px] font-mono text-content-dim">
          <span className="flex items-center gap-2">
            <span>{lines.toLocaleString()} lines</span>
            <span className="text-surface-border">|</span>
            <span>{sizeBytes.toLocaleString()} bytes</span>
            {truncated && (
              <span className="text-amber-400" title="Only the first megabyte is shown">
                · truncated
              </span>
            )}
          </span>

          <span className="flex items-center gap-1.5 truncate">
            {note ? (
              <>
                <Check className="h-3.5 w-3.5 text-emerald-400" />
                <span className="text-emerald-400">{note}</span>
              </>
            ) : truncated ? (
              <span className="text-amber-400">Only the first megabyte is shown, so this file is read-only here.</span>
            ) : isDirty ? (
              <span>Ctrl+S saves</span>
            ) : (
              <span>Read-only until you type</span>
            )}
          </span>
        </div>
      </div>
    </div>,
    document.body
  );
};
