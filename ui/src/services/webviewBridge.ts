/**
 * Modern WebView2 Bidirectional IPC Bridge Service
 * Handles Promise-based request/response correlation and real-time backend event dispatching.
 */

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: any): void;
        addEventListener(type: string, listener: (event: any) => void): void;
        removeEventListener(type: string, listener: (event: any) => void): void;
      };
    };
    callBridge?: (action: string, data?: any) => Promise<any>;
  }
}

type EventListener = (payload: any) => void;

class WebViewBridge {
  private pendingRequests = new Map<string, { resolve: (val: any) => void; reject: (err: any) => void; timer: any }>();
  private listeners = new Map<string, Set<EventListener>>();
  private wildcardListeners = new Set<(event: string, payload: any) => void>();
  private isInitialized = false;

  constructor() {
    this.init();
  }

  public get isAvailable(): boolean {
    return typeof window !== 'undefined' && !!window.chrome?.webview;
  }

  private init(): void {
    if (this.isInitialized || typeof window === 'undefined') return;
    this.isInitialized = true;

    // Attach global legacy compatibility helper
    window.callBridge = (action: string, data?: any) => this.invoke(action, data);

    if (window.chrome?.webview) {
      window.chrome.webview.addEventListener('message', (event: any) => {
        this.handleIncomingRaw(event.data);
      });
    } else {
      // Browser dev host only. Same-origin check matters here: without it any framing page can
      // post a fake response or event into the UI.
      window.addEventListener('message', (event: MessageEvent) => {
        if (event.origin !== window.location.origin) return;
        if (event.data && typeof event.data === 'object' && (event.data.event || event.data.responseId)) {
          this.handleIncomingRaw(event.data);
        }
      });
    }
  }

  private handleIncomingRaw(data: any): void {
    let parsed = data;
    if (typeof data === 'string') {
      try {
        parsed = JSON.parse(data);
      } catch {
        return;
      }
    }
    if (!parsed || typeof parsed !== 'object') return;

    // 1. Correlate Request/Response
    const responseId = parsed.responseId || parsed.requestId;
    if (responseId && this.pendingRequests.has(responseId)) {
      const pending = this.pendingRequests.get(responseId)!;
      this.pendingRequests.delete(responseId);
      clearTimeout(pending.timer);

      if (parsed.success === false) {
        pending.reject(new Error(parsed.error || parsed.data || 'Action failed'));
      } else {
        pending.resolve(parsed.data);
      }
      return;
    }

    // 2. Dispatch Backend Events
    const eventName = parsed.event || parsed.type;
    const payload = parsed.payload !== undefined ? parsed.payload : parsed.data;

    if (eventName) {
      this.dispatchEvent(eventName, payload);
    }
  }

  public dispatchEvent(eventName: string, payload: any): void {
    const handlers = this.listeners.get(eventName);
    if (handlers) {
      for (const handler of handlers) {
        try {
          handler(payload);
        } catch (err) {
          console.error(`[WebViewBridge] Error in handler for event "${eventName}":`, err);
        }
      }
    }

    for (const wild of this.wildcardListeners) {
      try {
        wild(eventName, payload);
      } catch (err) {
        console.error(`[WebViewBridge] Error in wildcard handler for "${eventName}":`, err);
      }
    }
  }

