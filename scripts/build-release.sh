#!/usr/bin/env bash
# Đóng gói bản phát hành cho Windows x64 (chạy được trên Linux).
# Kết quả:
#   dist/ManagerWindows-<version>-win-x64.zip           bản cài đặt (App + Service, chạy install.cmd với quyền admin)
#   dist/ManagerWindows-<version>-win-x64-portable.zip  bản portable (giải nén là chạy ManagerWindows.App.exe)
#   dist/SHA256SUMS.txt
# App chỉ publish MỘT lần rồi chép vào cả hai gói, nên giao diện và tính năng của App ở hai bản giống hệt nhau;
# bản portable chỉ khác ở file portable.txt và không có dịch vụ nền.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET="${DOTNET:-$(command -v dotnet || echo /workspace/.toolchain/dotnet/dotnet)}"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
rm -rf dist && mkdir -p dist/build/app dist/build/service

"$DOTNET" test tests/ManagerWindows.Core.Tests -c Release --nologo -v q

# Tự chứa runtime .NET để máy văn phòng không cần cài thêm gì.
"$DOTNET" publish src/ManagerWindows.App -c Release -r win-x64 --self-contained true -o dist/build/app --nologo -v q -p:DebugType=none
"$DOTNET" publish src/ManagerWindows.Service -c Release -r win-x64 --self-contained true -o dist/build/service --nologo -v q -p:DebugType=none

# Bản cài đặt: Service trước, App sau (file runtime trùng nhau là cùng một bản .NET).
INST=dist/ManagerWindows
mkdir -p "$INST"
cp -r dist/build/service/. "$INST/"
cp -r dist/build/app/. "$INST/"
cp scripts/install.ps1 scripts/uninstall.ps1 scripts/install.cmd scripts/uninstall.cmd README.md "$INST/"

# Bản portable: đúng bộ file App ở trên + portable.txt.
PORT=dist/portable/ManagerWindows-Portable
mkdir -p "$PORT"
cp -r dist/build/app/. "$PORT/"
cp scripts/portable.txt "$PORT/portable.txt"

# Kiểm tra: mọi file của App trong hai gói phải giống hệt nhau.
python3 - "$INST" "$PORT" dist/build/app <<'PY'
import hashlib, os, sys
inst, port, app = sys.argv[1:]
def h(p): return hashlib.sha256(open(p, 'rb').read()).hexdigest()
bad = []
for root, _, files in os.walk(app):
    for f in files:
        rel = os.path.relpath(os.path.join(root, f), app)
        src = h(os.path.join(app, rel))
        for pkg in (inst, port):
            if h(os.path.join(pkg, rel)) != src: bad.append((pkg, rel))
if bad:
    sys.exit(f"File App khác nhau giữa hai gói: {bad[:10]}")
print("App giống hệt nhau ở hai gói:", sum(len(f) for _, _, f in os.walk(app)), "file")
PY

zipdir() { python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', sys.argv[2], sys.argv[3])" "$1" "$2" "$3"; }
zipdir "dist/ManagerWindows-$VERSION-win-x64" dist ManagerWindows
zipdir "dist/ManagerWindows-$VERSION-win-x64-portable" dist/portable ManagerWindows-Portable
rm -rf dist/build
ls -lh dist/*.zip
(cd dist && sha256sum ./*.zip > SHA256SUMS.txt && cat SHA256SUMS.txt)
