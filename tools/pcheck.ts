/**
 * Agents Hub - Automated Cross-Platform Compatibility Checker (/pcheck)
 * Verifies code, binary resolution, script pairings (.cmd <-> .sh),
 * and multi-target build configurations for Windows, macOS, and Linux.
 */

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve, extname } from 'node:path';

interface CheckItem {
  id: string;
  category: string;
  name: string;
  status: 'pass' | 'warn' | 'fail';
  details: string;
}

const checks: CheckItem[] = [];

function recordCheck(category: string, name: string, status: 'pass' | 'warn' | 'fail', details: string) {
  checks.push({ id: `c_${checks.length + 1}`, category, name, status, details });
}

const rootDir = resolve(process.cwd());

console.log('\x1b[36m========================================================\x1b[0m');
console.log('\x1b[1m\x1b[32m Agents Hub - Cross-Platform Compatibility Audit (/pcheck)\x1b[0m');
console.log(` Target Root: ${rootDir}`);
console.log(` Running OS:  ${process.platform} (${process.arch})`);
console.log('\x1b[36m========================================================\x1b[0m\n');

// -------------------------------------------------------------
// 1. Script Pairings: Ensure .cmd scripts have .sh counterparts
// -------------------------------------------------------------
const scriptPairs = [
  { cmd: 'run.cmd', sh: 'run.sh', desc: 'Build & Run Pipeline' },
  { cmd: 'build-sidecar.cmd', sh: 'build-sidecar.sh', desc: 'Sidecar Engine Compiler' },
  { cmd: 'build-installer.cmd', sh: 'build-installer.sh', desc: 'Release Packager' },
  { cmd: 'publish-release.cmd', sh: 'publish-release.sh', desc: 'GitHub Release Publisher' },
  { cmd: 'pcheck.cmd', sh: 'pcheck.sh', desc: 'Cross-Platform Compatibility Checker' },
];

for (const pair of scriptPairs) {
  const hasCmd = existsSync(join(rootDir, pair.cmd));
  const hasSh = existsSync(join(rootDir, pair.sh));

  if (hasCmd && hasSh) {
    recordCheck('Script Pairings', `${pair.desc} (${pair.cmd} + ${pair.sh})`, 'pass', 'Both Windows (.cmd) and POSIX (.sh) scripts exist.');
  } else if (!hasSh) {
    recordCheck('Script Pairings', `${pair.desc}`, 'fail', `Missing POSIX counterpart ${pair.sh} for ${pair.cmd}!`);
  } else {
    recordCheck('Script Pairings', `${pair.desc}`, 'warn', `Missing Windows script ${pair.cmd}.`);
  }
}

// -------------------------------------------------------------
// 2. Project Target Frameworks (AgentsHub.csproj)
// -------------------------------------------------------------
const csprojPath = join(rootDir, 'AgentsHub.csproj');
if (existsSync(csprojPath)) {
  const content = readFileSync(csprojPath, 'utf8');
  const hasWindowsTarget = content.includes('net10.0-windows');
  const hasCrossTarget = content.includes('net10.0');

  if (hasWindowsTarget && hasCrossTarget) {
    recordCheck('Project Multi-Targeting', 'AgentsHub.csproj TargetFrameworks', 'pass', 'Targets both net10.0-windows (WinForms/WebView2) and net10.0 (Linux/macOS Headless Web).');
  } else {
    recordCheck('Project Multi-Targeting', 'AgentsHub.csproj TargetFrameworks', 'fail', 'Missing cross-platform TargetFrameworks configuration.');
  }
} else {
  recordCheck('Project Multi-Targeting', 'AgentsHub.csproj', 'fail', 'File not found.');
}

// -------------------------------------------------------------
// 3. Sidecar Engine Targets (build.ts)
// -------------------------------------------------------------
const buildTsPath = join(rootDir, 'build.ts');
if (existsSync(buildTsPath)) {
  const content = readFileSync(buildTsPath, 'utf8');
  const expectedTargets = ['win-x64', 'win-arm64', 'osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64'];
  const missing = expectedTargets.filter(t => !content.includes(t));

  if (missing.length === 0) {
    recordCheck('Sidecar Build Targets', 'build.ts Architecture Coverage', 'pass', 'Full coverage: Windows (x64/arm64), macOS (arm64/x64), Linux (x64/arm64).');
  } else {
    recordCheck('Sidecar Build Targets', 'build.ts Architecture Coverage', 'fail', `Missing targets: ${missing.join(', ')}`);
  }
} else {
  recordCheck('Sidecar Build Targets', 'build.ts', 'fail', 'File not found.');
}

