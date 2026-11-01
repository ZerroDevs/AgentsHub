import { createPortal } from 'react-dom';
import React, { useState, useRef, useEffect, useMemo } from 'react';
import { AlertTriangle, ArrowUp, Brain, Clock, FileCode, Folder, Paperclip, Pencil, Quote, Sparkles, Square, SquareTerminal, Terminal, GitBranch, X, FileText } from 'lucide-react';
import { ChatAttachment, QueuedPrompt, SlashCommand, TokenUsage } from '../../types/agent';
import { useSlashCommandStore } from '../../store/useSlashCommandStore';
import { rankByQuery } from '../../lib/fuzzy';
import { SlashCommandPalette } from './SlashCommandPalette';
import { ModelSelector } from './ModelSelector';
import { QwenModeChip, QwenModelChip } from './QwenPickers';
import { useSessionStore } from '../../store/useSessionStore';
import { webviewBridge } from '../../services/webviewBridge';
import { ImageLightbox } from './ImageLightbox';
import { PermissionModeButton } from './PermissionModeButton';
import { VyceModelChip } from './VyceModelChip';
import { OpenCodeModelChip } from './OpenCodeModelChip';
import { ZaiModelChip } from './ZaiModelChip';
import { TokenUsageTooltip } from './TokenUsageTooltip';
import { CircularQuotaWidget } from './CircularQuotaWidget';

interface ChatInputProps {
  onSendMessage: (prompt: string, attachments?: ChatAttachment[]) => void;
  /** Cancels the running turn; the same button morphs from send to stop. */
  onStop?: () => void;
  isExecuting?: boolean;
  activeModel?: string;
  /** Reasoning effort selected for the session, shown next to the model. */
  reasoningEffort?: string;
  /** Usage reported by the last completed turn, shown as the primary number. */
  lastTurnTokens?: TokenUsage;
  /** Accumulated usage for the whole session, shown as the secondary number. */
  sessionTokens?: TokenUsage;
  /** Completed replies in this session, so the token panel can say what the total adds up. */
  turnCount?: number;
  onOpenGitModal?: () => void;
  /** Active CLI id, so command and model discovery target the right engine. */
  activeCli?: string;
  /** Workspace root, used to discover project-level skills and command manifests. */
  workingDirectory?: string;
  /**
   * Which engine answers this session. `deepseek` runs chat.deepseek.com in the app's own WebView2,
   * `vyce` drives the Vyce Code TUI in a hidden console, so the model picker, the permission switch
   * and the quota ring do not apply to either.
   */
  engine?: 'agy' | 'deepseek' | 'qwen' | 'zai' | 'vyce' | 'opencode';
  /** Null until the host has answered; drives the sign-in bar for a DeepSeek session. */
  deepSeekSignedIn?: boolean | null;
  /** Opens the one-time DeepSeek sign-in window. */
  onDeepSeekLogin?: () => void;
  /** DeepSeek's DeepThink chip; undefined means its default, which is on. */
  deepThink?: boolean;
  /** DeepSeek's Search chip; undefined means its default, which is off. */
  webSearch?: boolean;
  /** Toggles one of those two chips for this session. */
  onDeepSeekOption?: (option: 'deepThink' | 'webSearch', value: boolean) => void;
  /** Session that owns the model/effort selection. */
  sessionId: string;
  /** Profile whose quota the footer ring reports. Quota is per account, never shared. */
  profileName?: string;
  /**
   * When set, sending is refused and this text explains why (e.g. the runner is stopped). The prompt
   * is deliberately kept so the user can start the runner and retry without retyping.
   */
  blockedNotice?: string | null;
  /** Starts the runner named in `blockedNotice`. */
  onStartRunner?: () => void;
  /** Prompts typed while a turn was running, oldest first. */
  queuedPrompts?: QueuedPrompt[];
  /** Drops one queued prompt, after it is sent or taken back for editing. */
  onRemoveQueued?: (id: string) => void;
}

/**
 * Finds the slash token the caret currently sits in. Only a token at the start of the input or
 * after whitespace opens the palette, so `src/foo` never triggers it.
 */
function findSlashToken(value: string, caret: number): { start: number; query: string } | null {
  const upto = value.slice(0, caret);
  const match = /(?:^|\s)\/([^\s/]*)$/.exec(upto);
  if (!match) return null;
  return { start: upto.lastIndexOf('/'), query: match[1] };
}

