#!/usr/bin/env bash
# Rebuild FolderLink end-to-end from a fresh Linux (or Windows/macOS) box:
# installs the .NET SDK if it's missing, runs the cross-platform test
# suite, cross-compiles the win-x64 self-contained single-file exe,
# copies it to the repo root, and rewrites checksums.txt to match.
#
# Usage: scripts/build.sh   (run from anywhere; paths are relative to this
# script's location)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "== .NET SDK not found — installing dotnet-sdk-8.0 via apt =="
    # This is the package that worked in the sandbox this was built in
    # (Ubuntu 24.04 "noble"). If apt isn't available, install the SDK
    # manually from https://dotnet.microsoft.com/download/dotnet/8.0
    # instead and re-run this script.
    sudo apt-get update
    sudo apt-get install -y dotnet-sdk-8.0
fi

echo "== dotnet version =="
dotnet --version

echo "== Running FolderLink.Core.Tests (cross-platform, no Windows needed) =="
dotnet test "$REPO_ROOT/src/FolderLink.Core.Tests"

echo "== Publishing win-x64 self-contained single-file exe =="
# EnableWindowsTargeting + the WindowsForms FrameworkReference (both set
# in src/FolderLink/FolderLink.csproj) are what make this possible from a
# non-Windows host — see AGENTS.md if this step ever starts failing.
rm -rf "$REPO_ROOT/src/FolderLink/publish"
dotnet publish "$REPO_ROOT/src/FolderLink" -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -o "$REPO_ROOT/src/FolderLink/publish"

cp "$REPO_ROOT/src/FolderLink/publish/FolderLink.exe" "$REPO_ROOT/FolderLink.exe"

echo "== Updating checksums.txt =="
HASH=$(sha256sum "$REPO_ROOT/FolderLink.exe" | cut -d' ' -f1)
cat > "$REPO_ROOT/checksums.txt" <<EOF
SHA256 checksum — verify your download matches this before running.

On Windows (PowerShell):
    Get-FileHash .\\FolderLink.exe

On Linux/macOS:
    sha256sum FolderLink.exe

$HASH  FolderLink.exe
EOF

echo "== Done =="
echo "FolderLink.exe: $(du -h "$REPO_ROOT/FolderLink.exe" | cut -f1)"
echo "SHA256: $HASH"
echo
echo "Remember to 'git add FolderLink.exe checksums.txt' (and anything"
echo "else you changed) and commit — this script doesn't commit for you."
