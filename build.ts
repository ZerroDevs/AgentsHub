/**
 * Cross-Platform Compilation Orchestrator for AgentsHub Sidecar Engine
 * Compiles TypeScript source to self-contained binaries for Windows, macOS, and Linux via Bun.
 */

import { existsSync, mkdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

interface TargetConfig {
  id: string;
  bunTarget: string;
  outputPath: string;
  description: string;
}

const TARGETS: TargetConfig[] = [
  {
    id: 'win-x64',
    bunTarget: 'bun-windows-x64',
    outputPath: 'bin/win-x64/AgentsEngine.exe',
    description: 'Windows (x64 Intel/AMD)'
  },
  {
    id: 'win-arm64',
    bunTarget: 'bun-windows-arm64',
    outputPath: 'bin/win-arm64/AgentsEngine.exe',
    description: 'Windows (ARM64 Snapdragon/Copilot+)'
  },
  {
    id: 'osx-arm64',
    bunTarget: 'bun-darwin-arm64',
    outputPath: 'bin/osx-arm64/AgentsEngine',
    description: 'macOS (Apple Silicon M1/M2/M3/M4)'
  },
  {
    id: 'osx-x64',
    bunTarget: 'bun-darwin-x64',
    outputPath: 'bin/osx-x64/AgentsEngine',
    description: 'macOS (x64 Intel)'
  },
  {
    id: 'linux-x64',
    bunTarget: 'bun-linux-x64',
    outputPath: 'bin/linux-x64/AgentsEngine',
    description: 'Linux (x64 Ubuntu/Debian/Arch/RHEL)'
  },
  {
    id: 'linux-arm64',
    bunTarget: 'bun-linux-arm64',
    outputPath: 'bin/linux-arm64/AgentsEngine',
    description: 'Linux (ARM64 Raspberry Pi/AWS Graviton)'
  }
];

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

async function compileTarget(target: TargetConfig, rootDir: string): Promise<boolean> {
  const fullOutPath = resolve(rootDir, target.outputPath);
  const outDir = dirname(fullOutPath);

  if (!existsSync(outDir)) {
    mkdirSync(outDir, { recursive: true });
  }

  const entryPoint = resolve(rootDir, 'sidecar/engine.ts');

  console.log(`\n[\x1b[36m*\x1b[0m] Compiling ${target.description}...`);
  console.log(`    Target:  ${target.bunTarget}`);
  console.log(`    Output:  ${target.outputPath}`);

  const start = performance.now();

  const proc = Bun.spawn([
    process.execPath,
    'build',
    entryPoint,
    '--compile',
    `--target=${target.bunTarget}`,
    `--outfile=${fullOutPath}`,
    '--minify'
  ], {
    stdout: 'inherit',
    stderr: 'inherit'
  });

  const exitCode = await proc.exited;
  const duration = ((performance.now() - start) / 1000).toFixed(2);

  if (exitCode === 0 && existsSync(fullOutPath)) {
    const size = statSync(fullOutPath).size;
    console.log(`    \x1b[32m✔ Success\x1b[0m in ${duration}s (${formatBytes(size)})`);
    return true;
  } else {
    console.error(`    \x1b[31m✖ Failed\x1b[0m with exit code ${exitCode}`);
    return false;
  }
}

async function main() {
  const args = process.argv.slice(2);
  const rootDir = process.cwd();

  let targetFilter: string | null = null;
  for (const arg of args) {
    if (arg.startsWith('--target=')) {
      targetFilter = arg.split('=')[1];
    } else if (arg === '--all') {
      targetFilter = 'all';
    }
  }

  // If no arg is passed, default to current host platform
  if (!targetFilter) {
    if (process.platform === 'win32') targetFilter = 'win-x64';
    else if (process.platform === 'darwin') targetFilter = process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64';
    else targetFilter = 'linux-x64';
  }

  const selectedTargets = targetFilter === 'all'
    ? TARGETS
    : TARGETS.filter(t => t.id === targetFilter || t.bunTarget === targetFilter);

  if (selectedTargets.length === 0) {
    console.error(`Unknown target: ${targetFilter}`);
    console.log(`Available targets: ${TARGETS.map(t => t.id).join(', ')}, all`);
    process.exit(1);
  }

  console.log('========================================================');
  console.log(' AgentsHub Sidecar Engine Cross-Compiler (Bun)');
  console.log(` Targets: ${selectedTargets.map(t => t.id).join(', ')}`);
  console.log('========================================================');

  let successCount = 0;
  for (const target of selectedTargets) {
    const ok = await compileTarget(target, rootDir);
    if (ok) successCount++;
  }

  console.log('\n========================================================');
  console.log(` Compilation completed: ${successCount}/${selectedTargets.length} succeeded.`);
  console.log('========================================================\n');

  if (successCount < selectedTargets.length) {
    process.exit(1);
  }
}

main().catch((err) => {
  console.error('[BUILD_FATAL]', err);
  process.exit(1);
});
