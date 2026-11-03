/**
 * Local Transport & OS-Agnostic Networking
 * Embedded HTTP & WebSocket Server using Bun.serve with pre-shared token authentication and signal handling.
 */

import { Server, ServerWebSocket } from 'bun';
import { InboundMessage, OutboundEvent } from '../types/protocol.ts';

export interface TransportOptions {
  port: number;
  authToken: string;
  onMessage: (msg: InboundMessage, ws: ServerWebSocket<ClientData>) => void;
  onShutdown: () => void;
}

export interface ClientData {
  authenticated: boolean;
  connectedAt: number;
}

/** Constant-time comparison so a local client cannot learn the token by timing responses. */
function safeEqual(candidate: string, expected: string): boolean {
  if (candidate.length !== expected.length) return false;
  let diff = 0;
  for (let i = 0; i < expected.length; i++) {
    diff |= candidate.charCodeAt(i) ^ expected.charCodeAt(i);
  }
  return diff === 0;
}

export class SidecarTransport {
  private server: Server<ClientData> | null = null;
  private authenticatedSockets = new Set<ServerWebSocket<ClientData>>();
  private options: TransportOptions;

  constructor(options: TransportOptions) {
    this.options = options;
  }

  public start(): number {
    const { port, authToken, onMessage, onShutdown } = this.options;

    this.server = Bun.serve<ClientData>({
      hostname: '127.0.0.1',
      port: port, // port 0 assigns dynamic port from OS
      fetch: (req, server) => {
        const url = new URL(req.url);

        // HTTP Health Check
        if (url.pathname === '/health') {
          return new Response(JSON.stringify({
            status: 'ok',
            pid: process.pid,
            uptimeSec: Math.round(process.uptime()),
            platform: process.platform,
            arch: process.arch
          }), {
            headers: { 'Content-Type': 'application/json' }
          });
        }

        // WebSocket Upgrade verification
        if (url.pathname === '/ws') {
          const queryToken = url.searchParams.get('token');
          const authHeader = req.headers.get('Authorization');
          const headerToken = authHeader?.replace(/^Bearer\s+/i, '');

          // Fail closed: a sidecar started without a token is not reachable by anyone, instead of
          // being reachable by everyone.
          const tokenMatch = authToken.length > 0 &&
                             ((typeof queryToken === 'string' && safeEqual(queryToken, authToken)) ||
                              (typeof headerToken === 'string' && safeEqual(headerToken, authToken)));

          if (!tokenMatch) {
            return new Response('Unauthorized: Invalid sidecar security token', { status: 401 });
          }

          const upgraded = server.upgrade(req, {
            data: {
              authenticated: true,
              connectedAt: Date.now()
            }
          });

          if (upgraded) return undefined;
          return new Response('WebSocket upgrade failed', { status: 400 });
        }

        return new Response('AgentsHub Sidecar Engine active', { status: 200 });
      },
      websocket: {
        open: (ws) => {
          if (ws.data.authenticated) {
            this.authenticatedSockets.add(ws);
            this.sendTo(ws, {
              event: 'server_ready',
              port: this.server?.port || 0,
              pid: process.pid,
              platform: process.platform as any,
              arch: process.arch,
              version: '2.0.0',
              timestamp: new Date().toISOString()
            });
          }
        },
        message: (ws, message) => {
          if (!ws.data.authenticated) {
            ws.close(1008, 'Unauthorized');
            return;
          }

          try {
            const raw = typeof message === 'string' ? message : new TextDecoder().decode(message);
            const parsed = JSON.parse(raw) as InboundMessage;
            onMessage(parsed, ws);
          } catch (err: any) {
            this.sendTo(ws, {
              event: 'error',
              code: 'INVALID_JSON',
              message: err?.message || 'Failed to parse JSON message',
              timestamp: new Date().toISOString()
            });
          }
        },
        close: (ws) => {
          this.authenticatedSockets.delete(ws);
        }
      }
    });

    this.registerSignalHandlers(onShutdown);
    return this.server.port ?? this.options.port;
  }

  public broadcast(event: OutboundEvent): void {
    const payload = JSON.stringify(event);
    for (const ws of this.authenticatedSockets) {
      try {
        ws.send(payload);
      } catch {}
    }
  }

  public sendTo(ws: ServerWebSocket<ClientData>, event: OutboundEvent): void {
    try {
      ws.send(JSON.stringify(event));
    } catch {}
  }

  public getPort(): number {
    return this.server?.port || 0;
  }

  public stop(): void {
    for (const ws of this.authenticatedSockets) {
      try {
        ws.close(1000, 'Server stopping');
      } catch {}
    }
    this.authenticatedSockets.clear();

    if (this.server) {
      this.server.stop(true);
      this.server = null;
    }
  }

  private registerSignalHandlers(onShutdown: () => void): void {
    const handleExit = () => {
      onShutdown();
      this.stop();
      process.exit(0);
    };

    process.on('SIGINT', handleExit);
    process.on('SIGTERM', handleExit);

    // Parent process death detection via stdin EOF
    process.stdin.on('end', handleExit);
    process.stdin.on('close', handleExit);
    process.stdin.resume(); // keep reading stdin for EOF signal
  }
}
