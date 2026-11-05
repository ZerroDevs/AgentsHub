#!/usr/bin/env bash
# ==============================================================================
# Agents Hub - Multi-Target Release & Package Builder for Linux & macOS
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

TARGETS=("linux-x64" "linux-arm64" "osx-arm64" "osx-x64")
if [ "$#" -gt 0 ]; then
    case "$1" in
        linux)
            TARGETS=("linux-x64" "linux-arm64")
            ;;
        mac|osx)
            TARGETS=("osx-arm64" "osx-x64")
            ;;
        all)
            TARGETS=("linux-x64" "linux-arm64" "osx-arm64" "osx-x64" "win-x64")
            ;;
        *)
            TARGETS=("$@")
            ;;
    esac
fi

OUTPUT_DIR="$SCRIPT_DIR/dist"
mkdir -p "$OUTPUT_DIR"

# Resolve version from AgentsHub.csproj
VERSION=$(grep -o '<Version>[^<]*' AgentsHub.csproj | sed 's/<Version>//' || echo "1.0.5")

echo "========================================================"
echo " Agents Hub Multi-Target Release Builder (POSIX)"
echo " Version: $VERSION"
echo " Targets: ${TARGETS[*]}"
echo " Output:  $OUTPUT_DIR"
echo "========================================================"

# 1. Build UI dist bundle if missing or outdated
echo "[*] Ensuring UI dist bundle is built..."
if [ -d "ui" ]; then
    (cd ui && npm run build)
fi

# 2. Build sidecar binaries
echo "[*] Ensuring sidecar binaries are compiled..."
chmod +x ./build-sidecar.sh 2>/dev/null || true
./build-sidecar.sh

# 3. Build each dotnet target
TEMP_STAGING="$SCRIPT_DIR/bin/Release/staging"
rm -rf "$TEMP_STAGING"
mkdir -p "$TEMP_STAGING"

for target in "${TARGETS[@]}"; do
    echo ""
    echo "[*] Building target: $target..."
    STAGE_DIR="$TEMP_STAGING/$target"
    mkdir -p "$STAGE_DIR"

    case "$target" in
        linux-x64)
            dotnet publish AgentsHub.csproj -f net10.0 -c Release -r linux-x64 --self-contained -o "$STAGE_DIR"
            rm -rf "$STAGE_DIR/Profiles" "$STAGE_DIR/ui/node_modules" "$STAGE_DIR/ui/src"
            cp LICENSE README.md "$STAGE_DIR/" 2>/dev/null || true
            TAR_PATH="$OUTPUT_DIR/AgentsHub-v$VERSION-linux-x64.tar.gz"
            tar -czf "$TAR_PATH" -C "$STAGE_DIR" .
            echo "  ✔ Generated: $TAR_PATH"
            ;;
        linux-arm64)
            dotnet publish AgentsHub.csproj -f net10.0 -c Release -r linux-arm64 --self-contained -o "$STAGE_DIR"
            rm -rf "$STAGE_DIR/Profiles" "$STAGE_DIR/ui/node_modules" "$STAGE_DIR/ui/src"
            cp LICENSE README.md "$STAGE_DIR/" 2>/dev/null || true
            TAR_PATH="$OUTPUT_DIR/AgentsHub-v$VERSION-linux-arm64.tar.gz"
            tar -czf "$TAR_PATH" -C "$STAGE_DIR" .
            echo "  ✔ Generated: $TAR_PATH"
            ;;
        osx-arm64)
            dotnet publish AgentsHub.csproj -f net10.0 -c Release -r osx-arm64 --self-contained -o "$STAGE_DIR"
            rm -rf "$STAGE_DIR/Profiles" "$STAGE_DIR/ui/node_modules" "$STAGE_DIR/ui/src"
            cp LICENSE README.md "$STAGE_DIR/" 2>/dev/null || true
            TAR_PATH="$OUTPUT_DIR/AgentsHub-v$VERSION-osx-arm64.tar.gz"
            tar -czf "$TAR_PATH" -C "$STAGE_DIR" .
            echo "  ✔ Generated: $TAR_PATH"
            ;;
        osx-x64)
            dotnet publish AgentsHub.csproj -f net10.0 -c Release -r osx-x64 --self-contained -o "$STAGE_DIR"
            rm -rf "$STAGE_DIR/Profiles" "$STAGE_DIR/ui/node_modules" "$STAGE_DIR/ui/src"
            cp LICENSE README.md "$STAGE_DIR/" 2>/dev/null || true
            TAR_PATH="$OUTPUT_DIR/AgentsHub-v$VERSION-osx-x64.tar.gz"
            tar -czf "$TAR_PATH" -C "$STAGE_DIR" .
            echo "  ✔ Generated: $TAR_PATH"
            ;;
        win-x64)
            dotnet publish AgentsHub.csproj -f net10.0-windows -c Release -r win-x64 --self-contained -o "$STAGE_DIR"
            rm -rf "$STAGE_DIR/Profiles" "$STAGE_DIR/ui/node_modules" "$STAGE_DIR/ui/src"
            ZIP_PATH="$OUTPUT_DIR/AgentsHub-v$VERSION-win-x64.zip"
            (cd "$STAGE_DIR" && zip -r "$ZIP_PATH" .)
            echo "  ✔ Generated: $ZIP_PATH"
            ;;
        *)
            echo "  [!] Unknown target $target, skipping."
            ;;
    esac
done

rm -rf "$TEMP_STAGING"
echo ""
echo "========================================================"
echo " SUCCESS: Cross-platform packages ready in $OUTPUT_DIR"
echo "========================================================"
