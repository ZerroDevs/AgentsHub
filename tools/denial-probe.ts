import { parsePermissionDenial, commandRuleTarget } from '../sidecar/session/sessionManager';

const cases: Array<[string, string]> = [
  [
    'unsandboxed command',
    'permission check failed for unsandboxed "echo hi": user denied permission to run command:\necho hi\nDo not attempt to circumvent this denial by rephrasing the command, using alternative tools/scripts (e.g. python, sh, curl), or accessing the same target resource. Proceed without performing this action.',
  ],
  [
    'sandboxed command',
    'permission check failed for command "echo hi": user denied permission to run command:\necho hi\nDo not attempt to circumvent this denial by rephrasing the command.',
  ],
  [
    'file write',
    'permission check failed for write_file "C:/x/hello.py": user denied permission for write_file(C:/x/hello.py)\nDo not attempt to circumvent this denial by rephrasing the command.',
  ],
  ['ordinary tool error', 'ENOENT: no such file or directory, open C:\\tmp\\x'],
  ['empty', ''],
];

for (const [label, message] of cases) {
  const parsed = parsePermissionDenial(message);
  console.log(label, '->', parsed ? JSON.stringify({ permission: parsed.permission, command: parsed.command, rule: parsed.rule }) : 'undefined');
}

const targets = ['echo hi', '"C:\\Program Files\\nodejs\\node.exe" -v', 'npm run dev', 'cd x && npm test', ''];
for (const cmd of targets) console.log(`target(${JSON.stringify(cmd)}) ->`, commandRuleTarget(cmd));
