import { SessionManager } from '../sidecar/session/sessionManager';
import { TaskQueue } from '../sidecar/queue/taskQueue';

const events: any[] = [];
const q = new TaskQueue(2, (e: any) => events.push(e));
const sm = new SessionManager(q, (e: any) => events.push(e));

await sm.handleSendChat({
  sessionId: `repro_${Date.now()}`,
  prompt: process.argv[2] || 'make python simple hello world print app and run it',
  profile: 'ZXR@1',
  skipPermissions: false,
  workingDirectory: process.argv[3],
});

for (const e of events) {
  if (e.event === 'tool_start') console.log('tool_start', e.tool, JSON.stringify(e.params).slice(0, 160));
  if (e.event === 'tool_output' && e.success === false) console.log('FAILED', e.tool, String(e.error).slice(0, 260));
  if (e.event === 'permission_denied') console.log('DENIED', JSON.stringify({ permission: e.permission, command: e.command, rule: e.rule }));
}
process.exit(0);
