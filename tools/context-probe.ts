/**
 * Proves session isolation: two chats must not share memory, and a chat must remember its own.
 *
 * 1. A: "remember ZQ7-MARIGOLD"           -> new conversation, id captured
 * 2. B: "which word?"  (no id)      -> must be a *different* conversation and must not know
 * 3. C: "which word?"  (A's id)     -> must resume A and answer ZQ7-MARIGOLD
 *
 * Runs the sidecar's own SessionManager in-process. Not part of the build.
 */
import { SessionManager } from '../sidecar/session/sessionManager';
import { TaskQueue } from '../sidecar/queue/taskQueue';

const cwd = 'C:/Users/Gaming/AppData/Local/Temp/cwdprobe';

async function turn(label: string, prompt: string, conversationId?: string) {
  const events: any[] = [];
  const q = new TaskQueue(2, (e: any) => events.push(e));
  const sm = new SessionManager(q, (e: any) => events.push(e));

  await sm.handleSendChat({
    sessionId: `${label}_${Date.now()}`,
    prompt,
    profile: 'ZXR@1',
    skipPermissions: true,
    workingDirectory: cwd,
    conversationId,
  });

  const started = events.find(e => e.event === 'conversation_started');
  const text = events
    .filter(e => e.event === 'chat_token')
    .map(e => e.text)
    .join('');

  return { conversationId: started?.conversationId as string | undefined, text: text.trim() };
}

const a = await turn('A', 'Remember the word ZQ7-MARIGOLD. Reply with just OK.');
console.log('A  conv:', a.conversationId, '| reply:', a.text.slice(0, 60));

const b = await turn('B', 'Which word did I ask you to remember? One word, or "none".');
console.log('B  conv:', b.conversationId, '| reply:', b.text.slice(0, 60));

const c = await turn('C', 'Which word did I ask you to remember? One word, or "none".', a.conversationId);
console.log('C  conv:', c.conversationId, '| reply:', c.text.slice(0, 60));

console.log('--- verdict ---');
console.log('distinct conversations:', a.conversationId !== b.conversationId);
console.log('B forgot (expected true):', !/zq7-marigold/i.test(b.text));
console.log('C remembered (expected true):', /zq7-marigold/i.test(c.text));
process.exit(0);
