/**
 * Prints the usage `agy` reports for a short conversation, one line per turn.
 * Tells apart "baseline context is expensive" from "the numbers are cumulative".
 */
import { SessionManager } from '../sidecar/session/sessionManager';
import { TaskQueue } from '../sidecar/queue/taskQueue';

const prompts = process.argv.slice(2);
const events: any[] = [];
const q = new TaskQueue(2, (e: any) => events.push(e));
const sm = new SessionManager(q, (e: any) => events.push(e));

let conv: string | undefined;
for (const [i, prompt] of prompts.entries()) {
  const before = events.length;
  await sm.handleSendChat({
    sessionId: `usage_${Date.now()}_${i}`,
    prompt,
    profile: 'ZXR@1',
    skipPermissions: true,
    workingDirectory: 'C:/Users/Gaming/AppData/Local/Temp/cwdprobe',
    conversationId: conv,
  });
  const slice = events.slice(before);
  conv = slice.find(e => e.event === 'conversation_started')?.conversationId ?? conv;
  const usage = slice.find(e => e.event === 'token_usage')?.usage;
  const reply = slice.filter(e => e.event === 'chat_token').map(e => e.text).join('').trim().slice(0, 40);
  console.log(
    `turn ${i + 1}: in=${usage?.inputTokens} out=${usage?.outputTokens} think=${usage?.thinkingTokens} total=${usage?.totalTokens} | ${reply}`
  );
}
process.exit(0);
