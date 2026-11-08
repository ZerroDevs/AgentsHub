#!/usr/bin/env bash
# Agents Hub - Cross-Platform Compatibility Audit (/pcheck)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if command -v bun >/dev/null 2>&1; then
    bun run "$SCRIPT_DIR/tools/pcheck.ts" "$@"
elif [ -x "$HOME/.bun/bin/bun" ]; then
    "$HOME/.bun/bin/bun" run "$SCRIPT_DIR/tools/pcheck.ts" "$@"
else
    node "$SCRIPT_DIR/tools/pcheck.ts" "$@"
fi