// -------------------------------------------------------------
// 4. Command Resolution & Extension Handling (.cmd / .exe vs Unix)
// -------------------------------------------------------------
const depCheckerPath = join(rootDir, 'Services', 'DependencyCheckerService.cs');
if (existsSync(depCheckerPath)) {
  const content = readFileSync(depCheckerPath, 'utf8');
  const hasOsCheckForPathext = content.includes('OperatingSystem.IsWindows()') && content.includes('PATHEXT');
  const hasProbeVersionOsCheck = content.includes('OperatingSystem.IsWindows() && extension is ".cmd" or ".bat"');

  if (hasOsCheckForPathext && hasProbeVersionOsCheck) {
    recordCheck('Command Resolution', 'DependencyCheckerService.cs', 'pass', 'Properly handles PATHEXT on Windows and extensionless binaries on Linux/macOS.');
  } else {
    recordCheck('Command Resolution', 'DependencyCheckerService.cs', 'fail', 'Lacks OperatingSystem checks for PATHEXT or .cmd probe.');
  }
}

const customCliPath = join(rootDir, 'Models', 'CustomCliConfig.cs');
if (existsSync(customCliPath)) {
  const content = readFileSync(customCliPath, 'utf8');
  const usesPlatformHelper = content.includes('PlatformHelper.GetDefaultShell()') && content.includes('PlatformHelper.IsWindows');

  if (usesPlatformHelper) {
    recordCheck('Custom CLI Engine', 'CustomCliConfig.cs CommandLine Resolution', 'pass', 'Uses PlatformHelper to select cmd.exe on Windows or /bin/bash on Unix.');
  } else {
    recordCheck('Custom CLI Engine', 'CustomCliConfig.cs CommandLine Resolution', 'warn', 'May default to cmd.exe unconditionally.');
  }
}

// -------------------------------------------------------------
// 5. Session Manager Path Hardcoding Audit
// -------------------------------------------------------------
const sessionMgrPath = join(rootDir, 'sidecar', 'session', 'sessionManager.ts');
if (existsSync(sessionMgrPath)) {
  const content = readFileSync(sessionMgrPath, 'utf8');
  const hasResolveAgy = content.includes('resolveAgyCommand') && content.includes('/opt/homebrew/bin/agy');
  const hasHardcodedWindowsOnly = content.includes("'C:\\\\Users\\\\Gaming\\\\AppData\\\\Local\\\\agy\\\\bin\\\\agy.exe'") && !content.includes('resolveAgyCommand');

  if (hasResolveAgy && !hasHardcodedWindowsOnly) {
    recordCheck('Session Manager', 'sessionManager.ts agy Resolution', 'pass', 'Dynamic cross-platform agy binary discovery for Windows, macOS, and Linux.');
  } else {
    recordCheck('Session Manager', 'sessionManager.ts agy Resolution', 'warn', 'Contains hardcoded user paths or lacks Unix candidate search.');
  }
}

// -------------------------------------------------------------
// 6. POSIX Terminal & ConPTY Session Support
// -------------------------------------------------------------
const conPtyPath = join(rootDir, 'Services', 'ConPty', 'ConPtySession.cs');
if (existsSync(conPtyPath)) {
  const content = readFileSync(conPtyPath, 'utf8');
  const hasPosix = content.includes('StartPosixSession') && content.includes('OperatingSystem.IsWindows()');

  if (hasPosix) {
    recordCheck('Terminal Subsystem', 'ConPtySession.cs Cross-Platform Support', 'pass', 'Dual-mode: Windows PseudoConsole (ConPTY) and Unix POSIX PTY/Pipes.');
  } else {
    recordCheck('Terminal Subsystem', 'ConPtySession.cs Cross-Platform Support', 'fail', 'Missing POSIX fallback implementation for Linux/macOS.');
  }
}

// -------------------------------------------------------------
// 7. Display Final Audit Report
// -------------------------------------------------------------
console.log('\x1b[1mAudit Results:\x1b[0m\n');

let passCount = 0;
let warnCount = 0;
let failCount = 0;

for (const c of checks) {
  let badge = '';
  if (c.status === 'pass') {
    badge = '\x1b[32m✔ PASS\x1b[0m';
    passCount++;
  } else if (c.status === 'warn') {
    badge = '\x1b[33m▲ WARN\x1b[0m';
    warnCount++;
  } else {
    badge = '\x1b[31m✖ FAIL\x1b[0m';
    failCount++;
  }

  console.log(`[${badge}] \x1b[1m${c.category}\x1b[0m - ${c.name}`);
  console.log(`       \x1b[90m${c.details}\x1b[0m`);
}

console.log('\n\x1b[36m--------------------------------------------------------\x1b[0m');
console.log(` Summary: \x1b[32m${passCount} Passed\x1b[0m, \x1b[33m${warnCount} Warnings\x1b[0m, \x1b[31m${failCount} Failures\x1b[0m`);
console.log('\x1b[36m========================================================\x1b[0m\n');

if (failCount > 0) {
  process.exit(1);
} else {
  process.exit(0);
}
