#!/usr/bin/env bash
# Đóng gói bản phát hành cho Windows x64 (chạy được trên Linux).
# Kết quả: dist/ManagerWindows-<version>-win-x64.zip (giải nén rồi chạy install.cmd với quyền admin).
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET="${DOTNET:-$(command -v dotnet || echo /workspace/.toolchain/dotnet/dotnet)}"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
OUT=dist/ManagerWindows
rm -rf dist && mkdir -p "$OUT"

"$DOTNET" test tests/ManagerWindows.Core.Tests -c Release --nologo -v q

# Tự chứa runtime .NET để máy văn phòng không cần cài thêm gì.
for p in ManagerWindows.App ManagerWindows.Service; do
  "$DOTNET" publish "src/$p" -c Release -r win-x64 --self-contained true -o "$OUT" --nologo -v q -p:DebugType=none
done

cp scripts/install.ps1 scripts/uninstall.ps1 scripts/install.cmd scripts/uninstall.cmd "$OUT/"
cp README.md "$OUT/README.md"
python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', 'dist', 'ManagerWindows')" "dist/ManagerWindows-$VERSION-win-x64"
ls -lh "dist/ManagerWindows-$VERSION-win-x64.zip"
