#!/usr/bin/env bash
# Agents Hub - Build & Run Pipeline (Cross-Platform)
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Ensure local .dotnet and .bun environments are in PATH
if [ -d "$HOME/.dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi
if [ -d "$HOME/.bun/bin" ]; then
    export PATH="$HOME/.bun/bin:$PATH"
fi

echo "========================================================"
echo " Agents Hub - Build and Launch Pipeline"
echo "========================================================"
echo ""

# ---------------------------------------------------------
# 1. Check the Antigravity CLI
# ---------------------------------------------------------
# The Antigravity engine drives `agy`. Without it that engine cannot start, and profile
# switching has nothing to switch. DeepSeek mode works regardless, so a missing CLI warns
# instead of stopping the run.
echo "[*] Step 1/4: Checking the Antigravity CLI (agy)..."

AGY_PATH=""
if command -v agy >/dev/null 2>&1; then
    AGY_PATH="$(command -v agy)"
elif [ -x "$HOME/.antigravity/bin/agy" ]; then
    # An installer writes a new PATH entry that this shell will never see.
    AGY_PATH="$HOME/.antigravity/bin/agy"
    export PATH="$HOME/.antigravity/bin:$PATH"
fi

if [ -n "$AGY_PATH" ]; then
    echo "[OK] Found agy at $AGY_PATH."
    echo ""
else
    echo "[WARN] agy was not found, so the Antigravity engine has no CLI to run."
    echo "       Install it from https://antigravity.google/cli and reopen this terminal"
    echo "       so PATH is picked up. DeepSeek mode works without it."
    echo ""
fi

# ---------------------------------------------------------
# 2. Check and Build Bun Sidecar Engine
# ---------------------------------------------------------
echo "[*] Step 2/4: Checking Bun runtime and building Sidecar Engine..."

BUN_CMD=""
if command -v bun >/dev/null 2>&1; then
    BUN_CMD="bun"
elif [ -x "$HOME/.bun/bin/bun" ]; then
    BUN_CMD="$HOME/.bun/bin/bun"
fi

if [ -z "$BUN_CMD" ]; then
    echo "[!] Bun not found. Installing Bun..."
    curl -fsSL https://bun.sh/install | bash
    export PATH="$HOME/.bun/bin:$PATH"
    BUN_CMD="$HOME/.bun/bin/bun"
fi

# Detect architecture
OS_TYPE="$(uname -s | tr '[:upper:]' '[:lower:]')"
ARCH_TYPE="$(uname -m)"
TARGET="linux-x64"

if [ "$OS_TYPE" = "darwin" ]; then
    if [ "$ARCH_TYPE" = "arm64" ]; then
        TARGET="osx-arm64"
    else
        TARGET="osx-x64"
    fi
elif [ "$OS_TYPE" = "linux" ]; then
    if [ "$ARCH_TYPE" = "aarch64" ] || [ "$ARCH_TYPE" = "arm64" ]; then
        TARGET="linux-arm64"
    else
        TARGET="linux-x64"
    fi
fi

echo "[*] Compiling Sidecar Engine ($TARGET)..."
"$BUN_CMD" run "$SCRIPT_DIR/build.ts" --target="$TARGET"
echo "[OK] Sidecar Engine built successfully."
echo ""

# ---------------------------------------------------------
# 3. Check and Build React / Vite Frontend
# ---------------------------------------------------------
echo "[*] Step 3/4: Checking dependencies and building React UI..."
if [ ! -d "ui/node_modules" ]; then
    echo "[*] ui/node_modules not found. Running npm install in ui..."
    (cd ui && npm install)
fi

echo "[*] Building React UI..."
if ! (cd ui && npm run build); then
    echo ""
    echo "[ERROR] React / Vite build failed. The message above names the file and line."
    echo "        If it points at a type error that no longer exists in the source, the installed"
    echo "        dependencies are stale: delete ui/node_modules and ui/package-lock.json,"
    echo "        then run this script again."
    echo ""
    exit 1
fi
echo "[OK] React frontend built successfully."
echo ""

# ---------------------------------------------------------
# 4. Launch Agents Hub via .NET 10
# ---------------------------------------------------------
echo "[*] Step 4/4: Starting Agents Hub via dotnet run..."
echo "========================================================"
echo ""

if [ "$OS_TYPE" = "darwin" ] || [ "$OS_TYPE" = "linux" ]; then
    # Headless cross-platform host mode
    dotnet run -f net10.0 "$@"
else
    # Windows desktop GUI mode
    dotnet run -f net10.0-windows "$@"
fi
