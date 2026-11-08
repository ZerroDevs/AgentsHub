#!/usr/bin/env bash
# ==============================================================================
# Agents Hub - Sidecar Engine Cross-Compiler for macOS & Linux
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

BUN_CMD=""

if command -v bun >/dev/null 2>&1; then
    BUN_CMD="bun"
elif [ -x "$HOME/.bun/bin/bun" ]; then
    BUN_CMD="$HOME/.bun/bin/bun"
else
    echo "[!] Bun runtime not found on system."
    echo "[*] Downloading and installing Bun automatically..."
    curl -fsSL https://bun.sh/install | bash
    if [ -x "$HOME/.bun/bin/bun" ]; then
        BUN_CMD="$HOME/.bun/bin/bun"
        echo "[✔] Bun installed successfully."
    else
        echo "[ERROR] Failed to locate or install Bun. Please install manually from https://bun.sh"
        exit 1
    fi
fi

echo "[*] Running Sidecar Cross-Compilation using $BUN_CMD..."
"$BUN_CMD" run build.ts "$@"
