#!/bin/bash
# Standalone Apple Silicon app: frozen upstream Python engine + shared .NET orchestration.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo 'Build on Apple Silicon macOS (or use the macOS workflow artifact).' >&2
    exit 1
fi
for tool in uv dotnet sips iconutil codesign ditto hdiutil; do
    command -v "$tool" >/dev/null || { echo "Missing build tool: $tool" >&2; exit 1; }
done
PYTHON="${PATCH_GOBLINS_PYTHON:-python3.12}"
command -v "$PYTHON" >/dev/null || { echo "Install a framework Python 3.12, e.g. brew install python@3.12" >&2; exit 1; }
BUILD="$ROOT/build/macos"
mkdir -p "$BUILD" "$ROOT/dist"
# The lock includes all transitive Python and Objective-C dependencies.
UV_PROJECT_ENVIRONMENT="$BUILD/venv" uv sync --locked --python "$PYTHON" --group macos-build --extra menubar --no-editable
PY="$BUILD/venv/bin/python"
dotnet publish dock/PatchGoblins.Backend/PatchGoblins.Backend.csproj \
    -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    --nologo -o "$BUILD/backend"
ICONSET="$BUILD/goblin.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" assets/goblin.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" assets/goblin.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$BUILD/goblin.icns"
"$PY" macos/setup.py py2app --bdist-base "$BUILD/py2app" --dist-dir "$BUILD/dist"
APP="$BUILD/dist/Patch Goblins.app"
# py2app sets the name from the script, so normalize the bundle and launcher.
if [[ ! -d "$APP" && -d "$BUILD/dist/main.app" ]]; then
    mv "$BUILD/dist/main.app" "$APP"
fi
if [[ -f "$APP/Contents/MacOS/main" ]]; then
    mv "$APP/Contents/MacOS/main" "$APP/Contents/MacOS/Patch Goblins"
    /usr/libexec/PlistBuddy -c 'Set :CFBundleExecutable Patch Goblins' "$APP/Contents/Info.plist"
fi
chmod +x "$APP/Contents/Resources/backend/patch-goblins-backend" "$APP/Contents/MacOS/cswap"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
"$APP/Contents/MacOS/cswap" --version
"$APP/Contents/Resources/backend/patch-goblins-backend" --help
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ROOT/dist/patch-goblins-macos-arm64.zip"
STAGE="$(mktemp -d "$BUILD/dmg.XXXXXX")"
trap 'rmdir "$STAGE" 2>/dev/null || true' EXIT
ditto "$APP" "$STAGE/Patch Goblins.app"
ln -s /Applications "$STAGE/Applications"
hdiutil create -ov -format UDZO -volname 'Patch Goblins' -srcfolder "$STAGE" "$ROOT/dist/patch-goblins-macos-arm64.dmg"
cd "$ROOT/dist"
shasum -a 256 patch-goblins-macos-arm64.zip > patch-goblins-macos-arm64.zip.sha256
shasum -a 256 patch-goblins-macos-arm64.dmg > patch-goblins-macos-arm64.dmg.sha256
echo "Built $APP and dist/patch-goblins-macos-arm64.{zip,dmg}"