export const ChatInput: React.FC<ChatInputProps> = ({
  onSendMessage,
  onStop,
  isExecuting = false,
  activeModel,
  reasoningEffort,
  engine,
  deepSeekSignedIn,
  onDeepSeekLogin,
  deepThink,
  webSearch,
  onDeepSeekOption,
  lastTurnTokens,
  sessionTokens,
  turnCount,
  onOpenGitModal,
  activeCli,
  workingDirectory,
  sessionId,
  profileName,
  blockedNotice,
  onStartRunner,
  queuedPrompts,
  onRemoveQueued,
}) => {
  // Seeded from the session so a tab switch restores what was typed, not an empty box.
  const [prompt, setPrompt] = useState(() => useSessionStore.getState().sessions[sessionId]?.draft ?? '');

  // Text quoted from the transcript lands here, ready to send or edit.
  useEffect(() => {
    const onQuote = (event: Event) => {
      const quoted = (event as CustomEvent<string>).detail;
      if (!quoted) return;
      setPrompt(prev => (prev.trim().length > 0 ? `${prev}\n\n${quoted}` : quoted));
    };

    window.addEventListener('agentshub:quote', onQuote);
    return () => window.removeEventListener('agentshub:quote', onQuote);
  }, []);
  const [attachments, setAttachments] = useState<ChatAttachment[]>([]);
  const [isDragging, setIsDragging] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  // Set briefly when a blocked send is attempted, so the notice visibly reacts instead of doing nothing.
  const [isNudging, setIsNudging] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const { commands, isLoading: isDiscovering, error: discoveryError, load: loadCommands } = useSlashCommandStore();

  const [isPaletteOpen, setIsPaletteOpen] = useState(false);
  const [paletteQuery, setPaletteQuery] = useState('');
  const [activeCommandIndex, setActiveCommandIndex] = useState(0);

  const visibleCommands = useMemo(
    () =>
      rankByQuery(commands, paletteQuery, command => command.name, command => command.description).map(
        entry => entry.item
      ),
    [commands, paletteQuery]
  );

  // Discovery is lazy: it runs the first time the palette opens for a given engine and workspace.
  useEffect(() => {
    if (!isPaletteOpen) return;
    void loadCommands({ cli: activeCli, cwd: workingDirectory });
  }, [isPaletteOpen, activeCli, workingDirectory, loadCommands]);

  useEffect(() => {
    if (activeCommandIndex >= visibleCommands.length) setActiveCommandIndex(0);
  }, [activeCommandIndex, visibleCommands.length]);

  const syncPalette = (value: string, caret: number) => {
    const token = findSlashToken(value, caret);
    if (!token) {
      setIsPaletteOpen(false);
      return;
    }
    setPaletteQuery(token.query);
    setIsPaletteOpen(true);
  };

  // Listen for file reference inserts from file tree
  useEffect(() => {
    const handleInsert = (e: any) => {
      const path = e.detail?.path;
      if (!path) return;

      // A file clicked in the FILES panel is the same thing as one picked from `@` completion, so
      // it becomes a chip too instead of a line of text in the box.
      setMentions(current => (current.includes(path) ? current : [...current, path]));
      textareaRef.current?.focus();
    };

    window.addEventListener('insert-prompt-file', handleInsert);
    return () => window.removeEventListener('insert-prompt-file', handleInsert);
  }, []);

  // Auto-resize textarea height
  useEffect(() => {
    if (textareaRef.current) {
      textareaRef.current.style.height = 'auto';
      textareaRef.current.style.height = `${Math.min(textareaRef.current.scrollHeight, 180)}px`;
    }
  }, [prompt]);


  /**
   * `@` completion over the workspace root.
   *
   * One directory listing, filtered in the browser: the tree is small enough that a round trip per
   * keystroke would be slower than filtering a list we already hold.
   */
  const onMentionDetect = async (text: string, caret: number) => {
    const before = text.slice(0, caret);
    const match = before.match(/(?:^|\s)@([^\s@]*)$/);

    if (!match) {
      setMention(null);
      setMentionItems([]);
      return;
    }

    const query = match[1];
    const start = caret - query.length - 1;
    setMention({ query, start });

    const rect = textareaRef.current?.getBoundingClientRect();
    if (rect) {
      setMentionPos({ top: rect.top, left: rect.left, width: rect.width });
    }

    try {
      const res: any = await webviewBridge.invoke('list_dir_entries', {
        path: workingDirectory || '.',
      });

      const entries = Array.isArray(res?.entries) ? res.entries : [];
      setMentionItems(
        entries
          .filter((entry: any) => typeof entry?.name === 'string')
          .map((entry: any) => ({
            name: entry.name,
            path: entry.path || entry.name,
            isDir: Boolean(entry.isDirectory ?? entry.isDir),
          }))
          .filter((entry: any) => entry.name.toLowerCase().includes(query.toLowerCase()))
          .slice(0, 8)
      );
    } catch {
      setMentionItems([]);
    }
  };

  const applyMention = (item: { name: string; path: string }) => {
    if (!mention) return;

    // The token is removed from the text and kept as a chip, which is the only way a textarea can
    // show a mention the way the reference does.
    const next = `${prompt.slice(0, mention.start)}${prompt.slice(mention.start + mention.query.length + 1)}`.replace(/\s{2,}/g, ' ');
    setPrompt(next);
    useSessionStore.getState().setDraft(sessionId, next);
    setMentions(current => (current.includes(item.path) ? current : [...current, item.path]));
    setMention(null);
    setMentionItems([]);
    textareaRef.current?.focus();
  };

  const processFiles = (files: FileList | File[]) => {
    const fileArray = Array.from(files);
    fileArray.forEach(file => {
      const isImg = file.type.startsWith('image/');
      const reader = new FileReader();

      if (isImg) {
        reader.onload = e => {
          const dataUrl = e.target?.result as string;
          const newAtt: ChatAttachment = {
            id: `att_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`,
            name: file.name,
            size: file.size,
            type: file.type,
            isImage: true,
            dataUrl,
          };
          setAttachments(prev => [...prev, newAtt]);
        };
        reader.readAsDataURL(file);
      } else {
        // Read text preview for code/text files if under 2MB
        if (file.size < 2 * 1024 * 1024) {
          reader.onload = e => {
            const text = e.target?.result as string;
            const newAtt: ChatAttachment = {
              id: `att_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`,
              name: file.name,
              size: file.size,
              type: file.type || 'text/plain',
              isImage: false,
              dataUrl: text ? `data:text/plain;base64,${btoa(unescape(encodeURIComponent(text)))}` : undefined,
            };
            setAttachments(prev => [...prev, newAtt]);
          };
          reader.readAsText(file);
        } else {
          const newAtt: ChatAttachment = {
            id: `att_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`,
            name: file.name,
            size: file.size,
            type: file.type || 'application/octet-stream',
            isImage: false,
          };
          setAttachments(prev => [...prev, newAtt]);
        }
      }
    });
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && e.target.files.length > 0) {
      processFiles(e.target.files);
      e.target.value = '';
    }
  };

  const handleRemoveAttachment = (id: string) => {
    setAttachments(prev => prev.filter(a => a.id !== id));
  };

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(true);
  };

  const handleDragLeave = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(false);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(false);
    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      processFiles(e.dataTransfer.files);
    }
  };

  const handlePaste = (e: React.ClipboardEvent<HTMLTextAreaElement>) => {
    if (e.clipboardData.files && e.clipboardData.files.length > 0) {
      e.preventDefault();
      processFiles(e.clipboardData.files);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (isPaletteOpen) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setActiveCommandIndex(prev => (visibleCommands.length ? (prev + 1) % visibleCommands.length : 0));
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        setActiveCommandIndex(prev =>
          visibleCommands.length ? (prev - 1 + visibleCommands.length) % visibleCommands.length : 0
        );
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        setIsPaletteOpen(false);
        return;
      }
      if ((e.key === 'Tab' || e.key === 'Enter') && !e.shiftKey) {
        const command = visibleCommands[activeCommandIndex];
        if (command) {
          e.preventDefault();
          applyCommand(command);
          return;
        }
      }
    }

    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  /**
   * Replaces the `/token` under the caret with the chosen command. A command that takes no argument
   * runs immediately, because selecting it from the palette is the same intent as pressing Enter.
   */
  const applyCommand = (command: SlashCommand) => {
    const caret = textareaRef.current?.selectionStart ?? prompt.length;
    const token = findSlashToken(prompt, caret);
    const start = token ? token.start : prompt.length;
    const inserted = `/${command.name}${command.takesArgument ? ' ' : ''}`;
    const nextPrompt = `${prompt.slice(0, start)}${inserted}${prompt.slice(caret)}`;

    setPrompt(nextPrompt);
    useSessionStore.getState().setDraft(sessionId, nextPrompt);
    setIsPaletteOpen(false);

    if (!command.takesArgument) {
      submitPrompt(nextPrompt);
      return;
    }

    requestAnimationFrame(() => {
      const position = start + inserted.length;
      textareaRef.current?.focus();
      textareaRef.current?.setSelectionRange(position, position);
    });
  };

  const submitPrompt = (raw: string) => {
    const text = raw.trim();

    // No `isExecuting` guard: a send during a turn is queued by the caller rather than dropped.
    if (!text && attachments.length === 0) return;

    if (blockedNotice) {
      setIsNudging(true);
      window.setTimeout(() => setIsNudging(false), 1600);
      return;
    }

    let enrichedPrompt = text;
    if (attachments.length > 0) {
      const fileHeaders = attachments.map(a => `[Attached: ${a.name} (${formatFileSize(a.size)})]`).join('\n');
      enrichedPrompt = enrichedPrompt ? `${enrichedPrompt}\n\n${fileHeaders}` : `Please analyze the attached files:\n${fileHeaders}`;
    }

    onSendMessage(enrichedPrompt, attachments);
    setPrompt('');
    setAttachments([]);
    setIsPaletteOpen(false);
    if (textareaRef.current) {
      textareaRef.current.style.height = 'auto';
    }
  };

  const handleSend = () => {
    // The engine still needs the paths, so they ride along with the text it receives.
    const withMentions = mentions.length > 0 ? `${prompt} ${mentions.map(p => `@${p}`).join(' ')}`.trim() : prompt;
    submitPrompt(withMentions);
    setMentions([]);
  };

  const formatFileSize = (bytes: number) => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const canQueue = prompt.trim().length > 0 || attachments.length > 0;
  const canSend = canQueue && !isExecuting;

  const turnTokens = lastTurnTokens?.totalTokens || 0;
  const isDeepSeek = engine === 'deepseek';
  /** Qwen is the same shape as DeepSeek: a web page we drive, not a CLI. */
  const isQwen = engine === 'qwen';
  const isOpenCode = engine === 'opencode';
  /** Z.ai is a page we drive, like DeepSeek, so it carries its own model chip and no quota ring. */
  const isZai = engine === 'zai';
  /** Vyce is the Vyce Code TUI, driven in a hidden console: no model picker, no policy switch. */
  const isVyce = engine === 'vyce';
  /** One-time hint that a guest account is the weaker way to use Qwen. */
  // sessionStorage, not localStorage: a guest who dismissed the hint once should not lose it
  // forever, and being signed out is a state that changes.
  const [qwenNudgeHidden, setQwenNudgeHidden] = useState(
    () => sessionStorage.getItem('agents_hub_qwen_nudge') === 'hidden'
  );
  /** null until the page answers. The notice only shows for a confirmed guest. */
  const [qwenSignedIn, setQwenSignedIn] = useState<boolean | null>(null);

  /**
   * Asks the Qwen page once per session whether anyone is signed in.
   *
   * A cold start has to load the page first, so this gets a deadline: without one the row sat on
   * "checking" forever and the composer said nothing about either state.
   */
  useEffect(() => {
    if (!isQwen) return;

    let alive = true;
    setQwenSignedIn(null);

    // Silence is not an accusation. A probe that times out hides the row rather than telling a
    // signed-in user they are a guest, which is what `!signedIn` did.
    const deadline = window.setTimeout(() => {
      if (alive) setQwenSignedIn(current => (current === null ? true : current));
    }, 12000);

    void webviewBridge
      .invoke('qwen_status')
      .then((res: any) => {
        if (!alive) return;

        // Only an explicit `anonymous: true` counts as a guest. `unknown` means the page did not
        // answer, and the honest thing then is to say nothing.
        setQwenSignedIn(res?.anonymous === true ? false : true);
      })
      .catch(() => {
        // A page that cannot answer says nothing about the account.
        if (alive) setQwenSignedIn(true);
      })
      .finally(() => window.clearTimeout(deadline));

    return () => {
      alive = false;
      window.clearTimeout(deadline);
    };
  }, [isQwen]);

  /** Files picked from `@` completion. Kept out of the text so they can render as chips. */
  const [mentions, setMentions] = useState<string[]>([]);

  /** Open `@` completion: what is being typed and where the token starts. */
  const [mention, setMention] = useState<{ query: string; start: number } | null>(null);
  const [mentionPos, setMentionPos] = useState<{ top: number; left: number; width: number } | null>(null);
  const [mentionItems, setMentionItems] = useState<Array<{ name: string; path: string; isDir: boolean }>>([]);

  /** Lines the quote toolbar added, so they can be shown as one chip and removed together. */
  const quoteLines = prompt.split('\n').filter(line => line.startsWith('> '));
  /** Data URL of the attachment being previewed, if any. */
  const [preview, setPreview] = useState<string | null>(null);
  /**
   * Shown until sign-in is *confirmed*, not only when it is known to be missing. A probe that is
   * slow or never answers used to leave the composer with no sign-in affordance at all.
   */
  const needsDeepSeekLogin = isDeepSeek && deepSeekSignedIn !== true;

  return (
    <div
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
      className="px-6 py-4 bg-transparent select-none max-w-4xl w-full mx-auto flex flex-col items-center"
    >
      {/* Hidden file input */}
      <input
        ref={fileInputRef}
        type="file"
        multiple
        onChange={handleFileChange}
        className="hidden"
        accept="image/*,.txt,.md,.json,.js,.jsx,.ts,.tsx,.py,.cs,.html,.css,.yml,.yaml,.sh,.cmd,.bat,.log,*"
      />

      <div className="relative w-full">
        {isPaletteOpen ? (
          <SlashCommandPalette
            commands={visibleCommands}
            query={paletteQuery}
            activeIndex={activeCommandIndex}
            isLoading={isDiscovering}
            error={discoveryError}
            engineLabel={activeCli || activeModel}
            onSelect={applyCommand}
            onHover={setActiveCommandIndex}
            onRefresh={() => void loadCommands({ cli: activeCli, cwd: workingDirectory }, { force: true })}
            onDismiss={() => setIsPaletteOpen(false)}
          />
        ) : null}

        {/* Solid, flat, one radius step above a panel. A blurred glass card with a 2xl shadow was
            the loudest surface in the app and it was an input field. */}
        <div
          className={`w-full rounded-xl border transition-colors overflow-hidden bg-surface-card ${
            isDragging
              ? 'border-sky-400 ring-2 ring-sky-500/30 bg-sky-500/5'
              : 'border-surface-border focus-within:border-sky-500/70 focus-within:ring-1 focus-within:ring-sky-500/30'
          }`}
        >
        {/* Portalled: the composer clips its own children, so a fixed overlay inside it renders
            confined to the input box. */}
        {preview
          ? createPortal(<ImageLightbox src={preview} onClose={() => setPreview(null)} />, document.body)
          : null}

        {mentions.length > 0 ? (
          <div className="flex flex-wrap items-center gap-1.5 border-b border-surface-border/40 px-3.5 py-2">
            {mentions.map(path => (
              <span
                key={path}
                className="inline-flex max-w-[240px] items-center gap-1.5 rounded-lg bg-sky-500/15 py-1 pl-1.5 pr-1 text-[11px] text-accent"
                title={path}
              >
                <FileCode className="h-3.5 w-3.5 flex-shrink-0" />
                <span className="truncate font-mono">{path}</span>
                <button
                  type="button"
                  onClick={() => setMentions(current => current.filter(p => p !== path))}
                  className="flex-shrink-0 rounded p-0.5 text-accent/70 transition-colors hover:text-state-danger"
                  title="Remove"
                >
                  <X className="h-3 w-3" />
                </button>
              </span>
            ))}
          </div>
        ) : null}

        {/* Floats over the chat instead of growing the composer: an in-flow list pushed the input
            upward and covered the conversation. */}
        {mention && mentionItems.length > 0 && mentionPos
          ? createPortal(
              <div
                style={{
                  top: Math.max(8, mentionPos.top - Math.min(mentionItems.length * 28 + 10, 260)),
                  left: mentionPos.left,
                  width: Math.min(mentionPos.width, 420),
                }}
                className="fixed z-[125] max-h-[260px] overflow-y-auto rounded-xl border border-surface-border bg-surface-card py-1 shadow-2xl"
              >
                {mentionItems.map(item => (
                  <button
                    key={item.path}
                    type="button"
                    onClick={() => applyMention(item)}
                    className="flex w-full items-center gap-2 px-2.5 py-1.5 text-left font-mono text-[11px] text-content-muted transition-colors hover:bg-surface-hover hover:text-content-main"
                  >
                    {item.isDir ? (
                      <Folder className="h-3 w-3 flex-shrink-0 text-state-warning" />
                    ) : (
                      <FileCode className="h-3 w-3 flex-shrink-0 text-accent" />
                    )}
                    <span className="truncate">{item.name}</span>
                  </button>
                ))}
              </div>,
              document.body
            )
          : null}

        {/* A guest account works, but it runs out and forgets. Stated plainly, once, and only for a
            confirmed guest: the page is asked once rather than assumed. */}
        {/* Three states, all visible: asking, guest, signed in (nothing to say). */}
        {isQwen && qwenSignedIn === null && !qwenNudgeHidden ? (
          <div className="flex items-center gap-2 border-b border-surface-border/60 px-3.5 py-2">
            <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-content-dim" />
            <p className="text-[11px] text-content-dim">Checking whether you are signed in to Qwen...</p>
          </div>
        ) : null}

        {isQwen && qwenSignedIn === false && !qwenNudgeHidden ? (
          <div className="flex items-center gap-2 border-b border-violet-500/25 bg-violet-500/[0.08] px-3.5 py-2">
            <p className="flex-1 text-[11px] leading-snug">
              <span className="font-medium text-content-main">You are chatting as a guest.</span>{' '}
              <span className="text-content-muted">Sign in for more credits and saved chats.</span>
            </p>
            <button
              type="button"
              onClick={() => void webviewBridge.invoke('qwen_login', {}).catch(() => {})}
              className="rounded-md bg-violet-600 px-2 py-1 text-[10.5px] font-medium text-white transition-colors hover:bg-violet-500"
            >
              Sign in
            </button>
            <button
              type="button"
              onClick={() => {
                setQwenNudgeHidden(true);
                sessionStorage.setItem('agents_hub_qwen_nudge', 'hidden');
              }}
              className="rounded p-0.5 text-content-dim transition-colors hover:text-content-main"
              title="Dismiss"
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        ) : null}

        {/* Quoted text reads as a chip instead of a wall of '>' lines in the box. */}
        {quoteLines.length > 0 ? (
          <div className="flex items-start gap-2 border-b border-surface-border/40 px-3.5 py-2">
            <Quote className="mt-0.5 h-3 w-3 flex-shrink-0 text-accent" />
            <div className="min-w-0 flex-1">
              <p className="text-[10.5px] font-medium text-content-dim">
                Quoted from the conversation
              </p>
              <p className="truncate text-[11px] text-content-muted" title={quoteLines.join('\n')}>
                {quoteLines.map(line => line.replace(/^>\s?/, '')).join(' ')}
              </p>
            </div>
            <button
              type="button"
              onClick={() => {
                const kept = prompt
                  .split('\n')
                  .filter(line => !line.startsWith('> '))
                  .join('\n')
                  .trim();
                setPrompt(kept);
                useSessionStore.getState().setDraft(sessionId, kept);
              }}
              className="flex-shrink-0 rounded p-1 text-content-dim transition-colors hover:text-state-danger"
              title="Remove the quote"
            >
              <X className="h-3 w-3" />
            </button>
          </div>
        ) : null}

        {/* Attached Files Preview Bar */}
        {attachments.length > 0 && (
          <div className="flex flex-wrap items-center gap-2 px-3.5 pt-3 pb-1 border-b border-surface-border/40 max-h-36 overflow-y-auto">
            {attachments.map(att => (
              <div
                key={att.id}
                className="flex items-center gap-2 p-1.5 pr-2 rounded-lg bg-surface-deep/80 border border-surface-border text-xs text-content-main shadow-xs"
              >
                {att.isImage && att.dataUrl ? (
                  <img
                    src={att.dataUrl}
                    alt={att.name}
                    onClick={() => setPreview(att.dataUrl || null)}
                    className="w-7 h-7 cursor-zoom-in rounded object-cover flex-shrink-0 border border-surface-border"
                  />
                ) : (
                  <div className="w-7 h-7 rounded bg-sky-500/10 border border-sky-500/20 flex items-center justify-center flex-shrink-0 text-accent">
                    <FileText className="w-4 h-4" />
                  </div>
                )}
                <div className="min-w-0 max-w-[140px]">
                  <div className="text-[11px] font-medium truncate" title={att.name}>
                    {att.name}
                  </div>
                  <div className="text-[9.5px] font-mono text-content-dim">
                    {formatFileSize(att.size)}
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => handleRemoveAttachment(att.id)}
                  className="p-1 rounded text-content-dim hover:text-state-danger hover:bg-rose-500/10 transition ml-0.5"
                  title="Remove attachment"
                >
                  <X className="w-3 h-3" />
                </button>
              </div>
            ))}
          </div>
        )}

        {needsDeepSeekLogin ? (
          <div
            className="flex items-center gap-2 border-b border-sky-500/25 bg-sky-500/10 px-3.5 py-1.5 text-[11px] text-content-muted"
            role="status"
          >
            <Brain className="h-3.5 w-3.5 flex-shrink-0 text-accent" />
            <span className="flex-1">
              Answers come from chat.deepseek.com, driven in this app with your own account. Sign
              in once and it stays signed in.
            </span>
            {onDeepSeekLogin ? (
              <button
                type="button"
                onClick={onDeepSeekLogin}
                className="flex-shrink-0 rounded border border-sky-500/40 bg-sky-500/10 px-2 py-0.5 font-mono text-[10px] text-accent transition-colors hover:bg-sky-500/20"
              >
                Sign in
              </button>
            ) : null}
          </div>
        ) : blockedNotice ? (
          <div
            className={`flex items-center gap-2 border-b border-amber-500/25 bg-amber-500/10 px-3.5 py-1.5 text-[11px] text-state-warning ${
              isNudging ? 'animate-pulse-subtle' : ''
            }`}
            role="status"
          >
            <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0" />
            <span className="flex-1">{blockedNotice}</span>
            {onStartRunner ? (
              <button
                type="button"
                onClick={onStartRunner}
                className="flex-shrink-0 rounded border border-amber-500/40 bg-amber-500/10 px-2 py-0.5 text-[10.5px] text-state-warning transition-colors hover:bg-amber-500/20"
              >
                Start
              </button>
            ) : null}
          </div>
        ) : null}

        {/* Prompts typed during a turn wait here, in order, instead of being refused. */}
        {queuedPrompts && queuedPrompts.length > 0 ? (
          <div className="max-h-28 overflow-y-auto border-b border-surface-border/70" role="status">
            {queuedPrompts.map(item => (
              <div
                key={item.id}
                className="flex items-center gap-2 bg-surface-hover/50 px-3.5 py-1.5 text-[11px] text-content-muted"
              >
                <Clock className="h-3.5 w-3.5 flex-shrink-0 text-accent" />
                <span className="flex-1 truncate" title={item.prompt}>
                  {item.prompt}
                  {item.attachments?.length
                    ? ` · ${item.attachments.length} file${item.attachments.length === 1 ? '' : 's'}`
                    : ''}
                </span>
                <button
                  type="button"
                  onClick={() => {
                    // Back into the composer to be changed, out of the queue so it cannot also fire.
                    setPrompt(item.prompt);
                    setAttachments(item.attachments ?? []);
                    onRemoveQueued?.(item.id);
                    textareaRef.current?.focus();
                  }}
                  title="Edit this queued prompt"
                  className="flex-shrink-0 rounded p-1 text-content-dim transition hover:bg-surface-hover hover:text-content-main"
                >
                  <Pencil className="w-3 h-3" />
                </button>
                <button
                  type="button"
                  onClick={() => onRemoveQueued?.(item.id)}
                  title="Remove this queued prompt"
                  className="flex-shrink-0 rounded p-1 text-content-dim transition hover:bg-rose-500/10 hover:text-state-danger"
                >
                  <X className="w-3 h-3" />
                </button>
              </div>
            ))}
          </div>
        ) : null}

        {/* Input Textarea */}
        <textarea
          ref={textareaRef}
          value={prompt}
          onChange={e => {
            setPrompt(e.target.value);
            onMentionDetect(e.target.value, e.target.selectionStart ?? 0);
            syncPalette(e.target.value, e.target.selectionStart ?? e.target.value.length);
          }}
          onKeyDown={handleKeyDown}
          onKeyUp={e => {
            if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
              syncPalette(e.currentTarget.value, e.currentTarget.selectionStart ?? 0);
            }
          }}
          onClick={e => syncPalette(e.currentTarget.value, e.currentTarget.selectionStart ?? 0)}
          onPaste={handlePaste}
          onDragOver={event => {
            if (event.dataTransfer?.types?.includes('Files')) event.preventDefault();
          }}
          onDrop={event => {
            const files = event.dataTransfer?.files;
            if (!files || files.length === 0) return;
            event.preventDefault();
            processFiles(files);
          }}
          placeholder={
            isExecuting
              ? 'Working... type here to queue a follow-up, Enter sends it when this turn finishes'
              : attachments.length > 0
                ? 'Add instructions for attached files, or press Enter to send...'
                : 'Ask the agent to build, fix, or explain something'
          }
          rows={1}
          className="w-full px-4 pt-3.5 pb-2 bg-transparent text-xs text-content-main placeholder:text-content-dim focus:outline-none resize-none leading-relaxed font-sans max-h-48"
        />

        {/* Action bar inside input */}
        <div className="flex items-center justify-between gap-2 px-3.5 py-2 border-t border-surface-border/50 text-xs">
          {/* Left tools: Attach, Exec, Git, then the model + effort picker */}
          <div className="flex min-w-0 items-center space-x-2">
            <button
              type="button"
              onClick={() => fileInputRef.current?.click()}
              className="p-1 rounded text-content-dim hover:text-accent hover:bg-surface-hover transition flex items-center gap-1"
              title="Attach Images or Files (or drag & drop)"
            >
              <Paperclip className="w-3.5 h-3.5" />
              {attachments.length > 0 && (
                <span className="text-[10px] font-mono font-bold text-accent px-1 py-0.2 rounded-full bg-accent-soft">
                  {attachments.length}
                </span>
              )}
            </button>

            <span className="opacity-30">|</span>

            <button
              type="button"
              onClick={onOpenGitModal}
              className="flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] font-mono text-content-muted hover:text-content-main hover:bg-surface-hover transition"
              title="Inspect Git Changes"
            >
              <GitBranch className="w-3 h-3" />
              <span>Git</span>
            </button>

            <span className="opacity-30">|</span>

            {isVyce ? (
              /* The model list, the picker and the switch all live in the CLI's own menu; the chip
                 drives that menu rather than pretending to own the list. Mode and permissions stay
                 where they are, inside the TUI, so there is no control for them here. */
              <>
                <span
                  className="flex items-center gap-1 rounded px-1.5 py-0.5 font-mono text-[11px] text-emerald-400"
                  title="Answers come from Vyce Code, running in a hidden console"
                >
                  <SquareTerminal className="h-3 w-3" />
                  Vyce
                </span>

                <span className="opacity-30">|</span>

                <VyceModelChip workingDirectory={workingDirectory} />
              </>
            ) : isOpenCode ? (
              <>
                <span
                  className="flex items-center gap-1 rounded px-1.5 py-0.5 font-mono text-[11px] text-amber-400"
                  title="Answers come from the OpenCode CLI, running headless in this workspace"
                >
                  <Terminal className="h-3 w-3" />
                  OpenCode
                </span>

                <span className="opacity-30">|</span>

                <OpenCodeModelChip sessionId={sessionId} model={activeModel} />

                <span className="opacity-30">|</span>

                {/* Full access is what becomes `--auto`; a headless run cannot answer a prompt. */}
                <PermissionModeButton profileName={profileName} />
              </>
            ) : isZai ? (
              <>
                <span
                  className="flex items-center gap-1 rounded px-1.5 py-0.5 font-mono text-[11px] text-accent"
                  title="Answers come from chat.z.ai (GLM). Tools, skills and MCP run through the same protocol the other web engines use."
                >
                  <Sparkles className="h-3 w-3" />
                  Z.ai
                </span>

                <span className="opacity-30">|</span>

                <ZaiModelChip sessionId={sessionId} model={activeModel} />

                <span className="opacity-30">|</span>

                <PermissionModeButton profileName={profileName} engine="zai" />
              </>
            ) : isDeepSeek || isQwen ? (
              /* No model to pick and no permission policy to set: the engine is a web chat, which
                 decides both. Showing Gemini here was simply wrong. */
              <>
                <span
                  className="flex items-center gap-1 rounded px-1.5 py-0.5 font-mono text-[11px] text-accent"
                  title={isQwen ? 'Answers come from chat.qwen.ai' : 'Answers come from chat.deepseek.com, signed in as you'}
                >
                  {isQwen ? <Sparkles className="h-3 w-3" /> : <Brain className="h-3 w-3" />}
                  {isQwen ? 'Qwen' : 'DeepSeek'}
                </span>

                {/* Both chips portal their menu out of the composer, which clips its children. */}
                {isQwen ? (
                  <>
                    <QwenModelChip />
                    <QwenModeChip />
                  </>
                ) : null}

                <span className="opacity-30">|</span>

                {/* Their two composer chips, driven per session. Same mechanism the page uses:
                    click to flip, state read back from the button. */}
                {(isDeepSeek ? [
                  { key: 'deepThink' as const, label: 'DeepThink', on: deepThink !== false },
                  { key: 'webSearch' as const, label: 'Search', on: webSearch === true },
                ] : []).map(chip => (
                  <button
                    key={chip.key}
                    type="button"
                    onClick={() => onDeepSeekOption?.(chip.key, !chip.on)}
                    aria-pressed={chip.on}
                    title={
                      chip.key === 'deepThink'
                        ? 'Show the reasoning trace. Off answers faster.'
                        : 'Let DeepSeek search the web before answering.'
                    }
                    className={`rounded px-1.5 py-0.5 font-mono text-[10.5px] transition-colors ${
                      chip.on
                        ? 'bg-sky-500/15 text-accent'
                        : 'text-content-dim hover:bg-surface-hover hover:text-content-main'
                    }`}
                  >
                    {chip.label}
                  </button>
                ))}

                <span className="opacity-30">|</span>

                {/* Qwen runs the same tool loop as DeepSeek, so the same gate applies: without full
                    access an action that needs approval is refused and the model is told why. */}
                <PermissionModeButton profileName={profileName} engine={isQwen ? 'qwen' : 'deepseek'} />
              </>
            ) : (
              <>
                {/* Stays interactive while a turn runs: the change applies to the next turn, and
                    locking the picker made it look broken during long tool runs. */}
                <ModelSelector
                  sessionId={sessionId}
                  model={activeModel}
                  effort={reasoningEffort}
                  cli={engine ?? activeCli}
                />

                <span className="opacity-30">|</span>

                <PermissionModeButton profileName={profileName} />
              </>
            )}
          </div>

          {/* Right: quota ring, optional token readout, then the single morphing action button */}
          <div className="flex flex-shrink-0 items-center space-x-2.5">
            {isDeepSeek || isQwen || isZai || isVyce || isOpenCode ? null : <CircularQuotaWidget profileName={profileName} />}

            {turnTokens > 0 ? (
              <TokenUsageTooltip turn={lastTurnTokens} session={sessionTokens} turns={turnCount} />
            ) : null}

            {/* While a turn runs the round button is Stop, so this is the way to queue with the mouse. */}
            {isExecuting && canQueue ? (
              <button
                type="button"
                onClick={handleSend}
                aria-label="Queue prompt"
                title="Queue this prompt: it sends a few seconds after the running turn finishes"
                className="flex h-7 w-7 items-center justify-center rounded-full bg-sky-500/20 text-accent transition-colors hover:bg-sky-500/35 active:scale-95"
              >
                <ArrowUp className="h-3.5 w-3.5 stroke-[2.5]" />
              </button>
            ) : null}

            <button
              type="button"
              onClick={isExecuting ? onStop : handleSend}
              disabled={!isExecuting && !canSend}
              aria-label={isExecuting ? 'Stop generating' : 'Send prompt'}
              title={isExecuting ? 'Stop generating (aborts the running task)' : 'Send prompt'}
              className={`relative flex h-7 w-7 items-center justify-center rounded-full transition-all duration-200 ${
                isExecuting
                  ? 'bg-rose-600 text-white hover:bg-rose-500 active:scale-95'
                  : canSend
                    ? 'bg-sky-500 text-white hover:bg-sky-400 active:scale-95'
                    : 'cursor-not-allowed bg-sky-600/40 text-white/50'
              }`}
            >
              {/* Progress ring, only while a turn is in flight. */}
              {isExecuting ? (
                <span
                  aria-hidden="true"
                  className="action-ring absolute inset-0 rounded-full border-2 border-transparent border-t-white/80 border-r-white/30"
                />
              ) : null}

              {isExecuting ? (
                <Square className="h-2.5 w-2.5 fill-current" />
              ) : (
                <ArrowUp className="h-3.5 w-3.5 stroke-[2.5]" />
              )}
            </button>
          </div>
        </div>
        </div>
      </div>

    </div>
  );
};
