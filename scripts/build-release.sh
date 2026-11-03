#!/usr/bin/env bash
# Đóng gói bản phát hành cho Windows x64 (chạy được trên Linux).
# Kết quả:
#   dist/ManagerWindows-<version>-win-x64.zip           bản cài đặt (App + Service, chạy install.cmd với quyền admin)
#   dist/ManagerWindows-<version>-win-x64-portable.zip  bản portable (một file exe + rules, giải nén là chạy)
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
# Bản portable: chỉ App, gói thành một file exe (runtime và thư viện native tự giải nén khi chạy).
PORT=dist/portable/ManagerWindows-Portable
"$DOTNET" publish src/ManagerWindows.App -c Release -r win-x64 --self-contained true -o "$PORT" --nologo -v q \
  -p:DebugType=none -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
cp scripts/portable.txt "$PORT/portable.txt"

zipdir() { python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', sys.argv[2], sys.argv[3])" "$1" "$2" "$3"; }
zipdir "dist/ManagerWindows-$VERSION-win-x64" dist ManagerWindows
zipdir "dist/ManagerWindows-$VERSION-win-x64-portable" dist/portable ManagerWindows-Portable
ls -lh dist/*.zip
(cd dist && sha256sum ./*.zip > SHA256SUMS.txt && cat SHA256SUMS.txt)
