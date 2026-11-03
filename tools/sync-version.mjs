#!/usr/bin/env node
/**
 * Copies the repo-root VERSION into the files that cannot read it themselves: the two package.json
 * files, their lockfile, and the version lines in README.md and SECURITY.md.
 *
 * Nothing in the app reads any of these. The assembly, the UI bundle, the sidecar, the installer and
 * the update check all take the number from VERSION directly. This is for what ships on GitHub,
 * where a stale number is just a wrong badge.
 *
 * Run automatically before every UI or sidecar build; also `npm run sync-version`.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const version = readFileSync(join(root, 'VERSION'), 'utf8').trim().replace(/^[vV]\s*/, '');

if (!/^\d+\.\d+\.\d+$/.test(version)) {
  console.error(`VERSION must hold three numbers, for example 0.0.121. Found "${version}".`);
  process.exit(1);
}

/** Only the first `"version"` field, which is the package's own. */
const packageVersion = (text) => text.replace(/("version"\s*:\s*")[^"]*(")/, `$1${version}$2`);

/**
 * A lockfile carries the package version twice at the top (the root and the "" package) before any
 * dependency appears, so the first two hits are the ones to touch.
 */
const lockfileVersion = (text) => {
  let seen = 0;
  return text.replace(/("version"\s*:\s*")[^"]*(")/g, (match, before, after) =>
    seen++ < 2 ? `${before}${version}${after}` : match
  );
};

const readmeVersion = (text) =>
  text
    .replace(/^# Agents Hub v\S+/m, `# Agents Hub v${version}`)
    .replace(
      /\[!\[Version: v[^\]]*\]\([^)]*\)\]/,
      `[![Version: v${version}](https://img.shields.io/badge/Version-v${version}-2563eb?style=flat-square)]`
    )
    .replace(/^- \*\*Version\*\*: v\S+/m, `- **Version**: v${version}`);

const securityVersion = (text) =>
  text
    .replace(
      /^\| \*\*[^<*][^*]*\*\* \| Yes \|.*$/m,
      `| **${version}** | Yes | Current active release line. |`
    )
    .replace(
      /^\| \*\*<[^*]*\*\* \| No \|.*$/m,
      `| **< ${version}** | No | Older releases. Please upgrade to the latest release. |`
    );

const targets = [
  ['package.json', packageVersion],
  ['ui/package.json', packageVersion],
  ['ui/package-lock.json', lockfileVersion],
  ['README.md', readmeVersion],
  ['SECURITY.md', securityVersion],
];

const changed = [];

for (const [relative, edit] of targets) {
  const path = join(root, relative);
  let before;

  try {
    before = readFileSync(path, 'utf8');
  } catch {
    console.warn(`skipped ${relative}: not readable`);
    continue;
  }

  const after = edit(before);
  if (after === before) continue;

  writeFileSync(path, after);
  changed.push(relative);
}

console.log(
  changed.length > 0
    ? `version ${version}: updated ${changed.join(', ')}`
    : `version ${version}: already in sync`
);
