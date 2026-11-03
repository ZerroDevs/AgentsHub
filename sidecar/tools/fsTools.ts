/**
 * Safe File System Tools
 * Atomic file read/write, directory tree traversal with ignore limits, and unified diff patcher.
 */

import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';

export interface FileReadResult {
  content: string;
  sizeBytes: number;
  lines: number;
}

export interface TreeNode {
  name: string;
  path: string;
  type: 'file' | 'directory';
  sizeBytes?: number;
  children?: TreeNode[];
}

export class FsTools {
  private static readonly DEFAULT_IGNORES = new Set([
    'node_modules',
    '.git',
    'bin',
    'obj',
    '.vs',
    '.vscode',
    'dist',
    '.idea',
    '.DS_Store'
  ]);

  /**
   * Safely reads file contents with line count and byte length
   */
  public static async readFile(filePath: string): Promise<FileReadResult> {
    const fullPath = resolve(filePath);
    if (!existsSync(fullPath)) {
      throw new Error(`File not found: ${filePath}`);
    }

    const file = Bun.file(fullPath);
    const content = await file.text();
    const lines = content.split('\n').length;
    return {
      content,
      sizeBytes: file.size,
      lines
    };
  }

  /**
   * Performs an atomic write using a temporary swap file to prevent corruption on crash
   */
  public static async writeFileAtomic(filePath: string, content: string): Promise<{ bytesWritten: number }> {
    const fullPath = resolve(filePath);
    const targetDir = dirname(fullPath);

    if (!existsSync(targetDir)) {
      mkdirSync(targetDir, { recursive: true });
    }

    const tempPath = `${fullPath}.tmp.${Date.now()}.${Math.random().toString(36).substring(2, 8)}`;

    try {
      await Bun.write(tempPath, content);
      renameSync(tempPath, fullPath);
      return { bytesWritten: Buffer.byteLength(content, 'utf8') };
    } catch (err) {
      if (existsSync(tempPath)) {
        try {
          unlinkSync(tempPath);
        } catch {}
      }
      throw err;
    }
  }

  /**
   * Traverses directory tree recursively up to maxDepth, ignoring build artifacts
   */
  public static async getDirectoryTree(
    dirPath: string,
    maxDepth: number = 3,
    customIgnores: string[] = []
  ): Promise<TreeNode> {
    const rootPath = resolve(dirPath);
    const ignores = new Set([...this.DEFAULT_IGNORES, ...customIgnores]);

    const traverse = (currentPath: string, depth: number): TreeNode => {
      const name = currentPath === rootPath ? '.' : relative(rootPath, currentPath);
      const isDir = statSync(currentPath).isDirectory();

      if (!isDir) {
        const stat = statSync(currentPath);
        return {
          name,
          path: currentPath,
          type: 'file',
          sizeBytes: stat.size
        };
      }

      const node: TreeNode = {
        name,
        path: currentPath,
        type: 'directory',
        children: []
      };

      if (depth >= maxDepth) {
        return node;
      }

      try {
        const entries = readdirSync(currentPath, { withFileTypes: true });
        for (const entry of entries) {
          if (ignores.has(entry.name)) continue;

          const childPath = join(currentPath, entry.name);
          if (entry.isDirectory()) {
            node.children?.push(traverse(childPath, depth + 1));
          } else if (entry.isFile()) {
            try {
              const stat = statSync(childPath);
              node.children?.push({
                name: entry.name,
                path: childPath,
                type: 'file',
                sizeBytes: stat.size
              });
            } catch {}
          }
        }
      } catch {}

      return node;
    };

    return traverse(rootPath, 0);
  }

  /**
   * Applies unified diff hunk patch to a file
   */
  public static async applyDiffPatch(filePath: string, diffText: string): Promise<{ appliedChunks: number }> {
    const fullPath = resolve(filePath);
    let original = '';
    if (existsSync(fullPath)) {
      original = await Bun.file(fullPath).text();
    }

    const originalLines = original.split('\n');
    const diffLines = diffText.split('\n');

    let chunkCount = 0;
    const hunkHeaderRegex = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/;

    // Simple robust hunk processor
    let resultLines: string[] = [];
    let origIdx = 0;
    let inHunk = false;

    for (let i = 0; i < diffLines.length; i++) {
      const line = diffLines[i];
      const match = line.match(hunkHeaderRegex);

      if (match) {
        chunkCount++;
        inHunk = true;
        const oldStart = parseInt(match[1], 10) - 1;

        // Copy unaffected lines prior to this hunk
        while (origIdx < oldStart && origIdx < originalLines.length) {
          resultLines.push(originalLines[origIdx]);
          origIdx++;
        }
        continue;
      }

      if (!inHunk) continue;

      if (line.startsWith('+')) {
        resultLines.push(line.substring(1));
      } else if (line.startsWith('-')) {
        origIdx++; // skip line from original
      } else if (line.startsWith(' ')) {
        resultLines.push(originalLines[origIdx] ?? line.substring(1));
        origIdx++;
      }
    }

    // Append remainder of original file
    while (origIdx < originalLines.length) {
      resultLines.push(originalLines[origIdx]);
      origIdx++;
    }

    const patchedContent = resultLines.join('\n');
    await this.writeFileAtomic(fullPath, patchedContent);
    return { appliedChunks: Math.max(1, chunkCount) };
  }
}
