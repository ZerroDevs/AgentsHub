/**
 * Drives the real SessionManager with full access off and prints the events the UI would receive.
 *
 * Runs in-process (no transport): the sandboxed shell blocks loopback sockets, and the interesting
 * question is whether the sidecar promotes a refusal to `permission_denied`, not whether Bun can
 * bind a port. Not part of the build.
 */
import { SessionManager } from '../sidecar/session/sessionManager';
import { TaskQueue } from '../sidecar/queue/taskQueue';

const events: any[] = [];
const emit = (event: any) => {
  events.push(event);
  const name = event.event;
  if (name === 'permission_denied') {
    console.log('PERMISSION_DENIED ->', JSON.stringify({
      permission: event.permission,
      command: event.command,
      target: event.target,
      rule: event.rule,
    }));
  } else if (['tool_start', 'tool_output', 'result', 'session_status', 'error'].includes(name)) {
    console.log(name, JSON.stringify({
      tool: event.tool,
      success: event.success,
      state: event.state,
      error: typeof event.error === 'string' ? event.error.slice(0, 80) : undefined,
    }));
  }
};

const queue = new TaskQueue(2, emit as any);
const sm = new SessionManager(queue, emit as any);

await sm.handleSendChat({
  sessionId: 'probe_denial_1',
  prompt: 'Run the shell command exactly: echo hi',
  profile: 'ZXR@1',
  skipPermissions: false,
});

console.log('--- events seen ---');
console.log([...new Set(events.map(e => e.event))].join(', '));
process.exit(0);
