import versionFile from '../VERSION' with { type: 'text' };

/**
 * The number in VERSION at the repo root: the same file the .NET build and the UI bundle read.
 *
 * Bun inlines the text at bundle time, so a compiled `AgentsEngine` still carries the right
 * version without the file having to ship beside the binary.
 */
export const APP_VERSION = versionFile.trim().replace(/^[vV]\s*/, '');