  /**
   * Calls a C# backend action and awaits the correlated response.
   */
  public invoke<T = any>(action: string, payload: Record<string, any> = {}, timeoutMs = 25000): Promise<T> {
    return new Promise((resolve, reject) => {
      const requestId = `req_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;

      const timer = setTimeout(() => {
        if (this.pendingRequests.has(requestId)) {
          this.pendingRequests.delete(requestId);
          reject(new Error(`[WebViewBridge] Request timeout after ${timeoutMs}ms for action: ${action}`));
        }
      }, timeoutMs);

      this.pendingRequests.set(requestId, { resolve, reject, timer });

      const envelope = {
        action,
        requestId,
        ...payload,
      };

      if (window.chrome?.webview) {
        try {
          window.chrome.webview.postMessage(envelope);
        } catch (err) {
          clearTimeout(timer);
          this.pendingRequests.delete(requestId);
          reject(err);
        }
      } else if (import.meta.env.DEV) {
        // Layout work in a plain browser can still render, but this path never reaches a release
        // build: a LAN viewer must not be shown invented ports, PIDs or file lists.
        setTimeout(() => {
          this.mockDevResponse(action, payload, requestId);
        }, 50);
      } else {
        clearTimeout(timer);
        this.pendingRequests.delete(requestId);
        reject(new Error('This action needs the Agents Hub desktop app.'));
      }
    });
  }

  private mockDevResponse(action: string, payload: Record<string, any>, requestId: string): void {
    let mockData: any = { success: true };

    if (action === 'get_initial_state') {
      mockData = {
        version: 'v2.0.0 (Dev Host)',
        lanIp: '127.0.0.1',
        lanUrl: 'http://127.0.0.1:9095/',
        lanProxyPort: 9191,
        lanProxyRunning: true,
        runningPorts: [
          { name: 'Hub CLI', port: 9191, protocol: 'http', status: 'Running' },
          { name: 'Bun Sidecar', port: 8089, protocol: 'ws', status: 'Running' },
          { name: 'Antigravity VirtualHome', port: 9339, protocol: 'http', status: 'Running' },
        ],
        clis: [
          { name: 'Hub', type: 'Hub', state: 'Running', port: 9191, pid: 4892 },
          { name: 'Claude Code', type: 'ClaudeCode', state: 'Stopped', port: 9229, pid: 0 },
          { name: 'Antigravity CLI', type: 'AntigravityCli', state: 'Running', port: 9339, pid: 5120 },
        ],
        antigravity: {
          activeProfile: 'Work',
          profiles: [
            { name: 'Work', active: true, isRunning: false, provider: 'Google Antigravity' },
            { name: 'Personal', active: false, isRunning: false, provider: 'Google Antigravity' },
          ],
          model: 'Gemini 3.8 Flash (High)',
        },
        workspaces: [
          {
            id: 'ws_default',
            name: 'ChessSenseV2',
            workingDirectory: 'C:\\Projects\\ChessSenseV2',
            accentColor: '#38BDF8',
            antigravityProfile: 'Work',
            primaryCli: 'AntigravityCli',
            secondaryCli: 'none',
            splitMode: 'single',
            splitRatio: '0.5',
          },
          {
            id: 'ws_agentshub',
            name: 'AgentsHub.v2',
            workingDirectory: 'c:\\Users\\Gaming\\Desktop\\Folders-Z\\userscript\\WebAPPS\\APPS\\AgentsHub.v2',
            accentColor: '#10B981',
            antigravityProfile: 'Work',
            primaryCli: 'Hub',
            secondaryCli: 'ClaudeCode',
            splitMode: 'horizontal',
            splitRatio: '0.6',
          },
        ],
        activeWorkspaceId: 'ws_default',
        settings: {
          theme: 'dark',
          accentColor: '#38BDF8',
          notifications: true,
        },
      };
    } else if (action === 'get_model_catalog') {
      // Mirrors the real `agy models` output so the browser dev host can render the selector.
      mockData = {
        models: [
          {
            id: 'gemini-3.8-flash',
            label: 'Gemini 3.8 Flash',
            provider: 'Gemini',
            tiers: {
              high: 'gemini-3.8-flash-high',
              medium: 'gemini-3.8-flash-medium',
              low: 'gemini-3.8-flash-low',
            },
          },
          { id: 'claude-sonnet-4-6', label: 'Claude Sonnet 4.6 (Thinking)', provider: 'Claude', tiers: {} },
          { id: 'claude-opus-4-6-thinking', label: 'Claude Opus 4.6 (Thinking)', provider: 'Claude', tiers: {} },
          {
            id: 'gpt-oss-120b',
            label: 'GPT-OSS 120B',
            provider: 'GPT-OSS',
            tiers: { medium: 'gpt-oss-120b-medium' },
          },
        ],
        efforts: ['low', 'medium', 'high'],
        source: 'cli',
      };
    } else if (action === 'get_sidecar_status') {
      mockData = {
        status: 'Online',
        port: 8089,
        isOnline: true,
        pid: 3412,
        latencyMs: 1,
        activeSessions: 1,
        version: 'v2.0.0 (Bun Engine)',
      };
    } else if (action === 'get_system_telemetry') {
      mockData = {
        ramMb: 16276,
        totalRamMb: 32768,
        netKbps: 6,
        netMbps: 0.05,
        memoryUsedGb: 15.9,
        memoryTotalGb: 32.0,
        hubMemMb: 142,
      };
    } else if (action === 'get_agent_sessions') {
      mockData = {
        sessions: [
          {
            sessionId: 'session_chess_1',
            goal: 'what is the .py files we have in this project?',
            profile: 'Work',
            stepCount: 4,
            lastActiveUtc: new Date().toISOString(),
          },
          {
            sessionId: 'session_chess_2',
            goal: 'Setup fast chess move evaluator benchmark',
            profile: 'Work',
            stepCount: 12,
            lastActiveUtc: new Date(Date.now() - 3600000 * 2).toISOString(),
          },
          {
            sessionId: 'session_chess_3',
            goal: 'Fix pawn promotion en passant logic regression',
            profile: 'Personal',
            stepCount: 8,
            lastActiveUtc: new Date(Date.now() - 86400000 * 2).toISOString(),
          },
        ],
      };
    } else if (action === 'get_workspace_files') {
      mockData = {
        branch: 'main',
        rootPath: 'C:\\Projects\\ChessSenseV2',
        files: [
          { name: 'app.py', path: 'app.py', isDirectory: false, extension: '.py' },
          { name: 'gui_app.py', path: 'gui_app.py', isDirectory: false, extension: '.py' },
          { name: 'hello.py', path: 'hello.py', isDirectory: false, extension: '.py' },
          { name: 'ht.py', path: 'ht.py', isDirectory: false, extension: '.py' },
          { name: 'main.py', path: 'main.py', isDirectory: false, extension: '.py' },
          { name: 'contrast-check.py', path: '.agents/skills/antislop-human/contrast-check.py', isDirectory: false, extension: '.py' },
          { name: 'contrast-mcp.py', path: '.gemini/skills/antislop-human/contrast-mcp.py', isDirectory: false, extension: '.py' },
        ],
      };
    }

    this.handleIncomingRaw({
      responseId: requestId,
      success: true,
      data: mockData,
    });
  }

  /**
   * Registers a listener for an incoming backend event. Returns unsubscribe function.
   */
  public on(event: string, callback: EventListener): () => void {
    if (!this.listeners.has(event)) {
      this.listeners.set(event, new Set());
    }
    this.listeners.get(event)!.add(callback);

    return () => {
      const set = this.listeners.get(event);
      if (set) {
        set.delete(callback);
        if (set.size === 0) {
          this.listeners.delete(event);
        }
      }
    };
  }

  /**
   * Registers a listener for any event.
   */
  public onAny(callback: (event: string, payload: any) => void): () => void {
    this.wildcardListeners.add(callback);
    return () => {
      this.wildcardListeners.delete(callback);
    };
  }
}

export const webviewBridge = new WebViewBridge();
