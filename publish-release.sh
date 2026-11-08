#!/usr/bin/env bash
# ==============================================================================
# Agents Hub - GitHub Release Publisher for Linux & macOS
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

VERSION=$(grep -o '<Version>[^<]*' AgentsHub.csproj | sed 's/<Version>//' || echo "1.0.5")
TAG="v$VERSION"
OUTPUT_DIR="$SCRIPT_DIR/dist"

echo "========================================================"
echo " Agents Hub GitHub Release Publisher (POSIX)"
echo " Target Tag: $TAG"
echo "========================================================"

if ! command -v gh >/dev/null 2>&1; then
    echo "[!] GitHub CLI ('gh') not found in PATH."
    echo "    Install with: brew install gh (macOS) or sudo apt install gh (Ubuntu/Debian)"
    echo "    Artifacts are ready in: $OUTPUT_DIR"
    exit 1
fi

# Ensure packages exist in dist
if [ ! -d "$OUTPUT_DIR" ] || [ -z "$(ls -A "$OUTPUT_DIR" 2>/dev/null)" ]; then
    echo "[*] No release artifacts found in $OUTPUT_DIR. Running build-installer.sh..."
    chmod +x ./build-installer.sh 2>/dev/null || true
    ./build-installer.sh
fi

echo "[*] Publishing release $TAG to GitHub..."
gh release create "$TAG" "$OUTPUT_DIR"/* --title "Agents Hub $TAG" --notes "Agents Hub $TAG - Cross-Platform Release for Windows, macOS, and Linux."

echo "[✔] Release $TAG published successfully."
