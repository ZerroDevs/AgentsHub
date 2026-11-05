import { createPortal } from 'react-dom';
import React, { useState, useRef, useEffect, useMemo } from 'react';
import { AlertCircle, AlertTriangle, ArrowUp, Brain, Clock, Cpu, FileCode, Folder, KeyRound, Paperclip, Pencil, Quote, Sparkles, Square, SquareTerminal, Terminal, GitBranch, X, FileText } from 'lucide-react';
import { ChatAttachment, QueuedPrompt, SlashCommand, TokenUsage } from '../../types/agent';
import { useSlashCommandStore } from '../../store/useSlashCommandStore';
import { rankByQuery } from '../../lib/fuzzy';
import { SlashCommandPalette } from './SlashCommandPalette';
import { ModelSelector } from './ModelSelector';
import { QwenModeChip, QwenModelChip } from './QwenPickers';
import { useSessionStore } from '../../store/useSessionStore';
import { webviewBridge } from '../../services/webviewBridge';
import { uploadAttachment } from '../../services/attachmentUpload';
import { ImageLightbox } from './ImageLightbox';
import { PermissionModeButton } from './PermissionModeButton';
import { VyceModelChip } from './VyceModelChip';
import { OpenCodeModelChip } from './OpenCodeModelChip';
import { ZaiModelChip } from './ZaiModelChip';
import { ByokModelChip } from './ByokModelChip';
import { BYOK_PROVIDER_CHANGED } from './ByokProviderSwitcher';
import { TokenUsageTooltip } from './TokenUsageTooltip';
import { CircularQuotaWidget } from './CircularQuotaWidget';
import { LocalModelsModal } from '../modals/LocalModelsModal';

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
  engine?: 'agy' | 'deepseek' | 'qwen' | 'zai' | 'vyce' | 'opencode' | 'byok';
  /** Null until the host has answered; drives the sign-in bar for a DeepSeek session. */
  deepSeekSignedIn?: boolean | null;
  /** Opens the one-time DeepSeek sign-in window. */
  onDeepSeekLogin?: () => void;
  /** Antigravity sign-in state, null while the host has not answered yet. */
  antigravitySignIn?: { signedIn: boolean | null; profile: string; url: string | null } | null;
  /** Starts the Antigravity sign-in prompt and reports its URL. */
  onAntigravitySignIn?: () => void;
  /** Opens the URL the prompt printed, in the system browser. */
  onOpenAntigravitySignInPage?: (url: string) => void;
  /**
   * Hands a pasted authorization code to the waiting prompt. Resolves with what the host made of
   * it, so the box can say "expired, finish the new link" instead of swallowing the paste.
   */
  onAntigravitySubmitCode?: (code: string) => Promise<{ signedIn: boolean; error?: string | null }>;
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

/** One decimal below ten: a slow local model's difference between 1.2 and 1.9 matters. */
const formatRate = (value: number): string => (value < 10 ? value.toFixed(1) : value.toFixed(0));

