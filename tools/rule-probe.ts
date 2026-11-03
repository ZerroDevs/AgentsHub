/**
 * Answers one question: does a given `permissions.allow` rule actually let a given command run?
 *
 * Writes the rule into the profile's runtime settings, drives a real turn through the sidecar's own
 * SessionManager, prints the tool result, then restores the file. Not part of the build.
 *
 *   bun run tools/rule-probe.ts --rule "command(echo hi)" --command "echo hi" [--cwd DIR]
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { SessionManager } from '../sidecar/session/sessionManager';
import { TaskQueue } from '../sidecar/queue/taskQueue';

const args = process.argv.slice(2);
function flag(name: string): string | undefined {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : undefined;
}
const rules = args.reduce<string[]>((acc, arg, i) => {
  if (arg === '--rule' && args[i + 1]) acc.push(args[i + 1]);
  return acc;
}, []);
const command = flag('command');
if (!command) {
  console.log('usage: --command "<shell command>" [--rule "command(...)"]... [--cwd DIR]');
  process.exit(1);
}

const profile = flag('profile') || 'ZXR@1';
const settingsPath = join(
  process.cwd(),
  'bin',
  'Debug',
  'net10.0-windows',
  'Profiles',
  'Antigravity',
  profile,
  'VirtualHome',
  '.gemini',
  'antigravity-cli',
  'settings.json'
);

const original = readFileSync(settingsPath, 'utf8');
const settings = JSON.parse(original);
settings.permissions = { allow: rules };
writeFileSync(settingsPath, JSON.stringify(settings, null, 2));

const events: any[] = [];
const queue = new TaskQueue(2, (e: any) => events.push(e));
const sm = new SessionManager(queue, (e: any) => events.push(e));

try {
  await sm.handleSendChat({
    sessionId: `probe_rule_${Date.now()}`,
    prompt: `Run the shell command exactly: ${command}`,
    profile,
    skipPermissions: false,
    workingDirectory: flag('cwd'),
  });
} finally {
  writeFileSync(settingsPath, original);
}

const output = events.find(e => e.event === 'tool_output');
const denial = events.find(e => e.event === 'permission_denied');
console.log(
  JSON.stringify(
    {
      rules,
      command,
      tool: output?.tool,
      toolSuccess: output?.success,
      rule_emitted: denial?.rule,
      error: typeof output?.error === 'string' ? output.error.slice(0, 120) : undefined,
    },
    null,
    2
  )
);
process.exit(0);
