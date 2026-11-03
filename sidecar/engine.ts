/**
 * AgentsHub Cross-Platform Sidecar Engine
 * Entrypoint: CLI parsing, runtime initialization, and lifecycle coordination.
 */

import { TaskQueue } from './queue/taskQueue.ts';
import { PtyRunner } from './tools/ptyRunner.ts';
import { SessionManager } from './session/sessionManager.ts';
import { MessageRouter } from './server/router.ts';
import { SidecarTransport } from './server/transport.ts';
import { OutboundEvent } from './types/protocol.ts';

function parseArgs(): { port: number; token: string; headless: boolean } {
  let port = 0; // Default to dynamic OS port assignment
  let token = '';
  let headless = false;

  const args = process.argv.slice(2);
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--port' && i + 1 < args.length) {
      port = parseInt(args[++i], 10);
    } else if (arg.startsWith('--port=')) {
      port = parseInt(arg.split('=')[1], 10);
    } else if (arg === '--token' && i + 1 < args.length) {
      token = args[++i];
    } else if (arg.startsWith('--token=')) {
      token = arg.split('=')[1];
    } else if (arg === '--headless') {
      headless = true;
    }
  }

  // Fallback to environment variables if not passed via CLI
  if (!token && process.env.AGENTS_SIDECAR_TOKEN) {
    token = process.env.AGENTS_SIDECAR_TOKEN;
  }
  if (port === 0 && process.env.AGENTS_SIDECAR_PORT) {
    port = parseInt(process.env.AGENTS_SIDECAR_PORT, 10);
  }

  return { port, token, headless };
}

async function main() {
  const { port, token } = parseArgs();

  const ptyRunner = new PtyRunner();

  let transport: SidecarTransport;
  let boundPort = 0;

  const handleOutbound = (event: OutboundEvent) => {
    transport?.broadcast(event);
  };

  const taskQueue = new TaskQueue(4, handleOutbound);
  const sessionManager = new SessionManager(taskQueue, handleOutbound);
  const router = new MessageRouter(taskQueue, ptyRunner, sessionManager, handleOutbound, () => boundPort);

  transport = new SidecarTransport({
    port,
    authToken: token,
    onMessage: (msg) => {
      router.handleMessage(msg);
    },
    onShutdown: () => {
      taskQueue.cancelAll();
      ptyRunner.terminateAll();
    }
  });

  boundPort = transport.start();

  // Emits machine-readable handshake for parent process (C# .NET)
  const handshake = `[AGENTS_ENGINE_READY] port=${boundPort} pid=${process.pid} token=${token} platform=${process.platform}`;
  console.log(handshake);
}

main().catch((err) => {
  console.error('[AGENTS_ENGINE_FATAL]', err);
  process.exit(1);
});
