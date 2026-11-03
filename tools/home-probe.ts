import { SessionManager } from '../sidecar/session/sessionManager';
import { TaskQueue } from '../sidecar/queue/taskQueue';
const q = new TaskQueue(1, () => {});
const sm = new SessionManager(q, () => {});
console.log('AGENTS_PROFILES_DIR =', process.env.AGENTS_PROFILES_DIR || '(unset)');
console.log('cwd =', process.cwd());
console.log('ZXR@1 ->', sm.prepareVirtualHome('ZXR@1'));
process.exit(0);