export const ChatInput: React.FC<ChatInputProps> = ({
  onSendMessage,
  onStop,
  isExecuting = false,
  activeModel,
  reasoningEffort,
  engine,
  deepSeekSignedIn,
  onDeepSeekLogin,
  antigravitySignIn,
  onAntigravitySignIn,
  onOpenAntigravitySignInPage,
  onAntigravitySubmitCode,
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
  /** A file the composer refused, said out loud instead of dropped on the floor. */
  const [attachNotice, setAttachNotice] = useState<string | null>(null);
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

  /** Past this the upload would be a few minutes of slices; refusing is kinder than hanging. */
  const MAX_ATTACHMENT_BYTES = 64 * 1024 * 1024;

  /** A thumbnail is a preview, not a copy: only small images keep one. */
  const IMAGE_PREVIEW_BYTES = 2 * 1024 * 1024;

  const processFiles = (files: FileList | File[]) => {
    const localVisionMissing = byokIsLocal && localServer !== null && localServer.visionReady === false;

    Array.from(files).forEach(file => {
      if (file.size > MAX_ATTACHMENT_BYTES) {
        setAttachNotice(`${file.name} is ${formatFileSize(file.size)}; attachments are capped at 64 MB.`);
        return;
      }

      if (file.size === 0) {
        setAttachNotice(`${file.name} is empty, so there is nothing to send.`);
        return;
      }

      const isImage = file.type.startsWith('image/');

      // An image on a projector-less local model is refused before the turn rather than after it.
      if (isImage && localVisionMissing) {
        setAttachNotice(
          'The local model that is loaded has no vision projector, so it cannot read an image. ' +
            'Add the mmproj file in Local models → Flags, load it again, or attach a text file instead.'
        );
        return;
      }

      const id = `att_${Date.now()}_${Math.random().toString(36).slice(2, 6)}`;
      const attachment: ChatAttachment = {
        id,
        name: file.name,
        size: file.size,
        type: file.type || 'application/octet-stream',
        isImage,
        uploading: true,
      };

      setAttachments(prev => [...prev, attachment]);

      // Only a small image is read here, and only for its thumbnail; everything else goes to the
      // host in slices, so no large file is ever held as one string in the page.
      if (isImage && file.size <= IMAGE_PREVIEW_BYTES) {
        const reader = new FileReader();
        reader.onload = event => {
          const dataUrl = event.target?.result as string;
          if (!dataUrl) return;

          setAttachments(prev => prev.map(item => (item.id === id ? { ...item, dataUrl } : item)));
        };
        reader.readAsDataURL(file);
      }

      void uploadAttachment(file)
        .then(path =>
          setAttachments(prev => prev.map(item => (item.id === id ? { ...item, path, uploading: false } : item)))
        )
        .catch((error: unknown) => {
          setAttachments(prev => prev.filter(item => item.id !== id));
          setAttachNotice(
            `${file.name} could not be attached: ${error instanceof Error ? error.message : 'the upload failed.'}`
          );
        });
    });
  };

  /** Asks a web engine's page whether it really exposes a file input, instead of guessing. */
  const checkUpload = async () => {
    try {
      const res: any = await webviewBridge.invoke('web_upload_probe', { engine });
      setAttachNotice(res?.detail ?? 'The page did not answer.');
    } catch (error: unknown) {
      setAttachNotice(error instanceof Error ? error.message : 'The upload check could not run.');
    }
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

    // A path that does not exist yet cannot be handed to an engine.
    if (attachments.some(attachment => attachment.uploading)) {
      setAttachNotice('An attachment is still uploading. Send again in a moment.');
      return;
    }

    // An attachment restored from storage has a name and no file behind it. Sending would claim a
    // file the engine never receives, so it is named instead.
    if (attachments.some(attachment => !attachment.path)) {
      setAttachNotice(
        'That attachment was saved before a reload and has no file behind it. Remove it and attach the file again.'
      );
      return;
    }

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
  /** Bring-your-own-key runs the Claude Code CLI against the pasted provider. */
  const isByok = engine === 'byok';
  /** Vyce is the Vyce Code TUI, driven in a hidden console: no model picker, no policy switch. */
  const isVyce = engine === 'vyce';

  /**
   * Whether this BYOK chat runs on the model on this machine rather than a pasted key.
   *
   * Both are the Claude Code CLI on an OpenAI-shaped endpoint, so the engine is the same, but the
   * row said "Your key" over a model the person downloaded, which describes the wrong thing.
   */
  const [byokIsLocal, setByokIsLocal] = useState(false);
  const [localServer, setLocalServer] = useState<{
    running: boolean;
    installed: boolean;
    runOnRequest: boolean;
    /** False when the loaded model was started without its mmproj, so images cannot be read. */
    visionReady?: boolean;
  } | null>(null);
  const [localModelsOpen, setLocalModelsOpen] = useState(false);
  /**
   * How fast the local model answered: the peak and the average llama.cpp reported for the reply.
   *
   * Both come from the server's own timing lines, so neither is inferred from the text arriving.
   */
  const [localRate, setLocalRate] = useState<{
    tps: number;
    avg: number;
    tokens: number;
    streaming: boolean;
    /** Always true now: the readout only ever shows a number llama.cpp measured. */
    measured: boolean;
  } | null>(null);

  useEffect(() => {
    if (!isByok) {
      setByokIsLocal(false);
      setLocalServer(null);
      setLocalRate(null);
      return;
    }

    let alive = true;
    const read = () => {
      void webviewBridge
        .invoke('byok_models')
        .then((res: any) => {
          if (!alive) return;

          const isLocal = (res?.providerId ?? '') === 'local_llamacpp';
          setByokIsLocal(isLocal);
          setLocalServer(isLocal ? (res?.local ?? null) : null);

          const tps = Number(res?.local?.tokensPerSecond ?? 0);
          const avg = Number(res?.local?.averageTokensPerSecond ?? 0);
          setLocalRate(isLocal && tps > 0
            ? { tps, avg, tokens: Number(res?.local?.generatedTokens ?? 0), streaming: false, measured: true }
            : null);
        })
        .catch(() => {});
    };

    read();
    window.addEventListener(BYOK_PROVIDER_CHANGED, read);

    // A server started by a send or by Settings changes what this bar should say.
    const offServer = webviewBridge.on('local_server_changed', read);

    // Two sources feed this: the app's own count while text streams, then the server's measured
    // timing line. The measured one carries no average, so the turn's average survives it.
    const offRate = webviewBridge.on('local_model_rate', (payload: any) => {
      const tps = Number(payload?.tokensPerSecond ?? 0);
      const avg = Number(payload?.averageTokensPerSecond ?? 0);
      const tokens = Number(payload?.generatedTokens ?? 0);
      const streaming = payload?.streaming === true;
      const measured = payload?.estimated !== true;

      setLocalRate(prev => {
        if (tps <= 0 && avg <= 0) return prev;

        return {
          tps: tps > 0 ? tps : prev?.tps ?? 0,
          avg: avg > 0 ? avg : prev?.avg ?? 0,
          tokens: tokens > 0 ? tokens : prev?.tokens ?? 0,
          streaming,
          measured,
        };
      });
    });

    return () => {
      alive = false;
      window.removeEventListener(BYOK_PROVIDER_CHANGED, read);
      offServer?.();
      offRate?.();
    };
  }, [isByok]);

  /**
   * What this chat cannot do yet. A local model is a file someone has to load, so the composer says
   * so instead of sending a turn at a port with nothing behind it.
   */
  const localNotice = !byokIsLocal
    ? null
    : localServer && !localServer.installed
      ? 'llama.cpp is not installed. Add it in Settings → AI Engines & Runtimes.'        : localServer && !localServer.running
        ? localServer.runOnRequest
          ? 'The local server is off. Your first message starts it with the first model on the shelf.'
          : 'Local models do not start on their own. Open Local models and press Load.'
        : null;
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
  /**
   * Antigravity only shows the block once the host has confirmed there is no token. An unset engine
   * is agy: that is the default the dispatcher sends, so `engine === 'agy'` alone missed every
   * ordinary session and the sign-in block never appeared where it was needed.
   */
  const needsAntigravitySignIn =
    (engine === 'agy' || engine === undefined) && antigravitySignIn?.signedIn === false;
  /** The pasted code, kept out of the prompt so it cannot be submitted as a chat message. */
  const [authCode, setAuthCode] = useState('');
  const [authCodeError, setAuthCodeError] = useState<string | null>(null);
  const [authCodeBusy, setAuthCodeBusy] = useState(false);

  const submitAuthCode = async () => {
    const code = authCode.trim();
    if (!code || !onAntigravitySubmitCode || authCodeBusy) return;

    setAuthCodeBusy(true);
    setAuthCodeError(null);
    try {
      const result = await onAntigravitySubmitCode(code);
      if (result.signedIn) {
        setAuthCode('');
      } else {
        setAuthCodeError(result.error || 'That code was not accepted.');
      }
    } catch (err) {
      setAuthCodeError(err instanceof Error ? err.message : 'The code could not be checked.');
    } finally {
      setAuthCodeBusy(false);
    }
  };

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

      {localModelsOpen ? (
        <LocalModelsModal
          onClose={() => {
            setLocalModelsOpen(false);
            // Loading a model happens inside the dialog, so this is where the bar learns to go away.
            window.dispatchEvent(new CustomEvent(BYOK_PROVIDER_CHANGED));
          }}
        />
      ) : null}

      {localNotice ? (
        <div className="mb-2 flex w-full items-center gap-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-1.5 text-[11px] text-state-warning">
          <Cpu className="h-3.5 w-3.5 flex-shrink-0" />
          <span className="min-w-0 flex-1">{localNotice}</span>
          {localServer?.installed ? (
            <button
              type="button"
              onClick={() => setLocalModelsOpen(true)}
              className="flex-shrink-0 rounded border border-amber-500/40 px-1.5 py-0.5 font-mono text-[10px] transition-colors hover:bg-amber-500/15"
            >
              Local models
            </button>
          ) : null}
        </div>
      ) : null}

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

        {attachNotice ? (
          <div className="flex items-center gap-2 border-b border-amber-500/25 bg-amber-500/10 px-3.5 py-1.5 text-[11px] text-state-warning" role="status">
            <AlertCircle className="h-3.5 w-3.5 flex-shrink-0" />
            <span className="flex-1">{attachNotice}</span>
            <button
              type="button"
              onClick={() => setAttachNotice(null)}
              className="text-content-dim hover:text-content-main"
              aria-label="Dismiss"
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
                    {att.uploading ? 'uploading…' : formatFileSize(att.size)}
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

        {needsAntigravitySignIn ? (
          <div
            className="mb-2 w-full rounded-lg border border-sky-500/30 bg-sky-500/10 px-3.5 py-2.5 text-[11px]"
            role="status"
          >
            <div className="flex items-center gap-2">
              <KeyRound className="h-3.5 w-3.5 flex-shrink-0 text-accent" aria-hidden="true" />
              <span className="flex-1 text-content-main">
                Antigravity is not signed in for{' '}
                <span className="font-mono">{antigravitySignIn?.profile}</span>.
              </span>
              {onAntigravitySignIn ? (
                <button
                  type="button"
                  onClick={onAntigravitySignIn}
                  className="flex-shrink-0 rounded border border-sky-500/40 bg-sky-500/10 px-2 py-0.5 font-mono text-[10px] text-accent transition-colors hover:bg-sky-500/20"
                >
                  {antigravitySignIn?.url ? 'Restart sign-in' : 'Sign in'}
                </button>
              ) : null}
            </div>
            <ol className="mt-1.5 list-decimal space-y-0.5 pl-4 font-mono text-[10.5px] leading-relaxed text-content-muted">
              <li>Press Sign in, then finish the Google prompt in the browser it opens.</li>
              <li>Copy the long code that page shows.</li>
              <li>Paste the code into this box and send it. That is the whole step.</li>
            </ol>
            {antigravitySignIn?.url ? (
              <button
                type="button"
                onClick={() => onOpenAntigravitySignInPage?.(antigravitySignIn.url as string)}
                className="mt-1.5 font-mono text-[10px] text-accent underline underline-offset-2 transition-colors hover:text-content-main"
              >
                Open the sign-in page
              </button>
            ) : null}

            {/* The code goes here, not through the prompt box: the turn path has to shape-match a
                pasted code, and a near miss used to reach the model as an ordinary message. */}
            {onAntigravitySubmitCode ? (
              <div className="mt-2 flex flex-col gap-1.5">
                <div className="flex items-center gap-1.5">
                  <input
                    type="text"
                    value={authCode}
                    onChange={event => {
                      setAuthCode(event.target.value);
                      setAuthCodeError(null);
                    }}
                    onKeyDown={event => {
                      if (event.key === 'Enter') {
                        event.preventDefault();
                        void submitAuthCode();
                      }
                    }}
                    placeholder="Paste the authorization code here"
                    spellCheck={false}
                    autoComplete="off"
                    aria-label="Antigravity authorization code"
                    className="min-w-0 flex-1 rounded border border-surface-border bg-surface-card px-2 py-1 font-mono text-[10.5px] text-content-main placeholder:text-content-dim focus:border-sky-500 focus:outline-none"
                  />
                  <button
                    type="button"
                    onClick={() => void submitAuthCode()}
                    disabled={!authCode.trim() || authCodeBusy}
                    className="flex-shrink-0 rounded border border-sky-500/40 bg-sky-500/10 px-2 py-1 font-mono text-[10px] text-accent transition-colors hover:bg-sky-500/20 disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    {authCodeBusy ? 'Checking…' : 'Submit code'}
                  </button>
                </div>
                {authCodeError ? (
                  <p className="font-mono text-[10px] leading-relaxed text-state-warning" role="alert">
                    {authCodeError}
                  </p>
                ) : null}
              </div>
            ) : null}
          </div>
        ) : needsDeepSeekLogin ? (
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

            {(engine === 'qwen' || engine === 'zai') ? (
              <button
                type="button"
                onClick={() => void checkUpload()}
                className="rounded px-1 py-0.5 text-[10px] text-content-dim transition hover:bg-surface-hover hover:text-content-main"
                title="Ask this page whether it exposes a file input, so a failed attachment is diagnosable"
              >
                check upload
              </button>
            ) : null}

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
            ) : isByok ? (
              <>
                <span
                  className="flex items-center gap-1 rounded px-1.5 py-0.5 font-mono text-[11px] text-amber-400"
                  title={
                    byokIsLocal
                      ? 'Answers come from llama.cpp, running the model on this machine'
                      : 'Answers come from the Claude Code CLI, on the key and base URL you added'
                  }
                >
                  {byokIsLocal ? <Cpu className="h-3 w-3" /> : <KeyRound className="h-3 w-3" />}
                  {byokIsLocal ? 'Local' : 'Your key'}
                </span>

                <span className="opacity-30">|</span>

                <ByokModelChip sessionId={sessionId} model={activeModel} />

                <span className="opacity-30">|</span>

                {/* The CLI needs this flag before it may touch a file without asking. */}
                <PermissionModeButton profileName={profileName} engine="byok" />
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
            {/* The ring is an Antigravity profile's Gemini allowance. Any engine that answers from
                somewhere else has no such number, and showing one invented a quota for the wrong
                account. */}
            {isDeepSeek || isQwen || isZai || isVyce || isOpenCode || isByok ? null : (
              <CircularQuotaWidget profileName={profileName} />
            )}

            {byokIsLocal && localRate ? (
              <>
                <span
                  title={`llama.cpp measured this for its last reply${
                    localRate.tokens > 0 ? ` (${localRate.tokens} tokens generated)` : ''
                  }.`}
                  className="flex cursor-help items-baseline rounded font-mono text-[10.5px] tabular-nums text-content-dim"
                >
                  {formatRate(localRate.tps)} t/s
                </span>

                {localRate.avg > 0 ? (
                  <span
                    title="llama.cpp's own average for that reply: every generated token divided by the generation time. Lower than the peak because the first tokens are slower; prompt reading is not counted in either."
                    className="flex cursor-help items-baseline rounded font-mono text-[10.5px] tabular-nums text-content-dim"
                  >
                    avg {formatRate(localRate.avg)}
                  </span>
                ) : null}
              </>
            ) : null}

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
